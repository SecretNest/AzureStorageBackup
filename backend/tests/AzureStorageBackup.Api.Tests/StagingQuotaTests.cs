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

    private StagingArea Area(long limit, TimeSpan? handBackGrace = null) => new(_compressTemp, _stagedTemp, () => limit, handBackGrace);

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
    /// allowed to work. A global gate reading the raw total froze a concurrent backup's compression for the
    /// hours a 113.9 GB repair family took to drain ("check把stage room全用掉了, 并没有像backup一样分享").
    /// Each seat's contribution to the global gate is capped at its share: the oversized run saturates its own
    /// allowance and can stage nothing more, while the others keep exactly the share the split promised them.</summary>
    [Fact]
    public async Task An_Oversized_Family_Does_Not_Starve_The_Other_Seats()
    {
        using var area = Area(limit: 1000);
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

    /// <summary>
    /// The scene that made the old "fair share" switch a misnomer: a run that had the disk to itself filled the pool,
    /// then a second run started and sat at a fifth of it for the rest of the night. Fairness is judged on the
    /// **result** — what each run holds — not on whose turn it is. So the moment the seat count doubles, the early
    /// run is over its new share and stops compressing, and the late run keeps compressing until it holds as much.
    /// </summary>
    [Fact]
    public async Task A_Late_Run_Catches_Up_While_The_Early_One_Waits()
    {
        using var area = Area(limit: 1000);
        using var a = area.AcquireLease();

        // Alone, A owns the whole pool and fills it.
        var a1 = await area.StageAsync(Produce("a1", 500), a).WaitAsync(Patience);
        var a2 = await area.StageAsync(Produce("a2", 500), a).WaitAsync(Patience);
        Assert.Equal(1000, area.StagedBytes);

        // B arrives: shares are now 500 each, and A holds twice that.
        using var b = area.AcquireLease();
        var blockedA = area.StageAsync(Produce("a3", 100), a);

        // B compresses freely up to its share, even though the pool as a whole is past the ceiling.
        var b1 = await area.StageAsync(Produce("b1", 300), b).WaitAsync(Patience);
        var b2 = await area.StageAsync(Produce("b2", 200), b).WaitAsync(Patience);
        Assert.Equal(500, b.Bytes);
        Assert.False(blockedA.IsCompleted, "A is over its share and must not compress while B catches up");

        // Now both are at their share: B is capped too.
        var blockedB = area.StageAsync(Produce("b3", 100), b);
        await Task.Delay(200);
        Assert.False(blockedB.IsCompleted, "B has reached its share and must wait like A");
        Assert.False(blockedA.IsCompleted);

        // A drains below its share and may go again; B still cannot.
        area.Release(a1);
        area.Release(a2);
        var resumedA = await blockedA.WaitAsync(Patience);
        Assert.Equal(100, resumedA.Bytes);
        Assert.False(blockedB.IsCompleted);

        area.Release(b1);
        await blockedB.WaitAsync(Patience);
    }

    /// <summary>
    /// When more than one seat is allowed to compress, the lock is not first-come: it goes to the seat holding the
    /// **least**. Order of arrival at the lock says nothing about who is behind; holdings do.
    /// </summary>
    [Fact]
    public async Task When_Both_Have_Room_The_Seat_Holding_Less_Compresses_First()
    {
        using var area = Area(limit: 1000);
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();

        var a1 = await area.StageAsync(Produce("a1", 400), a).WaitAsync(Patience);
        var b1 = await area.StageAsync(Produce("b1", 100), b).WaitAsync(Patience);

        // Park the compression lock so both seats have to queue for it, A first.
        var releaseLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = area.StageAsync(async (dir, ct) =>
        {
            await releaseLock.Task.WaitAsync(ct);
            return await Produce("hold", 10)(dir, ct);
        });
        await Task.Delay(100);

        var order = new List<string>();
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> Recording(string name) => (dir, ct) =>
        {
            lock (order) order.Add(name);
            return Produce(name, 10)(dir, ct);
        };
        var nextA = area.StageAsync(Recording("a2"), a);
        await Task.Delay(100);
        var nextB = area.StageAsync(Recording("b2"), b);
        await Task.Delay(100);

        releaseLock.SetResult();
        await Task.WhenAll(holder, nextA, nextB).WaitAsync(Patience);

        Assert.Equal(["b2", "a2"], order);
        area.Release(a1);
        area.Release(b1);
    }

    /// <summary>
    /// Bytes that belong to no seat (a lease-less staging, a reservation with no lease) are still on the same
    /// physical disk, so the global gate must keep counting them at face value even though no share caps them.
    /// </summary>
    [Fact]
    public async Task Bytes_Held_By_No_Seat_Still_Count_Against_The_Ceiling()
    {
        using var area = Area(limit: 1000);
        using var a = area.AcquireLease();

        var anon = await area.StageAsync(Produce("anon", 800)).WaitAsync(Patience);
        var a1 = await area.StageAsync(Produce("a1", 300), a).WaitAsync(Patience);
        Assert.Equal(1100, area.StagedBytes);

        // A is far under its share (the whole limit, it is the only seat), yet the disk is over the ceiling.
        var blocked = area.StageAsync(Produce("a2", 10), a);
        await Task.Delay(200);
        Assert.False(blocked.IsCompleted, "the ceiling is a physical disk; seat-less bytes fill it like any other");

        area.Release(anon);
        await blocked.WaitAsync(Patience);
        area.Release(a1);
    }

    /// <summary>
    /// The decision about who compresses next must not be made in the instant the lock is handed back. A run's
    /// compressor is a loop: it lets go, hands the archive to an uploader, takes the next item and comes straight
    /// back — a few milliseconds during which it is in no queue at all. A neighbour that parked while it compressed
    /// is the only candidate at that instant, and "least holdings first" degenerates into strict alternation: the
    /// small-file run gets one item per big-file archive, its holdings never leave zero, and the big-file run fills
    /// its share regardless. So the seat that just let go is counted as if it were still waiting, for a short
    /// grace, and the parked seat only wins that comparison on holdings.
    /// </summary>
    [Fact]
    public async Task The_Seat_That_Just_Let_Go_Is_Not_Overtaken_While_It_Comes_Back()
    {
        using var area = Area(limit: 1000, handBackGrace: TimeSpan.FromSeconds(1));
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();

        var a1 = await area.StageAsync(Produce("a1", 400), a).WaitAsync(Patience);

        var order = new List<string>();
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> Recording(string name) => (dir, ct) =>
        {
            lock (order) order.Add(name);
            return Produce(name, 10)(dir, ct);
        };

        // B is compressing; A arrives and parks behind it with far more on the disk.
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var b1 = area.StageAsync(async (dir, ct) =>
        {
            await releaseB.Task.WaitAsync(ct);
            return await Produce("b1", 10)(dir, ct);
        }, b);
        await Task.Delay(100);
        var nextA = area.StageAsync(Recording("a2"), a);
        await Task.Delay(100);

        // B lets go and comes back a moment later, the way a compressor loop does.
        releaseB.SetResult();
        await b1.WaitAsync(Patience);
        await Task.Delay(20);
        var nextB = area.StageAsync(Recording("b2"), b);

        await Task.WhenAll(nextA, nextB).WaitAsync(Patience);
        Assert.Equal(["b2", "a2"], order);

        area.Release(a1);
        foreach (var item in new[] { b1.Result, nextA.Result, nextB.Result })
            area.Release(item);
    }

    /// <summary>The grace is a grace, not a reservation: a seat that does not come back forfeits the lock to whoever is parked.</summary>
    [Fact]
    public async Task A_Seat_That_Does_Not_Come_Back_Forfeits_The_Lock_After_The_Grace()
    {
        using var area = Area(limit: 1000, handBackGrace: TimeSpan.FromMilliseconds(50));
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();

        var a1 = await area.StageAsync(Produce("a1", 400), a).WaitAsync(Patience);

        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var b1 = area.StageAsync(async (dir, ct) =>
        {
            await releaseB.Task.WaitAsync(ct);
            return await Produce("b1", 10)(dir, ct);
        }, b);
        await Task.Delay(100);
        var nextA = area.StageAsync(Produce("a2", 10), a);
        await Task.Delay(100);

        releaseB.SetResult();
        var a2 = await nextA.WaitAsync(Patience);

        area.Release(a1);
        area.Release(a2);
        area.Release(await b1);
    }

    /// <summary>
    /// The grace exists to let holdings decide, so it is only extended when holdings would favour the seat letting go.
    /// A seat handing back the lock with as much as (or more than) the parked one has no claim to come back first,
    /// and the parked seat is granted on the spot — no idle wait on a lock nobody is using.
    /// </summary>
    [Fact]
    public async Task A_Seat_Letting_Go_With_More_On_The_Disk_Does_Not_Hold_The_Lock_Up()
    {
        using var area = Area(limit: 1000, handBackGrace: TimeSpan.FromSeconds(5));
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();

        var a1 = await area.StageAsync(Produce("a1", 100), a).WaitAsync(Patience);
        var b1 = await area.StageAsync(Produce("b1", 300), b).WaitAsync(Patience);

        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var b2 = area.StageAsync(async (dir, ct) =>
        {
            await releaseB.Task.WaitAsync(ct);
            return await Produce("b2", 10)(dir, ct);
        }, b);
        await Task.Delay(100);
        var nextA = area.StageAsync(Produce("a2", 10), a);
        await Task.Delay(100);

        releaseB.SetResult();
        // Well inside the five-second grace: A was granted at the hand-back, not after a wait.
        var a2 = await nextA.WaitAsync(TimeSpan.FromSeconds(1));

        foreach (var item in new[] { a1, b1, a2, await b2 })
            area.Release(item);
    }

    /// <summary>
    /// While the lock is being held for the seat that let go, a third seat arriving with **more** on the disk than
    /// the parked one may not walk in through the free-lock fast path: the parked seat's claim on holdings stands,
    /// and the newcomer queues behind it like anyone else.
    /// </summary>
    [Fact]
    public async Task A_Newcomer_Holding_More_Cannot_Slip_In_During_The_Grace()
    {
        using var area = Area(limit: 3000, handBackGrace: TimeSpan.FromMilliseconds(300));
        using var a = area.AcquireLease();
        using var b = area.AcquireLease();
        using var c = area.AcquireLease();

        var a1 = await area.StageAsync(Produce("a1", 400), a).WaitAsync(Patience);
        var c1 = await area.StageAsync(Produce("c1", 600), c).WaitAsync(Patience);

        var order = new List<string>();
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> Recording(string name) => (dir, ct) =>
        {
            lock (order) order.Add(name);
            return Produce(name, 10)(dir, ct);
        };

        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var b1 = area.StageAsync(async (dir, ct) =>
        {
            await releaseB.Task.WaitAsync(ct);
            return await Produce("b1", 10)(dir, ct);
        }, b);
        await Task.Delay(100);
        var nextA = area.StageAsync(Recording("a2"), a);
        await Task.Delay(100);

        // B lets go (the lock is now held for B), and C — holding more than A — arrives during that grace.
        releaseB.SetResult();
        await b1.WaitAsync(Patience);
        await Task.Delay(50);
        var nextC = area.StageAsync(Recording("c2"), c);

        await Task.WhenAll(nextA, nextC).WaitAsync(Patience);
        Assert.Equal(["a2", "c2"], order);

        foreach (var item in new[] { a1, c1, b1.Result, nextA.Result, nextC.Result })
            area.Release(item);
    }

}
