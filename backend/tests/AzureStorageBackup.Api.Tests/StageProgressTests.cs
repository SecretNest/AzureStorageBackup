using System.Collections.Concurrent;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Throttling and speed measurement for stage progress. Throttling is not an optimisation but a necessity: reporting
/// a million files one by one means a million object allocations, while the human eye can take in only a handful of
/// updates per second. But the wrap-up **must** force out one final state, or progress is forever one step short —
/// this project already fell into a hole of exactly that shape once, on the onItem count.
/// </summary>
public sealed class StageProgressTests
{
    [Fact]
    public void Throttles_Bursts_But_Never_Loses_The_Final_State()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 1000, seen.Add);

        for (var i = 0; i < 1000; i++)
        {
            tracker.Touch($"file{i}.bin");
            tracker.Advance(10);
        }
        tracker.Complete();

        // 1000 files must never produce 1000 reports.
        Assert.True(seen.Count < 50, $"expected heavy throttling, got {seen.Count} reports");

        // But the last one has to be the complete final state — one step short is "stuck at 99% forever".
        var final = seen[^1];
        Assert.Equal(1000, final.Processed);
        Assert.Equal(10_000, final.Bytes);
        Assert.Equal(100, final.Percent);
    }

    [Fact]
    public void Reports_The_Item_Currently_Being_Worked_On()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 2, seen.Add);

        tracker.Touch("/nas/photos/IMG_0001.CR2");
        tracker.Advance(1024);
        tracker.Complete();

        // At least one snapshot carries the path being worked on — when it hangs this is the only thing that says where.
        Assert.Contains(seen, s => s.CurrentItem == "/nas/photos/IMG_0001.CR2");
    }

    /// <summary>With an unknown total (a scan still running) we must not invent a percentage, and the same goes for the remaining time.</summary>
    [Fact]
    public void Unknown_Total_Yields_No_Percentage_And_No_Estimate()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Scanning", total: 0, seen.Add);

        tracker.Touch("/nas/photos");
        tracker.Advance(0);
        tracker.Complete();

        Assert.Null(seen[^1].Percent);
        Assert.Null(seen[^1].EstimatedRemaining);
    }

    [Fact]
    public void Tracks_Concurrent_Items_In_Flight()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 3, seen.Add);

        tracker.BeginItem("packs/p0001.7z");
        tracker.BeginItem("packs/p0002.7z");
        tracker.Complete();
        Assert.Equal(
            ["packs/p0001.7z", "packs/p0002.7z"],
            seen[^1].ActiveItems.Select(a => a.Label).OrderBy(x => x, StringComparer.Ordinal));

        tracker.EndItem("packs/p0001.7z", 5000);
        tracker.Complete();
        Assert.Equal(["packs/p0002.7z"], seen[^1].ActiveItems.Select(a => a.Label));
        Assert.Equal(5000, seen[^1].Bytes);
    }

    /// <summary>
    /// The "how much is on the wire, how much is being prepared, how much is queued" breakdown. What the user saw was
    /// a single ever-growing `N objects so far` in the backup detail: an upload-stage item has to go through 7z first
    /// (a 100 MB pack starts at tens of seconds) before any byte moves, and through those tens of seconds the in-flight
    /// list is empty and bytes are 0 — the UI cannot tell working from hung.
    /// </summary>
    [Fact]
    public void Reports_What_Is_Queued_And_What_Is_Being_Prepared()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        for (var i = 0; i < 5; i++)
            tracker.Enqueue();
        tracker.BeginWork(); // two items picked up by worker threads…
        tracker.BeginWork();
        // …one of which has already cleared the staging area and entered the upload leg
        tracker.BeginStaging();
        tracker.BeginPacking();
        tracker.EndPacking();
        tracker.EndStaging();
        tracker.BeginUpload("data/x", volumes: 1);
        tracker.BeginItem("packs/p0001.7z"); // in-flight registers **volumes**
        // the other one is holding the compress lock and producing
        tracker.BeginStaging();
        tracker.BeginPacking();
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(["packs/p0001.7z"], s.ActiveItems.Select(a => a.Label));
        Assert.Equal(1, s.Preparing); // the one holding the compress lock
        Assert.Equal(3, s.Queued);    // enqueued 5 - done 0 - in hand 2
        Assert.Equal(0, s.Processed); // none of this bookkeeping **ever** counts
    }

    /// <summary>
    /// "preparing" never exceeds 1: <see cref="StagingArea"/> holds one global compress lock, so only one item is
    /// producing at any instant. The worker pool is deliberately larger than that (<c>UploadConcurrency + 1</c>) so
    /// that finished archives can each grab an upload stream, not so that compression runs in parallel.
    /// <para>
    /// This number used to be derived as <c>items in hand - items uploading</c>, which counted threads "idling behind
    /// the compress lock" as "preparing" too: on the default config the UI showed 5 preparing, reading like five items
    /// making parallel progress when in fact one was compressing and four threads sat idle. It is also counter-evidence
    /// for the conclusion that **compression is the bottleneck** — the busier it looks, the idler it actually is.
    /// </para>
    /// </summary>
    [Fact]
    public void Preparing_Never_Exceeds_The_One_Item_Holding_The_Compress_Lock()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        for (var i = 0; i < 3; i++)
            tracker.BeginWork();  // three items in the hands of worker threads
        // one producing, the other two queued on the compress lock
        tracker.BeginStaging();
        tracker.BeginPacking();
        tracker.BeginStaging();
        tracker.BeginStaging();
        tracker.BeginUpload("data/x", volumes: 5);    // and one more has already entered the upload leg…
        for (var i = 1; i <= 5; i++)
            tracker.BeginItem($"data/big.{i:000}"); // …which alone has 5 volumes on the wire at once
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(5, s.ActiveItems.Count); // "N uploading" answers "how many streams are on the wire"
        Assert.Equal(1, s.Preparing);         // unrelated to items in hand or volume count: compression is serial
    }

    /// <summary>
    /// Items idling behind the archive lock get a column of their own; they are **not** folded into <c>queued</c>.
    /// <para>
    /// They used to be folded in, on the grounds that "from the user's point of view they are no different from work
    /// still sitting unclaimed in the queue — both are waiting in line". That stops holding the moment two backups run
    /// concurrently: the lock is global (<see cref="StagingArea"/> is a singleton, compression/packing is globally
    /// non-concurrent), so one backup's threads can spend a whole stretch queued behind a lock held by **another
    /// backup**. This backup's <c>preparing</c> is 0 then — it does not hold the lock — leaving ten thousand queued on
    /// screen and not one column able to say "something else is blocking it". What the user actually hit: all six
    /// threads of the 3 TB backup were queued on another backup's lock, and the UI showed
    /// <c>686 of 11,004 objects · 1 object starting upload · 10,317 objects queued</c> motionless for half a minute.
    /// </para>
    /// <para>
    /// Once split apart the diagnosis is free: <c>preparing=1</c> + someone waiting = we hold the lock, normal queueing;
    /// <c>preparing=0</c> + someone waiting = another run holds the lock, and you can go and stop that one.
    /// </para>
    /// </summary>
    [Fact]
    public void Items_Waiting_For_The_Archive_Lock_Are_Told_Apart_From_Queued()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        for (var i = 0; i < 3; i++)
            tracker.Enqueue();
        for (var i = 0; i < 3; i++)
        {
            tracker.BeginWork();     // all three claimed, nothing left in the queue
            tracker.BeginStaging();
        }
        tracker.BeginPacking();      // only one got the lock
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(1, s.Preparing);          // the one actually producing
        Assert.Equal(2, s.WaitingOnArchive);   // the other two idling on the lock
        Assert.Equal(0, s.Queued);             // the queue really is empty — no longer lumped in as "queued"
    }

    /// <summary>
    /// The shape when **another run** holds the lock: nothing of ours is producing, yet a pile is waiting. This screen
    /// is where the change came from; before the split it looked identical to "ten thousand items still waiting their turn".
    /// </summary>
    [Fact]
    public void Waiting_On_An_Archive_Lock_Held_By_Another_Run_Shows_Zero_Preparing()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 8, seen.Add);

        for (var i = 0; i < 8; i++)
            tracker.Enqueue();
        for (var i = 0; i < 5; i++)   // five threads claimed work, all queued behind the lock — which this run does not hold
        {
            tracker.BeginWork();
            tracker.BeginStaging();
        }
        tracker.BeginWork();          // the sixth: the one compressed earlier, already in the upload leg
        tracker.BeginStaging();
        tracker.EndStaging();
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(0, s.Preparing);         // ← this 0 used to be the only clue, and it explains nothing
        Assert.Equal(5, s.WaitingOnArchive);
        Assert.Equal(1, s.Uploading);
        Assert.Equal(2, s.Queued);            // not yet claimed
        Assert.Equal(8, s.Processed + s.Preparing + s.Queued + s.WaitingOnArchive + s.Uploading);
    }

    /// <summary>
    /// Inside the staging area an item can be parked on two completely different things, and until they were split the
    /// screen called both "waiting for the archive slot" — which made the diagnosis on that column actively wrong. A full
    /// pool shows preparing=0 with someone waiting, exactly the shape the column says means "another run holds the lock",
    /// so it sent the operator off to stop a backup that was not in the way. The lock points at a producer; the pool's
    /// byte ceiling points at the wire, and only an upload can clear it.
    /// <para>
    /// The split is arithmetic, not a new term: the two still add up to "inside staging, without the lock", so the item
    /// identity keeps balancing.
    /// </para>
    /// </summary>
    [Fact]
    public void A_Full_Pool_Is_Not_Reported_As_A_Lock_Held_Elsewhere()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 3, seen.Add);

        for (var i = 0; i < 3; i++)
            tracker.Enqueue();

        // The compressor is in the staging area and the pool is at its ceiling: no lock is involved at all.
        tracker.BeginWork();
        tracker.BeginStaging();
        tracker.BeginRoomWait();
        tracker.Complete();

        var full = seen[^1];
        Assert.Equal(1, full.WaitingOnRoom);
        Assert.Equal(0, full.WaitingOnArchive);   // ← this used to read 1, pointing at a lock nobody was holding
        Assert.Equal(0, full.Preparing);
        Assert.Equal(3, full.Processed + full.Preparing + full.Queued
            + full.WaitingOnRoom + full.WaitingOnArchive + full.Uploading);

        // An upload frees space; now the wait really is on the lock, and it is not ours (nothing of ours is packing).
        tracker.EndRoomWait();
        tracker.Complete();

        var onLock = seen[^1];
        Assert.Equal(0, onLock.WaitingOnRoom);
        Assert.Equal(1, onLock.WaitingOnArchive);
        Assert.Equal(0, onLock.Preparing);
        Assert.Equal(3, onLock.Processed + onLock.Preparing + onLock.Queued
            + onLock.WaitingOnRoom + onLock.WaitingOnArchive + onLock.Uploading);
    }

    /// <summary>
    /// Between finishing compression and starting the upload there is real work: a pack re-<c>Stat</c>s every member
    /// (rehashing the ones that changed), a single file looks up the dedup map, and a dedup hit is never uploaded at
    /// all. That work **must not** count as queued — reporting something that is working as "queued" is even more
    /// misleading than the old inflated preparing was.
    /// </summary>
    [Fact]
    public void Post_Packing_Verification_Is_Not_Reported_As_Queued()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        tracker.Enqueue();
        tracker.Enqueue();
        tracker.BeginWork();
        tracker.BeginStaging();
        tracker.BeginPacking();
        tracker.EndPacking();
        tracker.EndStaging();  // compressed; verifying members / checking dedup, not yet in the upload leg
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(0, s.Preparing); // the compress lock has already been handed back
        Assert.Equal(1, s.Queued);    // only the one still in the queue, not 2
    }

    /// <summary>Every volume needs its own <c>ItemProgress</c>: DeltaProgress keeps its running baseline per call, so
    /// parallel volumes sharing one instance would read each other's totals as a "rewind" and double-count them.</summary>
    [Fact]
    public void Parallel_Volumes_Each_Get_Their_Own_Progress_Baseline()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        var v1 = tracker.ItemProgress();
        var v2 = tracker.ItemProgress();
        // The two volumes interleave, each climbing from 0 to 100.
        v1.Report(40);
        v2.Report(60);
        v1.Report(100);
        v2.Report(100);
        tracker.Complete();

        Assert.Equal(200, seen[^1].Bytes);
    }

    /// <summary>Queue depth must be able to drain to zero. Unpaired counters (a failure path missing its finally, say)
    /// leave a few items hanging as "preparing" or "queued" forever, when in fact nothing is running.</summary>
    [Fact]
    public void Queue_Depth_Drains_To_Zero_When_Every_Item_Is_Done()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 3, seen.Add);

        for (var i = 0; i < 3; i++)
            tracker.Enqueue();
        for (var i = 0; i < 3; i++)
        {
            tracker.BeginWork();
            tracker.BeginStaging();
            tracker.BeginPacking();
            tracker.EndPacking();
            tracker.EndStaging();
            tracker.Advance(10);
            tracker.EndWork();
        }
        tracker.Complete();

        Assert.Equal(0, seen[^1].Queued);
        Assert.Equal(0, seen[^1].Preparing);
        Assert.Equal(3, seen[^1].Processed);
    }

    /// <summary>The counters advance independently, so any read is necessarily a half-beat-skewed snapshot — a consumer
    /// claiming an item before the enqueue bookkeeping lands is entirely normal timing. Without clamping at 0 the UI
    /// flashes "-1 queued".</summary>
    [Fact]
    public void Skewed_Counters_Never_Produce_Negative_Numbers()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        tracker.BeginWork(); // the enqueue has not landed yet, but the item is already claimed
        tracker.Complete();

        Assert.Equal(0, seen[^1].Queued);    // enqueued 0 - in hand 1 is negative, clamped to 0
        Assert.Equal(0, seen[^1].Preparing); // has not got the compress lock yet
    }

    /// <summary>
    /// Bytes during an upload must be counted **as they go**, not booked in one lump once a blob finishes: pushing a
    /// 100 MB pack takes tens of seconds, through which the speed window is empty and the readout drops to zero —
    /// exactly the "no speed visible" the user reported.
    /// </summary>
    [Fact]
    public async Task Streaming_Byte_Reports_Produce_A_Live_Speed_Without_Counting_Items()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        var progress = tracker.ItemProgress();
        progress.Report(1_000_000); // the SDK reports a **cumulative** value
        await Task.Delay(250);      // cross the throttle window so a second speed sample exists
        progress.Report(3_000_000);
        tracker.Complete();

        Assert.True(seen[^1].BytesPerSecond > 0, "in-flight bytes should feed the speed readout");
        Assert.Equal(3_000_000, seen[^1].Bytes);
        Assert.Equal(0, seen[^1].Processed); // a byte report is not a slot completion
    }

    /// <summary>A cumulative value going backwards = a retry restarting from scratch (or the next volume starting at 0).
    /// Retransmitted bytes must be counted **again**: for "current wire speed" that is right, those bytes really did
    /// cross the wire a second time.</summary>
    [Fact]
    public void A_Retry_That_Restarts_The_Byte_Count_Is_Treated_As_Fresh_Traffic()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        var progress = tracker.ItemProgress();
        progress.Report(100);
        progress.Report(300); // still accumulating within the same call → +200
        progress.Report(50);  // rewind: a retry from scratch → these 50 are fresh traffic
        tracker.Complete();

        Assert.Equal(350, seen[^1].Bytes);
    }

    /// <summary>
    /// Two readings of the same event cannot share one number: retransmitted bytes are fresh traffic for **speed**
    /// (they really did cross the wire again), but not for the **how much of this stream has been sent / how big is it**
    /// fraction — the numerator would overshoot the denominator.
    /// <para>
    /// Observed in the field: a 100 MB volume died halfway through, the retry restarted the whole volume, and the UI
    /// showed <c>DJI_0032.MP4 (30/36) — 200.0 MB / 100.0 MB · 100%</c> before completing normally.
    /// The percentage was clamped at 100 while the two byte figures plainly contradicted each other.
    /// </para>
    /// </summary>
    [Fact]
    public void A_Retry_Restarts_The_Per_Stream_Reading_Instead_Of_Overshooting_Its_Size()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        tracker.BeginItem("data/abc.030", "DJI_0032.MP4 (30/36)", totalBytes: 100);
        var progress = tracker.ItemProgress("data/abc.030");
        progress.Report(100); // the whole volume was pushed, then the connection died at the wrap-up
        progress.Report(30);  // retry: the SDK's cumulative value restarts from 0
        tracker.Complete();

        var flow = Assert.Single(seen[^1].ActiveItems);
        Assert.Equal(100, flow.Total);
        Assert.Equal(30, flow.Sent); // where this stream stands **right now**, not the sum of all attempts
        Assert.Equal(30, flow.Percent);
        // The speed side still includes the retransmit: all 130 bytes really did cross the wire.
        Assert.Equal(130, seen[^1].Bytes);
    }

    /// <summary>The preparing row's byte progress: 7z itself reports nothing usable, but on the streaming
    /// route the producer feeds the source into 7z's stdin and can count what it has fed — pipe backpressure
    /// keeps that count within a buffer of what 7z has consumed, so it is honest packing progress. Without
    /// it, "preparing: file — 113.949 GB" sits motionless for the whole production (field report). The
    /// counter belongs to the packing stretch: it opens at zero with BeginPacking and dies with EndPacking.</summary>
    [Fact]
    public void Packing_Progress_Rides_The_Preparing_Row_And_Resets_With_It()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        tracker.BeginPacking("/src/big.mkv", 1000);
        tracker.PackingProgress(300);
        tracker.PackingProgress(300);
        tracker.Complete();

        Assert.Equal(600, seen[^1].PreparingDone);
        Assert.Equal("/src/big.mkv", seen[^1].PreparingItem);
        Assert.Equal(1000, seen[^1].PreparingBytes);

        tracker.EndPacking();
        tracker.Complete();
        Assert.Equal(0, seen[^1].PreparingDone); // the stretch ended; nothing is preparing

        tracker.BeginPacking("/src/next.mkv", 500);
        tracker.Complete();
        Assert.Equal(0, seen[^1].PreparingDone); // a new stretch starts from zero, not from the last one's total
    }

    /// <summary>The two progress dialects must not be cross-wired. FileHasher.FullHashAsync reports
    /// **increments** (one Report per chunk read, carrying that chunk's size), while ItemProgress speaks
    /// the SDK's dialect of attempt-**cumulative** values. Fed straight into ItemProgress, the first chunk
    /// lands and every following equal-sized chunk computes delta 0 and is swallowed — in the field a
    /// 113.949 GB hash gate sat at exactly 80.0 KB (one 81920-byte buffer) for its entire read, at 0 B/s.
    /// ItemProgressFromIncrements is the adapter increment-speaking producers must go through.</summary>
    [Fact]
    public void Increment_Reports_Accumulate_Into_The_Stream_Reading()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Repairing", total: 1, seen.Add);

        tracker.BeginItem("/src/big.mkv", "/src/big.mkv", totalBytes: 245760);
        var progress = tracker.ItemProgressFromIncrements("/src/big.mkv");
        progress.Report(81920);
        progress.Report(81920);
        progress.Report(81920);
        tracker.Complete();

        var flow = Assert.Single(seen[^1].ActiveItems);
        Assert.Equal(245760, flow.Sent);       // three chunks, not one
        Assert.Equal(245760, seen[^1].Bytes);  // and the speed numerator saw every chunk too
    }

    /// <summary>In-flight begin/end **must not** count. The upload slot count carries an exactly-once constraint — a
    /// pack repacked because its members changed goes through several uploads, yet occupies exactly one slot of total.
    /// Let EndItem count on the side and the progress bar shoots past 100% (this repo already double-counted once on onItem).</summary>
    [Fact]
    public void In_Flight_Bookkeeping_Does_Not_Advance_The_Count()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        // The same slot gets repacked twice: two upload rounds, but it must count only once.
        tracker.BeginItem("packs/p0001.7z");
        tracker.EndItem("packs/p0001.7z", 100);
        tracker.BeginItem("packs/p0001.7z");
        tracker.EndItem("packs/p0001.7z", 100);
        tracker.Advance(0); // slot done; this is the only place that counts
        tracker.Complete();

        Assert.Equal(1, seen[^1].Processed);
        Assert.Equal(100, seen[^1].Percent); // exactly 100%, never over
    }

    // ---- Remaining time ----
    //
    // Field feedback: upload speed swings wildly and the remaining time jumps around with it. The root cause is that it
    // used to take the 10-second rolling window's speed as the denominator, while a backup's rhythm is "compress a pack
    // for tens of seconds → transfer for a few": during compression the window holds not one byte, the speed drops to
    // zero and the remaining time vanishes entirely; once compressed a tiny number suddenly pops up. Those tens of
    // seconds of compression are obviously part of the remaining time too.

    /// <summary>Through the tens of seconds compression pins the wire speed at 0, the remaining time must not vanish — that is precisely when it is needed most.</summary>
    [Fact]
    public async Task Remaining_Time_Survives_The_Stretches_Where_Nothing_Is_On_The_Wire()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        for (var i = 0; i < 4; i++) tracker.Enqueue(1_000_000); // four items, 1 MB of original bytes each
        tracker.SetTotal(4);
        tracker.BeginWork();
        await Task.Delay(60);
        tracker.Advance(0, work: 1_000_000); // one item done. Bytes go through ItemProgress; not one is added here
        await Task.Delay(250);               // cross the throttle to force out a report carrying the latest state
        tracker.Advance(0, work: 1_000_000);

        var last = seen[^1];
        Assert.Equal(0, last.BytesPerSecond);      // the speed window really does hold not one byte
        Assert.NotNull(last.EstimatedRemaining);   // but the remaining time is given anyway
        Assert.True(last.EstimatedRemaining > TimeSpan.Zero);
    }

    /// <summary>An item may be a 100 GB single file, or a pack of several hundred 5 KB files. The upload stage
    /// extrapolates by **original bytes** precisely so the two are not treated as equally heavy: when 1 item is done but
    /// 90% of the bytes are already through, the remaining time should speak in bytes, not say "3/4 to go".</summary>
    [Fact]
    public async Task Upload_Estimates_By_Bytes_Not_By_Item_Count()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        tracker.Enqueue(900);                                  // one big item
        for (var i = 0; i < 3; i++) tracker.Enqueue(100 / 3 + 1); // three small ones, about 100 in total
        tracker.SetTotal(4);
        tracker.BeginWork();
        await Task.Delay(300);
        tracker.Advance(0, work: 900); // the big one is done: 1/4 by item count, 9/10 by bytes

        var eta = seen[^1].EstimatedRemaining;
        Assert.NotNull(eta);
        // By bytes: about 1/9 of the elapsed time is left. By item count it would be 3x the elapsed time — an order of magnitude off.
        Assert.True(eta < TimeSpan.FromMilliseconds(200), $"expected a byte-weighted estimate, got {eta}");
    }

    /// <summary>
    /// A resumed run adopts everything its journal already vouches for, and does it in seconds. Those items are
    /// finished — they must count as progress — but they earned none of the elapsed time, so dividing by them says
    /// the rest of the run will take almost no time at all. Reported from a real resume, where the estimate came out
    /// in minutes for hours of work.
    /// <para>
    /// The pair is what production does per item: Advance writes the item off with 0 bytes (this stage always passes
    /// 0 there, supplying the real figure separately), then SetTransferred refreshes the authoritative cumulative
    /// total. A total that did not move is what identifies the skip; nothing else in either call can.
    /// </para>
    /// <para>
    /// On the injected clock rather than a delay, so the two answers are exact rather than merely far apart: three
    /// quarters of the work skipped means the wrong denominator reports a quarter of the right one.
    /// </para>
    /// </summary>
    [Fact]
    public void Upload_Ignores_Skipped_Work_When_Estimating()
    {
        var now = 0L;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add) { Clock = () => now };

        // Five items of equal size, so one is still outstanding at the end — an estimate only exists while
        // something remains.
        for (var i = 0; i < 5; i++) tracker.Enqueue(1000);
        tracker.SetTotal(5);
        tracker.BeginWork();

        // Three adopted from the journal, at no cost in time at all: the clock does not move.
        for (var i = 0; i < 3; i++)
        {
            tracker.Advance(0, work: 1000);
            tracker.SetTransferred(0);
        }

        // One real upload, which is what the whole elapsed second was spent on. 1000 of work left after it.
        now = 1000;
        tracker.Advance(0, work: 1000);
        tracker.SetTransferred(500);
        tracker.Complete();   // the throttle would otherwise decide whether the last state was ever published

        // One second bought 1000 of transferred work and 1000 remains, so the honest answer is one second.
        // Counting the three adopted items as time-earning puts 4000 under the division and answers 250ms.
        Assert.Equal(TimeSpan.FromSeconds(1), seen[^1].EstimatedRemaining);
    }

    /// <summary>
    /// The reported shape: a 5 TB run resumed, most of it adopted from the journal in seconds, and then one 70 GB
    /// file that hashes, compresses and uploads for hours. An item is written off all at once at completion, so
    /// through all of that the done side does not move while the elapsed side does — and the estimate climbed from
    /// 100 days to 765 days over an hour and a half of uninterrupted uploading.
    /// <para>
    /// What stops the climb is crediting the bytes of that item that have already landed. Here the clock is moved
    /// twice with volumes landing in between: without the credit the second answer must be larger than the first
    /// (the denominator frozen, the numerator growing), and it must not be.
    /// </para>
    /// </summary>
    [Fact]
    public void Upload_Counts_A_Huge_In_Flight_Item_Towards_The_Estimate()
    {
        var now = 0L;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add) { Clock = () => now };

        // 200 of adopted work, then one item worth 800 that is still going. Kept in a ratio the estimate will
        // actually answer for: below its reach bound it withholds the number, which is a different case.
        tracker.Enqueue(200);
        tracker.Enqueue(800);
        tracker.SetTotal(2);
        tracker.BeginWork();

        // Adopted from the journal: written off instantly, nothing on the wire.
        tracker.Advance(0, work: 200);
        tracker.SetTransferred(0);

        // The big one starts, and its volumes begin landing.
        tracker.BeginUpload("data/huge", volumes: 8);
        void Volume(int i)
        {
            var name = $"data/huge.{i:000}";
            tracker.BeginItem(name, owner: "data/huge", totalBytes: 100);
            // The bytes have to be reported as they flow: a volume that never registered any is one the
            // if-missing upload skipped, and the ledger deliberately does not credit those.
            tracker.ItemProgress(name).Report(100);
            tracker.EndItem(name, 100);
        }

        now = 1000;
        Volume(1);          // the first stream opening is where the estimate's clock starts
        now = 1500;
        tracker.Complete();
        var early = seen[^1].EstimatedRemaining;

        now = 2500;
        for (var i = 2; i <= 5; i++) Volume(i);
        tracker.Complete();
        var later = seen[^1].EstimatedRemaining;

        Assert.NotNull(early);
        Assert.NotNull(later);
        // Twice the elapsed bought three times the landed bytes, so the answer must come **down**. Frozen, it could
        // only go up — that is the 100-days-to-765-days climb.
        Assert.True(later < early, $"the estimate climbed while volumes were landing: {early} → {later}");
    }

    /// <summary>
    /// Off a real screen again, and the opposite failure to the one below: <c>1.628 TB / 5.320 TB original (30%) ·
    /// 109.4 KB uploaded (0% of original) · 13.7 MB/s · ~1d 18h left</c>. Even if every byte still to come went up
    /// at exactly the speed on that line it would take over three days, so the estimate was out by about half.
    /// <para>
    /// The in-flight item's landed bytes are stored bytes and the workload is source bytes, so the credit converts
    /// between them by the ratio this run has observed — completed timed work over what those items stored. On that
    /// screen the whole observation was <b>one</b> item: 109.4 KB. Every one of the 222 before it was a dedup or
    /// resume hit, correctly kept out of the timed side, which leaves the ratio resting on a sample four
    /// hundred-thousandths the size of what it is asked to convert.
    /// </para>
    /// <para>
    /// A ratio read off that sample is not a measurement, and it lands twice: the credit is added to what has moved
    /// and subtracted from what is left, so a factor of two in it is a factor of two in the answer. Worse, the
    /// product is what the reach bound is then tested against — the extrapolation manufactures the very sample size
    /// that excuses it from the check written to catch extrapolations. Below the bar the safe reading is the one
    /// this code already takes before any item has completed: count a landed byte as one byte of source work,
    /// which is exact for a store-only workload and credits too little rather than too much everywhere else.
    /// </para>
    /// <para>
    /// Here one hour lands 100 GiB against 3 TiB left, so crediting them one for one answers about 29 hours.
    /// Passing them through a ratio of two says 14, and the run is nowhere near twice as far along as it looks.
    /// </para>
    /// </summary>
    [Fact]
    public void Upload_Will_Not_Convert_In_Flight_Bytes_Through_An_Unmeasured_Ratio()
    {
        var now = 0L;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add) { Clock = () => now };

        const long kb = 1024L;
        const long gb = 1024L * 1024 * 1024;
        const long tb = 1024L * gb;

        tracker.Enqueue(tb);        // adopted from the journal, nothing on the wire for it
        tracker.Enqueue(200 * kb);  // the one item that really uploaded, and the entire sample
        tracker.Enqueue(3 * tb);    // the enormous one, still going
        tracker.SetTotal(3);
        tracker.BeginWork();
        tracker.SetTransferred(0);

        tracker.Advance(0, work: tb);
        tracker.SetTransferred(0);

        // 200 KiB of source stored as 100 KiB: a two-to-one ratio, measured on a single small file.
        now = 1_000;                // the first stream opening is where the estimate's clock starts
        tracker.BeginUpload("data/tiny", volumes: 1);
        tracker.BeginItem("data/tiny", owner: "data/tiny", totalBytes: 100 * kb);
        tracker.ItemProgress("data/tiny").Report(100 * kb);
        tracker.EndItem("data/tiny", 100 * kb);
        tracker.ConfirmUpload("data/tiny");
        tracker.EndUpload("data/tiny");
        tracker.Advance(0, work: 200 * kb);
        tracker.SetTransferred(100 * kb);

        // Then the big one, landing 100 GiB over the next hour and not written off in that time.
        tracker.BeginUpload("data/huge", volumes: 4096);
        for (var i = 0; i < 100; i++)
        {
            var name = $"data/huge.{i:000}";
            tracker.BeginItem(name, owner: "data/huge", totalBytes: gb);
            tracker.ItemProgress(name).Report(gb);
            tracker.EndItem(name, gb);
        }
        now = 1_000 + 3_600_000;
        tracker.Complete();

        var eta = seen[^1].EstimatedRemaining;
        Assert.NotNull(eta);
        // 100 GiB an hour against the 2,972 GiB those bytes leave behind. Doubling the credit answers 14.4.
        Assert.InRange(eta.Value.TotalHours, 29, 31);
    }

    /// <summary>
    /// The numbers off a real screen: a resumed 5.3 TB run with 44.9 KB moved, the first very large file in its
    /// hours-long hash-and-compress, and "~33976d left" printed from a hundred-million-fold extrapolation. Below
    /// the reach bound there is no honest answer, so none is given.
    /// </summary>
    [Fact]
    public void Upload_Withholds_An_Estimate_It_Cannot_Support()
    {
        var now = 0L;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add) { Clock = () => now };

        const long tb = 1024L * 1024 * 1024 * 1024;
        tracker.Enqueue(5 * tb);            // everything still to do
        tracker.Enqueue(45_000);            // and the handful of KB that has actually moved
        tracker.SetTotal(2);
        tracker.BeginWork();

        now = 1000;
        tracker.BeginItem("data/tiny", owner: "data/tiny", totalBytes: 45_000);
        tracker.ItemProgress("data/tiny").Report(45_000);
        tracker.EndItem("data/tiny", 45_000);
        tracker.Advance(0, work: 45_000);
        tracker.SetTransferred(45_000);
        now = 60_000;                       // and then a very large file goes quiet for a minute
        tracker.Complete();

        Assert.Null(seen[^1].EstimatedRemaining);
    }

    /// <summary>
    /// The floor that lets the number appear on a run too big for the reach bound to be met in any reasonable
    /// time. A gigabyte **on the wire** is taken as enough to say something, whatever is left.
    /// </summary>
    [Fact]
    public void Upload_Will_Estimate_Once_A_Gigabyte_Has_Moved()
    {
        var now = 0L;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add) { Clock = () => now };

        const long gb = 1024L * 1024 * 1024;
        tracker.Enqueue(5000 * gb);
        tracker.Enqueue(2 * gb);
        tracker.SetTotal(2);
        tracker.BeginWork();
        tracker.SetTransferred(0);   // as the orchestrator does — see the sibling test below for what it changes

        now = 1000;
        tracker.BeginUpload("data/first", volumes: 1);
        tracker.BeginItem("data/first", owner: "data/first", totalBytes: 2 * gb);
        tracker.ItemProgress("data/first").Report(2 * gb);
        tracker.EndItem("data/first", 0);
        tracker.ConfirmUpload("data/first");
        tracker.EndUpload("data/first");
        tracker.Advance(0, work: 2 * gb);
        tracker.SetTransferred(2 * gb);
        now = 2000;
        tracker.Complete();

        // Nowhere near a twentieth of what is left, but past the floor, so an answer is offered. On EtaSeconds, not
        // EstimatedRemaining: the latter has a crude fallback of its own that answers whether or not the floor was
        // cleared, so it cannot tell this test's subject apart from its absence.
        Assert.NotNull(seen[^1].EtaSeconds);
    }

    /// <summary>
    /// What the floor is a floor **of**. It excuses an estimate from the reach bound on the grounds that enough has
    /// been seen to extrapolate from, and the only thing this stage actually observes is bytes going over the wire —
    /// so that is what has to clear it. Measured against the workload instead, the compression ratio decides when
    /// the estimate is allowed to speak: ten gigabytes of text stored as a hundred megabytes clears a gigabyte of
    /// "moved" on a tenth of a gigabyte of evidence, and a run of nothing but text clears it on a hundredth.
    /// <para>
    /// It is the same confusion of source bytes for stored bytes that let a ratio read off 109.4 KB pass itself off
    /// as a sample, one term further out: there the product was tested against the floor, here the multiplicand is.
    /// Both are answered by measuring the sample where the sample is taken.
    /// </para>
    /// </summary>
    [Fact]
    public void Upload_Measures_Its_Sample_Floor_In_Bytes_Actually_Uploaded()
    {
        var now = 0L;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add) { Clock = () => now };

        const long mb = 1024L * 1024;
        const long gb = 1024 * mb;

        tracker.Enqueue(10 * gb);      // ten gigabytes of highly compressible source
        tracker.Enqueue(5000 * gb);    // and everything else, untouched
        tracker.SetTotal(2);
        tracker.BeginWork();
        // As the orchestrator does before the first item: without it EndItem keeps its own per-volume tally, the
        // refresh below then reads as "nothing moved", and the item is written off as a skip — which reaches the
        // answer through moved <= 0 and never gets near the floor this test is about.
        tracker.SetTransferred(0);

        // Stored as a hundred megabytes. That is the whole of what this run has watched cross the wire.
        now = 1000;
        tracker.BeginUpload("data/text", volumes: 1);
        tracker.BeginItem("data/text", owner: "data/text", totalBytes: 100 * mb);
        tracker.ItemProgress("data/text").Report(100 * mb);
        tracker.EndItem("data/text", 0);
        tracker.ConfirmUpload("data/text");
        tracker.EndUpload("data/text");
        tracker.Advance(0, work: 10 * gb);
        tracker.SetTransferred(100 * mb);
        now = 2000;
        tracker.Complete();

        // Five hundred times the reach bound allows, on a tenth of the evidence the floor asks for.
        //
        // Asserted on EtaSeconds rather than EstimatedRemaining, because the two part company here and it is
        // EtaSeconds the screen prints — stageLines draws the segment only when it is non-null. EstimatedRemaining
        // falls through to the crude "average bytes per item ÷ current speed", which off a single item and a
        // one-second window answers two seconds for five terabytes: a different estimator, and not this one.
        Assert.Null(seen[^1].EtaSeconds);
    }

    /// <summary>Stages that declare no workload (diff/restore/check) fall back to extrapolating by item count — still a
    /// whole-run average, still ignoring instantaneous speed. For diff the item count is the right proxy anyway: the
    /// vast majority of entries are just stat'ed and passed over.</summary>
    [Fact]
    public async Task Stages_Without_A_Declared_Workload_Fall_Back_To_Counting_Items()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 4, seen.Add);

        await Task.Delay(60);
        tracker.Advance(0); // an unchanged file: not one byte read, the speed window is bone empty
        await Task.Delay(250);
        tracker.Advance(0);

        Assert.Equal(0, seen[^1].BytesPerSecond);
        Assert.NotNull(seen[^1].EstimatedRemaining);
    }

    /// <summary>Once everything is done it must settle to null: leaving "0 seconds remaining" hanging on the UI looks more stuck than showing nothing.</summary>
    [Fact]
    public void No_Remaining_Time_Once_Everything_Is_Done()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        tracker.Enqueue(1000);
        tracker.Enqueue(1000);
        tracker.SetTotal(2);
        tracker.Advance(0, work: 1000);
        tracker.Advance(0, work: 1000);
        tracker.Complete();

        Assert.Null(seen[^1].EstimatedRemaining);
    }

    /// <summary>Workload only feeds the remaining time; it must never leak into <c>Bytes</c> — that number is "bytes
    /// that actually crossed the wire", and both the speed and the cumulative traffic in the UI point at it. Mix
    /// original bytes in and a dedup hit shows up as having transferred a pile of data.</summary>
    [Fact]
    public void Declared_Workload_Never_Leaks_Into_The_Transferred_Byte_Count()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        tracker.Enqueue(5_000_000);
        tracker.Advance(0, work: 5_000_000); // dedup hit: 5 MB original, 0 bytes on the wire
        tracker.Complete();

        Assert.Equal(0, seen[^1].Bytes);
    }

    /// <summary>
    /// A backup upload's rhythm is "compress a pack for tens of seconds → transfer for a few". The speed window used to
    /// timestamp by wall clock, so the same wire measured differently depending on how long the pause was: a pause
    /// shorter than the window dilutes it, one longer evicts the old samples wholesale and reports 0 on the spot.
    /// Speed answers "how fast is the wire", so those tens of seconds of compression have no business in the denominator.
    /// </summary>
    [Fact]
    public void Compression_Stalls_Do_Not_Dilute_The_Upload_Speed()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 2, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        // First volume: 1 MB in 1 second.
        tracker.BeginItem("v1");
        var first = tracker.ItemProgress();
        now += 1_000;
        first.Report(1 << 20);
        tracker.EndItem("v1", 0);

        // 30 seconds of compression — not one stream open. These 30 seconds must not enter the denominator.
        now += 30_000;

        // Second volume: another 1 MB in 1 second.
        tracker.BeginItem("v2");
        var second = tracker.ItemProgress();
        now += 1_000;
        second.Report(1 << 20);
        tracker.EndItem("v2", 0);

        // 2 MB over 2 seconds on the wire ≈ 1 MB/s. Diluted by the 30 seconds it would be 64 KB/s; with the old samples evicted, 0.
        Assert.InRange(seen[^1].BytesPerSecond, 900_000L, 1_150_000L);
    }

    /// <summary>
    /// The switch defaults to off: stages like scanning and diffing never register in-flight items, so the virtual
    /// clock would sit at 0 forever for them and the speed would be permanently 0. They must keep the wall clock as-is.
    /// </summary>
    [Fact]
    public void Stages_Without_In_Flight_Items_Keep_The_Wall_Clock_Speed()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 2, seen.Add) { Clock = () => now };

        tracker.Advance(1 << 20);
        now += 1_000;
        tracker.Advance(1 << 20);

        Assert.InRange(seen[^1].BytesPerSecond, 900_000L, 1_150_000L);
    }

    /// <summary>
    /// When a stream is open but not one byte moves (network wedged, SDK never firing a retry) no event triggers a
    /// report and the UI freezes on the numbers from just before the stall — invisible exactly when the problem most
    /// needs seeing. The heartbeat inside the active stretch pushes the speed window along so the speed falls to 0 on its own.
    /// </summary>
    [Fact]
    public void A_Stuck_Stream_Drags_The_Speed_Down_Instead_Of_Freezing_It()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        tracker.BeginItem("v1");
        var bytes = tracker.ItemProgress();
        now += 1_000;
        bytes.Report(4 << 20);
        now += 1_000;
        bytes.Report(8 << 20);   // cumulative value: another 4 MB
        Assert.True(seen[^1].BytesPerSecond > 0, "the speed must be visible while the stream is flowing");

        // The stream is still open, the bytes are not moving. The heartbeat ticks once a second.
        for (var i = 0; i < 12; i++)
        {
            now += 1_000;
            tracker.Tick();
        }

        Assert.Equal(0, seen[^1].BytesPerSecond);
    }

    /// <summary>
    /// A stage holding nothing at all, with nothing on the wire, has genuinely nothing to say — no timer should be
    /// publishing identical snapshots at it. The stretch is real rather than theoretical: the upload stage's tracker
    /// is constructed the moment the diff starts, and on a large scan it can wait a long time for its first item.
    /// <para>
    /// This case used to assert the same silence for a stage that had <b>claimed</b> an item and was compressing it,
    /// on the grounds that pure compression has "nothing new to report". That premise does not hold — <c>preparing</c>,
    /// the staged and checking byte columns and every queue depth all move throughout compression — and the silence it
    /// bought is exactly what left the UI displaying the snapshot from the last volume that transferred, for however
    /// long the stage went without transferring another. Its counterpart now lives in
    /// <c>UploadWaitVisibilityTests.The_Snapshot_Keeps_Refreshing_With_Work_In_Hand_And_Nothing_On_The_Wire</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void The_Heartbeat_Stays_Silent_While_The_Stage_Holds_Nothing()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        // No BeginWork and no BeginItem: not one item has reached this stage yet.
        for (var i = 0; i < 5; i++)
        {
            now += 1_000;
            tracker.Tick();
        }

        Assert.Empty(seen);

        // And once the last item is handed back, silence returns — the timer must not outlive the work.
        tracker.BeginWork();
        tracker.EndWork();
        seen.Clear();

        for (var i = 0; i < 5; i++)
        {
            now += 1_000;
            tracker.Tick();
        }

        Assert.Empty(seen);
    }

    /// <summary>
    /// The heartbeat tests above all inject a fake clock — <c>Heartbeat(bool)</c> sees <c>Clock is not null</c> and
    /// returns early, never newing up the production <see cref="System.Threading.Timer"/> at all. Which means that if
    /// someone deleted the <c>Heartbeat(on: true)</c> line inside <c>BeginItem</c>, every test above would stay green
    /// while the heartbeat in the product had already been muted. This test injects **no clock** and goes through the
    /// real <see cref="System.Threading.Timer"/>, which is the only way to cover that call site itself.
    /// </summary>
    [Fact]
    public async Task Real_Timer_Heartbeat_Publishes_Without_Any_Further_Manual_Event()
    {
        // Unlike the rest of this file: here the heartbeat runs on a real Timer thread, so writes to seen no longer
        // happen on the same thread that reads it — List<T> is not thread-safe, so ConcurrentQueue makes both lock-free.
        var seen = new ConcurrentQueue<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Enqueue, speedWhileInFlight: true);

        tracker.BeginItem("v1");
        var progress = tracker.ItemProgress();
        progress.Report(1_000_000);
        await Task.Delay(300); // cross the 200 ms throttle to confirm this manual report has landed
        var countBeforeHeartbeat = seen.Count;

        // The heartbeat period is 1 second. Wait 2.5 — 2.5x the period, so failing to get at least one tick is the
        // anomaly; that margin is for build-machine jitter on CI/NAS (preemption, GC pauses), not a bet on the 1-second
        // edge. A test that blows up once a month is worse than no test, so it is better to wait a bit longer.
        await Task.Delay(2_500);

        // Must be read before Complete(): Complete() forces out a final snapshot of its own, and if that slipped in,
        // the assertion would pretend to pass even if the heartbeat had never ticked once.
        var countAfterWaiting = seen.Count;

        tracker.Complete();

        Assert.True(
            countAfterWaiting > countBeforeHeartbeat,
            $"expected the real-time heartbeat to publish at least one snapshot with no further manual events, " +
            $"got {countAfterWaiting - countBeforeHeartbeat} extra reports before Complete()");
    }

    /// <summary>
    /// Concurrent uploads are the production default: several volumes in flight at once. The volume that finishes first
    /// must not stop the speed clock — as long as another stream is still open that time still counts toward the speed
    /// window, and only when the "last one" finishes does the clock really stop.
    /// The <c>_active.IsEmpty</c> check in <see cref="EndItem"/> exists exactly for this.
    /// Testing only strictly sequential Begin/End pairs (which is what every existing test does) cannot cover that
    /// branch — with that shape the serial case still computes correctly even if the IsEmpty check were deleted
    /// outright; the overlapping scene here is what exposes its removal.
    /// The second half then verifies "the clock really stopped": after b finishes, advance the injected clock by 5
    /// seconds and tick the heartbeat by hand — those 5 seconds must not sneak into the speed window, or the other
    /// half of the IsEmpty check has been missed.
    /// </summary>
    [Fact]
    public void An_Overlapping_Upload_Keeps_The_Clock_Running_Until_The_Last_Volume_Ends()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 2, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        tracker.BeginItem("a");
        tracker.BeginItem("b"); // a has not finished when b starts; the two streams overlap

        var aProgress = tracker.ItemProgress();
        var bProgress = tracker.ItemProgress();

        now += 1_000;
        aProgress.Report(1 << 20); // a sent 1 MB, taking 1 second
        tracker.EndItem("a", 0);   // a finishes, but b is still in flight — the clock must not stop

        now += 1_000;
        bProgress.Report(1 << 20); // b sent another 1 MB, taking 1 second. If a's finish had stopped the clock,
                                    // this tick would land on the same instant as the previous one and the measured speed would be 0.
        tracker.EndItem("b", 0);   // the last stream finishes; only now does the clock really stop

        // 2 MB crossed the wire in 2 seconds ≈ 1 MB/s. Had the clock been stopped early, the second tick would collide
        // with the first one's timestamp, spanMs would compute 0 and the speed readout would become 0 — exactly the
        // illusion the IsEmpty check is there to prevent.
        var afterB = seen[^1];
        Assert.InRange(afterB.BytesPerSecond, 900_000L, 1_150_000L);

        // The clock has stopped: advance the wall clock 5 seconds (far past the scale at which the speed window evicts
        // old samples) and tick the heartbeat by hand. If EndItem("b") failed to really stop the clock, those 5 idle
        // seconds would sneak into the denominator and dilute the speed readout, possibly down to 0.
        var countAfterB = seen.Count;
        now += 5_000;
        tracker.Tick();

        // With the clock frozen, Tick() should return early and publish nothing — asserting "no new snapshot" directly
        // is blunter than comparing two BytesPerSecond values that ought to be equal: the latter is only an indirect
        // corollary of "nothing was published" (nothing published ⇒ seen[^1] is still afterB itself ⇒ the two values
        // are trivially equal), and the reader has to reason backwards to see what the assertion guards against.
        Assert.Equal(countAfterB, seen.Count);
    }
}
