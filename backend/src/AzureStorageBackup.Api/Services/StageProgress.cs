using System.Collections.Concurrent;
using System.Diagnostics;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// One stream currently in flight. <paramref name="Label"/> is the human-facing name — during upload it is the **source file path**,
/// not the blob name: blobs are content-addressed (and with encryption on it is post-HMAC gibberish),
/// and <c>data/9f2a3b7c…001</c> means nothing to the person staring at the screen. A pack holds hundreds of files, too many to list,
/// so we report the pack id and the member count.
/// </summary>
/// <param name="Sent">Bytes of this stream that have already crossed the wire.</param>
/// <param name="Total">Total bytes of this stream; 0 = unknown (the download path does not know until the response headers arrive).</param>
public sealed record ActiveTransfer(string Label, long Sent, long Total)
{
    public int? Percent => Total > 0 ? (int)Math.Min(100, 100L * Sent / Total) : null;
}

/// <summary>
/// Where a work item can stall after it is packed but before its bytes actually hit the wire. The two are handled
/// completely differently, so they must be reported apart: one vague "waiting" fuses distinct ailments into a single symptom.
/// </summary>
public enum UploadWait
{
    /// <summary>Waiting on the first uploader of the same content in the same batch (reservation coordination). A given piece of content
    /// can only be settled by one uploader; latecomers hang on its completion signal — the wait lasts that item's entire upload.</summary>
    Peer,

    /// <summary>Waiting for a slot in the global upload gate. Only appears when other volumes have taken every slot; when the gate is free you grab one instantly and nothing is reported.</summary>
    Slot,
}

/// <summary>
/// The two channels the upload run hands work across (prober → compressor → uploaders). An item parked in one of them
/// is claimed but idle: no thread is doing anything to it, it is waiting for the next stage to have room.
/// </summary>
public enum HandoffQueue
{
    /// <summary>Probed, waiting for the single compressor (<c>probedQueue</c>).</summary>
    Compression,

    /// <summary>Compressed — or a dedup/resume hit, or a raw in-place item, none of which own an archive at all —
    /// waiting for an uploader (<c>stagedQueue</c>).</summary>
    Upload,
}

