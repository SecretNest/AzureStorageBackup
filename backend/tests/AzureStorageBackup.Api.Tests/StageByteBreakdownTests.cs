using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The byte breakdown inside progress. On a long run the questions a user needs answered are "how much is left, how
/// much is packed but not yet shipped, how much has actually landed in the cloud", and those three **must not
/// overlap** — count the same bytes twice and have them fail to add up, and the line is worse than no line at all.
/// <para>
/// Completion also switched from item count to source bytes: an item may be one 100 GB single file, or a pack of
/// several hundred 5 KB files, and counting by item treats the two as equally heavy.
/// </para>
/// </summary>
public sealed class StageByteBreakdownTests
{
    private static (StageTracker Tracker, List<StageProgress> Seen) Rig(
        int total = 0, Func<long>? stagedBytes = null, Func<int>? stagedFiles = null)
    {
        var seen = new List<StageProgress>();
        return (
            new StageTracker("Uploading", total, seen.Add, speedWhileInFlight: true, stagedBytes, stagedFiles),
            seen);
    }

    /// <summary>
    /// Transferred counts only streams that **finished**. While an in-flight stream is half sent its bytes belong to
    /// "in flight", not "transferred" — the transferred figure in the UI has to answer "how much is safely in the cloud".
    /// </summary>
    [Fact]
    public void Transferred_Counts_Only_Finished_Flows()
    {
        var (tracker, seen) = Rig();

        tracker.BeginItem("data/aaa.001", "photos/a.bin", 1000);
        tracker.ItemProgress("data/aaa.001").Report(400);
        tracker.Complete();
        Assert.Equal(0, seen[^1].TransferredBytes);   // still in flight; not one byte counts as "transferred"

        tracker.EndItem("data/aaa.001", 0);
        tracker.Complete();
        Assert.Equal(400, seen[^1].TransferredBytes); // only counted once it finished
    }

    /// <summary>
    /// The waiting figure is read off the pool itself rather than assembled from the queues feeding it. Whether an
    /// archive is parked in the hand-off channel or already claimed by an uploader makes no difference to it — that
    /// distinction is ownership, an implementation detail, and the two entries it used to produce read as the same
    /// thing counted twice while between them counting neither the volumes an uploader owns but has not started.
    /// </summary>
    [Fact]
    public void Waiting_Is_The_Pool_Minus_What_Is_On_The_Wire()
    {
        var pool = 10_000L;
        var files = 10;
        var (tracker, seen) = Rig(stagedBytes: () => pool, stagedFiles: () => files);

        // Nine volumes lying on the disk untouched, one on the wire. Who owns which is not this figure's business.
        tracker.BeginItem("d.001", "photos/a.bin", 1_000, owner: "data/a", staged: true);
        tracker.ItemProgress("d.001").Report(400);
        tracker.Complete();

        Assert.Equal(9_000, seen[^1].WaitingToUploadBytes);   // the in-flight volume comes out whole, not by what it has sent
        Assert.Equal(9, seen[^1].WaitingToUploadVolumes);

        // That volume finishes and leaves the pool: one file fewer on the disk, and nothing on the wire.
        tracker.EndItem("d.001", 0);
        pool = 9_000;
        files = 9;
        tracker.Complete();

        Assert.Equal(9_000, seen[^1].WaitingToUploadBytes);
        Assert.Equal(9, seen[^1].WaitingToUploadVolumes);
    }

    /// <summary>
    /// An in-flight volume comes out of the waiting figure **whole**, not by the part it has already sent. Its file
    /// really does lie in the pool in full until the transfer completes, but the entry claims nothing of it is moving,
    /// and the unsent tail is already on screen as that stream's own sent/total. Subtracting only the sent part is what
    /// put the same bytes in two entries at once.
    /// </summary>
    [Fact]
    public void An_In_Flight_Volume_Comes_Out_Whole()
    {
        var (tracker, seen) = Rig(stagedBytes: () => 5_000, stagedFiles: () => 2);

        tracker.BeginItem("d.001", "photos/a.bin", 3_000, owner: "data/a", staged: true);
        tracker.Complete();
        Assert.Equal(2_000, seen[^1].WaitingToUploadBytes);
        Assert.Equal(1, seen[^1].WaitingToUploadVolumes);

        // Half of it goes out. The waiting figure must not move — those bytes were never in it.
        tracker.ItemProgress("d.001").Report(1_500);
        tracker.Complete();
        Assert.Equal(2_000, seen[^1].WaitingToUploadBytes);
        Assert.Equal(1, seen[^1].WaitingToUploadVolumes);
    }

