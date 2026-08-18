using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class StagingAreaTests : IDisposable
{
    private readonly string _root;
    private readonly string _compressTemp;
    private readonly string _stagedTemp;

    public StagingAreaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-stage-" + Guid.NewGuid().ToString("N"));
        _compressTemp = Path.Combine(_root, "compress");
        _stagedTemp = Path.Combine(_root, "staged");
        Directory.CreateDirectory(_compressTemp);
        Directory.CreateDirectory(_stagedTemp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private StagingArea Area(long limit) => new(_compressTemp, _stagedTemp, () => limit);

    private StagingArea AreaP(Func<long> limit) => new(_compressTemp, _stagedTemp, limit);

    /// <summary>Fake compression: write one volume file of size bytes into compress-temp.</summary>
    private static Func<string, CancellationToken, Task<IReadOnlyList<string>>> Produce(string name, int size)
        => async (dir, ct) =>
        {
            var path = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(path, new byte[size], ct);
            return [path];
        };

    [Fact]
    public async Task Staged_Item_Is_Moved_From_Compress_To_Staged_Temp()
    {
        using var area = Area(limit: 1_000_000);

        var item = await area.StageAsync(Produce("v1", 500));

        Assert.Empty(Directory.GetFiles(_compressTemp));               // moved out of compress-temp
        var staged = Assert.Single(item.Files);
        // Staged files now live in a GUID subdirectory of staged-temp (isolation across backups, so identical names cannot overwrite).
        Assert.Equal(_stagedTemp, Path.GetDirectoryName(Path.GetDirectoryName(staged)));
        Assert.True(File.Exists(staged));
        Assert.Equal(500, item.Bytes);
        Assert.Equal(500, area.StagedBytes);
    }

    [Fact]
    public async Task Concurrent_Same_Named_Outputs_Do_Not_Collide()
    {
        using var area = Area(limit: 1_000_000);

        // Two stagings produce files with "the same name" (simulating different containers both starting at p0001.7z).
        // Compression is serial, but the two must land in different subdirectories, each with its content intact.
        var item1 = await area.StageAsync(Produce("p0001.7z", 100));
        var item2 = await area.StageAsync(Produce("p0001.7z", 200));

        var f1 = Assert.Single(item1.Files);
        var f2 = Assert.Single(item2.Files);
        Assert.NotEqual(f1, f2);                       // different paths
        Assert.True(File.Exists(f1) && File.Exists(f2));
        Assert.Equal(100, new FileInfo(f1).Length);    // each intact, neither overwritten
        Assert.Equal(200, new FileInfo(f2).Length);
        Assert.Equal(300, area.StagedBytes);

        area.Release(item1);
        Assert.False(File.Exists(f1));
        Assert.False(Directory.Exists(Path.GetDirectoryName(f1)));  // the emptied subdirectory is removed too
        Assert.True(File.Exists(f2));
    }

    [Fact]
    public async Task Compression_Is_Globally_Non_Concurrent()
    {
        using var area = Area(limit: 1_000_000);
        var concurrent = 0;
        var maxConcurrent = 0;

        Func<string, CancellationToken, Task<IReadOnlyList<string>>> Job(string name) => async (dir, ct) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref concurrent);
            var path = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(path, new byte[10], ct);
            return (IReadOnlyList<string>)[path];
        };

        await Task.WhenAll(
            area.StageAsync(Job("a")),
            area.StageAsync(Job("b")),
            area.StageAsync(Job("c")));

        Assert.Equal(1, maxConcurrent);
    }

    /// <summary>
    /// "Preparing" in the progress report counts only the item that actually holds the archive lock; whatever queues behind it
    /// is neither "preparing" nor "queued", but a column of its own (<see cref="StageProgress.WaitingOnArchive"/>).
    /// <para>
    /// The worker pool is far larger than the archive lock (<c>UploadConcurrency + 1</c>); the extra threads exist so that items
    /// done producing can each grab an upload stream. Progress used to derive "preparing" from "items in hand - items uploading",
    /// which swept all those threads idling on the lock into it: with the default config the UI showed 5 preparing, reading like
    /// five items advancing in parallel when in truth one was producing and four idling — precisely when producing is the bottleneck, the UI looked busiest.
    /// </para>
    /// <para>
    /// They were then folded into <c>queued</c>, which is just as wrong: this lock is global, and when two backups run
    /// concurrently it can sit entirely in **the other backup's** hands, at which point this backup's <c>preparing</c> is 0 and
    /// the screen shows nothing but a pile of "queued", unable to say "somebody else is blocking us". Hence a column of their own — see the two cases in <c>StageProgressTests</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Progress_Counts_Only_The_Lock_Holder_As_Preparing()
    {
        using var area = Area(limit: 1_000_000);
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 3, seen.Add);
        for (var i = 0; i < 3; i++)
        {
            tracker.Enqueue();
            tracker.BeginWork();   // all three picked up by worker threads, nothing left in the queue
        }

        var hold = new TaskCompletionSource();
        var holding = new TaskCompletionSource();
        Task<IReadOnlyList<string>> Blocking(string dir, CancellationToken ct) => Task.Run(async () =>
        {
            holding.TrySetResult();
            await hold.Task;
            var path = Path.Combine(dir, "v1");
            await File.WriteAllBytesAsync(path, new byte[10], ct);
            return (IReadOnlyList<string>)[path];
        }, ct);

        var first = area.StageAsync(Blocking, tracker: tracker);
        await holding.Task;                                        // the first item really has the lock and is producing
        var second = area.StageAsync(Produce("v2", 10), tracker: tracker);
        var third = area.StageAsync(Produce("v3", 10), tracker: tracker);

        tracker.Complete();   // force past the throttle to grab a snapshot of right now
        var s = seen[^1];
        Assert.Equal(1, s.Preparing);        // producing is serial, no matter how many queue up behind
        Assert.Equal(2, s.WaitingOnArchive); // the two idling on the lock
        Assert.Equal(0, s.Queued);           // the queue really is empty — they are no longer misreported as "queued"

        hold.SetResult();
        await Task.WhenAll(first, second, third);
        tracker.Complete();
        Assert.Equal(0, seen[^1].Preparing); // all handed back, nothing left dangling
        Assert.Equal(0, seen[^1].WaitingOnArchive);
        Assert.Equal(0, seen[^1].Queued);
    }

    /// <summary>A throw partway through producing must still hand back both counters — miss the finally and the UI hangs forever at
    /// "1 preparing" while nothing at all is running.</summary>
    [Fact]
    public async Task A_Failed_Compression_Still_Gives_Back_Its_Progress_Slots()
    {
        using var area = Area(limit: 1_000_000);
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);
        tracker.Enqueue();
        tracker.BeginWork();

        await Assert.ThrowsAnyAsync<Exception>(() => area.StageAsync(
            (_, _) => Task.FromException<IReadOnlyList<string>>(new IOException("disk full")),
            tracker: tracker));

        tracker.Complete();
        Assert.Equal(0, seen[^1].Preparing);
        Assert.Equal(0, seen[^1].Queued);
    }

    [Fact]
    public async Task Over_Limit_Blocks_Next_Compression_Until_Release()
    {
        using var area = Area(limit: 100);

        // Starting from 0 is allowed to overshoot: 150 > 100, and it still runs.
        var first = await area.StageAsync(Produce("v1", 150));
        Assert.Equal(150, area.StagedBytes);

        var secondStarted = false;
        var second = area.StageAsync(async (dir, ct) =>
        {
            secondStarted = true;
            return await Produce("v2", 10)(dir, ct);
        });

        await Task.Delay(150);
        Assert.False(secondStarted);     // backpressure: over the ceiling, the next compression does not start
        Assert.False(second.IsCompleted);

        area.Release(first);             // upload finished, space freed

        var item = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondStarted);
        Assert.Equal(10, item.Bytes);
    }

    [Fact]
    public async Task Backpressure_Reads_Limit_Live_From_Provider()
    {
        long limit = 100;                      // a tiny ceiling to start with
        using var area = AreaP(() => limit);

        // The first result may overshoot temporarily (it started below the ceiling).
        var first = await area.StageAsync(Produce("a", 500));
        Assert.Equal(500, area.StagedBytes);   // already past 100

        // The second compression should be blocked by backpressure (StagedBytes 500 >= limit 100).
        var blocked = area.StageAsync(Produce("b", 10));
        Assert.False(blocked.IsCompleted);

        // Raising the ceiling → waking still needs a Release to fire the signal; so instead Release the first one to free space.
        area.Release(first);                   // StagedBytes -> 0, wakes it
        var second = await blocked;
        Assert.Equal(10, area.StagedBytes);
    }

    /// <summary>
    /// The one thing <see cref="StagingArea.StageWithoutBackpressureAsync"/> exists to do: a caller the quota depends
    /// on to come back must not queue for that quota.
    /// <para>
    /// Everything in this pool is released by an upload, so an uploader that parks in the backpressure wait is
    /// waiting for itself. One doing it is merely slow; all of them doing it at once is permanent, and it is a scene
    /// a single network blip assembles — every in-flight upload trips into the suspend gate together, each drops the
    /// archive it was sending, the gate's timer then releases them all in one go, and they all come back to
    /// recompress what they have to resend against a pool nobody is left to drain. No error surfaces, no progress
    /// moves, and not even Suspend gets out: <c>downstreamGone</c> triggers on the uploaders being gone and these are
    /// alive, and the gate cannot downgrade because nobody is at it.
    /// </para>
    /// <para>
    /// The state this test puts the pool into is exactly that one — over the ceiling, with nothing in the test that
    /// will ever hand a byte back — so a bypass that quietly went back to waiting hangs here forever rather than
    /// running slowly. That is deliberate: the failure mode this guards against **is** a hang, and the gap between
    /// "returns in a millisecond" and "never returns" is what the timeout below measures, on any machine.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Restaging_Without_Backpressure_Does_Not_Wait_For_Room()
    {
        using var area = Area(limit: 100);

        // Fill the pool past its ceiling and keep it there. Nothing below releases these bytes, which is the whole
        // point: a re-staging uploader looking at this pool is looking at bytes only an uploader could return.
        var held = await area.StageAsync(Produce("held", 150));
        Assert.Equal(150, area.StagedBytes);

        // The ordinary entry point parks, and there is no release coming for it to be woken by.
        var blocked = area.StageAsync(Produce("blocked", 10));

        // The uploader's entry point goes straight through, with not one byte having been handed back in between.
        // Spelled out rather than left to WaitAsync so the failure says what happened: a bare TimeoutException from
        // this line reads like a slow machine, and it is the opposite — a wait that was never going to end.
        var restaged = area.StageWithoutBackpressureAsync(Produce("restaged", 10));
        if (await Task.WhenAny(restaged, Task.Delay(TimeSpan.FromSeconds(30))) != restaged)
            Assert.Fail(
                $"the uploader's entry point queued for room: {area.StagedBytes} bytes are staged against a "
                + "100-byte ceiling and nothing here will ever release them, which is the deadlock this method "
                + "exists to avoid — in a run it is every uploader waiting for a pool only an uploader can drain.");

        var item = await restaged;
        Assert.Equal(10, item.Bytes);
        Assert.Equal(160, area.StagedBytes);   // the overshoot the bypass permits, booked like any other staging
        // Cannot flake: `blocked` is waiting on a release signal nothing above fires, so it is incomplete at every
        // instant between its start and the Release below, on a machine of any speed.
        Assert.False(blocked.IsCompleted);

        // And the ordinary caller is still an ordinary caller: it moves when, and only when, room comes back.
        area.Release(held);
        var second = await blocked.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(10, second.Bytes);
    }

    /// <summary>
    /// The bypass skips the backpressure wait and nothing else. Compression stays globally serial, because the
    /// compression lock is not what deadlocks: it is held only while 7z runs and is always given back by its holder,
    /// whereas the quota is given back by a *different* thread than the one waiting for it. Letting a retry compress
    /// concurrently with the compression stage would put two 7z processes on the same temp disk and hand this run
    /// CPU the operator capped on purpose — a different bug, bought with the fix for this one.
    /// <para>
    /// Same shape as <see cref="Compression_Is_Globally_Non_Concurrent"/>, one bypass caller substituted in, so the
    /// two read as the one rule they are.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Restaging_Without_Backpressure_Still_Compresses_One_At_A_Time()
    {
        using var area = Area(limit: 1_000_000);
        var concurrent = 0;
        var maxConcurrent = 0;

        Func<string, CancellationToken, Task<IReadOnlyList<string>>> Job(string name) => async (dir, ct) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref concurrent);
            var path = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(path, new byte[10], ct);
            return (IReadOnlyList<string>)[path];
        };

        await Task.WhenAll(
            area.StageAsync(Job("a")),
            area.StageWithoutBackpressureAsync(Job("b")),
            area.StageWithoutBackpressureAsync(Job("c")));

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task Release_Deletes_Staged_Files()
    {
        using var area = Area(limit: 1_000_000);
        var item = await area.StageAsync(Produce("v1", 42));
        var path = item.Files[0];

        area.Release(item);

        Assert.False(File.Exists(path));
    }

    /// <summary>Fake compression: produce several volumes at once (v.001..v.00N), size bytes each.</summary>
    private static Func<string, CancellationToken, Task<IReadOnlyList<string>>> ProduceVolumes(
        string name, int count, int size)
        => async (dir, ct) =>
        {
            var paths = new List<string>();
            for (var i = 1; i <= count; i++)
            {
                var path = Path.Combine(dir, $"{name}.{i:000}");
                await File.WriteAllBytesAsync(path, new byte[size], ct);
                paths.Add(path);
            }
            return paths;
        };

    /// <summary>
    /// Every volume that finishes uploading has to be deleted, with the watermark stepping down volume by volume.
    /// <para>
    /// Deleting only once the whole family is uploaded makes the temp disk's peak equal **the entire archive** — a 100 GB file
    /// needs 100 GB of temp space (this has already crashed a real backup), and the watermark sits pinned at the ceiling the whole time, with later compressions jammed behind backpressure.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Volumes_Are_Released_One_By_One_As_They_Go_Up()
    {
        using var area = Area(limit: 1_000_000);
        var item = await area.StageAsync(ProduceVolumes("v", count: 4, size: 25));
        Assert.Equal(100, area.StagedBytes);

        for (var i = 0; i < item.Files.Count; i++)
        {
            area.ReleaseFile(item.Files[i]);
            Assert.False(File.Exists(item.Files[i]));
            Assert.Equal(100 - 25 * (i + 1), area.StagedBytes);
        }

        area.Release(item);                                  // tail backstop: only the emptied directory is left to delete
        Assert.Equal(0, area.StagedBytes);
        Assert.Empty(Directory.GetDirectories(_stagedTemp));
    }

    /// <summary>Per-volume release must be idempotent: after the upload path has released each volume, the whole-family Release at the tail walks them again.
    /// Double-debiting drives the watermark negative, and from then on backpressure never blocks compression — the temp disk has no ceiling at all any more.</summary>
    [Fact]
    public async Task Releasing_The_Same_Volume_Twice_Does_Not_Go_Negative()
    {
        using var area = Area(limit: 1_000_000);
        var a = await area.StageAsync(ProduceVolumes("a", count: 2, size: 50));
        var b = await area.StageAsync(Produce("b", 30));

        area.ReleaseFile(a.Files[0]);
        area.ReleaseFile(a.Files[0]);   // duplicate
        area.Release(a);                // whole-family backstop: the other volume is the one really being deleted
        area.Release(a);                // once more

        Assert.Equal(30, area.StagedBytes);   // only b left
        area.Release(b);
        Assert.Equal(0, area.StagedBytes);
    }

    /// <summary>Per-volume release must lift backpressure too — otherwise compression waits for the whole family to finish uploading and deleting volume by volume was pointless.</summary>
    [Fact]
    public async Task Releasing_A_Single_Volume_Wakes_The_Blocked_Compression()
    {
        using var area = Area(limit: 100);

        var first = await area.StageAsync(ProduceVolumes("v", count: 3, size: 50)); // 150 > 100
        var next = area.StageAsync(Produce("w", 10));

        await Task.Delay(150);
        Assert.False(next.IsCompleted);          // held back by backpressure

        area.ReleaseFile(first.Files[0]);        // release just one volume: 150 → 100, still right on the ceiling
        await Task.Delay(150);
        Assert.False(next.IsCompleted);

        area.ReleaseFile(first.Files[1]);        // release another: 100 → 50, dropping below the ceiling
        var item = await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(10, item.Bytes);

        area.Release(first);
    }

    [Fact]
    public async Task Empty_Produce_Leaves_No_Subdir()
    {
        using var area = Area(limit: 1_000_000);

        var item = await area.StageAsync((_, _) => Task.FromResult<IReadOnlyList<string>>([]));

        Assert.Empty(item.Files);
        Assert.Equal(0, item.Bytes);
        Assert.Equal(0, area.StagedBytes);
        Assert.Empty(Directory.GetDirectories(_stagedTemp)); // no empty GUID subdirectory left behind
    }

    [Fact]
    public async Task Partial_Move_Failure_Cleans_Up_And_Does_Not_Leak_Or_Miscredit()
    {
        using var area = Area(limit: 1_000_000);

        // Produce two paths: the first really exists, the second does not → the second File.Move throws (source missing).
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> produce = async (dir, ct) =>
        {
            var ok = Path.Combine(dir, "ok.7z");
            await File.WriteAllBytesAsync(ok, new byte[10], ct);
            return [ok, Path.Combine(dir, "missing.7z")];
        };

        await Assert.ThrowsAnyAsync<Exception>(() => area.StageAsync(produce));

        Assert.Empty(Directory.GetDirectories(_stagedTemp)); // moved files + subdirectory cleaned up, nothing leaked
        Assert.Equal(0, area.StagedBytes);                    // the exception path does not miscredit bytes
    }
}