/// <summary>
/// What a stage is currently doing. Backup/restore/check share one shape; only the stage names differ.
/// <para>
/// Why it exists: before this, the UI reported a stage exactly once, when it was **entered**. Diffing on a first backup
/// reads every file end to end to hash it — 1 TB of data on a 100 MB/s disk is three hours — showing a frozen 0% the whole time,
/// with no way to tell real work from a hang (and that FIFO bug did in fact hang for real).
/// </para>
/// </summary>
public sealed record StageProgress(
    string Stage,
    int Processed,
    /// <summary>0 = total unknown (e.g. the scan has not finished, so we have no idea how many files there are).</summary>
    int Total,
    long Bytes,
    /// <summary>The one item being processed right now (serial stages).</summary>
    string? CurrentItem,
    /// <summary>The several items being processed concurrently (upload/download stages).</summary>
    IReadOnlyList<ActiveTransfer> ActiveItems,
    long BytesPerSecond,
    /// <summary>Items doing local CPU work, neither queued nor producing transfer bytes. Each stage means something different by it:
    /// upload = holding the compression lock and producing volume files; restore/verify = download finished, now decompressing/hashing.
    /// This stretch can last tens of seconds (a 100 MB pack through 7z -mx9 is the compressing, unpacking a pack of the same size is the decompressing), and until now it was
    /// completely invisible in the UI: not in <see cref="ActiveItems"/>, producing no bytes, so even the speed window was empty.
    /// <para>
    /// The boundaries differ per stage: the upload stage uses that global compression lock inside <c>StagingArea</c>, so this number is only ever
    /// 0 or 1, while the worker pool is much larger (<c>UploadConcurrency + 1</c>); the extra threads exist so that packed
    /// items can each grab an upload stream, not to compress in parallel — they sit idle behind the lock and those items count as <see cref="Queued"/>.
    /// Restore/verify reuse the same pair of methods for this phase, but there is no global lock there and each group decompresses/hashes on its own,
    /// so the number can reach <c>DownloadConcurrency</c>, not 0/1.
    /// </para></summary>
    int Preparing = 0,
    /// <summary>Items nobody has picked up yet — only what is sitting in the queue. Items already picked up and idling behind the archive lock are **not**
    /// here; they have their own column (<see cref="WaitingOnArchive"/>), and the reasoning lives there.</summary>
    int Queued = 0,
    /// <summary>Seconds remaining, computed by <see cref="StageTracker"/> from "this stage's whole-run average progress";
    /// null when the stage declares no workload or has not finished a single item yet, in which case we fall back to the rough current-speed estimate below.</summary>
    double? EtaSeconds = null,
    /// <summary>Total **source-side** bytes declared by this stage (before compression). It grows during upload — diff enqueues as it decides,
    /// so this number keeps climbing until diff is done. 0 = the stage declares no workload.</summary>
    long WorkTotal = 0,
    /// <summary>Of those, the source-side bytes fully finished. **In-flight excluded**: an item is written off only once the whole item is done.</summary>
    long WorkDone = 0,
    /// <summary>Bytes of finished items actually pushed over the wire (after compression). Again **excluding in-flight** —
    /// that is exactly the difference from <see cref="Bytes"/>: that one adds as it transfers, is used for speed, and includes the part still in flight.</summary>
    long TransferredBytes = 0,
    /// <summary>
    /// Bytes that have **landed safely in the cloud while their owning item has not been written off yet** (after compression). A big item is cut into many volumes;
    /// when the first few volumes finish, those bytes really are in the cloud, but the item as a whole is not done, so they can neither enter
    /// <see cref="TransferredBytes"/> (that ledger counts per item, which is what makes it line up with the per-item
    /// <see cref="WorkDone"/>) nor still be in <see cref="StagedBytes"/> (the pool releases volume by volume).
    /// Without this field those bytes simply vanish from the UI, and the tens of minutes of a large-file upload look like nothing is happening.
    /// <para>When the item completes it is folded into TransferredBytes and reset to zero; 0 = no such half-finished item, and the UI hides the whole section.</para>
    /// </summary>
    long UnfinishedItemBytes = 0,
    /// <summary>
    /// Bytes in the staging pool that **have not been shipped yet** (after compression): the total size of every file in the pool, minus the part of
    /// the in-flight streams already sent. It rises when compression runs ahead of upload and falls back when upload catches up — this number puts
    /// "is compression or the network faster" right in your face.
    /// <para>
    /// What is subtracted is the in-flight **sent** bytes, not whole volumes: those volumes really are still lying in the pool in full (per-volume release deletes only after the transfer),
    /// and only a slice of them has shipped. Without the subtraction the same bytes get counted twice, here and in <see cref="ActiveItems"/>.
    /// </para>
    /// </summary>
    long StagedBytes = 0,
    /// <summary>
    /// How many bytes this stage has to push over the wire in total (after compression). 0 = unknown.
    /// <para>
    /// Only the download side can fill it in: which objects to pull and how big each one is are all recorded in the index. The upload side cannot — the size is known only after packing,
    /// so before the transfer starts the number does not exist. Old indexes missing volume sizes report 0 as well: better to show nothing than to hand out a too-small denominator,
    /// which would make the percentage run high the whole way and then sit stuck at 100%.
    /// </para>
    /// </summary>
    long TransferTotal = 0,
    /// <summary>
    /// Items this stage has piled onto disk waiting for the downstream to digest them (cumulative, only grows).
    /// <para>
    /// Diffing decides orders of magnitude faster than compress-and-upload (deciding one unchanged file costs a single stat), so diff inevitably floods the queue.
    /// Back then the write side would be blocked and the UI could only say "waiting for upload to catch up"; now the write side never stops and the surplus work
    /// lands in temp files (see <c>DiffWorkQueue</c>), so this number replaces that sentence — it says the same thing
    /// (how far ahead diff is), but as a quantity, and because of it diff can run to completion, which is what makes the upload's remaining time computable.
    /// </para>
    /// </summary>
    long SpilledItems = 0,
    /// <summary>
    /// **Items** that have entered the upload phase: everything past the staging phase. From here on an item either has volumes in flight, or is stuck in one of
    /// the phases <see cref="UploadWait"/> describes, or is reading from disk to check (<see cref="Checking"/>).
    /// <para>
    /// This and <see cref="ActiveItems"/> are two different units and cannot substitute for each other: that one holds **volumes**, and one item can have
    /// several volumes in flight at once, or none at all (while stuck). Without this column, an item that is "packed but not transferring a single byte"
    /// belongs to no column on screen — <c>processed + preparing + queued</c> adds up to less than the total, and what is missing
    /// is exactly the stuck item, discoverable only by lining up several screenshots and doing the subtraction.
    /// </para>
    /// </summary>
    int Uploading = 0,
    /// <summary>Of those, the items stuck on a same-batch reservation (<see cref="UploadWait.Peer"/>).</summary>
    int WaitingOnPeer = 0,
    /// <summary>Of those, the **volumes** stuck on the global upload gate (<see cref="UploadWait.Slot"/>).
    /// The gate queues per volume, so this one number's unit differs from <see cref="WaitingOnPeer"/>'s.</summary>
    int WaitingOnSlot = 0,
    /// <summary>
    /// Of those, the items **reading from disk to check**, pushing no bytes and waiting on nothing: single-file dedup pre-screening reads the whole file
    /// once to compute the three-segment hash, a pack needs a per-member <c>Stat</c> both before and after compression (changed members get fully re-read and re-hashed),
    /// and an encrypted multi-volume upload lists the cloud first to clear leftover volumes. On a NAS every one of these can run for tens of seconds.
    /// <para>
    /// It is a **subdivision** of <see cref="Uploading"/>, not a new phase: they all happen after leaving staging and before any in-flight volume is
    /// registered, so <c>Checking ≤ Uploading</c> and that item-count identity needs not a single character changed. The UI
    /// must subtract it out of "starting upload" — report one item in two columns and the books gain a phantom entry.
    /// </para>
    /// <para>
    /// The reason it gets its own column is the same one that added <see cref="Uploading"/> back then: these phases emit not one progress event,
    /// and the heartbeat only runs while a stream is transferring, so the screen shows a stone-still "1 object starting upload" for tens of seconds —
    /// neither starting nor uploading.
    /// </para>
    /// </summary>
    int Checking = 0,
    /// <summary>
    /// Items already picked up by a worker thread and idling behind the **archive lock**: only after taking the lock does an item get to produce its own volume files
    /// (compress, or merely pack when store-only, or merely copy when raw — all three paths take the same lock).
    /// <para>
    /// That lock is global: <see cref="StagingArea"/> is a singleton, so production does not run concurrently across backups either. A thread of one backup
    /// can therefore spend the entire stretch queued behind a lock held by **another backup**, while <see cref="Preparing"/> counts only our own lock holder —
    /// which is 0 at that moment. Folded into <see cref="Queued"/> (as it used to be), the screen is left with ten thousand "queued" entries and
    /// no column able to say "this backup is blocked by another run".
    /// </para>
    /// <para>
    /// Once split apart, telling them apart is free and we no longer have to expose the lock holder: <c>preparing=1</c> + someone waiting = the lock is in our own
    /// hands, normal queueing; <c>preparing=0</c> + someone waiting = the lock is held by another run.
    /// </para>
    /// <para>
    /// The item-count identity therefore gains a term:
    /// <c>Processed + Preparing + Queued + WaitingOnArchive + AwaitingCompression + AwaitingUpload + Uploading ≡ Total</c>.
    /// </para>
    /// </summary>
    int WaitingOnArchive = 0,
    /// <summary>
    /// Bytes already packed onto disk but **not yet cleared to travel**: stuck in the post-compression re-verify or the clear-leftover-cloud-volumes phase
    /// (the byte side of <see cref="Checking"/>). Already subtracted out of <see cref="StagedBytes"/>, so the two columns never overlap.
    /// <para>
    /// The reason for splitting it out is the same one that split out <see cref="Checking"/>. Output enters the backpressure ledger the moment it is packed (recording it a second
    /// late can blow out the temp disk), but at that moment it still has to pass the re-verify — and if the re-verify finds that a member changed during compression,
    /// this archive gets **thrown away whole and repacked**, with not one byte transferred. Calling that "ready to upload" is overpromising.
    /// </para>
    /// </summary>
    long CheckingBytes = 0,
    /// <summary>
    /// Items probed and parked in the hand-off channel waiting for the single compressor
    /// (<see cref="HandoffQueue.Compression"/>). Claimed, but nothing is being done to them.
    /// <para>
    /// Its own column because it is neither of its neighbours. <see cref="Queued"/> means "nobody has picked this up",
    /// which is no longer true — the prober read it and settled its content identity. <see cref="WaitingOnArchive"/>
    /// means "inside the staging area, queued on the global compression lock", which is also not it: an item here has
    /// not reached the staging area, because the compressor is one worker and processes one item at a time.
    /// Folded into <see cref="Uploading"/> (as it used to be) it becomes the UI's "N objects starting upload" — a
    /// number that climbs to the channel's ceiling and never comes down, describing items that are not uploading and
    /// not starting.
    /// </para>
    /// </summary>
    int AwaitingCompression = 0,
    /// <summary>
    /// Items parked in the hand-off channel waiting for an uploader (<see cref="HandoffQueue.Upload"/>): compressed,
    /// or a dedup/resume hit, or a raw in-place item.
    /// <para>
    /// This one has no ceiling to climb to. That channel is deliberately unbounded — what owns an archive is already
    /// bounded in bytes by the staging pool, which is the limit the operator configured — but the three entry kinds
    /// listed above own no archive and so are bounded by nothing. On a store-only workload (the media library the
    /// DontCompress rule exists for) the compressor's only limiter is disk read speed, so it can queue the whole
    /// dataset while the uploaders trickle. Every one of those items used to be reported as "starting upload".
    /// </para>
    /// </summary>
    int AwaitingUpload = 0)
{
    /// <summary>How many are stuck right now in one particular kind of wait.</summary>
    public int Waiting(UploadWait kind) => kind switch
    {
        UploadWait.Peer => WaitingOnPeer,
        UploadWait.Slot => WaitingOnSlot,
        _ => 0,
    };

    /// <summary>How many items are parked in one particular hand-off channel.</summary>
    public int Awaiting(HandoffQueue queue) => queue switch
    {
        HandoffQueue.Compression => AwaitingCompression,
        HandoffQueue.Upload => AwaitingUpload,
        _ => 0,
    };

    /// <summary>Source-side bytes not yet started (before compression).</summary>
    public long WorkRemaining => Math.Max(0, WorkTotal - WorkDone);

    /// <summary>
    /// Completion measured in **source-side bytes** (before compression). The upload stage should prefer it over <see cref="Percent"/>:
    /// one item can be a single 100 GB file or a pack of several hundred 5 KB files, and counting by item treats them
    /// as equally heavy — the UI races to 90% and then sits on the last item for half an hour.
    /// <para>
    /// Only produced once the total is settled (<see cref="Total"/> &gt; 0). The upload workload is enqueued by diff as it decides,
    /// so the denominator is still growing until diff finishes, and a percentage computed then shoots up and falls back. The item count gates on the same signal.
    /// </para>
    /// </summary>
    public int? WorkPercent => Total > 0 && WorkTotal > 0
        ? (int)Math.Min(100, 100L * WorkDone / WorkTotal)
        : null;

    public int? Percent => Total > 0 ? (int)Math.Min(100, 100L * Processed / Total) : null;

    /// <summary>
    /// Estimated time remaining. Prefers <see cref="EtaSeconds"/> — it extrapolates from "elapsed time × remaining work ÷ completed work",
    /// which is equivalent to using **whole-run average** throughput rather than the speed of this instant.
    /// <para>
    /// Why not compute it from <see cref="BytesPerSecond"/>: that is a 10-second rolling window, measuring "how fast the wire is right now".
    /// A backup's actual rhythm is "pack for tens of seconds → transfer for a few", and during compression the window holds not one byte, the speed drops to 0,
    /// the remaining time disappears entirely, and once packing ends a very small number suddenly pops out — what the user sees is "jittery". Yet those tens of seconds of compression
    /// are just as much a part of the remaining time, and the whole-run average accounts for them naturally.
    /// </para>
    /// <para>
    /// The fallback formula (when the stage declares no workload) is unchanged: rough-estimate from "average bytes per item × remaining items ÷ current speed".
    /// </para>
    /// </summary>
    public TimeSpan? EstimatedRemaining =>
        EtaSeconds is { } s
            ? TimeSpan.FromSeconds(s)
            : Total > 0 && Processed > 0 && Processed < Total && BytesPerSecond > 0 && Bytes > 0
                ? TimeSpan.FromSeconds((double)Bytes / Processed * (Total - Processed) / BytesPerSecond)
                : null;
}