    /// <summary>
    /// Only volumes that came out of the pool may be subtracted from it. The raw in-place route uploads the user's own
    /// file — never staged, never charged — so subtracting it would make both waiting columns under-report for as long
    /// as that transfer runs, and on a large raw file that is the whole upload.
    /// </summary>
    [Fact]
    public void A_Raw_In_Place_Upload_Is_Not_Subtracted_From_The_Pool()
    {
        var (tracker, seen) = Rig(stagedBytes: () => 5_000, stagedFiles: () => 2);

        tracker.BeginItem("data/raw", "movies/big.mp4", 40_000, owner: "data/raw", staged: false);
        tracker.Complete();

        Assert.Equal(5_000, seen[^1].WaitingToUploadBytes);   // the pool is untouched by it
        Assert.Equal(2, seen[^1].WaitingToUploadVolumes);
    }

    /// <summary>
    /// The object count and the byte count answer different questions and must be free to disagree: a dedup hit, a
    /// resume hit and a raw in-place item all queue owning no archive at all. On a store-only run the channel can hold
    /// the whole dataset while the pool holds nothing, and reporting bytes proportional to the count would turn that
    /// into a temp disk about to burst.
    /// </summary>
    [Fact]
    public void A_Deep_Queue_Can_Be_Worth_No_Bytes()
    {
        var (tracker, seen) = Rig(stagedBytes: () => 0, stagedFiles: () => 0);

        for (var i = 0; i < 5_000; i++)
            tracker.EnterHandoff(HandoffQueue.Upload);   // dedup/resume hits and raw in-place items: nothing in the pool
        tracker.Complete();

        Assert.Equal(5_000, seen[^1].WaitingToUploadObjects);
        Assert.Equal(0, seen[^1].WaitingToUploadBytes);
        Assert.Equal(0, seen[^1].WaitingToUploadVolumes);
    }

    /// <summary>
    /// The object count subtracts **distinct owners** in flight, not in-flight streams. One object can hold several
    /// volumes on the wire at once (the per-item window is the gate's capacity plus one), and subtracting per stream
    /// would strike the same object off as many times as it has volumes moving, driving the count to 0 while objects
    /// were plainly still waiting.
    /// </summary>
    [Fact]
    public void The_Object_Count_Subtracts_Owners_Not_Streams()
    {
        var (tracker, seen) = Rig(stagedBytes: () => 0, stagedFiles: () => 0);

        // Three objects in hand, past staging; one of them has three volumes on the wire.
        for (var i = 0; i < 3; i++)
            tracker.BeginWork();
        tracker.BeginItem("d.001", "a", 10, owner: "data/a", staged: true);
        tracker.BeginItem("d.002", "a", 10, owner: "data/a", staged: true);
        tracker.BeginItem("d.003", "a", 10, owner: "data/a", staged: true);
        tracker.Complete();

        Assert.Equal(2, seen[^1].WaitingToUploadObjects);   // 3 in hand − 1 owner on the wire, not − 3 streams
    }

    /// <summary>Every in-flight row must carry "who, how big, how much sent" — the label is the source file path, not the content-addressed blob name.</summary>
    [Fact]
    public void In_Flight_Carries_Label_Size_And_Progress()
    {
        var (tracker, seen) = Rig();

        tracker.BeginItem("data/9f2a3b7c.001", "photos/2024/IMG_0042.mov", 2000);
        tracker.ItemProgress("data/9f2a3b7c.001").Report(500);
        tracker.Complete();

        var flow = Assert.Single(seen[^1].ActiveItems);
        Assert.Equal("photos/2024/IMG_0042.mov", flow.Label);
        Assert.Equal(500, flow.Sent);
        Assert.Equal(2000, flow.Total);
        Assert.Equal(25, flow.Percent);
    }

    /// <summary>With the label omitted it falls back to the key, matching the old behaviour (the restore/verify paths have no source path to give yet).</summary>
    [Fact]
    public void A_Flow_Without_A_Label_Falls_Back_To_Its_Key()
    {
        var (tracker, seen) = Rig();

        tracker.BeginItem("packs/p0001.7z");
        tracker.Complete();

        Assert.Equal("packs/p0001.7z", Assert.Single(seen[^1].ActiveItems).Label);
    }

