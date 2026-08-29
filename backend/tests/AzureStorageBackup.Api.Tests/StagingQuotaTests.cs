using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The staging area's quota and concurrency when **several backups run at once**. The two only mean anything together:
/// <list type="number">
/// <item>the quota is split evenly across the runs currently in flight (configured-but-idle backups hold no seat);</item>
/// <item>waiting for space **must not** hold the global compression lock — otherwise handing anyone more quota is useless: the
/// run blocked by its own quota still sits stuck on the lock, nobody else can compress either, and it just deadlocks for a different reason.</item>
/// </list>
/// The global ceiling stays in force because the staging disk is a **physical** disk: the quota is about fairness, the global ceiling is about not filling the disk.
/// </summary>
public sealed class StagingQuotaTests : IDisposable
{
    private readonly string _root;
    private readonly string _compressTemp;
    private readonly string _stagedTemp;

    public StagingQuotaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-quota-" + Guid.NewGuid().ToString("N"));
        _compressTemp = Path.Combine(_root, "compress");
        _stagedTemp = Path.Combine(_root, "staged");
        Directory.CreateDirectory(_compressTemp);
        Directory.CreateDirectory(_stagedTemp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private StagingArea Area(long limit, bool fairShare = false) => new(_compressTemp, _stagedTemp, () => limit, () => fairShare);

    private static Func<string, CancellationToken, Task<IReadOnlyList<string>>> Produce(string name, int size)
        => async (dir, ct) =>
        {
            var path = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(path, new byte[size], ct);
            return [path];
        };

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// This is the acceptance point of the whole rework. Before it, backpressure waited **while holding the compression lock**, so
    /// the moment A was blocked by staging, B could not even start compressing — adding quota only made A start idling on the lock sooner, leaving B's position unchanged.
    /// </summary>
    [Fact]
    public async Task A_Run_Blocked_By_Its_Own_Quota_Releases_The_Compression_Lock()
    {
        using var area = Area(limit: 1000);   // two seats → 500 each
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();

        var itemA = await area.StageAsync(Produce("a1", 500), a);
        Assert.Equal(500, area.StagedBytes);

        // A has already filled its own half, so the next item must be blocked.
        var blockedA = area.StageAsync(Produce("a2", 100), a);
        await Task.Delay(200);
        Assert.False(blockedA.IsCompleted, "A has filled its own quota, this item should have been blocked");

        // And B must still be able to compress — A is waiting for space, but it must not be holding the global compression lock.
        var itemB = await area.StageAsync(Produce("b1", 400), b).WaitAsync(Patience);
        Assert.Equal(400, itemB.Bytes);

        // The moment A's usage is handed back, the blocked item continues immediately.
        area.Release(itemA);
        var resumed = await blockedA.WaitAsync(Patience);
        Assert.Equal(100, resumed.Bytes);
    }

    /// <summary>The quota is fairness, the global ceiling is disk safety — the latter must keep holding the line, or running in parallel fills the disk.</summary>
    [Fact]
    public async Task The_Global_Limit_Still_Caps_The_Sum_Across_Runs()
    {
        using var area = Area(limit: 1000);
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();

        // Both sides fill their own half; the total lands exactly on the ceiling.
        var itemA = await area.StageAsync(Produce("a1", 500), a);
        var itemB = await area.StageAsync(Produce("b1", 500), b);
        Assert.Equal(1000, area.StagedBytes);

        // At this point nobody should get in any more.
        var moreA = area.StageAsync(Produce("a2", 10), a);
        var moreB = area.StageAsync(Produce("b2", 10), b);
        await Task.Delay(200);
        Assert.False(moreA.IsCompleted, "the global ceiling is reached, A must not compress any more");
        Assert.False(moreB.IsCompleted, "the global ceiling is reached, B must not compress any more");

        area.Release(itemA);
        area.Release(itemB);
        await Task.WhenAll(moreA, moreB).WaitAsync(Patience);
    }

    /// <summary>
    /// With only one backup running, it should get the **whole** allowance. Configured-but-idle backups hold no seat —
    /// otherwise with ten backups configured and only one running, that one would only get a tenth of the staging disk.
    /// </summary>
    [Fact]
    public async Task A_Single_Active_Run_Gets_The_Whole_Limit()
    {
        using var area = Area(limit: 1000);
        using var only = area.AcquireLease();

        var first = await area.StageAsync(Produce("s1", 600), only).WaitAsync(Patience);
        Assert.Equal(600, first.Bytes);
        // 600 is already past "half of a two-seat split", yet with the disk to itself it must be let through.
        var second = await area.StageAsync(Produce("s2", 300), only).WaitAsync(Patience);
        Assert.Equal(300, second.Bytes);
    }

    /// <summary>Seats come and go with runs: when one backup finishes, whoever is left should get a bigger allowance immediately.</summary>
    [Fact]
    public async Task Finishing_A_Run_Hands_Its_Share_To_Whoever_Is_Left()
    {
        using var area = Area(limit: 1000);
        var a = area.AcquireLease();
        using var b = area.AcquireLease();

        // With two seats B's allowance is 500. Only **filling** it blocks the next item — the test is "let it through while current
        // usage is below the allowance" (the same semantics as An_Item_Larger_Than_The_Quota_Still_Gets_Through), so holding 400 blocks nothing.
        await area.StageAsync(Produce("b1", 500), b);
        var blocked = area.StageAsync(Produce("b2", 200), b);
        await Task.Delay(200);
        Assert.False(blocked.IsCompleted, "with two seats B only has half the allowance");

        // A finishes and hands its seat back → B has the whole allowance to itself, so that item should be let through.
        a.Dispose();
        var resumed = await blocked.WaitAsync(Patience);
        Assert.Equal(200, resumed.Bytes);
    }

    /// <summary>
    /// An item bigger than the whole quota must still be let through, or it could never be compressed at all. Keeping the existing
    /// semantics: as long as **current** usage is below the allowance we start compressing, letting this item's output overshoot temporarily.
    /// </summary>
    [Fact]
    public async Task An_Item_Larger_Than_The_Quota_Still_Gets_Through()
    {
        using var area = Area(limit: 1000);
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();

        // Seat allowance 500, output 900 — starting from zero, it must be let through.
        var item = await area.StageAsync(Produce("big", 900), a).WaitAsync(Patience);
        Assert.Equal(900, item.Bytes);
    }

    /// <summary>Callers without a seat (paths that do not care about fairness, plus the pre-existing tests) are bounded only by the global ceiling, behaving exactly as before.</summary>
    [Fact]
    public async Task Callers_Without_A_Lease_Are_Bounded_By_The_Global_Limit_Only()
    {
        using var area = Area(limit: 1000);
        using var a = area.AcquireLease();

        var item = await area.StageAsync(Produce("anon", 800)).WaitAsync(Patience);
        Assert.Equal(800, item.Bytes);
    }

    /// <summary>
    /// What backpressure looks like on screen. It is the state an upload-bound run spends most of its life in — the pool
    /// fills because the wire is slower than the compressor, exactly as designed — and it used to be reported as
    /// "waiting for the archive slot", the column whose documented reading is "another run holds the lock, go and stop
    /// it". Nobody held a lock here; there was simply no space, and only an upload could make some.
    /// </summary>
    [Fact]
    public async Task A_Run_Blocked_By_A_Full_Pool_Says_So_Instead_Of_Blaming_The_Lock()
    {
        using var area = Area(limit: 500);
        using var lease = area.AcquireLease();
        StageProgress? latest = null;
        var tracker = new StageTracker("Uploading", total: 2, p => Volatile.Write(ref latest, p));

        var filled = await area.StageAsync(Produce("a1", 500), lease, tracker: tracker).WaitAsync(Patience);

        // The pool is now at its ceiling, so the next item cannot even start producing.
        var blocked = area.StageAsync(Produce("a2", 100), lease, tracker: tracker);
        await Until(() => Volatile.Read(ref latest)?.WaitingOnRoom == 1, "the blocked item should report waiting for room");

        var s = Volatile.Read(ref latest)!;
        Assert.Equal(1, s.WaitingOnRoom);
        Assert.Equal(0, s.WaitingOnArchive);   // no lock is involved — the previous item let go of it long ago
        Assert.Equal(0, s.Preparing);
        Assert.False(blocked.IsCompleted);

        // An upload frees space, and it proceeds. The column empties with it.
        area.Release(filled);
        var resumed = await blocked.WaitAsync(Patience);
        Assert.Equal(100, resumed.Bytes);
        await Until(() => Volatile.Read(ref latest)?.WaitingOnRoom == 0, "the column must empty once the wait ends");
    }

    /// <summary>
    /// A wait that never happens must stay silent. Registering the phase unconditionally would force two publishes per
    /// item for nothing, which is the same trade the upload gate makes when it finds itself free — and at this repo's
    /// measured scale (hundreds of thousands of items) "for nothing" is the expensive part.
    /// </summary>
    [Fact]
    public async Task Staging_With_Room_To_Spare_Reports_No_Wait_At_All()
    {
        using var area = Area(limit: 1000);
        using var lease = area.AcquireLease();
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        await area.StageAsync(Produce("a1", 100), lease, tracker: tracker).WaitAsync(Patience);
        tracker.Complete();

        Assert.All(seen, p => Assert.Equal(0, p.WaitingOnRoom));
    }

    /// <summary>
    /// The seat counts volume **files** as well as bytes, because the UI states both ("N volumes, X GB waiting for
    /// uploading") and neither can be derived from the other. A whole archive lands at once — that is what makes the
    /// volume count worth showing, since the uploader receiving it can only start a handful of its volumes at a time.
    /// </summary>
    [Fact]
    public async Task A_Seat_Counts_Volume_Files_Alongside_Its_Bytes()
    {
        using var area = Area(limit: 10_000);
        using var lease = area.AcquireLease();

        Assert.Equal(0, lease.Files);

        var item = await area.StageAsync(
            async (dir, ct) =>
            {
                var files = new List<string>();
                foreach (var n in new[] { "a.001", "a.002", "a.003" })
                {
                    var path = Path.Combine(dir, n);
                    await File.WriteAllBytesAsync(path, new byte[100], ct);
                    files.Add(path);
                }
                return files;
            },
            lease).WaitAsync(Patience);

        Assert.Equal(3, lease.Files);      // the whole archive, in one go
        Assert.Equal(300, lease.Bytes);

        // Per-volume release: one uploaded volume is one file fewer, and the whole-family tail is idempotent, so
        // releasing again must not drive the count below what is really on the disk.
        area.ReleaseFile(item.Files[0]);
        Assert.Equal(2, lease.Files);
        Assert.Equal(200, lease.Bytes);

        area.Release(item);
        Assert.Equal(0, lease.Files);
        Assert.Equal(0, lease.Bytes);
    }

    /// <summary>
    /// A reservation books bytes with **no** files: it is temp space the caller manages itself (repair's compose
    /// directory, compaction's unpacked members), not volumes waiting to travel. Counting them as volumes would put a
    /// number in the UI that no upload will ever work off.
    /// </summary>
    [Fact]
    public async Task A_Reservation_Books_Bytes_But_No_Volumes()
    {
        using var area = Area(limit: 10_000);
        using var lease = area.AcquireLease();

        using (await area.ReserveAsync(500, lease))
        {
            Assert.Equal(500, lease.Bytes);
            Assert.Equal(0, lease.Files);
        }

        Assert.Equal(0, lease.Bytes);
        Assert.Equal(0, lease.Files);
    }

    private static async Task Until(Func<bool> reached, string because)
    {
        for (var i = 0; i < 500 && !reached(); i++)
            await Task.Delay(20);
        Assert.True(reached(), because);
    }
    /// <summary>An archive can legitimately exceed the whole ceiling (a single family cannot be split), and
    /// once it has, its excess is sunk disk cost — the bytes are on the disk whether or not anyone else is
    /// allowed to work. The old global gate read the raw total, so a 113.9 GB repair family froze a
    /// concurrent backup's compression for the hours the family took to drain ("check把stage room全用掉了,
    /// 并没有像backup一样分享"). Each seat's contribution to the GLOBAL gate is now capped at its fair share:
    /// the oversized run still saturates its own allowance (it can stage nothing more), while the others
    /// keep exactly the share the split promised them.</summary>
    [Fact]
    public async Task An_Oversized_Family_Does_Not_Starve_The_Other_Seats()
    {
        // Fair-share mode (Settings switch): 20% split as guarantees (100 each), 80% first-come shared.
        using var area = Area(limit: 1000, fairShare: true);
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();

        // A's single archive blows through the whole ceiling — legal, it cannot be split.
        var itemA = await area.StageAsync(Produce("huge", 5000), a);
        Assert.Equal(5000, area.StagedBytes);

        // B must still get its own share: its small item stages promptly instead of waiting for A to drain.
        var itemB = area.StageAsync(Produce("small", 100), b);
        var winner = await Task.WhenAny(itemB, Task.Delay(Patience));
        Assert.True(winner == itemB, "the other seat was starved by A's oversized family");
        area.Release(await itemB);
        area.Release(itemA);
    }

}
