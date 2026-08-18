using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The stretch after compression ends and before bytes start moving used to be completely invisible in the UI: the
/// item is not in <c>preparing</c> (that only counts whoever holds the compress lock), not in <c>queued</c> (that
/// only counts what is unclaimed or queued on the compress lock), and not in <c>uploading</c> (that counts in-flight
/// **volumes**). <see cref="StageTracker"/> had been tracking <c>_inUpload</c> all along, but the snapshot publisher
/// never read it.
/// <para>
/// The consequence was concrete: an item stalled in this stretch for minutes while the screen showed
/// <c>5,345 of 6,378 objects · nothing on the wire right now · 1 preparing · 1,031 queued</c> —
/// the three numbers add up to 6,377, the missing one is the stalled item, and not one column on screen mentions it.
/// The operator could only discover it existed by lining up three screenshots and doing the subtraction.
/// </para>
/// <para>
/// So both things have to hold: the item ledger must balance; and it must be able to say **what it is waiting on** —
/// the first uploader of the same content in the same batch, the global upload gate, or the cloud's response; the
/// three call for completely different handling.
/// </para>
/// </summary>
public sealed class UploadWaitVisibilityTests
{
    /// <summary>
    /// The item ledger has to balance: done + compressing + queued + in the upload leg = total. This is the only
    /// equation an operator has for judging "did some work vanish into thin air", so the numbers on screen must add up.
    /// </summary>
    [Fact]
    public void Counts_Add_Up_While_An_Item_Sits_Between_Compression_And_The_Wire()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 3, seen.Add, speedWhileInFlight: true);

        // Three items enqueued: one compressed and fully through, one holding the compress lock, one compressed and waiting to upload.
        tracker.Enqueue();
        tracker.Enqueue();
        tracker.Enqueue();

        tracker.Advance(100);          // first item: done

        tracker.BeginWork();           // second item: claimed → into the staging leg → got the compress lock
        tracker.BeginStaging();
        tracker.BeginPacking();

        tracker.BeginWork();           // third item: claimed → compressed and out of staging → in the upload leg, no volume in flight yet
        tracker.BeginStaging();
        tracker.EndStaging();
        tracker.BeginUpload("data/x");

        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(1, s.Processed);
        Assert.Equal(1, s.Preparing);
        Assert.Equal(1, s.Uploading);   // ← this column used not to be published at all
        Assert.Equal(3, s.Processed + s.Preparing + s.Queued + s.Uploading);
    }

    /// <summary>In-flight volumes must not make an item already in the upload leg count twice — the ledger would overshoot again.</summary>
    [Fact]
    public void An_Item_With_Volumes_On_The_Wire_Is_Still_One_Item()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true);

        tracker.Enqueue();
        tracker.BeginWork();
        tracker.BeginUpload("data/x");
        // One item can have several volumes in flight at once (MaxParallelPerItem) — it is still one item.
        tracker.BeginItem("data/abc.002", "photo.raw (2/9)", 1024);
        tracker.BeginItem("data/abc.003", "photo.raw (3/9)", 1024);
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(1, s.Uploading);
        Assert.Equal(2, s.ActiveItems.Count);
        Assert.Equal(1, s.Processed + s.Preparing + s.Queued + s.Uploading);
    }

    [Theory]
    [InlineData(UploadWait.Peer)]
    [InlineData(UploadWait.Slot)]
    public void The_Reason_An_Item_Is_Waiting_Reaches_The_Snapshot(UploadWait kind)
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true);

        tracker.BeginWait(kind);
        var waiting = seen[^1];
        tracker.EndWait(kind);
        tracker.Complete();

        Assert.Equal(1, waiting.Waiting(kind));
        Assert.Equal(0, seen[^1].Waiting(kind));
    }

    /// <summary>
    /// Entering a wait must be published **on the spot**; it must not be swallowed by the 200ms throttle.
    /// <para>
    /// This is not a nicety: while waiting, this caller produces no further events, and the heartbeat only runs while
    /// a stream is transferring (<see cref="StageTracker.Tick"/> returns immediately when the virtual clock is frozen).
    /// Zero streams on the wire + one swallowed publish = the UI frozen on a stale snapshot until the wait ends —
    /// precisely the "motionless for minutes" this round is fixing.
    /// </para>
    /// </summary>
    [Fact]
    public void Entering_A_Wait_Is_Published_Immediately_Even_Inside_The_Throttle_Window()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true);

        tracker.Advance(1);            // just published, so the throttle window is open
        var before = seen.Count;

        tracker.BeginWait(UploadWait.Slot);

        Assert.True(seen.Count > before, "entering a wait must publish immediately, not wait out the throttle");
        Assert.Equal(1, seen[^1].Waiting(UploadWait.Slot));
    }

    /// <summary>
    /// Part of "in the upload leg" is not waiting to start transferring at all, but reading the disk to check: a
    /// single file's dedup pre-screen reads the whole file to compute the three-segment hash, a pack <c>Stat</c>s every
    /// member both before and after compression (rehashing the changed ones in full), and an encrypted multi-volume
    /// upload lists the cloud first to clear leftover volumes. On a NAS each of these can run for tens of seconds.
    /// <para>
    /// What is split out is **display**, not the ledger: all of these happen after leaving the staging leg and before
    /// any in-flight volume is registered, so <c>checking ⊆ uploading</c> and the item-count identity needs not one
    /// word changed.
    /// </para>
    /// </summary>
    [Fact]
    public void Local_Checking_Work_Is_Told_Apart_From_Items_About_To_Upload()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 2, seen.Add, speedWhileInFlight: true);

        tracker.Enqueue();
        tracker.Enqueue();

        // First item: compressed and out of the staging leg, re-Stat'ing member by member — pushing no bytes, waiting on nothing.
        tracker.BeginWork();
        tracker.BeginStaging();
        tracker.EndStaging();
        tracker.BeginChecking();

        // Second item: also in the upload leg, and this one really is heading for the wire.
        tracker.BeginWork();
        tracker.BeginStaging();
        tracker.EndStaging();
        tracker.BeginUpload("data/x");

        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(1, s.Checking);
        Assert.Equal(2, s.Uploading);   // both items are in the upload leg; checking is a breakdown of one of them
        Assert.Equal(2, s.Processed + s.Preparing + s.Queued + s.Uploading);
    }

    /// <summary>
    /// Both entering and leaving this stretch must be published **on the spot**, for reasons word for word identical
    /// to <see cref="Entering_A_Wait_Is_Published_Immediately_Even_Inside_The_Throttle_Window"/>:
    /// while checking, this caller produces not one event, and the heartbeat only runs while a stream is transferring.
    /// A publish swallowed by the 200ms throttle gets no later compensation and the UI freezes on a stale snapshot —
    /// which is exactly the tens of seconds this column exists to explain, so swallowing it makes adding it pointless.
    /// </summary>
    [Fact]
    public void Entering_And_Leaving_Checking_Is_Published_Immediately_Even_Inside_The_Throttle_Window()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true);

        tracker.Advance(1);            // just published, so the throttle window is open
        var beforeBegin = seen.Count;

        tracker.BeginChecking();
        Assert.True(seen.Count > beforeBegin, "entering local checking must publish immediately");
        Assert.Equal(1, seen[^1].Checking);

        var beforeEnd = seen.Count;
        tracker.EndChecking();
        Assert.True(seen.Count > beforeEnd, "leaving local checking must publish immediately");
        Assert.Equal(0, seen[^1].Checking);
    }

    /// <summary>
    /// All four registration sites pair up inside <c>finally</c>, but the throwing path still has to guarantee this
    /// column can get back to zero — <c>BeginPacking</c> is exactly where this project fell over before (incremented
    /// without a pair, leaving <c>preparing</c> stuck at an inflated number for the rest of the run); for where the
    /// pairing idiom came from see <see cref="StagingArea"/>.
    /// </summary>
    [Fact]
    public void Checking_Never_Goes_Negative_Or_Sticks_High()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true);

        tracker.BeginChecking();
        tracker.EndChecking();
        tracker.EndChecking();   // one release too many (should not happen, but clamping beats showing a negative in the UI)
        tracker.Complete();

        Assert.Equal(0, seen[^1].Checking);
    }

    /// <summary>
    /// Only a full gate counts as "waiting for a slot". Normally a slot is acquired instantly, and flagging that would
    /// add a gratuitous forced publish per volume — a big item has thousands of volumes, so that is thousands of them.
    /// </summary>
    [Fact]
    public async Task Waiting_On_The_Upload_Slot_Is_Only_Reported_When_The_Gate_Is_Actually_Full()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 2, seen.Add, speedWhileInFlight: true);
        var gate = new VolumeUploadGate(1);
        var scope = new VolumeUploadScope(gate, tracker, maxParallelPerItem: 5);

        // Gate is empty: acquiring a slot must not produce any "waiting" reading.
        await scope.RunAsync("data/a.001", _ => Task.CompletedTask, CancellationToken.None);
        Assert.All(seen, s => Assert.Equal(0, s.Waiting(UploadWait.Slot)));

        // Gate is full: this one must be reported, or the UI is back to "nothing on the wire, and no word on what it is waiting for".
        await gate.AcquireAsync(0, 0, CancellationToken.None);
        var blocked = scope.RunAsync("data/b.001", _ => Task.CompletedTask, CancellationToken.None);

        // Let it run until it blocks on gate.WaitAsync.
        for (var i = 0; i < 100 && seen[^1].Waiting(UploadWait.Slot) == 0; i++)
            await Task.Delay(10);

        Assert.Equal(1, seen[^1].Waiting(UploadWait.Slot));

        gate.Release();
        await blocked;
        tracker.Complete();
        Assert.Equal(0, seen[^1].Waiting(UploadWait.Slot));
    }

    /// <summary>
    /// An item parked in one of the pipeline's hand-off queues has not entered the upload leg, and must not be
    /// reported as if it had.
    /// <para>
    /// <c>uploading = items in hand − items in staging</c> was written when one worker owned an item end to end:
    /// back then "in hand and not in staging" really did mean "past compression, on its way to the wire". Splitting
    /// the run into prober → compressor → uploaders added two states that satisfy the same subtraction while being
    /// neither: parked in <c>probedQueue</c> waiting for the single compressor, and parked in <c>stagedQueue</c>
    /// waiting for an uploader. The latter queue is unbounded for entries that own no archive — a dedup hit, a
    /// resume hit, a raw in-place item — so on a store-only workload the compressor can pile the whole dataset into
    /// it while the uploaders trickle.
    /// </para>
    /// <para>
    /// What that looked like on screen: <c>24 objects starting upload</c> climbing all run and never coming back
    /// down, while not one byte was on the wire. They are not starting upload; they are queued behind a stage.
    /// </para>
    /// </summary>
    [Fact]
    public void Items_Parked_In_A_Handoff_Queue_Are_Not_Counted_As_Entering_Upload()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 4, seen.Add, speedWhileInFlight: true);
        for (var i = 0; i < 4; i++)
            tracker.Enqueue();

        // Two probed and parked waiting for the compressor.
        tracker.BeginWork();
        tracker.EnterHandoff(HandoffQueue.Compression);
        tracker.BeginWork();
        tracker.EnterHandoff(HandoffQueue.Compression);

        // One compressed (or a dedup hit that owns no archive) and parked waiting for an uploader.
        tracker.BeginWork();
        tracker.EnterHandoff(HandoffQueue.Upload);

        // One genuinely in the upload leg: out of staging, no volume on the wire yet.
        tracker.BeginWork();
        tracker.BeginStaging();
        tracker.EndStaging();
        tracker.BeginUpload("data/x");

        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(2, s.AwaitingCompression);
        Assert.Equal(1, s.AwaitingUpload);
        Assert.Equal(1, s.Uploading);   // ← used to be 4: the three parked items were folded in here
        Assert.Equal(
            4,
            s.Processed + s.Preparing + s.Queued + s.WaitingOnArchive
                + s.AwaitingCompression + s.AwaitingUpload + s.Uploading);
    }

    /// <summary>
    /// Leaving a hand-off queue must bring the column back down — the whole complaint was a number that only ever
    /// grew. Over-releasing clamps at zero rather than showing a negative, the same defence
    /// <see cref="Checking_Never_Goes_Negative_Or_Sticks_High"/> covers for its own column.
    /// <para>
    /// The clock is driven by hand because entering a hand-off queue deliberately does <b>not</b> force a publish
    /// (see <see cref="StageTracker.EnterHandoff"/>: it is the densest event in the run). So the reading only becomes
    /// visible on the next publish past the throttle window, and stepping the clock is how the test reaches it
    /// without sleeping.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(HandoffQueue.Compression)]
    [InlineData(HandoffQueue.Upload)]
    public void Leaving_A_Handoff_Queue_Releases_The_Item(HandoffQueue queue)
    {
        var seen = new List<StageProgress>();
        var now = 0L;
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        tracker.Enqueue();
        tracker.BeginWork();
        tracker.EnterHandoff(queue);
        now += 1_000;
        tracker.Touch(null);   // any publish past the throttle window will do
        Assert.Equal(1, seen[^1].Awaiting(queue));
        Assert.Equal(0, seen[^1].Uploading);

        tracker.LeaveHandoff(queue);
        tracker.LeaveHandoff(queue);   // one release too many: clamp, do not show a negative
        tracker.Complete();

        Assert.Equal(0, seen[^1].Awaiting(queue));
        Assert.Equal(1, seen[^1].Uploading);
    }
}
