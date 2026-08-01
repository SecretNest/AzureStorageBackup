using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// diff→上传那条队列（三段：r 在内存等消费 / f 在临时文件 / w 在内存等成批写盘）。
/// 它存在的理由是「写侧永不阻塞」——diff 必须能一路跑到底，上传阶段的剩余时间才有分母
/// （见 <c>StageProgress.Eta</c> 开头那个 <c>_total &lt;= 0</c>）。
/// 所以这里每一条断言最终都在守同一件事：不管额度多小、活多大，写侧都不会停，而且一件不丢不乱。
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

    /// <summary>按件数设界、字节给足（测件数那条界时不想被字节那条抢先触发）。</summary>
    private static DiffQueueLimits ByItems(int maxItems, int writeBatch = 4, int refillBatch = 4) =>
        new(MaxCachedItems: maxItems, MaxCachedBytes: long.MaxValue,
            WriteBatchItems: writeBatch, WriteBatchBytes: long.MaxValue,
            RefillBatchItems: refillBatch);

    /// <summary>按字节设界、件数给足。</summary>
    private static DiffQueueLimits ByBytes(long maxBytes, long writeBatchBytes = long.MaxValue) =>
        new(MaxCachedItems: int.MaxValue, MaxCachedBytes: maxBytes,
            WriteBatchItems: int.MaxValue, WriteBatchBytes: writeBatchBytes,
            RefillBatchItems: 4);

    private static async Task<List<WorkItem>> DrainAsync(DiffWorkQueue queue, CancellationToken ct = default)
    {
        var got = new List<WorkItem>();
        while (await queue.DequeueAsync(ct) is { } item)
            got.Add(item);
        return got;
    }

    /// <summary>没超额就一件都不碰盘——正常规模下这条队列应当完全不产生文件。</summary>
    [Fact]
    public async Task Stays_In_Memory_While_Under_The_Limits()
    {
        using var queue = new DiffWorkQueue(SpillPath(), ByItems(maxItems: 100));

        for (var i = 0; i < 20; i++)
            queue.Enqueue(Single($"f{i}"));
        queue.CompleteAdding();

        var got = await DrainAsync(queue);

        Assert.Equal(20, got.Count);
        Assert.Equal(0, queue.SpilledItems);
        Assert.False(File.Exists(SpillPath()), "一件都没超额，不该建出溢出文件");
    }

    /// <summary>
    /// 件数是主旋钮：额度 10 件、灌 30 件不消费，前 10 件留在 r，其余 20 件必须走 w→f。
    /// 这个数是精确的，不是"大概"。
    /// </summary>
    [Fact]
    public void Bounds_The_Cache_By_Item_Count()
    {
        using var queue = new DiffWorkQueue(SpillPath(), ByItems(maxItems: 10, writeBatch: 1));

        for (var i = 0; i < 30; i++)
            queue.Enqueue(Single($"f{i}"));

        var (items, _, pendingWrite) = queue.Cached;
        Assert.Equal(10, items);
        Assert.Equal(0, pendingWrite);      // writeBatch=1，w 不留货
        Assert.Equal(20, queue.SpilledItems);
    }

    /// <summary>
    /// 光有件数管不住内存——这正是"小文件极多"那一格。件数给到天上、只卡字节：
    /// 一箱 40 个成员的活，额度只够两箱，第三箱起必须落盘。
    /// </summary>
    [Fact]
    public void Bounds_The_Cache_By_Bytes_When_Items_Are_Fat()
    {
        var fat = Pack([.. Enumerable.Range(0, 40).Select(i => $"dir/small{i:D3}.dat")]);
        using var queue = new DiffWorkQueue(SpillPath(), ByBytes(maxBytes: fat.EstimatedBytes * 2));

        for (var i = 0; i < 10; i++)
            queue.Enqueue(fat);

        var (items, bytes, pendingWrite) = queue.Cached;
        Assert.Equal(2, items);                                  // 件数没设界，是字节把它按住的
        Assert.True(bytes <= fat.EstimatedBytes * 2, $"r 段超额：{bytes}");
        // 这个用例里 w 段被故意开到无界（要隔离出 r 那条界），所以多出来的 8 件停在 w，
        // 一件都没写到盘上——SpilledItems 数的是**真正落过盘**的，不是"没进 r 的"。
        // w 自己那条界由下一条用例守。
        Assert.Equal(8, pendingWrite);
        Assert.Equal(0, queue.SpilledItems);
    }

    /// <summary>
    /// w 段也在内存里，也必须有字节界。只按件数限制 w 的话，小文件场景下几百个满员的箱子
    /// 就是好几个 GB——给 r 段设的额度会从这个后门被绕过去。
    /// </summary>
    [Fact]
    public void Bounds_The_Write_Buffer_By_Bytes_Too()
    {
        var fat = Pack([.. Enumerable.Range(0, 40).Select(i => $"dir/small{i:D3}.dat")]);
        // r 只装一件；w 的件数界给到天上，只留字节界＝两件的量。
        using var queue = new DiffWorkQueue(SpillPath(), new DiffQueueLimits(
            MaxCachedItems: 1, MaxCachedBytes: long.MaxValue,
            WriteBatchItems: int.MaxValue, WriteBatchBytes: fat.EstimatedBytes * 2,
            RefillBatchItems: 4));

        for (var i = 0; i < 20; i++)
            queue.Enqueue(fat);

        var (_, _, pendingWrite) = queue.Cached;
        Assert.True(pendingWrite <= 2, $"w 段超额：压着 {pendingWrite} 件");
        Assert.True(queue.SpilledItems >= 16, $"字节界没触发刷盘，只落了 {queue.SpilledItems} 件");
    }

    /// <summary>
    /// 一箱的成员数可以大于整个额度（1 字节的文件装满 100 MB 的箱子就是上亿个成员）。
    /// r 段空着时必须无条件收下它，否则那种活永远进不了内存，写读两侧一起停在原地。
    /// </summary>
    [Fact]
    public async Task Admits_An_Oversized_Item_When_The_Cache_Is_Empty()
    {
        using var queue = new DiffWorkQueue(SpillPath(), ByBytes(maxBytes: 200));

        var huge = Pack("a", "b", "c", "d", "e", "f", "g", "h", "i", "j");
        Assert.True(huge.EstimatedBytes > 200, "这一件本身就该超过整个额度，否则这条测试没测到东西");

        queue.Enqueue(huge);                 // r 空 → 无条件收下
        Assert.Equal(0, queue.SpilledItems);

        queue.Enqueue(Single("later"));      // r 已有货且超额 → 走 w
        queue.CompleteAdding();

        var got = await DrainAsync(queue);
        Assert.Equal(2, got.Count);
        Assert.Equal(10, got[0].Members);
        Assert.Equal("later", got[1].Single!.Path);
    }

    /// <summary>
    /// 超大件**落盘之后**同样要回得来。只在写侧留"至少备好一件"的例外是不够的：
    /// 回读侧没有同一条例外的话，它会被额度永远挡在文件里，泵和消费者一起停住。
    /// </summary>
    [Fact]
    public async Task Reads_Back_An_Oversized_Item_From_Disk()
    {
        using var queue = new DiffWorkQueue(SpillPath(), ByBytes(maxBytes: 200, writeBatchBytes: 1));

        queue.Enqueue(Single("first"));      // 占住 r
        var huge = Pack([.. Enumerable.Range(0, 60).Select(i => $"m{i:D2}")]);
        queue.Enqueue(huge);                 // 超额 → 落盘
        queue.CompleteAdding();

        Assert.True(queue.SpilledItems >= 1);

        var got = await DrainAsync(queue).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, got.Count);
        Assert.Equal("first", got[0].Single!.Path);
        Assert.Equal(60, got[1].Members);
    }

    /// <summary>
    /// FIFO 跨三段整体成立。这一条盯的是最容易写错的地方：f 或 w 非空时，新来的活也必须进 w——
    /// 直接塞 r 的话它会插到前面那些活之前。
    /// </summary>
    [Fact]
    public async Task Preserves_Fifo_Across_All_Three_Segments()
    {
        using var queue = new DiffWorkQueue(SpillPath(), ByItems(maxItems: 4, writeBatch: 3, refillBatch: 3));

        var expected = Enumerable.Range(0, 50).Select(i => $"f{i:D3}").ToList();
        foreach (var path in expected)
            queue.Enqueue(Single(path));
        queue.CompleteAdding();

        var got = await DrainAsync(queue);
        Assert.Equal(expected, got.Select(w => w.Single!.Path).ToList());
    }

    /// <summary>边写边读也不能乱序：消费者一边取，生产者一边灌，中间会反复跨越三段的边界。</summary>
    [Fact]
    public async Task Preserves_Fifo_While_Producing_And_Consuming_Concurrently()
    {
        using var queue = new DiffWorkQueue(SpillPath(), ByItems(maxItems: 5, writeBatch: 4, refillBatch: 4));

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
        using var queue = new DiffWorkQueue(SpillPath(), ByItems(maxItems: 6, writeBatch: 5, refillBatch: 5));

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
    /// f 空、w 里还压着货时，那些活要**直接进 r**，不必写下去再读回来。
    /// diff 收尾时那半批尤其明显——不走这条捷径就是纯粹白跑一趟盘。
    /// </summary>
    [Fact]
    public async Task Pending_Writes_Go_Straight_To_The_Cache_When_Nothing_Is_On_Disk()
    {
        // r 只装 1 件；w 的两条界都给到天上 → 永远不会主动刷盘。
        using var queue = new DiffWorkQueue(SpillPath(), new DiffQueueLimits(
            MaxCachedItems: 1, MaxCachedBytes: long.MaxValue,
            WriteBatchItems: int.MaxValue, WriteBatchBytes: long.MaxValue,
            RefillBatchItems: 8));

        for (var i = 0; i < 20; i++)
            queue.Enqueue(Single($"f{i:D2}"));
        queue.CompleteAdding();

        var got = await DrainAsync(queue);

        Assert.Equal(20, got.Count);
        Assert.Equal(0, queue.SpilledItems);
        Assert.False(File.Exists(SpillPath()), "f 一直是空的，一个字节都不该写到盘上");
    }

    /// <summary>
    /// CompleteAdding 之后 f 与 w 里剩的必须全部送到，读侧才收到 null。
    /// 早一步关闸就是把已经判完的活默默扔掉——备份少传文件，而且没人会发现。
    /// </summary>
    [Fact]
    public async Task Drains_Disk_And_Write_Buffer_Before_Signalling_Completion()
    {
        using var queue = new DiffWorkQueue(SpillPath(), ByItems(maxItems: 2, writeBatch: 3, refillBatch: 3));

        for (var i = 0; i < 100; i++)
            queue.Enqueue(Single($"f{i:D3}"));
        queue.CompleteAdding();

        var got = await DrainAsync(queue);
        Assert.Equal(100, got.Count);
        Assert.True(queue.SpilledItems > 0, "这个额度下必然落过盘，否则这条测试没测到回读");
    }

    /// <summary>
    /// 路径按长度前缀的二进制存取，不是按行。Linux 路径里可以有换行、制表、任何非 NUL 字节——
    /// 按行切一定会在某个用户的目录上切错，而切错的表现是备份少传文件，不是报错。
    /// </summary>
    [Fact]
    public async Task Round_Trips_Paths_Containing_Newlines_And_Unicode()
    {
        using var queue = new DiffWorkQueue(SpillPath(), ByItems(maxItems: 1, writeBatch: 1, refillBatch: 2));

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
        using var queue = new DiffWorkQueue(SpillPath(), ByItems(maxItems: 1, writeBatch: 1, refillBatch: 2));

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
        using var queue = new DiffWorkQueue(null, ByItems(maxItems: 2, writeBatch: 2));

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
        var queue = new DiffWorkQueue(path, ByItems(maxItems: 1, writeBatch: 1, refillBatch: 2));
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
    public void Dispose_While_The_Queue_Is_Still_Full_Does_Not_Hang()
    {
        var path = SpillPath("aborted");
        var queue = new DiffWorkQueue(path, ByItems(maxItems: 1, writeBatch: 2, refillBatch: 2));
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
