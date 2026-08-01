using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// diff→上传那条队列。它存在的理由是「写侧永不阻塞」——diff 必须能一路跑到底，
/// 上传阶段的剩余时间才有分母（见 <c>StageProgress.Eta</c> 开头那个 <c>_total &lt;= 0</c>）。
/// 所以这里每一条断言最终都在守同一件事：不管内存界多小、活多大，写侧都不会停。
/// </summary>
public sealed class DiffWorkQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "asb-spill-tests", Guid.NewGuid().ToString("N"));

    public DiffWorkQueueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 测试清理，尽力而为 */ }
    }

    private string SpillPath(string name = "q") => Path.Combine(_dir, $"{name}.spill");

    private static WorkItem Single(string path, long length = 10) =>
        new(new PlannedFile(path, length, new string('a', 64)), null);

    private static WorkItem Pack(params string[] paths) =>
        new(null, [.. paths.Select(p => new PlannedFile(p, 10, new string('b', 64)))]);

    private static async Task<List<WorkItem>> DrainAsync(DiffWorkQueue queue, CancellationToken ct = default)
    {
        var got = new List<WorkItem>();
        while (await queue.DequeueAsync(ct) is { } item)
            got.Add(item);
        return got;
    }

    /// <summary>没超界就一件都不落盘——正常规模下这条队列应当完全不碰磁盘。</summary>
    [Fact]
    public async Task Stays_In_Memory_While_Under_The_Limit()
    {
        using var queue = new DiffWorkQueue(SpillPath(), memberLimit: 100, batchItems: 8);

        for (var i = 0; i < 20; i++)
            queue.Enqueue(Single($"f{i}"));
        queue.CompleteAdding();

        var got = await DrainAsync(queue);

        Assert.Equal(20, got.Count);
        Assert.Equal(0, queue.SpilledItems);
        Assert.False(File.Exists(SpillPath()), "一件都没超界，不该建出溢出文件");
    }

    /// <summary>
    /// 界卡在**成员数**上，不是件数。上界 10、每件 1 个成员、灌 30 件不消费：
    /// 前 10 件进内存，其余 20 件必须落盘。这个数是精确的，不是"大概"。
    /// </summary>
    [Fact]
    public void Bounds_Memory_By_Member_Count_Not_Item_Count()
    {
        using var queue = new DiffWorkQueue(SpillPath(), memberLimit: 10, batchItems: 4);

        for (var i = 0; i < 30; i++)
            queue.Enqueue(Single($"f{i}"));

        Assert.Equal(20, queue.SpilledItems);
    }

    /// <summary>
    /// 一箱小文件的成员数可以大于整个上界。内存空着时必须无条件收下它，
    /// 否则那种活永远进不了内存，写读两侧一起停在原地——而这条队列的全部意义就是不停。
    /// </summary>
    [Fact]
    public async Task Admits_An_Oversized_Item_When_The_Cache_Is_Empty()
    {
        using var queue = new DiffWorkQueue(SpillPath(), memberLimit: 3, batchItems: 4);

        // 内存空 → 收下，哪怕它自己就是上界的三倍多。
        queue.Enqueue(Pack("a", "b", "c", "d", "e", "f", "g", "h", "i", "j"));
        Assert.Equal(0, queue.SpilledItems);

        // 内存里已经有货且超界 → 落盘。
        queue.Enqueue(Single("later"));
        Assert.Equal(1, queue.SpilledItems);

        queue.CompleteAdding();
        var got = await DrainAsync(queue);

        Assert.Equal(2, got.Count);
        Assert.Equal(10, got[0].Members);
        Assert.Equal("later", got[1].Single!.Path);
    }

    /// <summary>
    /// 越界之后 FIFO 仍然整体成立。这一条盯的是那个容易写错的地方：盘上还有货时，
    /// 新来的活也必须走盘——直接塞内存的话它会插到已落盘那些活的前面去。
    /// </summary>
    [Fact]
    public async Task Preserves_Fifo_Across_The_Memory_Disk_Boundary()
    {
        using var queue = new DiffWorkQueue(SpillPath(), memberLimit: 4, batchItems: 3);

        var expected = Enumerable.Range(0, 50).Select(i => $"f{i:D3}").ToList();
        foreach (var path in expected)
            queue.Enqueue(Single(path));
        queue.CompleteAdding();

        var got = await DrainAsync(queue);

        Assert.Equal(expected, got.Select(w => w.Single!.Path).ToList());
        Assert.Equal(46, queue.SpilledItems);
    }

    /// <summary>
    /// 边写边读也不能乱序：消费者一边取，生产者一边灌，中间会反复跨越内存/磁盘的边界。
    /// </summary>
    [Fact]
    public async Task Preserves_Fifo_While_Producing_And_Consuming_Concurrently()
    {
        using var queue = new DiffWorkQueue(SpillPath(), memberLimit: 5, batchItems: 4);

        var expected = Enumerable.Range(0, 500).Select(i => $"f{i:D4}").ToList();
        var consumer = Task.Run(() => DrainAsync(queue));

        foreach (var path in expected)
        {
            queue.Enqueue(Single(path));
            if (path.EndsWith('7'))
                await Task.Yield(); // 让消费者插进来，制造跨边界的时机
        }
        queue.CompleteAdding();

        var got = await consumer;
        Assert.Equal(expected, got.Select(w => w.Single!.Path).ToList());
    }

    /// <summary>多消费者并发：一件不丢、一件不重。</summary>
    [Fact]
    public async Task Multiple_Consumers_Lose_Nothing_And_Duplicate_Nothing()
    {
        using var queue = new DiffWorkQueue(SpillPath(), memberLimit: 6, batchItems: 5);

        var expected = Enumerable.Range(0, 400).Select(i => $"f{i:D4}").ToHashSet();
        foreach (var path in expected)
            queue.Enqueue(Single(path));
        queue.CompleteAdding();

        var consumers = Enumerable.Range(0, 6).Select(_ => Task.Run(() => DrainAsync(queue))).ToArray();
        var all = (await Task.WhenAll(consumers)).SelectMany(x => x).Select(w => w.Single!.Path).ToList();

        Assert.Equal(expected.Count, all.Count);          // 不重
        Assert.Equal(expected, all.ToHashSet());          // 不丢
    }

    /// <summary>
    /// CompleteAdding 之后盘上剩的必须全部被回读出来，读侧才收到 null。
    /// 早一步关闸就是把已经判完的活默默扔掉——备份少传文件，而且没人会发现。
    /// </summary>
    [Fact]
    public async Task Drains_The_Spill_File_Before_Signalling_Completion()
    {
        using var queue = new DiffWorkQueue(SpillPath(), memberLimit: 2, batchItems: 3);

        for (var i = 0; i < 100; i++)
            queue.Enqueue(Single($"f{i:D3}"));
        queue.CompleteAdding();
        Assert.Equal(98, queue.SpilledItems);

        var got = await DrainAsync(queue);
        Assert.Equal(100, got.Count);
    }

    /// <summary>
    /// 路径按长度前缀的二进制存取，不是按行。Linux 路径里可以有换行、制表、任何非 NUL 字节——
    /// 按行切一定会在某个用户的目录上切错，而切错的表现是备份少传文件，不是报错。
    /// </summary>
    [Fact]
    public async Task Round_Trips_Paths_Containing_Newlines_And_Unicode()
    {
        using var queue = new DiffWorkQueue(SpillPath(), memberLimit: 1, batchItems: 2);

        var nasty = new[]
        {
            "plain.txt",
            "with\nnewline.txt",
            "with\ttab.txt",
            "中文/目录/文件.txt",
            "emoji \U0001F600/x.bin",
            "quote\"and\\backslash.txt",
        };
        foreach (var p in nasty)
            queue.Enqueue(Single(p, length: 7));
        queue.CompleteAdding();

        var got = await DrainAsync(queue);
        Assert.Equal(nasty, got.Select(w => w.Single!.Path).ToArray());
        Assert.All(got, w => Assert.Equal(7, w.Single!.Length));
    }

    /// <summary>成员的三个字段都要原样回来，FullHash 为 null 的也是。</summary>
    [Fact]
    public async Task Round_Trips_Pack_Members_Including_Null_Hashes()
    {
        using var queue = new DiffWorkQueue(SpillPath(), memberLimit: 1, batchItems: 2);

        queue.Enqueue(new WorkItem(null, [new PlannedFile("keep", 1, "hash-a")]));
        queue.Enqueue(new WorkItem(null, [new PlannedFile("nohash", 2, null), new PlannedFile("b", 3, "hash-b")]));
        queue.Enqueue(new WorkItem(new PlannedFile("single", 4, null), null));
        queue.CompleteAdding();

        var got = await DrainAsync(queue);

        Assert.Equal(3, got.Count);
        Assert.Equal("hash-a", got[0].Pack![0].FullHash);
        Assert.Null(got[1].Pack![0].FullHash);
        Assert.Equal(2, got[1].Pack![0].Length);
        Assert.Equal("hash-b", got[1].Pack![1].FullHash);
        Assert.NotNull(got[2].Single);
        Assert.Null(got[2].Single!.FullHash);
    }

    /// <summary>不给溢出路径＝纯内存无界。写侧同样不阻塞，只是不碰盘。</summary>
    [Fact]
    public async Task Memory_Only_Mode_Never_Spills()
    {
        using var queue = new DiffWorkQueue(null, memberLimit: 2, batchItems: 2);

        for (var i = 0; i < 100; i++)
            queue.Enqueue(Single($"f{i:D3}"));
        queue.CompleteAdding();

        var got = await DrainAsync(queue);
        Assert.Equal(100, got.Count);
        Assert.Equal(0, queue.SpilledItems);
    }

    /// <summary>正常收尾要把自己的溢出文件删掉，不给下一次留垃圾。</summary>
    [Fact]
    public async Task Dispose_Deletes_Its_Own_Spill_File()
    {
        var path = SpillPath("owned");
        var queue = new DiffWorkQueue(path, memberLimit: 1, batchItems: 2);
        for (var i = 0; i < 10; i++)
            queue.Enqueue(Single($"f{i}"));
        queue.CompleteAdding();
        await DrainAsync(queue);
        Assert.True(File.Exists(path), "落过盘就该有文件");

        queue.Dispose();

        Assert.False(File.Exists(path));
    }

    /// <summary>中途 Dispose（备份被取消/抛异常）不能挂住：泵要退得出去，句柄要放得掉。</summary>
    [Fact]
    public void Dispose_While_The_Spill_Is_Still_Full_Does_Not_Hang()
    {
        var path = SpillPath("aborted");
        var queue = new DiffWorkQueue(path, memberLimit: 1, batchItems: 2);
        for (var i = 0; i < 500; i++)
            queue.Enqueue(Single($"f{i:D3}"));
        // 一件都不消费就中止。
        queue.Dispose();

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// 进程被 kill 之后留下的溢出文件由启动时的 ClearStale 兜底。
    /// 只清 *.spill：这个目录万一被指到别处，也不该把别人的东西一起端了。
    /// </summary>
    [Fact]
    public void ClearStale_Removes_Leftovers_But_Only_Spill_Files()
    {
        var stale = Path.Combine(_dir, "leftover.spill");
        var innocent = Path.Combine(_dir, "not-ours.txt");
        File.WriteAllText(stale, "junk");
        File.WriteAllText(innocent, "keep me");

        DiffWorkQueue.ClearStale(_dir);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(innocent));
    }

    /// <summary>目录还不存在时 ClearStale 要负责建出来，而不是抛。</summary>
    [Fact]
    public void ClearStale_Creates_The_Directory_When_Missing()
    {
        var fresh = Path.Combine(_dir, "nested", "spill");
        DiffWorkQueue.ClearStale(fresh);
        Assert.True(Directory.Exists(fresh));
    }
}
