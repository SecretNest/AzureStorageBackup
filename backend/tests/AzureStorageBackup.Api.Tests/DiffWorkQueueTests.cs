using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The diff→upload queue (three segments: r in memory waiting to be consumed / f in the temp file / w in memory
/// waiting to be written out in a batch).
/// It exists for one reason — the write side never blocks. The diff has to be able to run straight through, or the
/// upload stage's remaining time has no denominator (see the <c>_total &lt;= 0</c> at the top of
/// <c>StageProgress.Eta</c>).
/// So every assertion here ultimately guards the same thing: no matter how small the limits or how big an item, the
/// write side never stalls, and not one item is lost or reordered.
/// </summary>
public sealed class DiffWorkQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "asb-spill-tests", Guid.NewGuid().ToString("N"));

    public DiffWorkQueueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* test cleanup, best effort */ }
    }

    private string SpillPath(string name = "q") => Path.Combine(_dir, $"{name}.spill");

    private static WorkItem Single(string path, long length = 10) =>
        new(new PlannedFile(path, length, new string('a', 64)), null);

    private static WorkItem Pack(params string[] paths) =>
        new(null, [.. paths.Select(p => new PlannedFile(p, 10, new string('b', 64)))]);

    /// <summary>Bound by item count, with bytes set generously (when testing the item-count limit we do not want the byte one firing first).</summary>
    private static DiffQueueLimits ByItems(int maxItems, int writeBatch = 4, int refillBatch = 4) =>
        new(MaxCachedItems: maxItems, MaxCachedBytes: long.MaxValue,
            WriteBatchItems: writeBatch, WriteBatchBytes: long.MaxValue,
            RefillBatchItems: refillBatch);

    /// <summary>Bound by bytes, with item count set generously.</summary>
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

    /// <summary>Nothing goes over the limits, so nothing touches the disk — at normal scale this queue should produce no file at all.</summary>
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
        Assert.False(File.Exists(SpillPath()), "not one item went over the limits, no spill file should have been created");
    }

    /// <summary>
    /// Item count is the main knob: a limit of 10 items, push in 30 without consuming, and the first 10 stay in r
    /// while the other 20 must go w→f. That number is exact, not "roughly".
    /// </summary>
    [Fact]
    public void Bounds_The_Cache_By_Item_Count()
    {
        using var queue = new DiffWorkQueue(SpillPath(), ByItems(maxItems: 10, writeBatch: 1));

        for (var i = 0; i < 30; i++)
            queue.Enqueue(Single($"f{i}"));

        var (items, _, pendingWrite) = queue.Cached;
        Assert.Equal(10, items);
        Assert.Equal(0, pendingWrite);      // writeBatch=1, so w holds nothing back
        Assert.Equal(20, queue.SpilledItems);
    }

    /// <summary>
    /// Item count alone cannot hold memory down — this is exactly the "enormous numbers of small files" case. Item
    /// count set sky-high, bytes the only bound: with items of 40 members per pack, the limit fits two packs, so
    /// from the third pack on it must spill to disk.
    /// </summary>
    [Fact]
    public void Bounds_The_Cache_By_Bytes_When_Items_Are_Fat()
    {
        var fat = Pack([.. Enumerable.Range(0, 40).Select(i => $"dir/small{i:D3}.dat")]);
        using var queue = new DiffWorkQueue(SpillPath(), ByBytes(maxBytes: fat.EstimatedBytes * 2));

        for (var i = 0; i < 10; i++)
            queue.Enqueue(fat);

        var (items, bytes, pendingWrite) = queue.Cached;
        Assert.Equal(2, items);                                  // item count is unbounded here; bytes is what held it down
        Assert.True(bytes <= fat.EstimatedBytes * 2, $"r segment over the limit: {bytes}");
        // This case deliberately leaves the w segment unbounded (to isolate r's limit), so the extra 8 items sit in
        // w and not one was written to disk — SpilledItems counts what **really hit the disk**, not "what did not
        // make it into r". w's own limit is guarded by the next case.
        Assert.Equal(8, pendingWrite);
        Assert.Equal(0, queue.SpilledItems);
    }

    /// <summary>
    /// The w segment is in memory too, so it must have a byte limit as well. Bound w by item count alone and, in
    /// the small-file case, a few hundred full packs are several GB — the limit set for the r segment gets bypassed
    /// through this back door.
    /// </summary>
    [Fact]
    public void Bounds_The_Write_Buffer_By_Bytes_Too()
    {
        var fat = Pack([.. Enumerable.Range(0, 40).Select(i => $"dir/small{i:D3}.dat")]);
        // r holds one item only; w's item-count limit is sky-high, leaving only the byte limit = two items' worth.
        using var queue = new DiffWorkQueue(SpillPath(), new DiffQueueLimits(
            MaxCachedItems: 1, MaxCachedBytes: long.MaxValue,
            WriteBatchItems: int.MaxValue, WriteBatchBytes: fat.EstimatedBytes * 2,
            RefillBatchItems: 4));

        for (var i = 0; i < 20; i++)
            queue.Enqueue(fat);

        var (_, _, pendingWrite) = queue.Cached;
        Assert.True(pendingWrite <= 2, $"w segment over the limit: holding {pendingWrite} items");
        Assert.True(queue.SpilledItems >= 16, $"the byte limit did not trigger a flush, only {queue.SpilledItems} items spilled");
    }

    /// <summary>
    /// One pack can hold more members than the entire limit (fill a 100 MB pack with 1-byte files and that is
    /// hundreds of millions of members). When the r segment is empty it must be admitted unconditionally, otherwise
    /// such an item can never get into memory and the write and read sides stall in place together.
    /// </summary>
    [Fact]
    public async Task Admits_An_Oversized_Item_When_The_Cache_Is_Empty()
    {
        using var queue = new DiffWorkQueue(SpillPath(), ByBytes(maxBytes: 200));

        var huge = Pack("a", "b", "c", "d", "e", "f", "g", "h", "i", "j");
        Assert.True(huge.EstimatedBytes > 200, "this single item is supposed to exceed the whole limit by itself, otherwise this test tests nothing");

        queue.Enqueue(huge);                 // r is empty → admitted unconditionally
        Assert.Equal(0, queue.SpilledItems);

        queue.Enqueue(Single("later"));      // r already holds something and is over the limit → goes through w
        queue.CompleteAdding();

        var got = await DrainAsync(queue);
        Assert.Equal(2, got.Count);
        Assert.Equal(10, got[0].Members);
        Assert.Equal("later", got[1].Single!.Path);
    }

    /// <summary>
    /// An oversized item must also come back **after it has spilled to disk**. Keeping the "at least one item
    /// ready" exception on the write side alone is not enough: without the same exception on the read-back side the
    /// limit blocks it inside the file forever, and the pump and the consumers stall together.
    /// </summary>
    [Fact]
    public async Task Reads_Back_An_Oversized_Item_From_Disk()
    {
        using var queue = new DiffWorkQueue(SpillPath(), ByBytes(maxBytes: 200, writeBatchBytes: 1));

        queue.Enqueue(Single("first"));      // takes up r
        var huge = Pack([.. Enumerable.Range(0, 60).Select(i => $"m{i:D2}")]);
        queue.Enqueue(huge);                 // over the limit → spills to disk
        queue.CompleteAdding();

        Assert.True(queue.SpilledItems >= 1);

        var got = await DrainAsync(queue).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, got.Count);
        Assert.Equal("first", got[0].Single!.Path);
        Assert.Equal(60, got[1].Members);
    }

    /// <summary>
    /// FIFO holds across all three segments. This one watches the spot that is easiest to get wrong: when f or w is
    /// non-empty, a newly arriving item must go into w as well — push it straight into r and it jumps ahead of
    /// everything queued before it.
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

    /// <summary>Order must hold while writing and reading at once: consumers take while the producer pushes, crossing the three segments' boundaries over and over.</summary>
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
                await Task.Yield(); // let a consumer cut in, to manufacture boundary-crossing moments
        }
        queue.CompleteAdding();

        var got = await consumer;
        Assert.Equal(expected, got.Select(w => w.Single!.Path).ToList());
    }

    /// <summary>Concurrent consumers: not one item lost, not one duplicated.</summary>
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

        Assert.Equal(expected.Count, all.Count);          // no duplicates
        Assert.Equal(expected, all.ToHashSet());          // nothing lost
    }

    /// <summary>
    /// When f is empty and w still holds items, those items must go **straight into r** rather than be written out
    /// and read back. The half-batch at the end of the diff is the most obvious case — skipping this shortcut is a
    /// pure round trip to disk for nothing.
    /// </summary>
    [Fact]
    public async Task Pending_Writes_Go_Straight_To_The_Cache_When_Nothing_Is_On_Disk()
    {
        // r holds 1 item only; both of w's limits are sky-high → it never flushes to disk on its own.
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
        Assert.False(File.Exists(SpillPath()), "f was empty the whole time, not one byte should have been written to disk");
    }

    /// <summary>
    /// After CompleteAdding, everything left in f and w must be delivered before the read side gets null.
    /// Closing the gate one step early silently throws away work that was already judged — the backup uploads fewer
    /// files, and nobody will notice.
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
        Assert.True(queue.SpilledItems > 0, "at this limit it must have spilled, otherwise this test never exercised the read-back");
    }

    /// <summary>
    /// Paths are stored and read as length-prefixed binary, not line by line. A Linux path may contain newlines,
    /// tabs, any non-NUL byte — splitting on lines is bound to split wrong on some user's directory, and the symptom
    /// of splitting wrong is a backup that uploads fewer files, not an error.
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

    /// <summary>All three member fields must come back unchanged, including when FullHash is null.</summary>
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

    /// <summary>No spill path = pure unbounded memory. The write side still does not block, it just never touches the disk.</summary>
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

    /// <summary>A normal shutdown deletes its own spill file, leaving no garbage for next time.</summary>
    [Fact]
    public async Task Dispose_Deletes_Its_Own_Spill_File()
    {
        var path = SpillPath("owned");
        var queue = new DiffWorkQueue(path, ByItems(maxItems: 1, writeBatch: 1, refillBatch: 2));
        for (var i = 0; i < 10; i++)
            queue.Enqueue(Single($"f{i}"));
        queue.CompleteAdding();
        await DrainAsync(queue);
        Assert.True(File.Exists(path), "it spilled, so the file should exist");

        queue.Dispose();

        Assert.False(File.Exists(path));
    }

    /// <summary>Dispose mid-run (backup cancelled / threw) must not hang: the pump has to be able to exit and the handles have to be released.</summary>
    [Fact]
    public void Dispose_While_The_Queue_Is_Still_Full_Does_Not_Hang()
    {
        var path = SpillPath("aborted");
        var queue = new DiffWorkQueue(path, ByItems(maxItems: 1, writeBatch: 2, refillBatch: 2));
        for (var i = 0; i < 500; i++)
            queue.Enqueue(Single($"f{i:D3}"));
        // Abort without consuming a single item.
        queue.Dispose();

        Assert.False(File.Exists(path));
    }

    /// <summary>
    /// Spill files left behind after the process is killed are covered by ClearStale at startup.
    /// It clears only *.spill: should this directory ever get pointed somewhere else, it must not carry off somebody
    /// else's files along with ours.
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

    /// <summary>When the directory does not exist yet, ClearStale is responsible for creating it rather than throwing.</summary>
    [Fact]
    public void ClearStale_Creates_The_Directory_When_Missing()
    {
        var fresh = Path.Combine(_dir, "nested", "spill");
        DiffWorkQueue.ClearStale(fresh);
        Assert.True(Directory.Exists(fresh));
    }
}