    /// <summary>
    /// Completion is computed on source bytes. While the total is not yet settled (diff is still pushing work into
    /// the queue) it must be null — the denominator is still growing, so the percentage would shoot up and fall back.
    /// </summary>
    [Fact]
    public void Work_Percent_Waits_Until_The_Total_Is_Settled()
    {
        var (tracker, seen) = Rig();

        tracker.Enqueue(work: 800);
        tracker.Enqueue(work: 200);
        tracker.Advance(0, work: 500);
        tracker.Complete();
        Assert.Null(seen[^1].WorkPercent);   // item total not settled → the denominator can still grow

        tracker.SetTotal(2);                  // diff wraps up; the total is fixed from here on
        tracker.Complete();
        Assert.Equal(50, seen[^1].WorkPercent);
        Assert.Equal(1000, seen[^1].WorkTotal);
        Assert.Equal(500, seen[^1].WorkDone);
        Assert.Equal(500, seen[^1].WorkRemaining);
    }

    /// <summary>
    /// By bytes and by item count give **different** answers, which is exactly why we switched to bytes: one 100 GB
    /// item plus one 1 KB item, finish the small one, and it is 50% by item count but still practically 0 by bytes.
    /// </summary>
    [Fact]
    public void Byte_Percent_Does_Not_Follow_Item_Percent()
    {
        var (tracker, seen) = Rig(total: 2);

        tracker.Enqueue(work: 100_000_000_000);
        tracker.Enqueue(work: 1_000);
        tracker.Advance(0, work: 1_000);      // the small one is through
        tracker.Complete();

        Assert.Equal(50, seen[^1].Percent);   // by item count: half
        Assert.Equal(0, seen[^1].WorkPercent); // by bytes: barely moved
    }

    /// <summary>
    /// The download side can declare the total transfer size up front (the index records each volume's size); the
    /// upload side cannot — the size is only known once compression is done. A missing denominator must be 0 rather
    /// than an undersized number: computing a percentage from that runs inflated all the way, then sticks at 100%.
    /// </summary>
    [Fact]
    public void Transfer_Total_Is_Only_Reported_When_Declared()
    {
        var (tracker, seen) = Rig();

        tracker.Enqueue(work: 1000);                    // upload side: declares source bytes only
        tracker.Complete();
        Assert.Equal(0, seen[^1].TransferTotal);

        tracker.Enqueue(work: 500, transfer: 120);      // download side: declares both
        tracker.Enqueue(work: 500, transfer: 80);
        tracker.Complete();
        Assert.Equal(200, seen[^1].TransferTotal);
    }