/// <summary>
/// Accumulation and **throttling** of stage progress.
/// <para>
/// Throttling is a requirement, not an optimization: reporting a million files one by one costs a million object allocations, and the human eye cannot take in more than a few updates a second.
/// But when a stage wraps up it must force out a final state, or the progress will sit at 99% forever — this project has already had
/// that kind of "one step short" bug (see the onItem counting round).
/// </para>
/// </summary>
/// <param name="speedWhileInFlight">Whether the speed denominator counts only the time with "at least one in-flight item open".
/// Set true for stages that register in-flight items (upload/restore/verify): their rhythm is "pack for tens of seconds → transfer for a few",
/// and what the wall clock as denominator measures is neither the transfer speed nor the wall-clock throughput. Stages that never call <see cref="BeginItem"/>
/// (scan/diff/local check) must keep it false — the virtual clock never advances for them and the speed would be permanently 0.</param>
/// <param name="stagedBytes">
/// A reading of the staging pool's current occupancy (post-compression bytes). The upload stage passes this run's staging reservation; other stages have no pool and omit it.
/// Read fresh on every publish — it seesaws with compression and upload, so a cached value is permanently one beat behind.
/// </param>
public sealed class StageTracker(
    string stage, int total, Action<StageProgress> publish, bool speedWhileInFlight = false,
    Func<long>? stagedBytes = null) : IDisposable
{
    private const int ThrottleMs = 200;
    private const int SpeedWindowMs = 10_000;
    private const int HeartbeatMs = 1_000;
    // Hard cap on the sample queue, guarding against the growth where the "evict by time" half of the condition never holds while the virtual clock is frozen:
    // while frozen every sample carries the same Ms, tick - _samples.Peek().Ms is always 0, and nothing can be evicted at all.
    // Under 200ms throttling that is at most 5 samples/sec, so a normally full 10-second window holds about 50 — 256 is 5x that headroom,
    // this clause is unreachable in normal operation and only fires during a genuinely long freeze (dense publishing inside an active segment while the virtual
    // clock itself does not move) to sweep out stale leftovers, instead of relying on the luck of "how often do we publish".
    // Before touching this number (or lowering ThrottleMs), know what it costs when it fires: during a freeze all samples share the same Ms,
    // and count-based eviction drops from the head, so the first ones dropped are exactly the **pre-freeze** samples carrying a real spanMs; drop too many and
    // oldest.Ms == tick, spanMs == 0, and the speed collapses from "keep the last reading seen on the wire" to 0.
    private const int MaxSamples = 256;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    /// <summary>An in-flight stream: the key is the blob/volume name (unique), the value carries the human-facing label, bytes sent and total bytes.</summary>
    private sealed class InFlight(string label, long total, string? owner)
    {
        public string Label { get; } = label;
        public long Total { get; } = total;

        /// <summary>Which family (blobRef) this volume belongs to. <see cref="EndItem"/> uses it to book the bytes into the right ledger
        /// instead of piling them onto one global scalar — see <see cref="StageTracker.BeginUpload"/>.
        /// Null on the download side and on paths that do not go through the upload gate: those paths never touch this ledger.</summary>
        public string? Owner { get; } = owner;
        private long _sent;
        public long Sent => Interlocked.Read(ref _sent);

        /// <summary>How far this attempt has pushed. It is an **assignment**, not an accumulation — when a retry restarts the whole volume it rewinds along with it,
        /// which is what keeps this stream on the same footing as its own <see cref="Total"/>. Accumulate instead and a retransmit pushes the numerator past the denominator:
        /// we measured <c>200.0 MB / 100.0 MB</c> in the field (one retry), and that volume then transferred just fine.</summary>
        public void Set(long attemptCumulative) => Interlocked.Exchange(ref _sent, attemptCumulative);
    }

    private readonly ConcurrentDictionary<string, InFlight> _active = new(StringComparer.Ordinal);
    // (milliseconds, cumulative bytes) samples, used to compute the speed over the recent window. When file sizes vary wildly,
    // the whole-run average drifts away from the actual current speed for long stretches; only a rolling window matches what the user sees.
    private readonly Queue<(long Ms, long Bytes)> _samples = new();
    private readonly Lock _gate = new();

    private int _processed;
    private int _total = total;
    private long _bytes;
    private string? _current;
    private long _lastPublishMs = -ThrottleMs;
    private int _enqueued;
    private int _inWork;
    // **Items** that have entered the "upload" phase. _active.Count is not a substitute: that holds **volumes**,
    // and one item can have several volumes in flight at once, so the subtraction would erase items still compressing (preparing squashed to 0).
    private int _inUpload;
    // Items inside the staging phase, and among them the ones actually holding the compression lock (the latter is only ever 0 or 1, by definition of the lock).
    // They must be tracked separately, not derived from "items in hand - items uploading": that counts threads idling behind the lock as "preparing",
    // and under the default config the UI shows 5 preparing, which looks like five items progressing in parallel when really one is packing and four are idling.
    private int _inStaging;
    private int _inPacking;
    private int _inChecking;
    // Among those, the **bytes** already packed onto disk and stuck in checking. See StageProgress.CheckingBytes: output enters the
    // backpressure ledger the moment it is packed (that ledger wants "how much is on disk right now"), but it still has to pass the re-verify before it may travel, and if the re-verify
    // finds a member changed this archive gets thrown away whole and repacked. Subtracted out of staged, given its own column.
    private long _checkingBytes;
    // Current occupancy of each wait phase, indexed by the UploadWait ordinal. An array rather than one field per phase: callers index by the enum,
    // so adding a wait phase takes only one more enum member and the publish end needs no extra line per phase.
    private readonly int[] _waits = new int[Enum.GetValues<UploadWait>().Length];
    // Items parked in each hand-off channel, indexed by the HandoffQueue ordinal. Same array-per-enum shape as _waits,
    // and for the same reason: the caller indexes by the enum, so a third channel would cost one enum member.
    private readonly int[] _handoffs = new int[Enum.GetValues<HandoffQueue>().Length];
    // The "workload" used for remaining time. A different thing from _bytes: the latter is bytes that actually crossed the wire (post-compression, 0 on a dedup hit),
    // and using it as completion makes the remaining time jump around with the compression ratio and the dedup hit rate. When no stage declares a workload (0),
    // remaining time falls back to extrapolating from item counts.
    private long _totalWork;
    private long _doneWork;
    // Bytes that have landed safely in the cloud / on disk (post-compression). The difference from _bytes: that one adds as it transfers and includes the in-flight part,
    // and is used for speed; this one only counts what finished. On the upload side SetTransferred supplies the authoritative per-**item** reading; the download side accumulates per volume.
    private long _transferred;
    // Whether that authoritative reading has taken over. See SetTransferred.
    private bool _transferredByItem;
    // Bytes already in the cloud whose owning item has not been written off yet, **kept as one entry per family (blobRef)**.
    //
    // This used to be an increment-only scalar, reduced wholesale by the delta of _transferred at per-item write-off. That ledger did not balance on failure paths:
    // a family ships a few volumes and then the whole item falls over; those volumes were already added, while the per-item ledger deducts only once, for the attempt that succeeded,
    // and the difference hangs on the screen forever after (measured: one 3 TB backup accumulated 2 GB while not a single byte was transferring).
    // Clamping to 0 defends against negatives; it does not defend against this direction.
    //
    // Split per family, each entry has a well-defined life: BeginUpload opens it (a retry is a reset) → each volume's EndItem adds to it →
    // ConfirmUpload marks it confirmed by the cloud → the per-item write-off folds it into uploaded and deletes the entry outright; entries never confirmed
    // (failed, cancelled) are voided on the spot in EndUpload. Failing to balance simply cannot happen anymore.
    private readonly Dictionary<string, UnfinishedFamily> _unfinished = new(StringComparer.Ordinal);

    /// <summary>The ledger for one family's volumes that are in the cloud but not yet folded into uploaded.</summary>
    private sealed class UnfinishedFamily
    {
        public long Bytes;
        /// <summary>The cloud has confirmed the whole family transferred and the bytes have entered the per-item ledger (<c>state.AddUploaded</c>),
        /// so it only waits for the next <see cref="SetTransferred"/> to fold it into uploaded.
        /// A family that finishes without the mark = this attempt was voided, and its volumes do not count.</summary>
        public bool Confirmed;
    }
    // How many bytes this stage has to push over the wire in total (post-compression). Only the download side can declare it; the upload side knows only after packing, so it stays 0.
    private long _transferTotal;
    // Number of callers currently stuck on the downstream (>0 when the diff side is blocked by the bounded queue).
    private long _spilled;
    // The moment this stage really started working. The upload stage's tracker is constructed as soon as diff kicks off, and there may be a stretch of
    // idle waiting before the first item shows up; measuring the average speed from construction smears that idling in and stretches the ETA the whole way.
    // -1 = not started yet (stages where nobody calls BeginWork — diff, for one — are uniformly treated as "construction is the start", which is correct).
    private long _workStartMs = -1;

    // The timeline used for speed: it advances only while _active is non-empty (when speedWhileInFlight is true).
    // It freezes during compression, so the samples on both sides of the pause are contiguous within the window — the speed is neither diluted by idling
    // nor subject to "the whole batch of old samples ages out → report 0 on the spot → jump wildly once packing ends".
    private long _activeMs;
    // Start of the current active segment; -1 = not a single stream is open right now.
    private long _activeSince = -1;
    // A timer that runs only inside an active segment. Stopped during compression, it emits not one redundant snapshot.
    private Timer? _heartbeat;
    // Late callbacks arriving after Complete() (already queued on the thread pool, and Dispose cannot call them back) must be voided on the spot —
    // see how it is used in Tick().
    private bool _completed;

    /// <summary>Millisecond time source injected by tests. A 10-second speed window cannot be verified by actually waiting; with injection the whole tracker
    /// is fully deterministic in time. Null in production, which falls through to the internal <see cref="Stopwatch"/>.</summary>
    internal Func<long>? Clock { get; init; }

    private long NowMs() => Clock?.Invoke() ?? _clock.ElapsedMilliseconds;

    /// <summary>The timestamp used for speed. Stages with the switch on use the "only advances while a stream is open" virtual axis; the rest stay on the wall clock.</summary>
    private long SpeedNow(long now) =>
        speedWhileInFlight ? _activeMs + (_activeSince >= 0 ? now - _activeSince : 0) : now;

    /// <summary>Settle the total. Since pipelining, the upload stage's total **grows as it runs** (diff is still stuffing work into
    /// the queue), and until it settles we can only report 0 = unknown — report a still-growing denominator and the percentage races to 100 and falls back.</summary>
    public void SetTotal(int value)
    {
        lock (_gate)
        {
            _total = value;
            PublishIfDue(force: true);
        }
    }

    /// <summary>One item processed: count +1 and add the bytes read. **Does not touch** the current item — that is maintained by <see cref="Touch"/>,
    /// which keeps it parked on the last path entered, so that when things stall you can see exactly where.</summary>
    /// <param name="bytes">Bytes counted toward the speed and toward <c>Bytes</c>.</param>
    /// <param name="work">Workload counted toward the remaining-time estimate; defaults to the same as <paramref name="bytes"/>.
    /// The two differ in the upload stage: bytes are what actually went up after compression (0 on a dedup hit), while the workload is this item's
    /// original bytes — it must be the same quantity declared at <see cref="Enqueue"/> time, or the remaining work will not reach zero at completion.</param>
    public void Advance(long bytes, long? work = null)
    {
        lock (_gate)
        {
            _processed++;
            _bytes += bytes;
            _doneWork += work ?? bytes;
            PublishIfDue(force: false);
        }
    }

    /// <summary>
    /// "Bytes transferred" is henceforth an authoritative per-**item** reading supplied by the caller (an absolute value, not a delta), and the per-volume
    /// accumulation in <see cref="EndItem"/> steps aside. One call takes over; after that it refreshes as each item is written off.
    /// <para>
    /// The upload side has no choice but to use it, because this number has to be read side by side with the <b>per-item</b> original bytes written off (the UI's
    /// "X uploaded (N% of original)"). With per-volume accumulation, during the tens of minutes a big item spends packing and transferring, the numerator climbs while
    /// the denominator sits stone-still — it only jumps once the whole item completes — so the percentage structurally overshoots 100% (measured 112%, falling back
    /// to 99% after that item completed). The bigger the file the further off it gets, and it has nothing whatsoever to do with the compression ratio.
    /// </para>
    /// <para>
    /// It also fixes two biases inherent to per-volume accumulation: retransmitted bytes are no longer double-counted (<see cref="DeltaProgress"/> treats a rewind
    /// as "start over", which is right for the speed, but the cloud still holds just that one copy), and dedup hits are no longer counted as transferred
    /// (when if-missing runs into an already-existing blob, not one byte went over the wire). The per-item reading has neither problem by construction,
    /// and it shares a source with the "uploaded this run" figure in the completion log, so the UI and the log finally agree.
    /// </para>
    /// </summary>
    public void SetTransferred(long total)
    {
        lock (_gate)
        {
            _transferredByItem = true;
            // Confirmed families are folded into uploaded right here: their bytes are already included in total, both sides update at once,
            // and on screen those bytes slide from the right-hand column into the left-hand one without a flicker.
            //
            // What is deleted is an **entry**, not a number subtracted from a scalar — that is the whole point of rewriting this ledger: families never confirmed
            // (failed, cancelled) are simply not here, having been voided back in EndUpload, so "did we subtract too much or too little" does not arise.
            foreach (var owner in _unfinished.Where(e => e.Value.Confirmed).Select(e => e.Key).ToList())
                _unfinished.Remove(owner);
            _transferred = total;
            PublishIfDue(force: false);
        }
    }

    /// <summary>Move on to the next item (call it **before** processing). It only changes "what is being processed"; it does not count.</summary>
    public void Touch(string? current)
    {
        lock (_gate)
        {
            _current = current;
            PublishIfDue(force: false);
        }
    }

    /// <summary>One item queued. Called single-threaded by the producer side (diff), but concurrently with the consumer side, hence Interlocked.
    /// Do **not** use it to touch <c>_total</c>: that denominator keeps growing until diff wraps up, and a percentage off it races to 100 and falls back.</summary>
    /// <param name="work">This item's workload — its **source-side** bytes, before compression — accumulated into the stage's
    /// total workload, which is what completion and remaining time extrapolate from.
    /// It keeps growing until diff wraps up, so the ETA gates on <c>_total &gt; 0</c> just like the percentage does —
    /// extrapolate from a still-growing denominator and the remaining time shrinks to almost nothing and then springs back.</param>
    /// <param name="transfer">Bytes this item has to push over the wire (after compression). Only the download side can supply it, see
    /// <see cref="StageProgress.TransferTotal"/>.</param>
    public void Enqueue(long work = 0, long transfer = 0)
    {
        Interlocked.Increment(ref _enqueued);
        if (work > 0)
            Interlocked.Add(ref _totalWork, work);
        if (transfer > 0)
            Interlocked.Add(ref _transferTotal, transfer);
    }

    /// <summary>A worker thread picks up an item (from here it counts as "preparing", until <see cref="BeginItem"/> starts pushing bytes).</summary>
    public void BeginWork()
    {
        Interlocked.Increment(ref _inWork);
        // The first item being picked up = this stage really started working; the average speed is measured from here.
        Interlocked.CompareExchange(ref _workStartMs, NowMs(), -1);
        // Work in hand is enough to keep the heartbeat running — it must not wait for a stream to open. The stretches
        // that settle without transferring a byte (a dedup hit, a resume hit, a raw in-place item) would otherwise
        // leave the stage publishing nothing at all, and the UI displaying the snapshot from the last volume that
        // did transfer. See Tick().
        lock (_gate)
            Heartbeat(on: true);
    }

    /// <summary>A worker thread finished an item (call it on both success and failure). Like <see cref="Advance"/>, it **does not count** —
    /// slot counting belongs to Advance alone, and bumping the progress bar here on the side would push it past 100%.</summary>
    public void EndWork()
    {
        // Nothing in hand and nothing on the wire: stop the timer rather than let it publish identical snapshots
        // forever. Both halves are required — an item can finish while other volumes are still transferring, and a
        // stream can close while the stage still holds plenty of work. Complete()/Dispose() are not enough on their
        // own: between the last item and the wrap-up a stage can idle for a long time waiting on the diff.
        if (Interlocked.Decrement(ref _inWork) == 0)
            lock (_gate)
                if (_active.IsEmpty)
                    Heartbeat(on: false);
    }

    /// <summary>
    /// A family of volumes starts uploading (pair it with <see cref="EndUpload"/>). Again, it **does not count**.
    /// <para>
    /// It also opens an empty ledger entry for this family. <b>Opening it is a reset, not an insert</b>: a retry goes through the same <paramref name="owner"/>
    /// (a single file's blobRef is the content hash, and a pack's id is taken outside the retry — both stay unchanged across retries),
    /// so the volumes shipped by the previous attempt are wiped clean here and the second attempt counts from zero.
    /// </para>
    /// </summary>
    /// <param name="owner">This family's blobRef: <c>data/{hash}</c> or <c>packs/{packId}.7z</c>.</param>
    public void BeginUpload(string owner)
    {
        Interlocked.Increment(ref _inUpload);
        lock (_gate)
            _unfinished[owner] = new UnfinishedFamily();
    }

    /// <summary>
    /// The cloud has confirmed this whole family transferred and the bytes have entered the per-item ledger — it only waits for the next <see cref="SetTransferred"/>
    /// to fold it into uploaded. Must be called after <c>state.AddUploaded</c> and before <see cref="EndUpload"/>.
    /// <para>
    /// We do not clear the ledger right here, for the sake of **smoothness** across that gap: between the whole family finishing and the per-item write-off there is still
    /// the index write and the journal write to get through, and clearing now would make those bytes vanish between the two columns for a while even though they really are in the cloud.
    /// </para>
    /// </summary>
    public void ConfirmUpload(string owner)
    {
        lock (_gate)
            if (_unfinished.TryGetValue(owner, out var family))
                family.Confirmed = true;
    }

    /// <summary>This family's processing ends: anything not marked by <see cref="ConfirmUpload"/> is voided on the spot.
    /// Call it in a <c>finally</c> — the normal path and the throwing path use the same line, the only difference being whether it was confirmed.</summary>
    public void EndUpload(string owner)
    {
        Interlocked.Decrement(ref _inUpload);
        lock (_gate)
        {
            if (_unfinished.TryGetValue(owner, out var family) && !family.Confirmed)
                _unfinished.Remove(owner);
        }
    }

    /// <summary>
    /// Start waiting on one phase (pair it with <see cref="EndWait"/>).
    /// <para>
    /// Takes <c>_gate</c> and forces one publish, **not** subject to the 200ms throttle: while waiting, this caller produces no further events,
    /// and the heartbeat only runs while a stream is transferring (see that virtual-clock short-circuit in <see cref="Tick"/>). With zero streams transferring,
    /// a publish swallowed by the throttle gets no compensation later, and the UI freezes on the stale snapshot until the wait ends — which is exactly the few minutes
    /// this column exists to explain. Wait events themselves are not dense (the gate path even tries a non-blocking acquire first), so the cost is negligible.
    /// </para>
    /// </summary>
    public void BeginWait(UploadWait kind)
    {
        Interlocked.Increment(ref _waits[(int)kind]);
        lock (_gate)
            PublishIfDue(force: true);
    }

    public void EndWait(UploadWait kind)
    {
        Interlocked.Decrement(ref _waits[(int)kind]);
        lock (_gate)
            PublishIfDue(force: true);
    }

    /// <summary>An item enters the staging phase — at this moment it is most likely still queueing for the compression lock, so it counts as "queued"
    /// (pair it with <see cref="EndStaging"/>).</summary>
    /// <summary>How many items this stage has spilled to disk in total.
    /// <para>
    /// No throttling: the moment it goes from 0 to non-zero is precisely the start of "diff has run ahead of upload", and holding that back
    /// leaves an inexplicable silence in the UI. Every call after that just overwrites with the same value, and <see cref="PublishIfDue"/>
    /// converges it on its own throttle window.</para></summary>
    public void SetSpilled(long items)
    {
        var previous = Interlocked.Exchange(ref _spilled, items);
        lock (_gate)
            PublishIfDue(force: previous == 0 && items > 0);
    }

    public void BeginStaging() => Interlocked.Increment(ref _inStaging);

    public void EndStaging() => Interlocked.Decrement(ref _inStaging);

    /// <summary>
    /// An item is parked in one of the pipeline's hand-off channels (pair it with <see cref="LeaveHandoff"/>).
    /// Call it <b>before</b> the write that publishes the entry, so the next stage cannot pick it up and leave
    /// before this side has entered it — the count would go negative for the moment in between and clamp to 0,
    /// which on screen is this column flickering.
    /// <para>
    /// <b>No forced publish</b>, unlike <see cref="BeginWait"/> and <see cref="BeginChecking"/>. Those two fire once
    /// per item at most and mark a stretch nothing else reports; these fire on every single entry crossing every
    /// channel, which at the 500,000-item scale this repo measures is the densest event in the run. The column does
    /// not need the immediacy either: while a queue has depth the stage in front of it is working and publishing
    /// on its own, and when the whole run really is idle the throttle window is 200ms.
    /// </para>
    /// </summary>
    public void EnterHandoff(HandoffQueue queue) => Interlocked.Increment(ref _handoffs[(int)queue]);

    /// <summary>The next stage has taken the item out (or a drain discarded it). Must answer every
    /// <see cref="EnterHandoff"/> exactly once — miss one and this column carries an entry that never goes away for
    /// the rest of the run, with <see cref="StageProgress.Uploading"/> under-reporting by the same amount.</summary>
    public void LeaveHandoff(HandoffQueue queue) => Interlocked.Decrement(ref _handoffs[(int)queue]);

    /// <summary>The compression lock is taken and volume files really start being produced (pair it with <see cref="EndPacking"/>).
    /// The UI's "N preparing" in the upload stage counts only this, so by definition of the lock it is always 0 or 1; restore/verify
    /// reuse the same pair of methods to mark the "download finished, now decompressing/hashing" stretch of local CPU work, and there is no global lock there —
    /// several groups decompress at once, so the number can exceed 1.
    /// <para>
    /// Takes <c>_gate</c> and issues one <see cref="PublishIfDue"/>: the moves on either side of this phase (<c>EndItem</c> removing the in-flight item,
    /// then entering this phase) produce no bytes of their own, and without an explicit push here the fact that preparing went from 0 to 1
    /// could only reach the UI when some other call happens to publish next — and right after a download, during decompression/hashing, there is precisely no other
    /// call running, so this beat would stay stuck on the stale snapshot and the UI would freeze until this phase ends, which is exactly the "freeze" it is meant to fix.
    /// The 200ms throttle still applies; not every call really publishes.</para></summary>
    public void BeginPacking()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _inPacking);
            PublishIfDue(force: false);
        }
    }

    public void EndPacking()
    {
        lock (_gate)
        {
            Interlocked.Decrement(ref _inPacking);
            PublishIfDue(force: false);
        }
    }

    /// <summary>
    /// Start a stretch of disk-reading checks (pair it with <see cref="EndChecking"/>): dedup pre-screening's full read and hash, a pack's per-member
    /// <c>Stat</c>, listing leftover cloud volumes before an encrypted multi-volume upload. See <see cref="StageProgress.Checking"/> for the meaning.
    /// <para>
    /// <b>Forced publish</b>, for the same reason as <see cref="BeginWait"/> and unlike <see cref="BeginPacking"/>:
    /// during a check this caller produces not one event, and the heartbeat only runs while a stream is transferring (see that
    /// virtual-clock short-circuit in <see cref="Tick"/>). With zero streams transferring, a publish swallowed by the throttle gets no compensation later, and the UI freezes on the stale snapshot
    /// until this phase ends — and that is exactly the tens of seconds this column exists to explain, so swallowing it makes adding the column pointless.
    /// </para>
    /// <para>
    /// The cost is negligible: registration happens per **item** (once for a single file, once before and once after for a pack), not per volume the way
    /// in-flight registration does — a big item has a thousand volumes, and that is the scale at which you cannot force a publish.
    /// </para>
    /// </summary>
    /// <param name="bytes">Bytes of the archive being checked that are **already on disk**, subtracted out of staged (see
    /// <see cref="StageProgress.CheckingBytes"/>). Just omit it for the phases where the archive does not exist yet (dedup pre-screening's full read of the source file,
    /// a pack's per-member stat **before** compression) — at that point there is not a single byte in the pool.</param>
    public void BeginChecking(long bytes = 0)
    {
        Interlocked.Increment(ref _inChecking);
        if (bytes > 0)
            Interlocked.Add(ref _checkingBytes, bytes);
        lock (_gate)
            PublishIfDue(force: true);
    }

    /// <param name="bytes">Must pass the same number as the paired <see cref="BeginChecking"/> — miss one repayment and
    /// this column carries an entry that never goes away for the rest of the run, with the staged column permanently under-reporting along with it.</param>
    public void EndChecking(long bytes = 0)
    {
        Interlocked.Decrement(ref _inChecking);
        if (bytes > 0)
            Interlocked.Add(ref _checkingBytes, -bytes);
        lock (_gate)
            PublishIfDue(force: true);
    }

    /// <summary>Register an in-flight transfer object. The upload stage registers **volumes** (<c>data/xxx.007</c>),
    /// not items — what the UI's "N uploading" has to answer is "how many streams are on the wire right now".
    /// <para>
    /// The empty→non-empty transition also starts the speed clock: the compression and queueing before it do not enter the speed denominator.
    /// The collection's add/remove was moved inside the lock so that "is it empty" and the clock switch are settled in the same critical section.
    /// </para></summary>
    /// <param name="label">The human-facing name (the upload stage passes the **source file path**, not the content-addressed blob name).
    /// Omitted, it falls back to the key itself, matching the previous behavior.</param>
    /// <param name="totalBytes">Total bytes of this stream; 0 = unknown (a download does not know until the response headers arrive).</param>
    /// <param name="owner">Which family this volume belongs to (<see cref="BeginUpload"/>'s blobRef). Mandatory on the upload side —
    /// it decides which ledger this volume's bytes go into; the download side and paths that skip the upload gate can just omit it.</param>
    public void BeginItem(string item, string? label = null, long totalBytes = 0, string? owner = null)
    {
        lock (_gate)
        {
            if (!_active.TryAdd(item, new InFlight(label ?? item, totalBytes, owner)))
                return;
            if (speedWhileInFlight && _activeSince < 0)
            {
                _activeSince = NowMs();
                Heartbeat(on: true);
            }
        }
    }

    /// <summary>
    /// Build a progress callback to hand to the uploader: it turns "cumulative bytes within this call" into deltas and adds them to the stage's byte count as the transfer runs.
    /// **One per upload item** — the cumulative baseline is per-call, and sharing one instance makes somebody else's progress look like a rewind.
    /// <para>
    /// Items that use it should call <c>EndItem(item, 0)</c> when they end: the bytes were already counted piece by piece during the transfer,
    /// so adding the total again at wrap-up is double counting.
    /// </para>
    /// </summary>
    /// <param name="item">The key matching <see cref="BeginItem"/>: these bytes are booked against that stream's ledger,
    /// which is what lets the UI show "how much this one sent / how big it is". Omitted, it only accumulates the stage total and lands on no particular stream.</param>
    public IProgress<long> ItemProgress(string? item = null) =>
        new DeltaProgress((delta, attemptCumulative) => AddBytes(item, delta, attemptCumulative));

    /// <summary>
    /// Book some bytes. **Two units, two numbers**, deliberately not shared:
    /// <list type="bullet">
    /// <item>The stage total accumulates by **delta** — it is the numerator for the speed, and retransmitted bytes have to count again, because those bytes really did cross
    /// the wire a second time and that is how fast the network is right now.</item>
    /// <item>This stream's own reading is assigned from **this attempt's cumulative value** — it is the numerator of the UI's "how much sent / how big",
    /// on the same footing as the denominator (this volume's nominal size). When a retry restarts the whole volume, it rewinds along with it.</item>
    /// </list>
    /// The two used to share one delta, so a retransmit pushed the numerator past the denominator: we measured
    /// <c>DJI_0032.MP4 (30/36) — 200.0 MB / 100.0 MB · 100%</c> in the field (the percentage clamped at 100 while
    /// the two byte counts plainly contradicted each other), and that volume then transferred just fine.
    /// </summary>
    private void AddBytes(string? item, long delta, long attemptCumulative)
    {
        lock (_gate)
        {
            _bytes += delta;
            if (item is not null && _active.TryGetValue(item, out var flow))
                flow.Set(attemptCumulative);
            PublishIfDue(force: false);
        }
    }

    /// <summary>
    /// The SDK reports a cumulative value within one upload call, and our <see cref="RetryPolicy"/> retries make it restart from 0
    /// (same for multi-volume uploads, where each volume starts from 0 on its own). A rewind is uniformly treated as "start over", and both numbers are handed out together:
    /// the delta (retransmits count as new traffic) and this attempt's cumulative value (retransmits rewind); see <see cref="AddBytes"/> for what each is for.
    /// <para>
    /// With chunked parallel uploads <see cref="Report"/> is called concurrently, hence the lock; the callback is fired inside the lock too,
    /// because "compute" and "book" have to be one transaction — outside the lock two callbacks could arrive out of order and the later one's stale cumulative value
    /// would overwrite the newer one, which on screen is a jump backwards.
    /// </para>
    /// </summary>
    private sealed class DeltaProgress(Action<long, long> onProgress) : IProgress<long>
    {
        private readonly Lock _gate = new();
        private long _last;

        public void Report(long cumulative)
        {
            lock (_gate)
            {
                var delta = cumulative >= _last ? cumulative - _last : cumulative;
                var restarted = cumulative < _last;
                _last = cumulative;
                // Let a **rewind** through even when delta == 0: the moment a retry restarts from 0 the delta is exactly 0,
                // and that is precisely the moment this stream's reading has to rewind; swallow it and the UI stays on the old number.
                if (delta > 0 || restarted)
                    onProgress(delta, cumulative);
            }
        }
    }

    /// <summary>An in-flight item ends: remove it from the in-flight set and add its bytes, **without counting**.
    /// Counting belongs solely to <see cref="Advance"/> — the upload's slot counting has an exact "exactly once" constraint
    /// (a pack may be repacked several times because its members changed, yet it always occupies just one slot in total),
    /// and bumping it here on the side would double count and push the progress bar past 100%.
    /// <para>When the last stream finishes, this active segment's duration is booked and the speed clock stops there until the next stream opens.</para></summary>
    public void EndItem(string item, long bytes)
    {
        lock (_gate)
        {
            if (_active.TryRemove(item, out var flow))
            {
                // This stream is done: move its bytes from "in flight" into "transferred". The UI's "transferred" has to be able to answer
                // "how much has **landed safely in the cloud**", so the in-flight part never counts; only what finished does.
                // When an authoritative per-item reading exists (SetTransferred) this steps aside — **per-volume** accumulation and per-item
                // write-off of workload are out of sync, and the two numbers side by side do not read as a sentence; see SetTransferred for the reasoning.
                // Stepping aside does not mean throwing these bytes away: they really are in the cloud, so they are first booked into **their own family's** ledger
                // and folded in when the whole item completes (see _unfinished). We book the nominal size (Total) rather than Sent: the latter is
                // wherever the last attempt pushed to, stopping halfway on the failure/cancel paths. That difference used to leave drift behind;
                // now a voided family's entry is wiped whole, so neither choice accumulates a balance — booking Total is purely because it is this volume's real size.
                // Sent == 0 means this volume never hit the wire at all (if-missing ran into an existing blob), and the per-item ledger will not count it either,
                // so we do not add it here either.
                if (_transferredByItem)
                {
                    // owner null = the upload side forgot to pass the family name. Better to leave this volume out of the ledger (this column under-reports)
                    // than to conjure an entry nobody owns and nobody can delete — that is exactly the shape of the old drift.
                    if (flow.Owner is { } owner && _unfinished.TryGetValue(owner, out var family))
                        family.Bytes += flow.Sent > 0 ? (flow.Total > 0 ? flow.Total : flow.Sent) : 0;
                }
                else
                    _transferred += flow.Sent + bytes;
                if (speedWhileInFlight && _active.IsEmpty && _activeSince >= 0)
                {
                    _activeMs += NowMs() - _activeSince;
                    _activeSince = -1;
                    // The speed clock stops here regardless; the heartbeat only stops if the stage has nothing left
                    // in hand either. Closing the last stream is precisely when the old code went silent, and the
                    // snapshot it went silent on is the one that stayed on screen — see Tick().
                    if (Volatile.Read(ref _inWork) == 0)
                        Heartbeat(on: false);
                }
            }
            _bytes += bytes;
            PublishIfDue(force: false);
        }
    }

    /// <summary>One heartbeat tick: recompute the speed window and publish. A stalled stream produces no events at all,
    /// and without this the speed would stay frozen at whatever number it read before the stall.</summary>
    internal void Tick()
    {
        lock (_gate)
        {
            // The stage has already wrapped up: the final snapshot published by Complete() is the last thing the UI should see.
            // Dispose() cannot recall a callback already queued or already running, and by the time it reaches this lock Complete()
            // has long since let go — without this guard another snapshot would appear after the final one, breaking the promise that "the last one is the real final state".
            if (_completed)
                return;
            // Nothing on the wire **and** nothing in hand: there is genuinely nothing to say, so do not keep a timer
            // publishing identical snapshots at a stage that is only waiting to be handed its first item.
            //
            // With work in hand this must go through, and that is the whole point of the condition. "No stream open"
            // is not the same as "nothing happening": a dedup hit, a resume hit and a raw in-place item all settle
            // without a byte going over the wire, so a run whose remaining work is mostly hits can spend hours here.
            // Bailing out unconditionally — as this used to — stopped the stage publishing altogether for that whole
            // stretch, and the UI went on displaying the snapshot taken the instant the last volume finished:
            // its queue depths, its byte columns, its "nothing on the wire", all of them a photograph. Measured in
            // the field as `+66.8 MB on the cloud · 24 objects starting upload` motionless for hours, disappearing
            // whenever a transfer started (a live snapshot finally replaced it) and returning to the identical
            // figures when that transfer ended. A frozen line reads as a hang, and hides one.
            //
            // The reason the bail-out existed is real, and it moved rather than disappeared: samples must not enter
            // the speed window while the virtual clock is frozen. PublishIfDue now skips the sample instead — see
            // there.
            if (speedWhileInFlight && _activeSince < 0 && Volatile.Read(ref _inWork) == 0)
                return;
            PublishIfDue(force: false);
        }
    }

    /// <summary>Start/stop the heartbeat along with the active segment. Must be called while holding <c>_gate</c>.
    /// An injected clock = a unit test is driving <see cref="Tick"/> by hand, and not stacking a real timer on top of that is what makes the result deterministic.</summary>
    private void Heartbeat(bool on)
    {
        if (Clock is not null)
            return;
        if (on)
        {
            // The heartbeat runs on a thread-pool timer thread, where no caller can catch what it throws — it lands in the runtime's hands, and
            // .NET's default behavior there is to take down the entire process. After Task 3 this callback hangs off the onProgress passed in by RestoreOrchestrator/
            // BackupChecker, which is the caller's own code, and its chance of failing is not zero.
            // Progress reporting is a nice-to-have side path; better to lose this tick than to drag a running backup/restore/verify down to die with it,
            // so it must be swallowed here — every other path (Advance/Touch/EndItem, etc.) runs on the caller's thread where
            // the exception can propagate back to someone who can handle it, and those places should **not** copy this catch.
            _heartbeat ??= new Timer(_ =>
            {
                try
                {
                    Tick();
                }
                catch
                {
                    // See the comment above: swallowed on purpose, do not let a timer-thread exception reach the process. But having swallowed it we cannot
                    // act as if nothing happened — do nothing and the next tick runs Tick() exactly the same way, publish is most likely
                    // still that broken sink, so the exception happens once a second and is quietly eaten every time, and the whole tracker
                    // spends the rest of its life retrying invisibly, with no trace for anyone to notice that progress reporting died long ago.
                    // Both more extreme options were considered: do nothing = retry unchanged, which is the invisible loop just described;
                    // rethrow = hit .NET's default behavior and take down the entire process, letting a "nice-to-have" side path
                    // kill a running backup/restore/verify, which is worse than swallowing. The middle ground is stopping the timer: a sink that just failed
                    // is most likely still broken, retrying will produce nothing, so it is better to pull it out first — this stops only this tracker's
                    // heartbeat and does not affect the other state-change paths (Advance/Touch/EndItem, etc.), which keep running on the caller's
                    // thread with exceptions still handed to someone who can handle them; those places should not copy this catch.
                    // Note this is not a permanent shutdown: StopHeartbeat sets _heartbeat back to null, and Heartbeat(on:true)
                    // is a `??=`, so the **next active segment** builds a new timer and tries again. That granularity is exactly what we want —
                    // from "once a second" down to "once a segment", which both stops the invisible loop and lets it pick itself back up if the sink recovers.
                    // When Tick() throws, the lock was already released inside its own try block (the lock statement's finally semantics),
                    // so re-taking _gate here is safe and will not self-deadlock.
                    lock (_gate)
                    {
                        StopHeartbeat();
                    }
                }
            }, null, Timeout.Infinite, Timeout.Infinite);
            _heartbeat.Change(HeartbeatMs, HeartbeatMs);
        }
        else
            _heartbeat?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void StopHeartbeat()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
    }

    /// <summary>Wrap up the stage: publish once unconditionally so the progress is settled, and stop the heartbeat.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _current = null;
            // If PublishIfDue throws (say the publish callback itself is broken), the two wrap-up lines below must not be skipped along with it —
            // skip _completed=true and Tick() will treat a stage that "should already have wrapped up" as still alive and keep processing it;
            // skip StopHeartbeat() and the timer stays running as an unowned leak. Both lines are pure in-memory
            // state cleanup and cannot throw a second exception of their own, so one layer of finally is enough to spare them from going down with the ship.
            try
            {
                PublishIfDue(force: true);
            }
            finally
            {
                _completed = true;
                StopHeartbeat();
            }
        }
    }

    /// <summary>This column's reading: the sum over every family not yet folded into uploaded. At most
    /// <c>UploadConcurrency + 1</c> families transfer at once (usually just one after item-age arbitration), so this sum always has a single-digit number of terms.
    /// The caller must hold <c>_gate</c>.</summary>
    private long UnfinishedBytes()
    {
        long sum = 0;
        foreach (var family in _unfinished.Values)
            sum += family.Bytes;
        return sum;
    }

    private void PublishIfDue(bool force)
    {
        var now = NowMs();
        if (!force && now - _lastPublishMs < ThrottleMs)
            return;
        _lastPublishMs = now;

        // Throttling uses the wall clock (it governs "how often to refresh the UI"), the speed uses the virtual axis (it governs "how much transfer time these bytes took").
        var tick = SpeedNow(now);
        // While the virtual clock is frozen the sample is **skipped**, not taken. Every sample during a freeze would
        // carry the same Ms, so the time-based eviction below can never remove it and the queue fills with identical
        // entries until the MaxSamples backstop starts dropping from the head — and the first ones dropped are exactly
        // the pre-freeze samples that carry a real span, which collapses the reading from "the last speed seen on the
        // wire" to 0. It also says nothing: _bytes cannot move with no stream open, so the sample is a duplicate by
        // construction.
        //
        // This is what lets Tick() go on publishing through a freeze (see there). Before, the tick itself was skipped,
        // which protected the window and froze the whole snapshot along with it.
        if (!speedWhileInFlight || _activeSince >= 0)
        {
            _samples.Enqueue((tick, _bytes));
            // Besides time-based eviction (the main path), a hard count-based eviction, as a backstop against any
            // path that manages to publish densely inside one active segment (see the comment on MaxSamples).
            while (_samples.Count > 1 && (tick - _samples.Peek().Ms > SpeedWindowMs || _samples.Count > MaxSamples))
                _samples.Dequeue();
        }

        long speed = 0;
        if (_samples.Count > 1)
        {
            var oldest = _samples.Peek();
            var spanMs = tick - oldest.Ms;
            if (spanMs > 0)
                speed = (_bytes - oldest.Bytes) * 1000 / spanMs;
        }

        // The several counters advance independently, so what we read is a snapshot half a beat out of step — without clamping at 0 the UI would flash negative numbers.
        var inWork = Volatile.Read(ref _inWork);
        var preparing = Math.Max(0, Volatile.Read(ref _inPacking));
        // Not started = still in the queue, picked up by no thread. Those already picked up but queueing for the archive lock are split out of here
        // (waitingOnArchive); see StageProgress.WaitingOnArchive for the reason.
        // Deliberately **not** the "enqueued - done - packing - uploading" subtraction: between the end of packing and the start of the transfer there is real work
        // (a pack re-Stats every member, a single file looks up the dedup map, and a dedup hit does not even upload), and the subtraction would report all of it
        // as "queued" — calling items that are working queued is more misleading than the inflated preparing it replaced.
        // The most time-consuming parts of that work now register as _inChecking and get their own column in the UI (see StageProgress.Checking);
        // the arithmetic here is unchanged — they still belong to uploading, and checking is merely a subdivision of it.
        // Those inside staging that have not got the lock: all queueing behind the archive lock. Its own column, **not** folded into queued —
        // with two backups running concurrently that lock can be in someone else's hands the whole time, and queued cannot say that (see WaitingOnArchive).
        var waitingOnArchive = Math.Max(0, Volatile.Read(ref _inStaging) - preparing);
        var queued = Math.Max(0, Volatile.Read(ref _enqueued) - _processed - inWork);
        // Claimed but idle in a hand-off channel: no thread is doing anything to them, they are waiting for the next
        // stage to have room. They come out of the uploading subtraction below — see StageProgress.AwaitingCompression.
        var awaitingCompression = Math.Max(0, Volatile.Read(ref _handoffs[(int)HandoffQueue.Compression]));
        var awaitingUpload = Math.Max(0, Volatile.Read(ref _handoffs[(int)HandoffQueue.Upload]));

        // In-flight snapshot. Each stream's sent bytes are updated concurrently, so what we take here are readings from a single instant,
        // and the subtraction below uses the same batch of values — reading twice would occasionally make "staged" compute a negative that gets clamped back to 0, which on screen is a jump.
        var inFlight = _active.Values
            .Select(f => new ActiveTransfer(f.Label, f.Sent, f.Total))
            .ToList();
        // Not yet shipped out of the staging pool: pool occupancy − the part of the in-flight streams already sent (those volumes still lie in the pool in full,
        // since per-volume release deletes only after the transfer) − the archives still stuck in checking and not cleared to travel. The three never overlap:
        // checking happens entirely before the first volume takes off, so one archive cannot be hit by both of the latter two subtractions.
        // Without the subtraction the same bytes get counted twice here and in ActiveItems / CheckingBytes.
        var checkingBytes = Math.Max(0, Volatile.Read(ref _checkingBytes));
        var staged = stagedBytes is null
            ? 0
            : Math.Max(0, stagedBytes() - inFlight.Sum(f => f.Sent) - checkingBytes);

        publish(new StageProgress(
            stage, _processed, _total, _bytes, _current, inFlight, speed, preparing, queued,
            Eta(now), Volatile.Read(ref _totalWork), _doneWork, _transferred, UnfinishedBytes(), staged,
            Volatile.Read(ref _transferTotal), Interlocked.Read(ref _spilled),
            // Items that have left the compression/staging phase = items in hand - items still in staging - items
            // parked in a hand-off channel.
            //
            // Deliberately **not** _inUpload (the BeginUpload/EndUpload pair): that only starts counting at UploadStagedBlobAsync,
            // and between the end of packing and getting there lies the reservation coordination — an item can stall there for minutes
            // while not being in _inUpload. Use that as the unit and the item ledger fails to balance exactly when balancing matters most,
            // and a ledger that does not balance is the very reason this column exists. This subtraction folds that gap in as well, so
            // processed + preparing + queued + waitingOnArchive + awaitingCompression + awaitingUpload + uploading ≡ total
            // is an identity, independent of any call site.
            //
            // The two hand-off terms are what the subtraction was missing. It was written when one worker owned an item end to end,
            // where "in hand and not in staging" did mean "past compression, on its way to the wire". Splitting the run into
            // prober → compressor → uploaders added two states that satisfy the same subtraction while being neither, and folding them
            // in here is what put "24 objects starting upload" on screen, climbing all run with nothing on the wire.
            Math.Max(0, inWork - Volatile.Read(ref _inStaging) - awaitingCompression - awaitingUpload),
            Math.Max(0, Volatile.Read(ref _waits[(int)UploadWait.Peer])),
            Math.Max(0, Volatile.Read(ref _waits[(int)UploadWait.Slot])),
            Math.Max(0, Volatile.Read(ref _inChecking)),
            waitingOnArchive,
            checkingBytes,
            awaitingCompression,
            awaitingUpload));
    }

    /// <summary>
    /// Remaining time = elapsed working time × remaining amount ÷ completed amount. That is, extrapolate from **this stage's whole-run average progress**
    /// rather than from the last 10 seconds of network speed — the latter swings between 0 and peak under the "pack for tens of seconds, transfer for a few" rhythm,
    /// while those tens of seconds of compression are just as much part of the remaining time, which the whole-run average accounts for naturally.
    /// <para>
    /// The "amount" prefers the declared workload (upload stage = original bytes); with nobody declaring one, it falls back to item counts.
    /// The upload stage has no choice but to use bytes: one item can be a single 100 GB file or a pack of several hundred 5 KB files,
    /// and extrapolating by item count treats them as equally heavy. Conversely the diff stage is right to use item counts — there, the vast majority of entries pass with a single stat.
    /// </para>
    /// <para>
    /// A known rough edge: the progress of the in-flight item does not count (write-off happens all at once at completion). When only one 100 GB file is left transferring,
    /// the remaining time climbs the whole way and only drops once it finishes. Fixing that means folding in the partial progress of in-flight items, which requires each item's
    /// expected total (known only after packing) — more cost than benefit, so let it be accurate in the normal "many items" case first.
    /// </para>
    /// </summary>
    private double? Eta(long now)
    {
        if (_total <= 0)   // The total is not settled yet (diff is still stuffing work into the queue) — no denominator at all, do not guess
            return null;

        var totalWork = Volatile.Read(ref _totalWork);
        var (total, done) = totalWork > 0 ? (totalWork, _doneWork) : (_total, _processed);
        if (done <= 0 || done >= total)
            return null;

        var startMs = Volatile.Read(ref _workStartMs);
        var elapsedMs = now - (startMs < 0 ? 0 : startMs);
        if (elapsedMs <= 0)
            return null;

        return (double)elapsedMs * (total - done) / done / 1000;
    }

    /// <summary>Stop the heartbeat. <see cref="Complete"/> already did it once when the stage wrapped up; missing it on an exception path does not matter —
    /// all three in-flight registrations call <see cref="EndItem"/> pairwise in a <c>finally</c>, and the heartbeat already stopped the moment the last stream ended.
    /// <para>
    /// As in <see cref="Complete"/>, set <see cref="_completed"/> first: stopping the timer alone cannot block the one tick of callback already queued on
    /// the thread pool that Dispose cannot call back, and leaving that crack open defeats the purpose — <see cref="Tick"/> relies on exactly
    /// this flag, not on whether the timer stopped.
    /// </para></summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _completed = true;
            StopHeartbeat();
        }
    }
}