    /// <summary>
    /// "diff has run ahead of the upload and started spilling work to disk" has to be visible, and the **first** one
    /// must publish immediately — that instant is the whole reason this readout exists, and squeezing it into the
    /// throttle window makes the UI go quiet for a while and then blurt out a big number.
    /// Later updates go through the throttle as usual: it is the same number growing, none worth interrupting for.
    /// </summary>
    [Fact]
    public void First_Spilled_Item_Is_Published_Immediately()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 100, seen.Add);

        tracker.Touch("photos/a.bin");
        tracker.Advance(0);
        Assert.Equal(0, seen[^1].SpilledItems);

        tracker.SetSpilled(1);
        Assert.Equal(1, seen[^1].SpilledItems);

        // The 0 → non-zero transition has already happened; from here it is the same number growing, and no single
        // update is worth breaking the throttle for. We do not assert "the next tick has not been published" — that
        // would bet on the throttle window's length. What we guard is a different thing: once anything forces a
        // publish (the stage wrap-up does), it must carry the latest value rather than stay at 1.
        tracker.SetSpilled(4096);
        tracker.Complete();
        Assert.Equal(4096, seen[^1].SpilledItems);
    }

    /// <summary>
    /// The upload side's "transferred" is booked per **item**, not per volume — because it is read alongside the
    /// original bytes, which are also settled per item. A big item splits into many volumes, and when the first few
    /// finish those bytes **really are in the cloud** (per-volume accumulation does not over-report), but the original
    /// bytes only jump once the whole item completes. Numerator per volume, denominator per item: two truthful numbers
    /// that cannot form a readable ratio — the "X uploaded (N% of original)" in the UI structurally overshoots 100%
    /// (measured 112%, falling back to 99% once that item completed); the bigger the file the worse it gets, and it
    /// has nothing to do with the compression ratio.
    /// </summary>
    [Fact]
    public void Uploaded_Never_Runs_Ahead_Of_The_Original_Bytes_It_Is_Compared_With()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);   // upload side declares: transferred bytes are taken over by the item-level reading

        // A 10 GB item split into 4 volumes, 8 GB after compression. The first 3 volumes are through.
        tracker.Enqueue(10_000);
        foreach (var (vol, size) in new[] { ("d.001", 2_000L), ("d.002", 2_000L), ("d.003", 2_000L) })
        {
            tracker.BeginItem(vol, "photos/big.bin", size);
            tracker.ItemProgress(vol).Report(size);
            tracker.EndItem(vol, 0);
        }
        tracker.Complete();

        // Those 6 GB really are in the cloud, but the item has not settled yet (WorkDone is still 0). Reporting it
        // now means numerator without denominator — exactly where the 112% came from.
        Assert.Equal(0, seen[^1].WorkDone);
        Assert.Equal(0, seen[^1].TransferredBytes);

        // The last volume lands and the whole item settles: both numbers land at the same instant, and only now does the ratio mean anything.
        tracker.BeginItem("d.004", "photos/big.bin", 2_000);
        tracker.ItemProgress("d.004").Report(2_000);
        tracker.EndItem("d.004", 0);
        tracker.Advance(0, 10_000);
        tracker.SetTransferred(8_000);
        tracker.Complete();

        Assert.Equal(10_000, seen[^1].WorkDone);
        Assert.Equal(8_000, seen[^1].TransferredBytes);
        Assert.True(seen[^1].TransferredBytes <= seen[^1].WorkDone, "transferred must not run ahead of the original bytes it is compared with");
    }

    /// <summary>
    /// The item-level reading is an **absolute** value, not a delta: it comes from the run's "only booked once the
    /// whole item is through" ledger (<c>RunState.UploadedBytes</c>), the same source as the "uploaded this run" figure
    /// in the completion log, so UI and log agree. It also comes immune to two biases inherent in per-volume
    /// accumulation — retransmitted bytes (DeltaProgress treats a rewind as "starting over", which is right for speed,
    /// but the cloud still holds one copy) and if-missing hitting an existing blob (not one byte on the wire).
    /// </summary>
    [Fact]
    public void The_Item_Level_Reading_Overrides_Per_Volume_Accumulation()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);

        // The same volume died halfway and was redone: 1500 bytes crossed the wire, only 1000 landed in the cloud.
        tracker.BeginItem("d.001", "a.bin", 1000);
        tracker.ItemProgress("d.001").Report(500);
        tracker.ItemProgress("d.001").Report(1000);   // cumulative rewind = retransmit; DeltaProgress treats it as starting over
        tracker.EndItem("d.001", 0);
        tracker.Advance(0, 4000);
        tracker.SetTransferred(1000);                 // the item-level ledger only counts the copy that actually landed in the cloud
        tracker.Complete();

        Assert.Equal(1000, seen[^1].TransferredBytes);
        // The speed ledger **still** includes the retransmit — those bytes really did cross the wire again, and current wire speed wants exactly that.
        Assert.Equal(1500, seen[^1].Bytes);
    }

    /// <summary>
    /// Booking per item squeezes "finished volumes" out of uploaded, but those bytes **really are in the cloud** and
    /// must not simply vanish from the UI. They land in a column of their own, folded into uploaded and zeroed when the item completes.
    /// </summary>
    [Fact]
    public void Volumes_Already_On_The_Cloud_Are_Shown_While_Their_Item_Is_Unfinished()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);

        // One item split into two volumes, 8000 after compression. The first volume is through.
        tracker.BeginUpload("data/d");
        tracker.BeginItem("d.001", "photos/big.bin", 5_000, "data/d");
        tracker.ItemProgress("d.001").Report(5_000);
        tracker.EndItem("d.001", 0);
        tracker.Complete();

        Assert.Equal(0, seen[^1].TransferredBytes);          // the item is not complete, so it cannot enter this ledger
        Assert.Equal(5_000, seen[^1].UnfinishedItemBytes);   // but it is already in the cloud, so it has to stay visible

        tracker.BeginItem("d.002", "photos/big.bin", 3_000, "data/d");
        tracker.ItemProgress("d.002").Report(3_000);
        tracker.EndItem("d.002", 0);
        tracker.ConfirmUpload("data/d");
        tracker.EndUpload("data/d");
        tracker.Advance(0, 10_000);
        tracker.SetTransferred(8_000);
        tracker.Complete();

        Assert.Equal(8_000, seen[^1].TransferredBytes);
        Assert.Equal(0, seen[^1].UnfinishedItemBytes);   // zeroed once folded in; the whole row disappears from the UI
    }

    /// <summary>
    /// With several items in flight, one completing **must not** zero this column — that would wipe out volumes other
    /// items have already sent. The ledger is conservation-based: add when a volume finishes, subtract by uploaded's
    /// delta when an item settles, and that delta is exactly all the volumes of the item just archived, so there is no
    /// need to know which volume belongs to which item.
    /// </summary>
    [Fact]
    public void One_Item_Finishing_Does_Not_Wipe_Another_Items_Uploaded_Volumes()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);

        void Volume(string owner, string name, string label, long size)
        {
            tracker.BeginItem(name, label, size, owner);
            tracker.ItemProgress(name).Report(size);
            tracker.EndItem(name, 0);
        }

        tracker.BeginUpload("data/a");
        tracker.BeginUpload("data/b");
        Volume("data/a", "a.001", "a.bin", 500);    // A's first half
        Volume("data/b", "b.001", "b.bin", 1_000);  // B's first half (concurrent)
        Volume("data/a", "a.002", "a.bin", 500);    // A is complete
        tracker.ConfirmUpload("data/a");
        tracker.EndUpload("data/a");
        tracker.Advance(0, 900);
        tracker.SetTransferred(1_000);    // A settles: 1000 = A's two volumes
        tracker.Complete();

        Assert.Equal(1_000, seen[^1].TransferredBytes);
        Assert.Equal(1_000, seen[^1].UnfinishedItemBytes);  // what remains is exactly B's already-sent volume, no collateral damage

        Volume("data/b", "b.002", "b.bin", 1_000);
        tracker.ConfirmUpload("data/b");
        tracker.EndUpload("data/b");
        tracker.Advance(0, 2_500);
        tracker.SetTransferred(3_000);
        tracker.Complete();

        Assert.Equal(3_000, seen[^1].TransferredBytes);
        Assert.Equal(0, seen[^1].UnfinishedItemBytes);
    }

    /// <summary>
    /// When if-missing hits an existing blob not one byte goes on the wire and the item-level ledger does not book it
    /// — so this column must not add either, or it can never subtract back to 0 and the UI carries an "uploaded" figure
    /// that never existed. On a rerun after an interruption these hits happen in swathes.
    /// </summary>
    [Fact]
    public void A_Skipped_Blob_Never_Enters_The_Unfinished_Column()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);

        tracker.BeginUpload("data/d");
        tracker.BeginItem("d.001", "a.bin", 5_000, "data/d");   // declares 5000, but not one byte is sent
        tracker.EndItem("d.001", 0);
        tracker.Complete();

        Assert.Equal(0, seen[^1].UnfinishedItemBytes);
    }

    /// <summary>
    /// A family sends a few volumes, then the whole item dies and the retry succeeds — the discarded attempt's volumes
    /// must not linger in this column.
    /// <para>
    /// This ledger used to be an add-only scalar, subtracted by uploaded's delta when an item settled. The two sides do
    /// not balance on the failure path: the first attempt's sent volumes were added, while the item-level ledger only
    /// deducts once for the successful attempt, and the difference hangs on the screen forever — measured at 2 GB over
    /// one 3 TB backup run, at a moment when not a byte was being transferred. It is now booked per **family**: an
    /// attempt the cloud never confirmed is wiped out entirely and the retry starts from zero.
    /// </para>
    /// </summary>
    [Fact]
    public void Volumes_From_A_Discarded_Attempt_Do_Not_Linger()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);

        void Volume(string owner, string name, long size)
        {
            tracker.BeginItem(name, "photos/big.bin", size, owner);
            tracker.ItemProgress(name).Report(size);
            tracker.EndItem(name, 0);
        }

        // First attempt: two of the three volumes went before it died. No ConfirmUpload — the cloud never confirmed the family.
        tracker.BeginUpload("data/abc");
        Volume("data/abc", "data/abc.001", 1_000);
        Volume("data/abc", "data/abc.002", 1_000);
        tracker.EndUpload("data/abc");
        tracker.Complete();

        Assert.Equal(0, seen[^1].UnfinishedItemBytes);

        // Second attempt: all three volumes land, the cloud confirms, and the item-level settle folds them into uploaded.
        tracker.BeginUpload("data/abc");
        Volume("data/abc", "data/abc.001", 1_000);
        Volume("data/abc", "data/abc.002", 1_000);
        Volume("data/abc", "data/abc.003", 1_000);
        tracker.ConfirmUpload("data/abc");
        tracker.EndUpload("data/abc");
        tracker.Advance(0, 5_000);
        tracker.SetTransferred(3_000);
        tracker.Complete();

        Assert.Equal(3_000, seen[^1].TransferredBytes);
        Assert.Equal(0, seen[^1].UnfinishedItemBytes);   // cleanly zero, no residue from the two discarded volumes
    }

    /// <summary>
    /// Discarding one family wipes **only** its own row: other families transferring concurrently must not lose a
    /// single byte of the volumes already in the cloud. A scalar ledger cannot do this — it does not know which volume
    /// belongs to which family and can only add and subtract in bulk.
    /// </summary>
    [Fact]
    public void Discarding_One_Family_Leaves_The_Others_Untouched()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);

        void Volume(string owner, string name, long size)
        {
            tracker.BeginItem(name, owner, size, owner);
            tracker.ItemProgress(name).Report(size);
            tracker.EndItem(name, 0);
        }

        tracker.BeginUpload("data/aaa");
        tracker.BeginUpload("data/bbb");
        Volume("data/aaa", "data/aaa.001", 500);
        Volume("data/bbb", "data/bbb.001", 1_000);   // B runs concurrently with A

        tracker.EndUpload("data/aaa");               // A died, never confirmed
        tracker.Complete();

        Assert.Equal(1_000, seen[^1].UnfinishedItemBytes);   // only B's volume is left, untouched by the fallout
    }

    /// <summary>
    /// An archive enters the pool the moment compression ends (backpressure wants "how much is on disk right now",
    /// and booking it a second late can blow out the temp disk), but at that instant it still has to go through
    /// post-compression re-verification, and a multi-volume one must first clear leftover volumes in the cloud — not
    /// one volume is cleared to go. Counting that stretch as "ready to upload" over-promises: if re-verification finds
    /// a member changed during compression, the whole archive is thrown away and recompressed, and not a byte is ever transferred.
    /// </summary>
    [Fact]
    public void Bytes_Still_Being_Verified_Are_Not_Ready_To_Upload()
    {
        long pool = 0;
        var files = 0;
        var (tracker, seen) = Rig(stagedBytes: () => pool, stagedFiles: () => files);

        pool = 100_000;                       // a pack finished compressing; the output hit the disk and entered the backpressure ledger
        files = 4;
        tracker.BeginChecking(100_000, 4);    // but it is re-verifying member by member
        tracker.Complete();

        Assert.Equal(0, seen[^1].WaitingToUploadBytes);     // not "ready to send"
        // Both units come out together, or the two waiting columns contradict each other — the bytes would be gone
        // while their volumes stayed behind.
        Assert.Equal(0, seen[^1].WaitingToUploadVolumes);
        Assert.Equal(100_000, seen[^1].CheckingBytes);      // but "still being checked"

        tracker.EndChecking(100_000, 4);
        tracker.Complete();

        Assert.Equal(100_000, seen[^1].WaitingToUploadBytes);   // only counts once it has been checked
        Assert.Equal(4, seen[^1].WaitingToUploadVolumes);
        Assert.Equal(0, seen[^1].CheckingBytes);
    }

    /// <summary>
    /// The checking stretches where no archive exists yet (a single file's dedup pre-screen, a pack's per-member stat
    /// **before** compression) carry no bytes: what they check is the source file, and the pool holds not one byte yet.
    /// The item-count column still counts them as usual.
    /// </summary>
    [Fact]
    public void Checking_Before_Anything_Is_Staged_Moves_No_Bytes()
    {
        long pool = 0;
        var (tracker, seen) = Rig(stagedBytes: () => pool);

        tracker.BeginChecking();   // dedup pre-screen: read the whole source file to hash it, no archive involved
        tracker.Complete();

        Assert.Equal(0, seen[^1].CheckingBytes);
        Assert.Equal(1, seen[^1].Checking);   // the item-count column carries on as usual
    }

    /// <summary>Stages with no pool (scanning/diffing/local checks) report nothing waiting to upload, in either unit, and that entry disappears from the UI entirely.</summary>
    [Fact]
    public void Stages_Without_A_Pool_Report_Nothing_Waiting()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 10, seen.Add);

        tracker.Advance(100);
        tracker.Complete();

        Assert.Equal(0, seen[^1].WaitingToUploadBytes);
        Assert.Equal(0, seen[^1].WaitingToUploadVolumes);
    }
}
