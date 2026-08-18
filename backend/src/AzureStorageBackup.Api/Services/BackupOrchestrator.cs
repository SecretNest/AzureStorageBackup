using System.Collections.Concurrent;
using System.Threading.Channels;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Tunable options for the backup engine (ignore / don't-compress / don't-group rules + per-stage options).</summary>
public sealed record BackupEngineOptions
{
    public IgnoreRuleSet Ignore { get; init; } = new([]);
    public IgnoreRuleSet? DontCompress { get; init; }
    public IgnoreRuleSet? DontGroup { get; init; }

    /// <summary>Matches are allowed to be packed across directories (for hash-sharded directory trees; empty = everything packs per directory).</summary>
    public IgnoreRuleSet? CrossDirGroup { get; init; }
    public ScanOptions Scan { get; init; } = new();
    public DiffOptions Diff { get; init; } = new();
    public PlanOptions Plan { get; init; } = new();
    public RetentionPolicy Retention { get; init; } = new();

    /// <summary>Volume size in bytes; null = no splitting (single archive). Large files / large packs become multi-volume blobs (§7).</summary>
    public long? VolumeBytes { get; init; }

    /// <summary>Upload concurrency cap (PRD 3.4, default 5). Compression stays globally serial; only uploads run in parallel.</summary>
    public int UploadConcurrency { get; init; } = 5;

    /// <summary>Network retry/backoff policy for uploads (PRD 4.1).</summary>
    public RetryOptions Upload { get; init; } = new();

    /// <summary>Dead-weight compaction threshold (default 30%, M4 §6).</summary>
    public double DeadWeightThreshold { get; init; } = 0.30;

    /// <summary>Whether to write debug-level logs (includes the file names touched, short retention).</summary>
    public bool VerboseLogging { get; init; }

    /// <summary>When repacking dead weight, whether members missing locally may be filled in by downloading the cloud pack (a per-data-tier switch; false by default for Archive).</summary>
    public bool AllowRepackDownload { get; init; } = true;

    /// <summary>Cap on reprocessing attempts when the same member keeps changing during post-compression re-verification (PRD §5.1, default 5).</summary>
    public int ProcessingMaxAttempts { get; init; } = 5;

    /// <summary>
    /// Whether diffing overlaps with "compress + upload" (on by default). With it on, uploading starts the
    /// moment a verdict comes out, so the network need not wait for every hash to finish; the price is that
    /// the diff's reads and the compressor's reads land on the same disk at the same time. On a NAS with
    /// spinning disks the two read streams can drag each other down enough that the net gain is negative —
    /// in that case turn it off and go back to the old behavior of "decide everything first, then upload".
    /// </summary>
    public bool OverlapDiffAndUpload { get; init; } = true;
}

/// <summary>One backup execution request.</summary>
public sealed record BackupRequest
{
    public required Account Account { get; init; }
    public required string Container { get; init; }
    public required string LocalRoot { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Password { get; init; }
    public AccessTier IndexTier { get; init; } = AccessTier.Hot;
    public AccessTier DataTier { get; init; } = AccessTier.Hot;
    public BackupEngineOptions Options { get; init; } = new();
}

/// <summary>One backup execution result.</summary>
/// <param name="ChangedFiles">Count of Added + Modified items (identically equal to <see cref="NewFiles"/> + <see cref="ModifiedFiles"/>).</param>
/// <param name="ChangedBytes">The **source-side raw** bytes of those files (uncompressed, not deduplicated).</param>
public sealed record BackupRunResult(int Version, int ChangedFiles, long ChangedBytes, int UnreadableFiles)
{
    /// <summary>Number of files the previous version did not have.</summary>
    public int NewFiles { get; init; }

    /// <summary>Number of files whose content changed.</summary>
    public int ModifiedFiles { get; init; }

    /// <summary>Number of files the previous version had and this one does not.</summary>
    public int DeletedFiles { get; init; }

    /// <summary>
    /// The source-side raw size those deleted files had, taken from the previous version's index entries — by now
    /// the files themselves are gone, so the index is the only thing left that knows how big they were. Same unit as
    /// <see cref="ChangedBytes"/>: uncompressed, not deduplicated. Without it, "12 deleted" cannot tell twelve empty
    /// log stubs from twelve disk images.
    /// <para>
    /// **Not the space the cloud gave back.** Older versions still reference that content and it stays in the
    /// container until retention retires them; what was actually freed is <see cref="CleanupReport.FreedBytes"/>.
    /// The two are different quantities and do not add up.
    /// </para>
    /// </summary>
    public long DeletedBytes { get; init; }

    /// <summary>
    /// Bytes this run actually pushed to the cloud (archive size **after** compression/encryption). Content
    /// that hit dedup counts for exactly zero bytes — it never went through the upload step at all. Read it
    /// together with <see cref="ChangedBytes"/> to see how much compression and dedup each saved.
    /// </summary>
    public long UploadedBytes { get; init; }

    /// <summary>What the retention cleanup at the end of the backup deleted (<see cref="CleanupReport.Empty"/> when cleanup did not run).</summary>
    public CleanupReport Cleanup { get; init; } = CleanupReport.Empty;

    /// <summary>
    /// Why the retention cleanup was skipped, or null when it ran. Set when cleanup could not reach the cloud: the
    /// version is committed by then, so the run is a success, but an empty <see cref="Cleanup"/> would otherwise be
    /// indistinguishable from "there was nothing to clean up" — and those two want opposite reactions from whoever
    /// reads the summary.
    /// </summary>
    public string? CleanupSkipped { get; init; }

    /// <summary>The moment this backup started running; the same value written into the version record's <see cref="BackupVersion.StartedAt"/>.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The moment the version was committed; the same value written into the version record's <see cref="BackupVersion.CreatedAt"/>.
    /// This is **not** the moment the run ended: retention cleanup still has to run after the commit. Both the
    /// completion toast and the restore dropdown read this value; letting each take its own clock would write
    /// two different times for the same backup.</summary>
    public DateTimeOffset CompletedAt { get; init; }
}

/// <summary>Backup pipeline stages.</summary>
public enum BackupStage
{
    Scanning,
    Diffing,
    Uploading,
    WritingIndex,
    Finalizing,
    CleaningUp,
    Completed,
}

/// <summary>Progress snapshot (PRD backup design §2: percentage + changed file count/size). Polled by the frontend.</summary>
public sealed record BackupProgress(
    BackupStage Stage, int ChangedFiles, long ChangedBytes, int UploadedItems, int TotalItems)
{
    public int Percent => TotalItems == 0 ? (Stage == BackupStage.Completed ? 100 : 0)
        : (int)Math.Min(100L, 100L * UploadedItems / TotalItems);

    /// <summary>What each currently running stage is doing (which file it is on, how much is done, how fast).
    /// Now that the pipeline overlaps them, Diffing and Uploading run **at the same time**, so this is a list
    /// rather than a single value: report only one of them and the UI cannot see the other one moving.</summary>
    public IReadOnlyList<StageProgress> Details { get; init; } = [];

    /// <summary>The headline detail. Serial stages (scanning, writing the index, …) have exactly one, and this is it.
    /// This single-value field is kept so that callers that "only look at one" (the existing frontend and tests) need not first check whether there is a second.</summary>
    public StageProgress? Detail
    {
        get => Details.Count > 0 ? Details[0] : null;
        init => Details = value is null ? [] : [value];
    }
}

/// <summary>
/// The backup orchestrator (M4 design §4): chains Scan→Diff→Plan→Compress→Upload→WriteIndex→Finalize and produces one new version.
/// Both data blobs and packs are always 7z archives; data blobs are content-addressed by fullHash for dedup.
/// Compression goes through the shared StagingArea (globally non-concurrent + backpressure). Retention cleanup and progress reporting came later.
/// </summary>
public sealed class BackupOrchestrator(
    LocalFileScanner scanner,
    BackupDiffer differ,
    GroupingPlanner planner,
    IFileCompressor compressor,
    IBlobUploader uploader,
    IBlobClientFactory factory,
    IBackupInfoStore store,
    StagingArea staging,
    RetentionCleaner cleaner,
    IFileHasher hasher,
    ILocalIndexCache indexCache,
    TrackedInfoStore trackedInfo,
    INotifier? notifier = null,
    IOperationLog? opLog = null,
    VerboseFileLog? verboseLog = null,
    DiffWorkQueueFactory? spillFactory = null)
{
    /// <summary>
    /// Mutable state for one run: the counters accumulated as it goes, plus this run's pack-id issuer. Passed
    /// down as a parameter rather than made an instance field: the orchestrator is scoped in DI, so no second
    /// backup within one request will share it — but "each run's books are kept on that run's own object"
    /// should be guaranteed by the signature, not hold by accident of how the type happens to be registered.
    /// Several upload consumers touch it concurrently, so both the counters and the id issuer go through Interlocked.
    /// </summary>
    private sealed class RunState(StagingArea.StagingLease staging)
    {
        /// <summary>This run's seat in the staging area: the staging-disk quota is split evenly across the **runs currently in flight**, and seats come and go with runs.</summary>
        public StagingArea.StagingLease Staging => staging;

        private long _uploadedBytes;
        private readonly string _packTag = Guid.NewGuid().ToString("N")[..8];
        private int _packSeq;

        /// <summary>
        /// Issue a new pack id. It must be unique **across runs**: unlike data blobs, packs are not content-addressed —
        /// there is no trace of the content in the name, and "just keep counting up from the highest id in the info file"
        /// re-issues an id whenever the previous run failed. That run already uploaded packs/p0001.7z but never managed
        /// to write the info file, so the next run starts from p0001 again — and that same-numbered pack contains
        /// **a different set of members**. Uploads are if-missing, so the name collision is skipped, yet the index claims
        /// the pack holds this run's members — restore simply cannot find them in that pack, and a batch of files is
        /// silently missing. On the Archive data tier it hides even better: the reason for skipping changes from
        /// "already exists" to BlobArchived, which is still a skip.
        /// <para>
        /// One random prefix per run is enough. The only requirement on a pack id was ever "no duplicates"; nothing
        /// depends on it being contiguous or ordered: PackIdOf only splits off a prefix, dead-weight compaction rewrites
        /// under the same name, and the index records the full name.
        /// </para>
        /// </summary>
        public string NextPackId() => $"p{_packTag}{Interlocked.Increment(ref _packSeq):D4}";

        /// <summary>Bytes this run actually pushed to the cloud (post-compression). Dedup hits take an early return and never reach here.</summary>
        public long UploadedBytes => Interlocked.Read(ref _uploadedBytes);

        public void AddUploaded(long bytes) => Interlocked.Add(ref _uploadedBytes, bytes);

    }

    /// <summary>
    /// The fallback when no spill directory is configured (unit tests, no scratch disk given): pure memory, unbounded.
    /// When a spill directory is configured the quotas in <see cref="DiffWorkQueueFactory"/> apply instead and this one is not used.
    /// </summary>
    private static readonly DiffQueueLimits InMemoryOnlyLimits =
        new(MaxCachedItems: int.MaxValue, MaxCachedBytes: long.MaxValue);

    /// <summary>
    /// Progress aggregation for the pipeline. Diffing and Uploading run **at the same time**, so an update from
    /// either side must be published together with the other side's latest snapshot — publish only your own line
    /// and the two rows in the UI will keep erasing each other.
    /// </summary>
    private sealed class PipelineReporter(IProgress<BackupProgress>? sink)
    {
        private readonly Lock _gate = new();
        private StageProgress? _diff;
        private StageProgress? _upload;
        private BackupStage _stage = BackupStage.Diffing;
        private int _changedFiles;
        private long _changedBytes;
        private int _uploaded;
        private int _total;

        public void ReportDiff(StageProgress d) { lock (_gate) { _diff = d; Publish(); } }
        public void ReportUpload(StageProgress u) { lock (_gate) { _upload = u; Publish(); } }

        public void SetChanged(int files, long bytes)
        {
            lock (_gate) { _changedFiles = files; _changedBytes = bytes; }
        }

        public void SetUploaded(int done) { lock (_gate) { _uploaded = done; Publish(); } }

        /// <summary>Both streams have finished: retire the diff detail line; only now is the total settled.</summary>
        public void Settle(int total)
        {
            lock (_gate) { _diff = null; _stage = BackupStage.Uploading; _total = total; Publish(); }
        }

        private void Publish() => sink?.Report(
            new BackupProgress(_stage, _changedFiles, _changedBytes, _uploaded, _total)
            {
                Details = (_diff, _upload) switch
                {
                    (null, null) => [],
                    (null, { } u) => [u],
                    ({ } d, null) => [d],
                    var (d, u) => [d!, u!],
                },
            });
    }

    /// <summary>Wait for both streams to stop, swallowing their exceptions — the caller already holds the one it is going to throw.</summary>
    private static async Task SettleAsync(IEnumerable<Task> consumers)
    {
        try { await Task.WhenAll(consumers); }
        catch { /* the first error to surface is the root cause; this only has to "wait for a clean stop" */ }
    }

    /// <summary>
    /// The one <see cref="StageTracker.EndWork"/> a work item owes, shared out among however many pieces that item
    /// travels through the pipeline as.
    /// <para>
    /// <see cref="StageTracker.BeginWork"/> is called once, by the prober, so the item counts as in hand for its
    /// whole journey across both queues; the identity
    /// <c>processed + preparing + queued + waitingOnArchive + uploading ≡ total</c> holds only if exactly one
    /// EndWork answers it. That is not simply "the uploader does it": a single file is one queue entry, but a pack
    /// pool splits into groups, each with its own pack id and its own entry, and a member re-queued under a new
    /// hash adds a further group after the earlier ones are already in flight. So the entry count is not known when
    /// the item starts and the entries do not finish in order — whoever puts the last share back makes the call.
    /// </para>
    /// </summary>
    private sealed class WorkShare(StageTracker tracker)
    {
        /// <summary>Starts at one: the stage currently holding the item, which is also the stage that may still
        /// produce more entries from it. Handing the item to the next stage hands that share along with it.</summary>
        private int _outstanding = 1;

        /// <summary>One more piece of this item is about to exist. Call it **before** the piece can be picked up by
        /// another thread, or the count can reach zero between the two and fire EndWork early.</summary>
        public void Take() => Interlocked.Increment(ref _outstanding);

        public void Release()
        {
            if (Interlocked.Decrement(ref _outstanding) == 0)
                tracker.EndWork();
        }
    }

    /// <summary>
    /// What the prober hands the compressor. <see cref="Hit"/> non-null means the probe already settled where this
    /// content lives — a dedup or resume hit — so there is nothing to compress; it still travels on so that every
    /// single-file item's settle happens in one place.
    /// </summary>
    private sealed record ProbedItem(WorkItem Item, BlobPlacement? Hit, WorkShare Share);

    /// <summary>
    /// What the compressor hands an uploader: one archive's worth of work, and everything that must be handed back
    /// exactly once whether or not an uploader ever gets to it. <see cref="Handoff"/> is null for a probe hit, which
    /// owns no archive.
    /// </summary>
    private sealed record PendingUpload(
        StagedHandoff? Handoff, WorkShare Share, Func<CancellationToken, Task> RunAsync);

    private static PlannedFile ToPlannedFile(PackEntry m) => new(m.Path, m.Length, m.FullHash);

    /// <summary>
    /// Is this entry a "zero-length regular file" — the kind that needs no compression, no upload, and certainly
    /// should not occupy a content-addressed address.
    /// <para>
    /// Empty files used to go through storage like everything else: compressed into a 7z archive **larger than the
    /// original** (0 → 131 bytes), or, when store-only and unencrypted, uploaded raw as a 0-byte blob. Those two
    /// shapes are **completely different bytes** in the cloud, yet every empty file has the exact same fullHash, so
    /// they all pile onto the same data/{hash}: whoever uploads first decides the raw flag that later arrivals get
    /// in their index, and the run that disagrees restores the 7z archive itself as the file's content.
    /// Keep them out of storage and this entire class of problem disappears.
    /// </para>
    /// <para>
    /// Only <see cref="EntryKind.File"/> counts: a symlink's content is the Target field in the index, length does
    /// not represent it, and restore has a separate branch for it (<c>Kind == "symlink"</c>) — this rule must not
    /// casually change that behavior.
    /// </para>
    /// </summary>
    private static bool IsEmptyFile(ScannedEntry entry) => entry.Kind == EntryKind.File && entry.Length == 0;

    public async Task<BackupRunResult> RunAsync(
        BackupRequest request, IProgress<BackupProgress>? progress = null, CancellationToken ct = default,
        BackupRunControl? control = null)
    {
        // Take the start timestamp before any I/O: this is the moment the operator thinks of as "when this backup started".
        var startedAt = DateTimeOffset.UtcNow;
        var source = $"backup:{request.Account.Id}/{request.Container}";
        // Remembers the last stage reported, so a failure can say where it happened.
        //
        // Until now the failure record carried ex.Message and nothing else, and an Azure message names no stage: the
        // 3 TB incident was pinned to cleanup only because the operator noticed it always struck at the end, and
        // that is not a diagnostic anyone should have to rely on twice. Wrapping the progress sink rather than
        // touching each of the twelve Report call sites keeps this from going stale when a stage is added.
        var cursor = new StageCursor();
        progress = cursor.Wrap(progress);
        await Record(NotificationEvents.BackupStart, source, $"Backup started: {request.Name}", request.Container, ct);
        try
        {
            var result = await RunCoreAsync(request, startedAt, progress, ct, control);
            // For the layout and the "omit zero values" rule see BackupSummary: that message goes into both the
            // operation log and the webhook notification, and it is the one line the operator is guaranteed to read,
            // so what this run touched, how much was added in the cloud, and how much was cleaned up all belong in it.
            await Record(NotificationEvents.BackupSuccess, source, $"Backup succeeded: {request.Name}",
                BackupSummary.Format(result), ct);
            return result;
        }
        // Must come before the generic catch (Exception ex) below: BackupSuspendedException is an Exception too,
        // and with the order reversed the catch-all would grab it first, so the user would see "Backup failed" —
        // while the state on disk is in fact perfectly intact and the next run would pick up where it left off.
        catch (BackupSuspendedException ex)
        {
            // Persist the reason to disk first, then send the notification. The order cannot be reversed: the
            // notification goes over the network, and a timeout or failure could take this line down with it —
            // while the next startup relies entirely on that on-disk marker to tell "the user pressed pause himself"
            // (do not restart it for him) apart from "shutdown interrupted it" (do resume it).
            //
            // Written here rather than in SettleStopAsync: this is the only place all three suspend reasons pass
            // through — the gate-demotion one (AutoSuspended) is thrown straight up from deep inside the pipeline
            // and never goes through SettleStopAsync. And the catch over in BackupRunner cannot see it: control was
            // already disposed by its `await using` before that point, and the account id and container name only
            // live in local variables at this level.
            control?.MarkSuspended(ex.Reason);
            // Use the BackupFailure subscription channel, but drop the level to Warning:
            // the channel is chosen because whoever subscribed to "the backup didn't finish" wants exactly this
            // message, and adding a new notification event bit for it would mean every existing user gets nothing
            // by default — a silent default you only discover on the day something goes wrong.
            // The level is dropped because this is not an error: Error would keep it durably in the audit log and
            // put a red line in the UI, while this is really a resumable midpoint. The wording spells out what to do next.
            await Record(NotificationEvents.BackupFailure, source, $"Backup suspended: {request.Name}",
                $"{ex.Message} Progress is saved; run this backup again to pick up where it stopped.",
                ct, OperationLogLevel.Warning);
            throw;
        }
        // Must likewise come before the generic catch (Exception), for an even harder reason than the Suspend one:
        // a cancel the user pressed is **something he did himself**, not an accident. Falling into the catch-all below would
        //   1) durably write an Error into the operation log: "Backup failed: photos — Backup stopped by user.",
        //      after which this backup wears a red line until someone manually Resets it;
        //   2) fire a failure webhook that wakes the user at midnight to look at the button he pressed himself.
        // Both directly contradict the definition of RunStatus.Canceled (neither a failure nor a success).
        //
        // Before Task 9, Cancel worked by cancelling ct, so Record's _recordGate.WaitAsync(ct) threw on the spot and
        // nothing was recorded — the correct outcome, but by coincidence. Now that ct is left alone, it has to be spelled out.
        //
        // The difference from the Suspend branch: this one **sends no notification**. "Didn't finish, can be resumed"
        // is something the user needs to know while he is away; a cancel is not — he is staring at the UI right now.
        // A log line is still written, but at Info level and short retention: the audit trail should record
        // "this run was stopped by a person, it did not crash", not leave an alert someone has to act on.
        catch (OperationCanceledException) when (control is { Stop: not StopKind.None })
        {
            // Use CancellationToken.None: by the time we get here the run's own token has most likely fired, and with it even this one line could not be written.
            await Record(NotificationEvents.BackupFailure, source, $"Backup canceled: {request.Name}",
                "Stopped by user. Blocks already uploaded are kept for the next run.",
                CancellationToken.None, OperationLogLevel.Info, notify: false, durable: false);
            throw;
        }
        catch (Exception ex)
        {
            // The stage goes in the body rather than the title: the title is what notification rules and the UI's
            // failure list match on, and it has to stay stable across runs.
            await Record(NotificationEvents.BackupFailure, source, $"Backup failed: {request.Name}",
                $"Failed during {cursor.Stage}. {ex.Message}", ct);
            throw;
        }
    }

    /// <summary>
    /// Tracks the last stage reported through the progress sink, so the failure record can name it. Deliberately not
    /// a field on the orchestrator: it is registered as scoped, and "each run's own bookkeeping lives on that run"
    /// should follow from the code rather than from how the type happens to be registered.
    /// </summary>
    private sealed class StageCursor
    {
        private int _stage = (int)BackupStage.Scanning;

        /// <summary>The last stage reported. Volatile because the pipeline reports from worker threads.</summary>
        public BackupStage Stage => (BackupStage)Volatile.Read(ref _stage);

        /// <summary>Wraps the caller's sink; a null sink still needs wrapping, since the stage matters even when nobody is watching the progress.</summary>
        public IProgress<BackupProgress> Wrap(IProgress<BackupProgress>? inner) => new Sink(this, inner);

        private sealed class Sink(StageCursor owner, IProgress<BackupProgress>? inner) : IProgress<BackupProgress>
        {
            public void Report(BackupProgress value)
            {
                Volatile.Write(ref owner._stage, (int)value.Stage);
                inner?.Report(value);
            }
        }
    }

    // Logging/notification share one EF DbContext through scoped services (not thread-safe). Collision/warning
    // reports happen inside concurrent upload tasks, so the whole reporting path is serialized to keep concurrent
    // DbContext access from taking the backup down.
    private readonly SemaphoreSlim _recordGate = new(1, 1);

    /// <param name="notify">false = operation log only, no webhook push. Used for a user-initiated cancel: that
    /// message is worth keeping for the audit trail, but pushing it to the person who just pressed the button while watching the UI is not.</param>
    /// <param name="durable">Whether the log entry is durable (audit, kept until the backup is deleted) or short-lived (14 days).</param>
    private async Task Record(
        NotificationEvents evt, string source, string title, string body, CancellationToken ct,
        OperationLogLevel? level = null, bool notify = true, bool durable = true)
    {
        await _recordGate.WaitAsync(ct);
        try
        {
            if (opLog is not null)
                await opLog.AppendAsync(level ?? EventLog.LevelOf(evt), source, $"{title} — {body}", ct, durable);
            if (notifier is not null && notify)
                await notifier.NotifyAsync(evt, title, body, ct);
        }
        finally
        {
            _recordGate.Release();
        }
    }

    // In verbose mode write a per-file debug log (including the file name) into **a text file per backup per date**
    // (VerboseFileLog) rather than SQLite — this keeps one DB write per file from becoming the bottleneck on very
    // large backups, and separates high-frequency diagnostics from the queryable audit log.
    private async Task LogFileAsync(BackupRequest request, string path, CancellationToken ct)
    {
        if (!request.Options.VerboseLogging || verboseLog is null)
            return;
        await verboseLog.AppendAsync(request.Container, $"Backed up {path}", ct);
    }

    private async Task<BackupRunResult> RunCoreAsync(
        BackupRequest request, DateTimeOffset startedAt, IProgress<BackupProgress>? progress, CancellationToken ct,
        BackupRunControl? control = null)
    {
        var opts = request.Options;
        var password = request.Password;

        // An upload-side failure must stop the diff (reading more from disk is pointless), but must **not** abort
        // the other uploads already in flight — same way the old Task.WhenAll wrapped up: let the in-flight ones
        // finish, then throw the first real exception. Any stop the user issues goes through here as well:
        // reading from disk is equally pointless then.
        //
        // Created **at the very top** rather than down at the pipeline: scanning and reading version indexes can
        // each take minutes (this repo has measured a 200,000-entry scan and a 500,000-entry index), and both
        // happen before any upload. If the token only existed from the pipeline onward, a stop pressed two minutes
        // into the scan would not take effect until the whole tree had been walked and every version index read;
        // and SuspendAsync/CancelAsync only return once a terminal state is reached, so the HTTP request would hang
        // for that entire time.
        using var stopProducing = control is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, control.StopToken);
        // The consumers' token: only Stop now interrupts in-flight uploads. Suspend / Finish current files go
        // through "check at the top of the loop and break", letting the in-flight item finish.
        using var working = control is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, control.AbortToken);

        // A stop the user pressed and "the process is shutting down" (ct) are two different things: the latter is not one of this run's stop kinds, so it keeps propagating.
        bool StoppedByUser() => control is { Stop: not StopKind.None } && !ct.IsCancellationRequested;

        // The common wrap-up when the two pre-upload phases (scanning, reading version indexes) are stopped.
        // Both run before any upload, so there is nothing in flight and no leftover volumes to clear; but the
        // journal still has to be flushed to disk, and the exception type still has to be mapped per stop kind
        // (Suspend → BackupSuspendedException, both cancel kinds → OperationCanceledException) — if the raw
        // cancellation escaped, BackupRunner would record a Suspend as Canceled, and that whole distinction is
        // the entire point of this feature. SettleStopAsync takes care of it.
        async Task<T> BeforeUploadAsync<T>(Func<CancellationToken, Task<T>> body)
        {
            T result;
            try
            {
                result = await body(stopProducing.Token);
            }
            catch (OperationCanceledException) when (StoppedByUser())
            {
                throw await SettleStopAsync(control!.Stop);
            }
            // The token is not necessarily observed (reading an index that hits the local cache does no I/O at all), so ask again after returning.
            if (StoppedByUser())
                throw await SettleStopAsync(control!.Stop);
            return result;
        }

        // Stop wrap-up: the journal is always flushed (Cancel too — blocks already uploaded are kept for the next
        // run to reuse, which is exactly what the user asked for), and Stop now additionally deletes the leftover
        // volumes of in-flight files.
        // CancellationToken.None throughout: the run's own token has most likely already fired, and not a single
        // cleanup step could be carried out with it.
        async Task<Exception> SettleStopAsync(StopKind kind)
        {
            if (kind == StopKind.StopNow)
                await PurgeInFlightAsync(request, control!);
            await control!.FlushAsync(fsync: true, CancellationToken.None);
            if (kind != StopKind.Suspend)
                return new OperationCanceledException("Backup stopped by user.");
            // The reason is set by whoever issued the stop (see BackupRunControl.RequestStop): this one wrap-up
            // path serves both a user-pressed pause and a shutdown-triggered suspend, and the difference has to be
            // carried all the way into the marker on disk.
            var reason = control.SuspendReason;
            return new BackupSuspendedException(reason, reason == SuspendReason.ShuttingDown
                ? "Suspended for shutdown."
                : "Suspended by user.");
        }

        // 0. Make sure the container exists (an HTTP-triggered backup is self-sufficient)
        await factory.CreateServiceClient(request.Account)
            .GetBlobContainerClient(request.Container)
            .CreateIfNotExistsAsync(cancellationToken: ct);

        // 1. Scan
        progress?.Report(new BackupProgress(BackupStage.Scanning, 0, 0, 0, 0));
        var scanTracker = new StageTracker("Scanning", total: 0, d =>   // the total is only known once the scan finishes, hence total=0
            progress?.Report(new BackupProgress(BackupStage.Scanning, 0, 0, 0, 0) { Detail = d }));
        var scan = await BeforeUploadAsync(
            t => scanner.ScanAsync(request.LocalRoot, opts.Ignore, opts.Scan, t, scanTracker));
        scanTracker.Complete();

        // The scope filtered out every single file: diff would call everything in the previous version deleted and
        // write an empty version. The old versions are still there, so this is not data loss, but it is certainly a
        // mistake (say, the wrong directory level got ticked) and must not happen silently.
        // An empty root with no scope configured is normal and does not count here.
        // Neither does the case where some paths could not be read: a dropped SMB/NFS mount puts the whole subtree
        // into Unreadable rather than Entries/EmptyDirs ("can't read it ≠ deleted"), and Entries/EmptyDirs being
        // empty then only means the mount point did not answer — it says nothing about whether the scope is right.
        // Reporting that as a scope misconfiguration would send the user off to change a scope that was correct all
        // along, while the real problem (the mount never came up) gets buried.
        if (scan.Entries.Count == 0 && scan.EmptyDirs.Count == 0 && scan.Unreadable.Count == 0
            && !opts.Scan.Scope.IsAll)
            throw new InvalidOperationException(
                "The configured scope selects no files under the local root. "
                + "Nothing would be backed up, so this run was stopped. "
                + "Check the scope selection on this backup.");

        // No local authoritative state yet = this is the **first run** of this config against this container: either
        // a freshly created backup, or "config deleted, container kept, then recreated on the same container" —
        // deleting the config wiped it via localState.RemoveAsync.
        // The latter is exactly the case where the journal was thrown away wholesale and those blocks lost their
        // protection, so the final cleanup must do an orphan sweep to make good on the promise the delete-config
        // endpoint wrote down. This has to be asked **before** LoadAsync: that call backfills local state from the
        // cloud info file along the way.
        var firstRun = trackedInfo is not null
            && !await trackedInfo.HasLocalAsync(request.Account, request.Container, ct);

        // 2. Load the previous version. The info file prefers the local authoritative copy (§3.3, avoids reading a Cold info file from the cloud); the large version index prefers the local cache.
        var info = (trackedInfo is not null
            ? await trackedInfo.LoadAsync(request.Account, request.Container, password, ct)
            : await store.ReadInfoAsync(request.Account, request.Container, password, ct))
            ?? NewInfo(request);
        var identity = info.Backup.CreatedAt.UtcTicks;
        VersionIndex? previous = null;
        if (info.Versions.Count > 0)
        {
            var last = info.Versions[^1];
            previous = await BeforeUploadAsync(t => indexCache.ReadAsync(
                request.Account, request.Container, last.Version, identity, last.IndexBlob, password, last.IndexVolumes, t));
        }

        // Data blob addressing scheme: encrypted backups use keyed addresses to prevent fingerprinting (the key is derived from the password + the salt in the info file).
        var addressing = new BlobAddressScheme(password, info.Backup.KdfSalt);

        // The purely local dedup resolver: builds a "content identity → existing blob" map from the locally cached
        // indexes of the retained versions, and the backup uses it to decide dedup/collision/volumes/raw while
        // issuing **no cloud HEAD at all**. This is the only path — whether the backup was created by this tool or
        // imported from an existing container: import pulls every version's index into the local cache (see the
        // /import endpoint) and lands the info file too (TrackedInfoStore.SeedFromCloudAsync).
        //
        // There used to be a fallback of "no local index, so send a cloud HEAD and compare metadata"; it is gone.
        // Trusting whatever is lying around in the cloud with no local authority is dangerous in itself: you do not
        // know who wrote those blobs, with what password, or whether the content is still correct — and one wrong
        // "already exists" silently records a file that was never uploaded as backed up.
        var indexes = new List<VersionIndex>(info.Versions.Count);
        var lastVer = info.Versions.LastOrDefault()?.Version;
        foreach (var v in info.Versions)
            indexes.Add(previous is not null && v.Version == lastVer
                ? previous
                : await BeforeUploadAsync(t => indexCache.ReadAsync(
                    request.Account, request.Container, v.Version, identity, v.IndexBlob, password, v.IndexVolumes, t)));

        // Open the journal: the baseline version and the addressing identity are only complete at this point. Recovery uses those two to decide whether a journal still counts.
        if (control is not null)
            await control.OpenJournalAsync(
                request.Account.Id, request.Container, lastVer ?? 0, request.LocalRoot, addressing.Identity,
                startedAt, ct, firstRun);

        // The dedup table is built **after** opening the journal: the adopted blocks (present in the cloud, not yet
        // in any index) have to go into the table alongside the indexed ones, otherwise a file with the same content
        // at a different path would delete and re-upload them. See the confirmed parameter of Build for the reasoning.
        var localResolver = LocalDedupResolver.Build(addressing, indexes, control?.Resume.ConfirmedBlobs());

        // 3./4./5. Pipeline the diff with "pack + compress + upload".
        // These three used to be strictly serial: Diffing runs to completion → Plan → Uploading. On a first backup
        // the diff reads every file end to end to hash it, and during those hours not one byte goes over the network.
        // Plan does not actually have to be that global barrier — classification only looks at path and length
        // (see GroupingPlanner.Classify) and is settled the moment the scan finishes.
        var packOptions = opts.Plan with
        {
            DontGroup = opts.DontGroup,
            CrossDirGroup = opts.CrossDirGroup,
            // Packing needs this to split each directory into a compressed group and a store-only group — without
            // wiring it through, the rule only applies to single-file blobs and packed small files still get
            // compressed as a whole box (which was exactly the defect in this feature before).
            DontCompress = opts.DontCompress,
        };
        var classification = planner.Classify(scan.Entries, packOptions);

        var storageByPath = new ConcurrentDictionary<string, StorageRef>(StringComparer.Ordinal);
        var tailByPath = new ConcurrentDictionary<string, string>(StringComparer.Ordinal); // tail hash of single-file blobs → index entry
        // Files whose content changed while being processed: override the diff-time index entry with the new hash/metadata once it settles (§9, PRD special note D).
        var overrides = new ConcurrentDictionary<string, EntryOverride>(StringComparer.Ordinal);
        // Became unreadable only after the diff (hit when the compress/upload stage reopens the source file):
        // treated exactly like files that were already unreadable at diff time — no blob is produced, the index
        // carries the old entry forward, it counts into UnreadableFiles, and it must never take the whole run down
        // (a gap in the M4 design §3).
        var postDiffUnreadable = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        // The seat is held for the whole run: the staging-disk quota is split evenly across the runs currently holding a seat, and it is returned when the run ends.
        using var stagingLease = staging.AcquireLease();
        var state = new RunState(stagingLease);
        var reporter = new PipelineReporter(progress);
        // The diff declares no byte workload; remaining time is extrapolated from the **item count** (see
        // StageTracker.Eta): this stage's cost is mostly spread over "at least one stat per entry", and only the
        // few changed files actually get read end to end — extrapolating by bytes would let an unchanged 100 GB
        // file fly by in a second and make the remaining time collapse on the spot.
        var diffTracker = new StageTracker("Diffing", scan.Entries.Count, reporter.ReportDiff);
        // The upload total **grows as we go** (the diff is still pushing work into the queue), so report 0 = unknown
        // at first: computing a percentage against a still-growing denominator makes it shoot to 100 and fall back.
        // Same for the workload, which accumulates item by item via Enqueue.
        // Speed only counts the time when "there is traffic on the wire": most of this stage is spent in 7z, and
        // putting compression in the denominator measures neither transfer speed nor wall-clock throughput
        // (see StageTracker.SpeedNow).
        // The staged-pool reading is wired in: the "compressed, not yet sent" figure in the UI is that value minus the in-flight bytes already sent.
        var uploadTracker = new StageTracker(
            "Uploading", total: 0, reporter.ReportUpload, speedWhileInFlight: true,
            stagedBytes: () => stagingLease.Bytes);
        // "Bytes transferred" uses the item-level authoritative reading (RunState.UploadedBytes) rather than a
        // per-volume accumulation — it has to be read side by side with the raw bytes that are settled per item, so
        // the two must be measured the same way. This line claims ownership before the first item completes; see the note on SetTransferred.
        uploadTracker.SetTransferred(0);

        var totalItems = 0;
        var uploadedItems = 0;
        // work = the **raw** bytes this item corresponds to. Actual transferred bytes cannot be used as the measure
        // of progress: a dedup hit transfers zero bytes, and the compression ratio swings wildly by file type, so
        // remaining time computed from it would jump around with the hit rate and the ratio.
        void ReportItem(long work)
        {
            // The slot count belongs here (it carries the existing "exactly once" constraint); the tracker only handles in-flight items and bytes/speed.
            reporter.SetUploaded(Interlocked.Increment(ref uploadedItems));
            uploadTracker.Advance(0, work);
            // Transferred bytes and workload are settled **at the same moment, on the same item**, which is what
            // makes the percentage in the UI readable. Both paths have already called AddUploaded before reaching
            // here (single files before the return, packs inside UploadStagedPackAsync), so this snapshot already
            // includes the item that just finished.
            uploadTracker.SetTransferred(state.UploadedBytes);
        }

        // Concurrency permits are issued per **volume**, not per item (see VolumeUploadScope): one item can be a
        // thousand volumes split out of a single big file, and issuing per item would let that whole stretch occupy
        // exactly one stream, making the number in the settings do nothing at all for large files.
        // Between items the permits are arbitrated **by item age**, not first-come-first-served (see
        // VolumeUploadGate): there are UploadConcurrency + 1 uploaders, and first-come-first-served would leave that
        // many items half-finished at once. An interruption throws away more than that now: those, plus every entry
        // the compressor has already put on the staged queue that no uploader has claimed (see DrainQueues) — local
        // CPU only, since not a byte of a queued entry has reached the container.
        var streams = Math.Max(1, opts.UploadConcurrency);
        var uploadGate = new VolumeUploadGate(streams);
        var uploadScope = new VolumeUploadScope(uploadGate, uploadTracker, streams);
        // Pack ids are shared across concurrent directories (content-addressed data blobs are unaffected; a pack id
        // only has to be unique). Pack id allocation lives in RunState (see NextPackId): it must be unique across runs.

        // The queue decouples the two streams: when staging is full it blocks only the compression side, and the
        // diff keeps reading from disk — if backpressure propagated all the way back to the diff, the disk would
        // stall along with it and this whole rework would have been for nothing.
        //
        // The write side **never blocks**: what does not fit in memory spills to disk (see DiffWorkQueue). That is
        // the precondition for showing a remaining time at all — the upload stage's ETA cannot be computed until
        // SetTotal, and that total is only settled once the diff finishes. The moment the write side is blocked by
        // the queue, the diff can only move at the upload's pace, "diff done" = "only one queue depth left to do",
        // and the remaining time refuses to appear until the very tail of the run.
        //
        // With overlap turned off, spilling matters even more: nobody is consuming at all then, so all the work piles up until the diff ends.
        var overlap = opts.OverlapDiffAndUpload;
        using var work = spillFactory?.Create() ?? new DiffWorkQueue(null, InMemoryOnlyLimits);

        // The stopProducing / working tokens are created at the very top of this method (see the note there): scanning and index reading must be able to see a stop too.

        // The consumer side is three stages joined by two queues:
        //
        //   DiffWorkQueue ─▶ [prober × 1] ─▶ probedQueue ─▶ [compressor × 1] ─▶ stagedQueue ─▶ [uploaders × N]
        //
        // One worker used to own an item from compression through the last volume of its upload, and there were
        // only UploadConcurrency + 1 of them. Once that many items were uploading, none was left to reach
        // StageAsync and compression stopped outright — measured with 23 items queued, 4.5 GB staged, and both
        // preparing and waitingOnArchive at zero, which is to say the 7z stage was not merely blocked but empty.
        // Enlarging the worker pool cannot fix that, and that is the argument for the whole shape: any extra worker
        // eventually enters its own item's upload phase and is consumed the same way, and a reserved worker fails
        // for the same reason — once it finishes compressing it still has to deliver its own item. As long as one
        // worker owns an item from compression through upload, no pool size guarantees a compressor.
        //
        // The user-visible consequence was that StagedLimitBytes did nothing: the worker pool saturated long before
        // the staging pool did, so the limit was never the binding constraint at any value. After the split the
        // compressor's only blocking point is that quota.
        //
        // An item that hits a transient error waits in front of the suspend gate and retries once let through, and
        // the **unit** of retry is unchanged by the split — only its owner moved.
        //
        // Single file: the unit is the whole item, and it now sits on the uploader. Each attempt compresses and
        // stages from scratch (the failed pass disposed its own archive), and before the if-missing upload of
        // volumes it first clears the leftover volumes from the previous attempt.
        //
        // Pack: one item is **an entire pool**, which ProcessPackAsync slices into several groups, each taking its
        // own pack id. Retrying the whole item would roll one hiccup in group 9 back to group 1 — and after rolling
        // back it takes **new** pack ids, so the archives the first 8 groups already uploaded end up referenced by
        // no index at all, just occupying space in the container, with a record in info.Packs pointing at each
        // orphan. So pack retries stay pushed down into the group (see RunGroupAsync).

        // "The stage downstream of me is gone." Splitting one loop into three turned every hand-off into a place
        // where an upstream stage waits on a resource that **only the downstream stage hands back**, and a resource
        // nobody is left to hand back is a wait with no end:
        //
        // · the compressor's one blocking point is the staging quota (StagingArea.WaitForRoomAsync), and that quota
        //   comes back only from a live uploader — volume by volume as it sends, or in one go when it disposes a
        //   dropped entry's archive. With every uploader dead the pool stays full and the compressor sits in
        //   _releaseSignal.WaitAsync forever, SettleAsync(consumers) waits on it forever, the run never ends, the
        //   busy lock is never released, and not one word of the error that killed the uploaders ever surfaces.
        // · the prober's is a slot in probedQueue, and only the compressor frees one.
        //
        // This is not an exotic corner. Any systemic upload failure kills all max(2, N+1) uploaders one entry at a
        // time, and the worst case is the auto-suspend feature working exactly as designed: when the gate's patience
        // runs out every uploader throws BackupSuspendedException out of WithPauseAsync at once, while the compressor
        // sails on, because 7z and the local disk never raise the transient errors the gate reacts to. Before the
        // split all six workers were uploaders too, so they all left and released their own quota on the way out.
        //
        // Neither `working` nor control.Stop covers it: `working` is linked only to ct and control.AbortToken, and a
        // gate downgrade sets no stop kind (only RequestStop does — see BackupRunControl). So the two feeding stages
        // get a token of their own, cancelled the moment the stage in front of them is gone. It plays the same role
        // for them that stopProducing plays for the diff, and like the diff (see the catch below the diff's try) they
        // treat that particular cancellation as "stop quietly", **not** as an error: the exception that has to reach
        // the user is the one that killed the downstream stage, and a cancellation thrown from an earlier task in the
        // consumers list would be the one Task.WhenAll surfaces instead, masking it.
        using var downstreamGone = new CancellationTokenSource();
        // A stop belongs here for the same reason a dead downstream does: what these two stages have in hand is
        // worth nothing once the run is winding down. The probe's content identity is persisted nowhere and the
        // compressor's archive goes to a queue DrainQueues is about to release, so finishing either only delays the
        // stop. The uploaders stay on `working` — a volume that completes can be journalled, and that is the one
        // piece of in-flight work a suspend should wait for. StopNow is unaffected: it fires _abort → working, which
        // cancels the uploads too, so `feeding` is already covered on that path via working.Token.
        using var feeding = CancellationTokenSource.CreateLinkedTokenSource(
            working.Token, downstreamGone.Token, control?.StopToken ?? CancellationToken.None);
        // True when this run is being torn down by the line above rather than by anything the user did — the filter
        // for "exit quietly" on both feeding stages. A stop-triggered cancellation deliberately does not satisfy
        // this: it falls through to the catch's rethrow, propagates out of the loop task, and is then swallowed by
        // SettleAsync's catch-all along with every other consumer's exception, so the exception the caller actually
        // sees is the one SettleStopAsync builds from control.Stop, not whatever shape this cancellation took here.
        bool DownstreamGone() => downstreamGone.IsCancellationRequested && !working.IsCancellationRequested;

        // The uploader count stays UploadConcurrency + 1: the extra consumer is what keeps the volume gate's
        // hand-off from stalling at item boundaries (see VolumeBlobIO), and that reasoning is untouched by the split.
        // The prober and the compressor are one each, for the reasons on the two channels below.
        var uploaders = Math.Max(2, Math.Max(1, opts.UploadConcurrency) + 1);
        // How many of them are still alive. The trigger for "the upload side is gone" is the **last** one leaving,
        // not the first one faulting: one uploader dying of a permanently refused blob is an ordinary failure, and
        // the run's remaining work still gets compressed, uploaded and journalled by its siblings — that is how it
        // behaved when every worker owned an item end to end, and a resumable run's journal is worth more than an
        // early exit. It is only when none is left that the compressor's quota is never coming back.
        var liveUploaders = uploaders;

        // Stage 1 → 2. The probe is disk-bound: on a candidate hit it reads the whole file end to end to derive its
        // content identity. One worker, deliberately — concurrency does not make a disk faster, and on spinning
        // media or a NAS share it makes it slower by turning one sequential read into several competing seeks. What
        // this stage buys is overlap, not parallelism: while it reads the next item, the compressor works on the
        // previous one, so the CPU stops waiting on a hash and the disk stops waiting on 7z.
        //
        // Small on purpose, and Wait rather than Drop. What this stage buys is **overlap, not buffering**: one or two
        // items of lookahead is the whole benefit, and everything past that is a copy of DiffWorkQueue held on the
        // heap instead of on disk. That queue exists precisely to cap the consumer backlog — 2,000 items / 64 MB by
        // default, remainder spilled to a file (see the DiffWorkQueueFactory registration in Program.cs) — and that
        // cap is stated as the precondition of the whole spill design a few lines above. Left unbounded, this channel
        // relays out of the bounded queue at one head-read per item while the compressor consumes at 7z speed, two
        // rates orders of magnitude apart, so it drains the spill file back into memory in its entirety on exactly
        // the runs the spill was built for: at the 500,000-entry scale this repo has measured, WorkItem.EstimatedBytes
        // puts that at roughly 170 MB of live objects, on a NAS.
        var probedQueue = Channel.CreateBounded<ProbedItem>(
            new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true,
            });
        // Stage 2 → 3. Compression is globally serial (StagingArea holds one compression lock across every run), so
        // this side is one worker by definition — a pool would be a queue in front of a lock that admits one.
        //
        // This one carries no depth limit: the depth of everything that owns an archive is already bounded in bytes
        // by the staging pool, which is the bound the operator configured, and a second, item-count bound would
        // reintroduce a constraint that fires before the one they set.
        //
        // Two kinds of entry own no archive and are therefore bounded by nothing here: a dedup or resume hit (since
        // the pipeline was split in two) and a raw in-place item (since the raw route stopped copying). For a
        // store-only workload — the media library the DontCompress rule exists for — the compressor's only limiter
        // is then disk read speed, so it can hash and queue the whole dataset while the uploaders trickle.
        //
        // Accepted deliberately, on two counts. What it holds is a closure, a content identity and a path per
        // entry — no copy of any file, and nothing charged to a quota another backup on this machine shares. And
        // what it widens is the stretch between an in-place item's hash and its upload, which the guard around that
        // upload measures rather than races (see InPlaceSource): the guard's verdict does not weaken with time, only
        // its chance of tripping rises, and a trip costs one wasted upload, a take-back and a retry through the
        // copying route.
        //
        // Bounding it would have to count only the entries that own no handoff, or it fires before the operator's
        // byte limit for everything else — which means a second resource with its own exactly-once release on the
        // queue-write failure, the stop drain, DrainQueues and the uploader's pickup, sitting alongside the item
        // ledger and the staging quota. Two disciplines of that kind is what this pipeline's correctness already
        // rests on; a third, bought for a window that costs no correctness, is not a trade worth making here.
        var stagedQueue = Channel.CreateUnbounded<PendingUpload>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });

        // Putting an entry on the staged queue is the moment its ownership passes to an uploader, so the share that
        // uploader will put back has to be taken **before** the entry can be picked up. If the write itself fails,
        // nothing downstream exists to hand back either the share or the archive, and both are owed here.
        async Task HandOffAsync(
            StagedHandoff? handoff, WorkShare share, Func<CancellationToken, Task> run, CancellationToken token)
        {
            share.Take();
            try
            {
                await stagedQueue.Writer.WriteAsync(new PendingUpload(handoff, share, run), token);
            }
            catch
            {
                handoff?.Dispose();
                share.Release();
                throw;
            }
        }

        async Task ProbeLoopAsync()
        {
            try
            {
                while (await work.DequeueAsync(feeding.Token) is { } item)
                {
                    // Work that has not started yet is not done after a stop.
                    //
                    // The item already in hand is a different matter here than it used to be, and than it is on
                    // the upload side. "Finish the current item, then stop" is a promise about work worth
                    // finishing, and a probe's is not: its content identity is persisted nowhere, so a stop
                    // cancels feeding.Token and cuts the read off mid-file rather than making the operator wait
                    // for a hundred gigabytes to be hashed for nothing. The promise still holds where it means
                    // something — an uploader on working.Token finishes its volume and journals it.
                    if (control is { Stop: not StopKind.None })
                        break;

                    // Hold here while the gate is closed — this is the entry path a pause needs, next to the
                    // failure path WithPauseAsync already covers. It sits **above** BeginWork on purpose: an item
                    // parked here has not been claimed, so nothing of the ledger is owed while the run waits, and
                    // an OperationCanceledException out of the wait costs exactly what the break above does.
                    //
                    // The stop check is repeated below rather than merely being made once above, because being
                    // released is not the same as being resumed: RequestStop sets the intent and *then* downgrades
                    // the gate (BackupRunControl.RequestStop), precisely so a parked worker wakes up — and a worker
                    // woken that way must not go on to take another item. WaitIfPausedAsync ignores its released
                    // value for the same reason: what a release means is decided here.
                    if (control is not null)
                        await control.Gate.WaitIfPausedAsync(feeding.Token);
                    if (control is { Stop: not StopKind.None })
                        break;

                    // BeginWork belongs to the first stage, so an item counts as in hand for its whole journey
                    // across both queues. Between here and BeginUpload lie the probe, compression and staging, and
                    // pushing a 100 MB box through 7z can take tens of seconds — the UI has to show that stretch,
                    // otherwise it reads as "nothing is happening". WorkShare owes the single matching EndWork.
                    uploadTracker.BeginWork();
                    var share = new WorkShare(uploadTracker);
                    var handed = false;
                    try
                    {
                        if (item.Single is { } single)
                        {
                            var localPath = Local(request, single.Path);
                            // Behind the gate, with the unreadable check inside it, for the same two reasons the
                            // compressor's staging call is — see StageProbedAsync.
                            await WithPauseAsync(control, async () =>
                            {
                                BlobPlacement? hit;
                                try
                                {
                                    hit = await ProbeAndResumeAsync(
                                        request, single, localPath, localResolver, uploadTracker, control,
                                        feeding.Token);
                                }
                                catch (Exception ex)
                                {
                                    if (!await TrySettleUnreadableAsync(
                                            request, single, localPath, ex, postDiffUnreadable, ReportItem,
                                            feeding.Token))
                                        throw;
                                    return;
                                }
                                await probedQueue.Writer.WriteAsync(new ProbedItem(item, hit, share), feeding.Token);
                                handed = true;
                            }, feeding.Token);
                        }
                        else
                        {
                            // A pack has no pre-compression probe: its members were content-matched against the
                            // existing packs back on the diff side (see TryFindPackMember), and whatever is left has
                            // to be compressed before anything about it can be decided.
                            await probedQueue.Writer.WriteAsync(
                                new ProbedItem(item, Hit: null, share), feeding.Token);
                            handed = true;
                        }
                    }
                    finally
                    {
                        // Handing the item on hands its share along with it; stopping here means putting it back.
                        if (!handed)
                            share.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (DownstreamGone())
            {
                // The compressor is gone, so this loop's only customer is gone with it. Leave quietly rather than
                // faulting: whatever killed the compression side is already on its way up through Task.WhenAll, and
                // this task comes **before** it in the consumers list — fault here and it is this cancellation the
                // user is shown instead of the real cause.
                await stopProducing.CancelAsync();
            }
            catch
            {
                await stopProducing.CancelAsync(); // stop making the diff read the disk for nothing
                throw;
            }
            finally
            {
                // However this loop ends, the compressor has to learn there is no more work, or it waits forever.
                probedQueue.Writer.Complete();
            }
        }

        // Where the two paths part. Everything this stage decides is decided **before** anything is uploaded, and
        // everything it puts on the staged queue is a self-contained "finish this" closure — an uploader never has
        // to come back and ask this loop a question, which is what lets the two run at their own paces.
        async Task StageProbedAsync(ProbedItem probed, CancellationToken token)
        {
            var share = probed.Share;

            // A dedup or resume hit: nothing to compress, and no archive to own. It still travels through the queue
            // rather than settling here, so that every single-file item's settle happens in one place — and so this
            // loop never waits on the journal append or the collision webhook that settle can do.
            if (probed.Hit is { } hit)
            {
                var file = probed.Item.Single!;
                await HandOffAsync(null, share, t => WithPauseAsync(
                    control,
                    () => FinishBlobAsync(
                        request, file, hit, storageByPath, tailByPath, overrides, ReportItem, control, t),
                    t), token);
                return;
            }

            if (probed.Item.Single is { } single)
            {
                var localPath = Local(request, single.Path);
                var storeOnly = opts.DontCompress?.MatchesFileOrAncestorDir(single.Path) ?? false;
                StagedBlob? staged = null;
                // Behind the gate for the same reason this stretch was behind it when one worker owned the whole
                // item: a NAS blip while 7z reads the source has to wait for the share to come back, not take the
                // run down. And the unreadable check sits **inside** the gate, exactly as HandleBlobAsync has it —
                // outside, a file that genuinely cannot be read looks transient to the gate, gets retried until its
                // patience runs out, and suspends the whole run over one file.
                await WithPauseAsync(control, async () =>
                {
                    try
                    {
                        staged = await StageBlobAsync(
                            request, single, localPath, storeOnly, bypassQuota: false, allowInPlace: true,
                            uploadTracker, state, token);
                    }
                    catch (Exception ex)
                    {
                        if (!await TrySettleUnreadableAsync(
                                request, single, localPath, ex, postDiffUnreadable, ReportItem, token))
                            throw;
                    }
                }, token);
                if (staged is not { } compressed)
                    return;   // settled as unreadable inside the gate: this item stops here

                await HandOffAsync(compressed.Handoff, share, async t =>
                {
                    // The retry unit is still the whole item, exactly as it is for a pack group: the first pass uses
                    // the archive the compressor just produced, and any retry compresses a fresh one, because the
                    // failed pass disposed its own. Reusing a disposed archive would upload volume files the staging
                    // area has already deleted. The slot is emptied before the upload starts, not after it ends, so
                    // no later pass can find it pointing at a dead archive.
                    //
                    // That recompression happens **on this uploader**, and it is why it may not wait for staging room
                    // (bypassQuota). Everything in the pool is released by an uploader, so an uploader that parks on
                    // the quota is waiting for itself; and the failure that sends it here is systemic by nature — a
                    // network outage trips every in-flight upload into the gate together, the compressor meanwhile
                    // fills the pool to the ceiling exactly as designed, and the gate then releases every waiter in
                    // one go. All of them arriving here at once with the pool full and no owner left is a wait with no
                    // end: downstreamGone does not fire (the uploaders are alive, not gone), the gate cannot downgrade
                    // (nobody is at it), and only StopNow cancels the token they are blocked on — so Suspend and
                    // "finish current files" both hang. The overshoot the bypass permits is one archive per uploader.
                    // See StagingArea.StageWithoutBackpressureAsync.
                    StagedBlob? pending = compressed;
                    await WithPauseAsync(control, async () =>
                    {
                        BlobPlacement placement;
                        try
                        {
                            // allowInPlace stays true here: nothing about a network hiccup makes uploading from the
                            // source unsafe, and the guard on the far side of the upload judges each attempt on its
                            // own. The one retry that must copy is the one the guard itself asks for, and that one
                            // is issued inside UploadStagedBlobItemAsync rather than here.
                            var blob = pending ?? await StageBlobAsync(
                                request, single, localPath, storeOnly, bypassQuota: true, allowInPlace: true,
                                uploadTracker, state, t);
                            pending = null;
                            placement = await UploadStagedBlobItemAsync(
                                request, single, blob, addressing, localResolver, uploadScope, uploadTracker, state,
                                control, t);
                        }
                        catch (Exception ex)
                        {
                            if (!await TrySettleUnreadableAsync(
                                    request, single, localPath, ex, postDiffUnreadable, ReportItem, t))
                                throw;
                            return;
                        }
                        await FinishBlobAsync(
                            request, single, placement, storageByPath, tailByPath, overrides, ReportItem, control, t);
                    }, t);
                }, token);
                return;
            }

            // A pack pool is not one unit of compression: ProcessPackAsync slices it into groups, each with its own
            // pack id and its own retry unit, so it produces one queue entry per group. The group loop, the attempt
            // counter, re-queueing a member that changed under 7z and demoting one that grew past the threshold all
            // stay on this side — they are decided before the upload, and they mutate state that one ProcessPackAsync
            // invocation owns single-threadedly, which is what it already assumes. Only the record-and-settle tail
            // travels with the archive.
            var poolStoreOnly = probed.Item.StoreOnly;
            await ProcessPackAsync(
                request, probed.Item.Pack!, poolStoreOnly, addressing, localResolver, info, storageByPath,
                tailByPath, overrides, postDiffUnreadable, uploadScope, ReportItem, uploadTracker, state, control,
                token,
                // This invocation runs on the compression stage, where waiting for staging room is the backpressure
                // the whole split exists to make binding. What it hands to an uploader is marked below, one call at a time.
                bypassQuota: false,
                dispatch: async (packId, members, bytes, t) =>
                {
                    GroupAttempt? attempt = null;
                    // Behind the gate for the same reason the single-file staging above is: when one caller ran both
                    // halves back to back this stretch was inside RunGroupAsync's own gate, and a transient failure
                    // in it still has to wait rather than take the run down.
                    await WithPauseAsync(control, async () => attempt = await CompressGroupAsync(
                        request, packId, members, poolStoreOnly, bypassQuota: false, uploadTracker, state, t), t);
                    var group = attempt!;

                    await HandOffAsync(group.Handoff, share, async u =>
                    {
                        // bypassQuota: this runs on an uploader. The first pass consumes the archive above, but a
                        // retry recompresses right here, and an uploader that waits for staging room waits on a pool
                        // only an uploader drains — see the same note on the single-file closure above.
                        var (rejudged, recorded, volumes) = await RunGroupAsync(
                            request, packId, members, poolStoreOnly, bypassQuota: true, precompressed: group,
                            uploadScope, uploadTracker, state, control, u);
                        await SettlePackAsync(
                            request, packId, recorded, volumes, poolStoreOnly, info, storageByPath, ReportItem,
                            bytes, control, u);

                        // Almost always the very list the compressor already acted on: RunGroupAsync only re-judges
                        // the members when it had to recompress, which takes a transient failure mid-upload. Compare
                        // by reference so the ordinary pass costs nothing.
                        if (ReferenceEquals(rejudged, group.Changed))
                            return;

                        // It did recompress, and that fresh pass can exclude members the handed-over attempt
                        // considered stable — someone rewrote them while the gate was holding this group. They are
                        // in no pack, and the loop that would have re-queued them settled its own verdict long ago
                        // and moved on. Left alone they reach the index with a length and no storage reference,
                        // which is the exact shape of silent data loss. Hand them to a standalone ProcessPackAsync
                        // instead — the same treatment a dangling alias gets — and it converges: their stale
                        // diff-time hash makes the first group exclude them, the tail re-reads them under their
                        // settled hash, and the next group stores them. onItem is the no-op because this group's
                        // slot was reported once already, just above.
                        //
                        // The whole list, unfiltered: a recompression only ever judges the members the handed-over
                        // pass kept (see retryMembers in RunGroupAsync), so everything in here comes out of
                        // group.Stable and is disjoint from group.Changed by construction. The reverse — a member
                        // this loop already re-queued turning up in the retry's archive too — is prevented there
                        // rather than mopped up here, because by this point the second copy is already in the cloud.
                        //
                        // bypassQuota again, and for a wider reason than the retry above: with no dispatch this call
                        // compresses **and** uploads on this uploader, so every archive it makes is one an uploader
                        // is waiting on itself to release.
                        var stranded = rejudged;
                        if (stranded.Count > 0)
                            await ProcessPackAsync(
                                request, [.. stranded.Select(ToPlannedFile)], poolStoreOnly, addressing,
                                localResolver, info, storageByPath, tailByPath, overrides, postDiffUnreadable,
                                uploadScope, static _ => { }, uploadTracker, state, control, u, bypassQuota: true);
                    }, t);

                    return group.Changed;
                });
        }

        async Task CompressLoopAsync()
        {
            try
            {
                await foreach (var probed in probedQueue.Reader.ReadAllAsync(feeding.Token))
                {
                    // The same promise the single worker made at the top of its loop, kept one stage further down:
                    // work that has not started is not done after a stop. Before the split the prober's check was
                    // the only one needed, because taking an item and compressing it were the same act; now the
                    // prober can be a long way ahead, and without this check a stop would still have to compress
                    // everything it had run ahead of. Nothing has been compressed yet, so all this entry owes back
                    // is the item ledger.
                    if (control is { Stop: not StopKind.None })
                    {
                        probed.Share.Release();
                        continue;
                    }
                    try
                    {
                        // The gate, and the repeat of the check above it. Inside this try rather than beside the
                        // check above, because by this point the item **is** claimed: the prober handed its share
                        // over with the entry, and the wait below can end in an OperationCanceledException
                        // (feeding is cancelled by a stop, by an abort, or by the upload side dying). Above the
                        // try that share would simply be gone, and the uploading column would stay inflated by it
                        // for the rest of the run. The finally hands it back on every exit, which is also what
                        // makes the bare `continue` below equivalent to the release-and-continue above it.
                        //
                        // Why check the stop intent again: RequestStop sets the intent and then downgrades the
                        // gate to wake whoever is parked at it, so a release here can mean "the user resumed" or
                        // "a stop is on its way", and only the check tells them apart. Nothing has been compressed
                        // yet in either case, so all this entry owes back is the item ledger.
                        if (control is not null)
                            await control.Gate.WaitIfPausedAsync(feeding.Token);
                        if (control is { Stop: not StopKind.None })
                            continue;

                        await StageProbedAsync(probed, feeding.Token);
                    }
                    finally
                    {
                        // The share the prober handed over. Whatever this loop made of the item took a share of its
                        // own before it could be picked up, so this only fires EndWork when the item stops here:
                        // settled as unreadable, or a pool whose every group was a resume hit.
                        probed.Share.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (DownstreamGone())
            {
                // Every uploader is gone, which means the staging quota this loop waits on is never coming back. Leave
                // quietly, for the same reason the prober does: the exception that has to reach the user is the one
                // that killed the uploaders, and this task sits ahead of them in the consumers list.
                await stopProducing.CancelAsync();
            }
            catch
            {
                await stopProducing.CancelAsync();
                throw;
            }
            finally
            {
                stagedQueue.Writer.Complete();
                // However this loop ends — drained, faulted, or cancelled by the line above — the prober must not be
                // left waiting for a probedQueue slot that only this loop frees. Cancelling on the ordinary path too
                // is safe and deliberate: the only way out of the loop above without an exception is probedQueue
                // being completed, which the prober does in its own finally, so by then it has already finished.
                await downstreamGone.CancelAsync();
            }
        }

        async Task UploadLoopAsync()
        {
            try
            {
                await foreach (var entry in stagedQueue.Reader.ReadAllAsync(working.Token))
                {
                    // "Finish the current item, then stop", now measured per queue entry: what an uploader has in
                    // hand runs to completion, what is still queued never starts. Discarding a queued entry costs
                    // the local CPU that compressed it and nothing else — not a byte of it reached the container —
                    // but the pool quota it holds is booked on a singleton shared by every run, so it has to go back
                    // here rather than wait for the wrap-up.
                    if (control is { Stop: not StopKind.None })
                    {
                        entry.Handoff?.Dispose();
                        entry.Share.Release();
                        continue;
                    }
                    try
                    {
                        // The gate, and the repeat of the check above it — inside this try for the reason the
                        // compressor's is, and here it is worth more than a progress column: this entry owns a
                        // compressed archive, whose quota is booked on a singleton shared by every run on the
                        // machine. An OperationCanceledException out of the wait (working is cancelled by ct and
                        // by Stop now) above the try would leak that quota until the process restarts, and it is
                        // reachable exactly when a Stop now lands on a paused run. The finally hands both back on
                        // every exit, so the bare `continue` below does what the release-and-continue above does.
                        //
                        // Repeating the check is what keeps "finish the item in hand, then hold" from turning into
                        // "take one more": RequestStop downgrades the gate after setting the intent, so a worker
                        // parked here is woken by the very stop it must now obey. What is still queued never
                        // starts; not a byte of it reached the container, so discarding it costs only local CPU.
                        if (control is not null)
                            await control.Gate.WaitIfPausedAsync(working.Token);
                        if (control is { Stop: not StopKind.None })
                            continue;

                        await entry.RunAsync(working.Token);
                    }
                    finally
                    {
                        // Idempotent, and normally a no-op: the upload path disposes its own archive the moment the
                        // cloud confirms. This is the backstop for an entry whose run threw before it got that far.
                        // The quota that archive holds lives on a singleton shared by every run and is at the same
                        // time the backpressure gate on output, so leaking it throttles compression process-wide
                        // until a restart.
                        entry.Handoff?.Dispose();
                        entry.Share.Release();
                    }
                }
            }
            catch
            {
                await stopProducing.CancelAsync();
                throw;
            }
            finally
            {
                // Last one out turns off the light. A systemic failure — the network down, or the gate's patience
                // exhausted, in which case every uploader throws BackupSuspendedException at once — takes all of
                // them, and from that moment the compressor is waiting on a pool nobody will ever drain. Firing here
                // rather than in the catch above is what keeps a single permanently refused blob from cutting the
                // compression side short while its siblings are still working (see liveUploaders).
                //
                // It fires on the ordinary ending too, when stagedQueue completes and every uploader drains out.
                // Harmless: the compressor completes that channel on its way out, so by then it has already finished
                // and cancelled this source itself.
                if (Interlocked.Decrement(ref liveUploaders) == 0)
                    await downstreamGone.CancelAsync();
            }
        }

        // Everything the pipeline is still carrying once its loops have stopped. The two queues differ in what they
        // owe back:
        //
        // · stagedQueue entries own a compressed archive each. Discarding one costs local CPU only — not a byte of
        //   it reached the container — but its quota is booked on a singleton shared by every run and is at the same
        //   time the backpressure gate on output, so leaving it booked makes this machine's other backups pay for it
        //   until the process restarts.
        // · probedQueue entries hold no quota, nothing having been compressed yet — they hold heap, which is what
        //   this channel's depth limit is for, not anything a drain can hand back. All they owe is the item ledger,
        //   or the uploading column stays inflated by however many were in flight for the rest of the run.
        //
        // The loops hand these back themselves on a stop, one entry at a time. This is the backstop for the exits
        // they cannot cover: an aborted token throws straight out of ReadAllAsync with entries still in the channel,
        // and a stage that faults leaves whatever the stage in front of it had already handed over. Safe only
        // **after** the consumers have settled — the compressor is the sole writer of stagedQueue and completes it
        // on its way out, so once it is gone nothing can be written in behind this drain.
        void DrainQueues()
        {
            // Called only after the consumers have settled — that is a precondition, not a preference, and the
            // order below does not soften it. probedQueue is SingleReader, and its single reader is the
            // compressor's ReadAllAsync; a TryRead here while that loop is alive is a second concurrent reader on
            // a channel configured for one, which is undefined behaviour in its fast path rather than a question
            // of which entry ends up where. Draining probedQueue first is only so that an entry cannot be moved
            // across into stagedQueue behind a drain that has already passed it; it does not make an early call
            // safe, and there is no ordering that would.
            while (probedQueue.Reader.TryRead(out var probed))
                probed.Share.Release();
            while (stagedQueue.Reader.TryRead(out var entry))
            {
                entry.Handoff?.Dispose();
                entry.Share.Release();
            }
        }

        // One prober, one compressor, `uploaders` uploaders — the counts and the reasoning for each are up where
        // `uploaders` is worked out, next to the two channels.
        //
        // The ORDER matters, and it is a seam rather than a rule: Task.WhenAll surfaces the first faulted task in
        // list order, so the two feeding stages outrank every uploader. The prober's and the compressor's own
        // comments already say this about the cancellation they throw on the way out, and take care not to fault
        // ahead of the real cause. What no comment covered until now is the other half: a NON-cancellation fault
        // in either of them would outrank the uploaders' cause the same way, and there is nothing in place to keep
        // it from doing so. It costs nothing today because neither stage has a blocking call that can fail on its
        // own account — the compressor's one wait is the staging quota, which throws only cancellation. Add one
        // that can fail for a reason of its own and this is the line to re-read.
        List<Task> consumers = [];
        void StartConsumers() =>
            consumers = [
                Task.Run(ProbeLoopAsync, working.Token),
                Task.Run(CompressLoopAsync, working.Token),
                .. Enumerable.Range(0, uploaders).Select(_ => Task.Run(UploadLoopAsync, working.Token))];

        if (overlap)
            StartConsumers();

        // In-flight packing state. The diff advances single-threaded in scan order, so none of this needs a lock.
        // Cross-box dedup of pack members within this run: a later arrival with the same content does not enter a
        // box, it just hangs off the first one, and everything is backfilled at the end.
        var aliasTable = new PackAliasTable();
        var dirPending = new Dictionary<string, List<PlannedFile>>(StringComparer.Ordinal);
        var dirRemaining = new Dictionary<string, int>(classification.DirectoryCandidates, StringComparer.Ordinal);
        // The cross-directory path splits into two independent pipelines by compressibility (index 0 = compressed
        // box, 1 = store-only box): a box can only have one compression mode, so this cut has to be made before
        // packing. The two count and seal independently and do not affect each other's three limits.
        var crossPending = new List<PlannedFile>[] { [], [] };
        var crossBytes = new long[2];
        var crossPathBytes = new long[2];
        var changedFiles = 0;
        long changedBytes = 0;

        var reportedSpill = 0L;
        void Enqueue(WorkItem item)
        {
            Interlocked.Increment(ref totalItems);
            // Declare this item's raw bytes as the workload for the remaining-time estimate. On completion
            // ReportItem settles the same amount (Length for a single file, the sum of member lengths for a box);
            // the two must match or the remaining amount never reaches zero.
            uploadTracker.Enqueue(item.Single?.Length ?? item.Pack!.Sum(f => f.Length));

            // Never blocks: what does not fit in memory spills to disk. That lets the diff run straight through, which is what gives SetTotal a chance to settle early.
            work.Enqueue(item);

            // How much spilled has to be said out loud — it is the direct reading of "how far the diff is ahead of
            // the upload", something that used to show only indirectly by CurrentItem sitting still.
            // Report only when the number changes: SetSpilled takes the publish lock, and at normal scale nothing
            // spills at all, so reporting unconditionally would put a useless lock on every item of the diff's hot path.
            var spilled = work.SpilledItems;
            if (spilled != reportedSpill)
            {
                reportedSpill = spilled;
                diffTracker.SetSpilled(spilled);
            }
        }

        async Task OnChangeAsync(FileChange c, CancellationToken token)
        {
            // The diff passes the gate too: "pause the backup" means the disk stops being read at all, not merely
            // that the pipeline stops draining. Parking the differ's callback parks the walk that calls it.
            //
            // No stop check on either side of this one, unlike the three consumer loops, because the diff has no
            // "check and break" to reach: it stops by having its token cancelled, and always has. `token` here is
            // stopProducing, linked to control.StopToken, so a stop cancels this wait outright and the cancellation
            // travels the diff's own established exit — the catch below the diff's try, and SettleStopAsync after
            // it. The gate's other way out, a patience downgrade, sets no stop intent for a check to find anyway;
            // it takes the consumers down, and each of them cancels stopProducing on its way out (see the catches
            // in the three loops above). What is left — released with the run still healthy — is a resumed diff,
            // which is the whole point.
            //
            // Nothing is claimed at this point: the item this callback may go on to build does not exist yet, and
            // the local Enqueue that books it into the ledger is only ever reached from below this wait.
            if (control is not null)
                await control.Gate.WaitIfPausedAsync(token);

            var changed = c.Kind is ChangeKind.Added or ChangeKind.Modified && c.Current is not null;
            if (changed)
            {
                changedFiles++;
                changedBytes += c.Current!.Length;
                reporter.SetChanged(changedFiles, changedBytes);
            }

            if (!classification.ByPath.TryGetValue(c.Path, out var klass))
                return;

            // FullHash may be empty — for single-file blobs the full-content hash is deferred to the compression
            // pass (see DeferFullHash).
            // Zero-byte files are stopped right here: they have no content to store, Length==0 in the index entry is
            // complete information by itself, and restore creates an empty file from that (see IsEmptyFile). A null
            // file takes exactly the same existing path as "this entry did not change" — the directory counter still
            // decrements and box-sealing timing is unaffected.
            var file = changed && !IsEmptyFile(c.Current!)
                ? new PlannedFile(c.Path, c.Current!.Length, c.FullHash)
                : null;

            // File-level dedup for pack members: this content already sits in some existing pack → point straight at
            // it, no packing, no compression, no upload. Only for entries that would be grouped (the single-file
            // blob path has its own content-addressed dedup).
            //
            // Duplicates within one box are already eliminated by 7z's solid archive (the dictionary matches across
            // members); what is saved here is the **cross-box, cross-version** part: compression does not share a
            // dictionary between boxes, so the same content really would be stored twice.
            //
            // This is **read-only** with respect to existing backups: not one byte of the old indexes changes, there
            // is merely one more way to get a hit. The reference shape written after a hit (Kind=pack + Ref +
            // EntryName) is byte-for-byte what it was before, so retention cleanup collecting references by Ref,
            // dead-weight compaction grouping surviving members by EntryName, and restore pulling a member out of the
            // archive by EntryName all stay untouched (RetentionCleaner's comment about "same content at different
            // paths dedups to the same fullHash but is still two members" foresaw this day long ago).
            if (file is not null && klass.Category != FileCategory.SingleFile
                && localResolver is not null
                && file.FullHash is { } packHash && c.HeadHash is { } packHead
                && localResolver.TryFindPackMember(packHash, file.Length, packHead, c.TailHash) is { } priorMember)
            {
                storageByPath[c.Path] = new StorageRef
                {
                    Kind = "pack", Ref = priorMember.PackId, EntryName = priorMember.EntryName,
                };
                // From here it takes exactly the same existing path as "this entry's content did not change": the
                // directory counter still decrements, box-sealing timing is unaffected, and it takes no upload slot
                // and needs no settling.
                file = null;
            }

            // Cross-box member dedup within this run. The tier above looks up packs from **existing versions**
            // (_packMembers is built only from historical indexes), and boxes sealed during this run are not in it —
            // so on a first backup, or when a large batch of duplicate small files is added at once, identical
            // content split across different boxes really would be stored once per box (compression does not share a
            // dictionary between boxes, so nothing is saved).
            //
            // The later arrival does not enter a box, it just hangs off the first one; which pack it ends up pointing
            // at is not known until the consumers finish (the leader may be rewritten inside the compression window,
            // may become unreadable, may grow past the threshold and switch to a single-file blob), so the backfill
            // is done all at once at the end. The decision looks only at the final state, which is why not a single
            // concurrency primitive is needed here.
            //
            // Ordering cannot clash with the tier above: if the leader hits an existing pack, later files with the
            // same content hit it too through the same table and the same four criteria and never reach this point.
            // So any leader entering this table is necessarily "newly packed in this run".
            //
            // This is **a note about the path, not a safety constraint** — swapping the two tiers would not produce
            // wrong data either (the first arrival would become this run's leader, but it would still hit the
            // cross-version tier and get the existing pack's StorageRef, and the final backfill copies that verbatim
            // to the aliases, so both orders yield byte-identical indexes). It is written down so nobody assumes
            // swapping them would break and is afraid to touch it, and so nobody assumes this order is required for
            // safety. The only reason for the current order is one less level of indirection.
            //
            // The silent exception: an alias does not enter a box, which means its source file is not opened a second
            // time this run — the existing invariant "every pack member is re-verified after compression" does not
            // hold for aliases. If it is rewritten or deleted inside the compression window, this run will not notice.
            // The index is still self-consistent: what is stored is the leader's content at diff time = the alias's
            // content at diff time, and the entry records the diff-time hash/mtime — this is not data loss, and the
            // next run re-backs it up as soon as the mtime changes, but this silent exception has to be written down.
            if (file is not null && klass.Category != FileCategory.SingleFile
                && file.FullHash is { } aliasHash && c.HeadHash is { } aliasHead && c.TailHash is { } aliasTail
                && aliasTable.TryClaim(aliasHash, file.Length, aliasHead, aliasTail, c.Path))
            {
                // Ends exactly like the tier above: take the existing "this entry did not change" path.
                // storageByPath is left for the final backfill — which pack the leader lands in is not known yet.
                file = null;
            }

            switch (klass.Category)
            {
                case FileCategory.SingleFile:
                    // Single file: as soon as the verdict is in, go straight to streaming compress-and-upload without waiting for anyone.
                    if (file is not null)
                        Enqueue(new WorkItem(file, null));
                    return;

                case FileCategory.CrossDirectoryGroup:
                    // The scan results are sorted in ordinal path order, the same order cross-directory packing
                    // uses, so the packs produced by "fill while diffing, seal when full" are byte-identical to
                    // those from "wait for the whole diff, then pack once".
                    if (file is not null)
                    {
                        // Split first, then pack. After the split each lane is **still in ordinal path order**
                        // (filtering a scan-ordered sequence does not change relative order), which happens to
                        // equal the planner's SplitByCompressibility result of "split into two groups first, then
                        // sort within each by path" — that is exactly why the two agree, and touching the sort on
                        // either side would break it.
                        var storeOnly = packOptions.DontCompress?.MatchesFileOrAncestorDir(file.Path) ?? false;
                        var side = storeOnly ? 1 : 0;

                        // All three limits share GroupingPlanner.GroupIsFull: this spot, the planner's pure
                        // function, and the re-splitting done before compression must be measured identically,
                        // otherwise the invariant "the actual output matches the planner" breaks
                        // (PipelinedBackupTests guards exactly that, using the pure function as the baseline).
                        if (crossPending[side].Count > 0
                            && GroupingPlanner.GroupIsFull(
                                crossPending[side].Count, crossBytes[side], crossPathBytes[side], file, packOptions))
                        {
                            Enqueue(new WorkItem(null, crossPending[side], storeOnly));
                            crossPending[side] = [];
                            crossBytes[side] = 0;
                            crossPathBytes[side] = 0;
                        }
                        crossPending[side].Add(file);
                        crossBytes[side] += file.Length;
                        crossPathBytes[side] += GroupingPlanner.EntryArgBytes(file.Path);

                        // Seal as soon as it is full; do not wait for the next file to push it over. The check
                        // above asks "would taking this one too go over the limit", which requires a next file;
                        // but the member count and path-bytes limits do not depend on who comes next (see
                        // GroupTakesNoMore), and the box is already settled at this moment. The cost of waiting
                        // is measured in scan order: if there are no cross-directory candidates for a long stretch
                        // afterwards (say, nothing but large files taking the single-file path), this box would
                        // hang around until the diff's final sweep sealed it, wasting the entire diff. The
                        // resulting boxes are unaffected — when this condition holds, the next file would
                        // necessarily make GroupIsFull hold as well.
                        if (GroupingPlanner.GroupTakesNoMore(crossPending[side].Count, crossPathBytes[side], packOptions))
                        {
                            Enqueue(new WorkItem(null, crossPending[side], storeOnly));
                            crossPending[side] = [];
                            crossBytes[side] = 0;
                            crossPathBytes[side] = 0;
                        }
                    }
                    return;

                default:
                    // Per directory: a box can only be sealed once the **whole directory** has been judged —
                    // unchanged files, unreadable ones, and ones whose hash turns out to match after all
                    // (MetadataOnly) must not go into a pack, and none of that is known before the diff.
                    var dir = klass.GroupKey!;
                    if (file is not null)
                    {
                        if (!dirPending.TryGetValue(dir, out var pending))
                            dirPending[dir] = pending = [];
                        pending.Add(file);
                    }
                    if (--dirRemaining[dir] == 0 && dirPending.Remove(dir, out var members))
                    {
                        // Packing is still the planner's pure function's job, only the input becomes "the files in
                        // this group that actually changed". Splitting by compressibility happens inside it too, so
                        // this directory may seal two boxes at once (one per compression mode).
                        foreach (var pack in planner.Plan(members, packOptions).Packs)
                            Enqueue(new WorkItem(null, [.. pack.Members.Select(ToPlannedFile)], pack.StoreOnly));
                    }
                    return;
            }
        }

        // For entries taking the single-file blob path the diff need not read the whole file to compute the full
        // hash: on that path the hash falls out of the compression read pass (StreamAndStageAsync) and afterwards
        // overwrites whatever the diff recorded. Classification only looks at path and length and is settled once
        // the scan finishes, so this verdict can be given before the diff even starts.
        bool DeferFullHash(string path) =>
            classification.ByPath.TryGetValue(path, out var k) && k.Category == FileCategory.SingleFile;

        DiffResult diff;
        try
        {
            try
            {
                diff = await differ.DiffAsync(
                    request.LocalRoot, scan, previous, opts.Diff, stopProducing.Token, diffTracker, OnChangeAsync,
                    DeferFullHash);

                // Final sweep: seal the boxes that never filled up. The two cross-directory lanes may each have a
                // remainder; the per-directory ones were in theory all sealed when their counter hit zero, and this
                // is only here to leave no survivors.
                for (var side = 0; side < crossPending.Length; side++)
                    if (crossPending[side].Count > 0)
                        Enqueue(new WorkItem(null, crossPending[side], StoreOnly: side == 1));
                // This fallback branch goes through the planner too: what it accumulated is the **unpacked** raw
                // list, and sealing it into one box directly would both mix the two compression modes and ignore
                // the three limits (blowing argv out on member count/path bytes means E2BIG). Going through Plan
                // is what keeps it measured the same way as the normal path.
                foreach (var leftover in dirPending.Values.Where(m => m.Count > 0))
                    foreach (var pack in planner.Plan(leftover, packOptions).Packs)
                        Enqueue(new WorkItem(null, [.. pack.Members.Select(ToPlannedFile)], pack.StoreOnly));
            }
            finally
            {
                diffTracker.Complete();
                work.CompleteAdding(); // no matter what, the consumers must learn "there is no more work", or they wait forever
            }

            // Everything from here to the settle below is inside this try for one reason: **it runs while the
            // consumers are still running**, and every one of these calls can throw. RecordUnreadableWarningsAsync
            // goes through the record gate, the operation log and the notifier — this repository has had
            // "SQLite Error 5: database is locked" out of exactly that path — and SetTotal/Settle call straight into
            // the caller's progress sink, which is somebody else's code. Left outside, one such throw walks out of
            // this method with three stages still live: `using var downstreamGone` is disposed on the way, the last
            // uploader's finally then calls CancelAsync on a disposed source and gets ObjectDisposedException, and —
            // the part that outlives the run — SettleAsync/DrainQueues never happen, so every queued entry's pool
            // quota stays booked on a process-wide singleton that gates compression for every backup on the machine
            // until the process restarts.
            //
            // Overlap disabled: only start working once all the work has been accumulated, back to the old "decide everything first, then upload" behavior.
            if (!overlap)
                StartConsumers();

            // The diff is done and no more work will appear in the queue → only now is the upload denominator settled, and only now does the percentage in the UI mean anything.
            uploadTracker.SetTotal(totalItems);
            reporter.Settle(totalItems);

            // Unreadable files count as neither changed nor deleted, and the index stage silently carries the old
            // entries forward — but the operator has to be told.
            // Placed before waiting on the uploads: this path runs every single time, and pushing it to the very end
            // would let one upload failure swallow these warnings along the way — precisely when "some files could not
            // be read" is what the operator most needs to hear.
            await RecordUnreadableWarningsAsync(request, scan, diff, ct);

            // Settle the consumers first, then check for a stop request: once stopped, no version index may be
            // written — writing a version for a run that did not finish amounts to claiming the files that were never
            // uploaded are backed up.
            //
            // There is a window between this check and the index write below: if the stop request arrives just after
            // the check passes, this run finishes writing the index and reports Completed, while SuspendAsync/
            // CancelAsync still return true once a terminal state is reached.
            // That is **inherent and acceptable**: at that moment the work really is all done, "stop" has nothing left
            // to stop, and the user gets a complete, successful backup — a better outcome than stopping at the door.
            // Do **not** add a lock to serialize the index write against stop requests: that lock would span three I/O
            // operations (write index + write info file + write local cache), and a stop request comes in synchronously
            // from the HTTP thread (Cancel() inside BackupRunner.RequestStop runs its callbacks on the current thread),
            // so the request from the user pressing stop would hang on a chain of cloud writes — all to remove a race
            // whose outcome was already correct.
            await SettleAsync(consumers);
            DrainQueues();
            if (control is { Stop: var stopKind } && stopKind != StopKind.None)
                throw await SettleStopAsync(stopKind);
            await Task.WhenAll(consumers);
        }
        catch (OperationCanceledException) when (stopProducing.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The diff was told to stop: either the upload side failed, or the user issued a stop.
            //
            // A stop that already settled inside the try above lands here as well: SettleStopAsync returns an
            // OperationCanceledException for every kind but Suspend, and RequestStop has already cancelled
            // stopProducing, so this filter matches it. SettleStopAsync therefore runs a **second** time —
            // PurgeInFlightAsync re-lists a container it has already emptied, and the journal is fsynced again with
            // nothing pending. Both are idempotent, so the outcome and the exception the user sees are unchanged;
            // the cost is one extra container listing on a path that is already winding down.
            control?.Gate.Downgrade();   // see the catch below; RequestStop has usually done it already, and it repeats safely
            await SettleAsync(consumers);
            DrainQueues();
            if (control is { Stop: var stopped } && stopped != StopKind.None)
                throw await SettleStopAsync(stopped);
            await Task.WhenAll(consumers);
            throw; // the consumers somehow did not throw: hand this cancellation up rather than silently calling it a success
        }
        catch
        {
            // The busy lock is only released when RunAsync returns, so returning early would leave a pile of
            // compression/uploads running outside the lock. Reached twice over on the paths that settle inside the
            // try above (a stop, or a consumer's fault surfacing from Task.WhenAll) — harmless, because both calls
            // are idempotent: SettleAsync on already-completed tasks and DrainQueues on drained channels do nothing.
            //
            // The gate comes down FIRST, and it is not tidiness: SettleAsync is an unbounded Task.WhenAll over
            // loops that park at that gate, and a user pause holds it with no timer and no patience by design
            // (PatienceExhausted answers no while the hold stands). So a fault out of the tail of this try — the
            // progress sink, RecordUnreadableWarningsAsync, the diff itself — arriving while the operator has the
            // run paused would wait here for as long as the pause lasts, with nothing able to end it: nothing on
            // this path cancels the consumers' tokens, and the only other Downgrade caller is RequestStop, which
            // the operator would have to reach for without being told why. The run would never return, so the busy
            // lock would never come back and the process-wide staging quota would stay booked.
            // Downgrading here says the true thing — this run is over and will never wait at that gate again —
            // and leaves the parked loops to drain what is queued and exit, exactly as they do when a fault lands
            // on a run nobody paused.
            control?.Gate.Downgrade();
            await SettleAsync(consumers);
            DrainQueues();
            throw;
        }

        // Wrap-up for this run's cross-box dedup: backfill the aliases hanging off each leader with the leader's
        // own StorageRef.
        // Done here rather than at hit time because the decision looks only at the **final state** — whether the
        // leader gets rewritten inside the compression window, becomes unreadable, or grows past the threshold and
        // switches to a single-file blob is only known once every consumer has finished. That is why the packing
        // side needs no concurrency primitive at all, and why there is no race of "the diff just attached an alias
        // while the consumer had already condemned the leader".
        var orphanAliases = new List<PlannedFile>();
        foreach (var (leaderPath, aliases) in aliasTable.AliasesByLeader)
        {
            // Two real paths plus one redundant safety net, covering every way a leader can go astray:
            //   overrides has it            → the content changed inside the compression window and a new hash was written;
            //   storage is not a pack or missing → it grew past the threshold and switched to a single-file blob, or the whole group became unreadable.
            //   postDiffUnreadable has it   → **unreachable** today: by the time a leader is marked by
            //     MarkPostDiffUnreadableAsync it has necessarily already been excluded from the stable pack,
            //     RecordPack never wrote storageByPath for it, and the "storage missing" clause above already covers
            //     this case completely. It is kept as a zero-cost safety net — against a future where these two
            //     things get decoupled (say, postDiffUnreadable grows its own path that does not go through
            //     storageByPath) and this check quietly stops holding.
            // If any of them hits, the alias's content is **no longer equal** to what the leader finally stored — it
            // must not point there, or the index points at someone else's content and restore produces wrong data.
            //
            // These three criteria assume that overrides / postDiffUnreadable / storageByPath are **append-only for
            // the whole run** (which is true today — nowhere in the code does anything Remove/TryRemove from them).
            // The moment someone adds a removal (say, "clear the failure marker after a successful retry"), a leader
            // that went astray would look intact at wrap-up time, the aliases would point at it anyway, and restore
            // would produce someone else's content — and no test would go red, because the existing tests pin "the
            // current state of the three tables", not "whether anything is ever removed from them". Before changing
            // how these three tables are written, think through whether this assumption still holds.
            var leaderStorage = storageByPath.GetValueOrDefault(leaderPath);
            if (leaderStorage is { Kind: "pack" }
                && !overrides.ContainsKey(leaderPath)
                && !postDiffUnreadable.ContainsKey(leaderPath))
            {
                // The whole StorageRef is copied verbatim: Ref and EntryName are the leader's, and the shape is
                // byte-for-byte what RecordPack always wrote, so retention cleanup / dead-weight compaction /
                // restore / check all need no changes.
                foreach (var a in aliases)
                    storageByPath[a.Path] = leaderStorage;
            }
            else
            {
                orphanAliases.AddRange(aliases.Select(a => new PlannedFile(a.Path, a.Length, a.FullHash)));
            }
        }

        // Dangling aliases: the leader went astray, but they are fine themselves and should not be dragged down
        // with it. Run them again and the first one naturally becomes the new leader. They no longer dedup against
        // each other — reaching this path requires the leader to be rewritten or become unreadable precisely inside
        // the compression window.
        //
        // The premise "this is rare anyway" deserves a discount: when a share drops off mid-run on a NAS, the
        // leaders in that subtree turn into postDiffUnreadable **in bulk**, the perfectly healthy aliases hanging
        // off them and scattered elsewhere dangle **in bulk**, and then get re-run **serially** in the loop below —
        // not necessarily a short stretch, possibly a whole subtree.
        //
        // The progress trade-off is harsher than "the UI sits at 100%": uploadTracker.BeginWork()/EndWork() do not
        // wrap this re-run (the pair belongs to the pipeline — BeginWork in the prober, the matching EndWork owed by
        // that item's WorkShare — and this loop runs after every stage has settled), onItem is the no-op below, so
        // ReportItem never runs and SetTransferred is never called — the bytes uploaded by the re-run are
        // **completely invisible** in the readings until uploadTracker.Complete() further down, and the in-flight
        // item count stays 0 throughout. Sitting at 100% while silently running for a long time is, for this user
        // base (mostly on NAS boxes, no command line available), the shape most likely to be mistaken for a hang.
        //
        // This stretch deliberately has no try around it: wrapping and catching would make BuildEntries produce
        // entries for these aliases with Length > 0 and Storage == null (Added has a null CarriedStorage, and
        // storageByPath does not have it either) — and that is the real shape of silent data loss. Letting it throw
        // is correct: the run fails, no index is written, the orphan packs are reclaimed by retention cleanup, and
        // the next run starts over. Without writing this down, someone will eventually "just add a try".
        //
        // onItem is static _ => { }: Enqueue is "once per WorkItem", while how many groups ProcessPackAsync splits
        // into by GroupIsFull is its own decision, so the outside cannot declare the matching count in advance and
        // patching the denominator by hand would only get it wrong. Zero in, zero out, naturally balanced. For
        // precedent see the spot above where a changed member switches to a single-file blob.
        //
        // storeOnly is computed from **the alias's own path**, written the same way as at packing time: the rule
        // matches by path, an alias and its leader may well live in different directories with different compression
        // modes, and a box can only have one.
        foreach (var side in orphanAliases.ToLookup(
                     f => packOptions.DontCompress?.MatchesFileOrAncestorDir(f.Path) ?? false))
        {
            // Sort in ordinal path order to stay consistent with the sorting discipline of the file path (see the
            // comments around crossPending): a dangling re-run is a standalone ProcessPackAsync call and is not
            // bound by the invariant "within a group it is still ordinal path order" (AliasesByLeader enumerates in
            // "order of first leader appearance", with aliases in insertion order inside that, so the whole thing is
            // interleaved). Sorting here is purely for consistency with the file path's discipline — it affects the
            // solid compression ratio and the group split points, not correctness.
            var pool = side.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            await ProcessPackAsync(request, pool, side.Key, addressing, localResolver, info,
                storageByPath, tailByPath, overrides, postDiffUnreadable, uploadScope, static _ => { },
                uploadTracker, state, control, ct);
        }

        // Same as with scanning/diffing: without forcing a terminal report, the bytes from the last batch would
        // never be published — throttling holds them inside the final window, and there is no further report after that.
        uploadTracker.Complete();

        var total = totalItems;
        var uploaded = uploadedItems;

        // 6. Build the second-level index of the new version
        var entries = BuildEntries(diff, storageByPath, tailByPath, overrides, postDiffUnreadable);
        var version = (info.Versions.LastOrDefault()?.Version ?? 0) + 1;
        var index = new VersionIndex
        {
            Version = version,
            Entries = entries,
            EmptyDirs = CarryEmptyDirs(scan, previous),
        };

        // 7. WriteIndex (upload the second-level index first)
        progress?.Report(new BackupProgress(BackupStage.WritingIndex, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
        var (indexBlob, indexVolumes) = await store.WriteIndexAsync(request.Account, request.Container, version, index, password, request.IndexTier, ct);
        // The local index cache Put is deferred until the info file commits successfully (see below), so that a write conflict on the info file does not leave a ghost cache entry for an uncommitted version.

        // 8/9. Finalize (atomically update the info file)
        progress?.Report(new BackupProgress(BackupStage.Finalizing, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
        var completedAt = DateTimeOffset.UtcNow;
        info.Versions.Add(new BackupVersion
        {
            Version = version,
            CreatedAt = completedAt,
            StartedAt = startedAt,
            IndexBlob = indexBlob,
            IndexVolumes = indexVolumes,
            Stats = new VersionStats(entries.Count, entries.Sum(e => e.Length), diff.ChangedFiles, diff.ChangedBytes),
        });
        if (trackedInfo is not null)
            await trackedInfo.WriteAsync(request.Account, request.Container, info, password, request.IndexTier, ct);
        else
            await store.WriteInfoAsync(request.Account, request.Container, info, password, request.IndexTier, ct);

        // The info file is committed → now write the version index into the local cache (a conflict would already have thrown in the previous step and never reach here).
        if (indexCache is not null)
            await indexCache.PutAsync(request.Account.Id, request.Container, version, identity, index, ct);

        // The index is committed and the journal has served its purpose. It must be deleted before cleanup:
        // keep it and cleanup will think this content is still "in flight" and not dare touch it; delete it earlier
        // than the info file commit and there is a gap where neither side claims it, and the freshly uploaded
        // content gets deleted as an orphan.
        if (control is not null)
            await control.CompleteAsync();

        // 10. Cleanup (drop expired versions and the data they exclusively own, per the retention policy, §10)
        progress?.Report(new BackupProgress(BackupStage.CleaningUp, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));

        // Both the version index and the info file are committed (steps 7/8/9 above), so this run is already a
        // complete, successful backup — the cleanup that follows is incidental maintenance, not part of "this
        // backup" itself. A stop request at this moment must never get this already-successful backup recorded as
        // Suspended/Canceled: that is exactly why SettleStopAsync is not used here.
        //
        // After Task 9 dismantled the old path where Cancel() cancelled the run's own ct directly, this became the
        // one remaining loose end: neither stopProducing nor working is wired into this stretch, so if the user
        // presses stop during the CleaningUp stage, dead-weight compaction still runs its downloads, recompression
        // and re-uploads to completion (possibly minutes to hours), while CancelAsync/SuspendAsync only return once
        // a terminal state is reached — and the HTTP request hangs for just as long.
        //
        // If the stop request has already landed **before entering cleanup**, skip the whole thing: the expired
        // versions that should be dropped and the dead weight that should be compacted will be picked up by the next
        // run's cleaner, which walks every version anyway, so nothing is permanently left uncleaned or uncompacted.
        // Do not remove this "skip" as an optional optimization — it is the entire reason this section exists.
        CleanupReport cleanup;
        // Non-null when cleanup was skipped because it could not reach the cloud. Carried out on the result so the
        // run summary can say so: a "success" that quietly stopped applying the retention policy is a success the
        // operator would rather hear about while the container is still small.
        Exception? cleanupError = null;
        if (control is { Stop: not StopKind.None })
        {
            cleanup = CleanupReport.Empty;
        }
        else
        {
            try
            {
                cleanup = await cleaner.CleanupAsync(request.Account, request.Container, password, new CleanupOptions
                {
                    Retention = request.Options.Retention,
                    DataTier = request.DataTier,
                    VolumeBytes = request.Options.VolumeBytes,
                    DeadWeightThreshold = request.Options.DeadWeightThreshold,
                    LocalRoot = request.LocalRoot,
                    AllowRepackDownload = request.Options.AllowRepackDownload,
                    // The compaction tacked onto the wrap-up uses **this run's own** seat: taking a separate one
                    // would inflate the denominator of the even split and shrink the quota of other backups running
                    // in parallel.
                    // ct is stopProducing.Token (not the bare ct): if the stop request arrives *while compaction is
                    // under way*, this must be interruptible too, not only at the single check before entering
                    // cleanup — which is exactly the behavior this stretch already enjoyed before Task 9, when the
                    // old Cancel() cancelled the run's own ct; this restores it.
                }, info, stopProducing.Token, stagingLease, sweepOrphans: control?.SweepNeeded ?? false);
            }
            catch (OperationCanceledException) when (stopProducing.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Compaction was interrupted midway: the version was committed long ago, so this is still a
                // successful backup, only the cleanup did not finish — this cancellation must not escape as-is, or
                // the branches at the top of RunAsync would treat it as Suspended/Canceled and claw back a
                // successful backup. What was skipped is left for the next run's cleaner.
                cleanup = CleanupReport.Empty;
            }
            // The same reasoning as the branch above, for the case nobody asked for: the cloud went away.
            //
            // It used to be that only a *cancellation* was recognised as "the version is already committed, this is
            // still a success". Anything else — a timeout, a 5xx, a dropped connection — walked past it into the
            // catch-all at the top of RunAsync and condemned the whole run. That is how a three-day 3 TB backup
            // came back as "Backup failed: Retry failed after 6 tries. (…exceeded the configured timeout of
            // 0:01:40.)": the data was already sitting in the cloud, the info file already listed the version, and
            // the only thing that had actually failed was the housekeeping tacked onto the end.
            //
            // And it struck at the end *because* of what cleanup is: the one stretch whose work grows with the size
            // of the backup — listing data/ and packs/ in full, deleting what retired versions exclusively own,
            // downloading and repacking archives for dead-weight compaction — while having none of the volume
            // splitting or per-item retry that carries the upload path through a bad patch of network. The bigger
            // the backup, the longer that stretch runs and the more certain it is to meet one.
            //
            // ct is left out of the filter on purpose: if the process itself is shutting down, the run really is
            // over and the suspend/cancel branches upstream must still get their exception.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                cleanup = CleanupReport.Empty;
                cleanupError = ex;
                // Warning, not Error: nothing was lost and nothing needs doing by hand — the next run's cleaner
                // walks every version anyway. Silence would be worse though: a container quietly growing past its
                // retention policy, with compaction never running, has to be explainable from the log alone.
                // CancellationToken.None for the same reason the cancel branch uses it: this line is the only
                // record that the housekeeping was skipped, and it must not be lost to a token firing mid-write.
                await Record(NotificationEvents.BackupFailure, $"backup:{request.Account.Id}/{request.Container}",
                    $"Backup succeeded, cleanup did not: {request.Name}",
                    $"Version {version} is committed and restorable; retention cleanup and dead-weight compaction were skipped and will be retried by the next run. {ex.Message}",
                    CancellationToken.None, OperationLogLevel.Warning);
            }
        }

        progress?.Report(new BackupProgress(BackupStage.Completed, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));

        // Count every category in a single pass: on a 500,000-entry index each extra Count(…) pass is another
        // 500,000 delegate invocations, and all these numbers live in the same list anyway.
        var newFiles = 0;
        var modifiedFiles = 0;
        var deletedFiles = 0;
        long deletedBytes = 0;
        var unreadableFiles = 0;
        foreach (var c in diff.Changes)
        {
            switch (c.Kind)
            {
                case ChangeKind.Added: newFiles++; break;
                case ChangeKind.Modified: modifiedFiles++; break;
                // A Deleted change is synthesized from a previous-version entry and always carries it, so Length is
                // right there for free; symlinks weigh 0 (their content is the Target string), which is what they
                // occupied at the source too.
                case ChangeKind.Deleted: deletedFiles++; deletedBytes += c.Previous?.Length ?? 0; break;
                case ChangeKind.Unreadable: unreadableFiles++; break;
                default: break;   // MetadataOnly / Unchanged: nothing was touched this run, so it stays out of the summary
            }
        }

        return new BackupRunResult(version, diff.ChangedFiles, diff.ChangedBytes,
            unreadableFiles + postDiffUnreadable.Count)
        {
            // Deliberately do **not** subtract files found unreadable post-diff from added/modified: subtracting
            // produces books that say "340 changed" while "128 + 209 ≠ 340", which nobody can make sense of without
            // the source in front of them. Unreadable files get their own line item so anyone can balance the books
            // themselves (see BackupSummaryTests).
            NewFiles = newFiles,
            ModifiedFiles = modifiedFiles,
            DeletedFiles = deletedFiles,
            DeletedBytes = deletedBytes,
            UploadedBytes = state.UploadedBytes,
            Cleanup = cleanup,
            CleanupSkipped = cleanupError?.Message,
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };
    }

    /// <summary>One Record call per unreadable file: the source stays the backup source, and the message keeps the
    /// verbatim reason the system gave (in-use / insufficient permissions / device read error all need different
    /// handling, and flattening them into "cannot read" leaves the operator with nowhere to start).
    /// Reuses the UnrecoverableError event — sharing one push channel with "kept changing during processing" (the
    /// only push channel is the notification webhook; the operation log is pull-only, and in a single-user unattended
    /// setting not pushing means nobody finds out), which also makes the persisted log level Error (decision: acceptable).
    /// When the same file stays unreadable across several consecutive runs, it must be reported again every run —
    /// silence would make the operator think the problem fixed itself (decision 8).
    /// <para>
    /// An unreadable **directory** pushes only one summary: one push per entry under it would make a 5,000-file
    /// directory into 5,000 webhooks, which is both a notification storm and a way to stall the backup on pushing
    /// (each one goes through _recordGate and waits on an HTTP round trip).
    /// What the operator needs to know is "this whole directory could not be read, affecting N files", not 5,000
    /// copies of the identical reason.
    /// </para></summary>
    private async Task RecordUnreadableWarningsAsync(
        BackupRequest request, ScanResult scan, DiffResult diff, CancellationToken ct)
    {
        var source = $"backup:{request.Account.Id}/{request.Container}";
        var unreadableDirs = scan.Unreadable.Where(u => u.IsDirectory).ToList();

        foreach (var dir in unreadableDirs)
        {
            var affected = diff.Changes.Count(c => c.Kind == ChangeKind.Unreadable && IsUnder(dir.Path, c.Path));
            await Record(NotificationEvents.UnrecoverableError, source,
                $"Directory unreadable, skipped: {dir.Path}",
                $"{affected} entr{(affected == 1 ? "y" : "ies")} carried forward from the previous version. {dir.Reason}", ct);
        }

        foreach (var c in diff.Changes.Where(c => c.Kind == ChangeKind.Unreadable))
        {
            if (unreadableDirs.Any(d => IsUnder(d.Path, c.Path)))
                continue; // already covered by the directory summary above
            await Record(NotificationEvents.UnrecoverableError, source,
                $"File unreadable, skipped: {c.Path}", c.UnreadableReason ?? "", ct);
        }
    }

    /// <summary>Whether path lies under dir. When dir is the root ("" or "."), it covers everything.</summary>
    private static bool IsUnder(string dir, string path) =>
        dir is "" or "." || path.StartsWith(dir + "/", StringComparison.Ordinal);

    /// <summary>The empty-directory list of the new version. An unreadable directory cannot have its contents
    /// listed this run, so neither it nor the empty directories below it appear in this scan — using the scan result
    /// directly would make those directories vanish into thin air after a restore, so the entries from the previous
    /// version that lie under an unreadable directory are carried over verbatim.</summary>
    private static List<string> CarryEmptyDirs(ScanResult scan, VersionIndex? previous)
    {
        var dirs = new List<string>(scan.EmptyDirs);
        var unreadableDirs = scan.Unreadable.Where(u => u.IsDirectory).ToList();
        if (unreadableDirs.Count == 0 || previous is null)
            return dirs;

        var known = new HashSet<string>(dirs, StringComparer.Ordinal);
        foreach (var d in previous.EmptyDirs)
        {
            if (unreadableDirs.Any(u => IsUnder(u.Path, d)) && known.Add(d))
                dirs.Add(d);
        }
        dirs.Sort(StringComparer.Ordinal);
        return dirs;
    }

    /// <summary>Found unreadable only after the diff (when the compress/upload stage reopens the source file):
    /// reuses exactly the same notification channel, the same UnrecoverableError event and the same message format
    /// as diff-time unreadability — the operator does not need to tell which stage the file failed to open in, only
    /// that "this file could not be read this run".</summary>
    private async Task RecordPostDiffUnreadableAsync(BackupRequest request, string path, string reason, CancellationToken ct) =>
        await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Account.Id}/{request.Container}",
            $"File unreadable, skipped: {path}", reason, ct);

    /// <summary>The content identity obtained in a single read pass: the three-segment hash + length + the mtime at read time, plus whether it is stored as raw bytes.</summary>
    private sealed record BlobContent(
        string FullHash, string HeadHash, string TailHash, long Length, DateTimeOffset Mtime, bool Raw);

    /// <summary>Where a single-file blob finally lands: the storage reference + the identity of the content actually stored.</summary>
    /// <param name="Resumed">Hit a journal record from the previous run showing it was already uploaded. It has been recorded once, so this run need not record it again.</param>
    private sealed record BlobPlacement(
        string Ref, bool Collision, int Volumes, IReadOnlyList<long> VolumeSizes, BlobContent Content,
        bool Resumed = false);

    /// <summary>
    /// Handle a single-file content-addressed blob: **one read pass** hashes and compresses at the same time, then uploads data/{hash}.
    /// <para>
    /// The order is the reverse of what it used to be. It was "compute the full hash → look up dedup → skip
    /// everything on a hit → otherwise read again to compress"; now it is "read, compress and hash together → the
    /// name is only known once compression finishes". The price is that content which already exists gets
    /// compressed for nothing, so a pre-filter that only reads the file head was added in front (length + head
    /// hash): only when the local index really has a candidate do we fall back to the old path (one read pass for
    /// the full hash, and on a hit not one byte is compressed). A first backup has no candidates at all and takes
    /// the single-pass fast path throughout.
    /// </para>
    /// <para>
    /// This also eliminates a whole class of race: the hash is now computed over **exactly the bytes that go into
    /// the archive**, so the two cannot disagree, and this path therefore no longer needs post-processing
    /// re-verification, nor a second open of the source file to build an override entry for the index (which is
    /// precisely where "content changed and then got locked" used to crash). The pack path still hashes before
    /// compressing and keeps its re-verification.
    /// </para>
    /// </summary>
    /// <param name="bypassQuota">Passed straight through to <see cref="StageBlobAsync"/>: true when this whole
    /// compress-and-upload runs on an uploader thread, which must not wait on the staging quota it is itself
    /// responsible for returning (see <see cref="StagingArea.StageWithoutBackpressureAsync"/>).</param>
    private async Task HandleBlobAsync(
        BackupRequest request, PlannedFile file, bool bypassQuota,
        BlobAddressScheme addressing, LocalDedupResolver localResolver,
        ConcurrentDictionary<string, StorageRef> storageByPath, ConcurrentDictionary<string, string> tailByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, ConcurrentDictionary<string, string> postDiffUnreadable,
        VolumeUploadScope uploadScope, Action<long> onItem, StageTracker uploadTracker, RunState state,
        BackupRunControl? control, CancellationToken ct)
    {
        var localPath = Local(request, file.Path);
        var storeOnly = request.Options.DontCompress?.MatchesFileOrAncestorDir(file.Path) ?? false;

        BlobPlacement placement;
        try
        {
            placement = await PlaceBlobAsync(
                request, file, localPath, storeOnly, bypassQuota, addressing, localResolver, uploadScope,
                uploadTracker, state, control, ct);
        }
        // This try wraps more than reading the source file — it also wraps compression, staging and upload — so the
        // exception type alone is not enough to conclude "the file cannot be read"; see
        // <see cref="TrySettleUnreadableAsync"/> for the whole reasoning, which is why that check is a method and
        // not three inline conditions.
        catch (Exception ex)
        {
            if (await TrySettleUnreadableAsync(request, file, localPath, ex, postDiffUnreadable, onItem, ct))
                return;
            throw;
        }

        await FinishBlobAsync(
            request, file, placement, storageByPath, tailByPath, overrides, onItem, control, ct);
    }

    /// <summary>
    /// Recognise "this run failed to store this file" and settle it as such: no blob is produced, the index carries
    /// the old entry forward or omits it, one warning is logged, and the run continues. Readable at diff time and
    /// unreadable afterwards (when compression / raw upload reopens the source file) is handled exactly like
    /// diff-stage unreadability — nothing is written to storageByPath/overrides, and this single file must never
    /// take the whole run down.
    /// <para>
    /// The exception type on its own is not enough to conclude it: <see cref="BlobUploader"/> classifies
    /// IOException as a retryable network error and rethrows it verbatim once the retry budget runs out, in exactly
    /// the same shape. Accepting on type alone would turn one NAS network outage into a pile of "file unreadable,
    /// carrying the old entry forward" while the run still reports success — the operator sees "Backup succeeded,
    /// 0 changed files" when in fact nothing was uploaded. Hence the second look at the source file: only a
    /// genuinely unreadable one gets degraded, and if it opens fine the exception keeps propagating and the run
    /// fails loudly. <see cref="ArchiveMembersMissingException"/> needs no such look — it is only thrown when 7z did
    /// not put this file into the archive intact, which is already proof, and it is thrown before the upload so no
    /// empty archive is left in the cloud.
    /// </para>
    /// <para>
    /// A predicate called from inside a <c>catch</c> rather than the exception filter it used to be, because
    /// **a filter cannot await** and both halves of the settle do: the warning goes through the record gate, and
    /// once the pipeline runs as three stages all three of them need this same check.
    /// </para>
    /// </summary>
    /// <returns>true when this exception was recognised and the item settled; false when the caller must rethrow.</returns>
    private async Task<bool> TrySettleUnreadableAsync(
        BackupRequest request, PlannedFile file, string localPath, Exception ex,
        ConcurrentDictionary<string, string> postDiffUnreadable, Action<long> onItem, CancellationToken ct)
    {
        if (ex is not ArchiveMembersMissingException
            && !((ex is IOException or UnauthorizedAccessException) && SourceUnreadable(localPath)))
            return false;
        await MarkPostDiffUnreadableAsync(request, file.Path, ex.Message, postDiffUnreadable, ct);
        onItem(file.Length);
        return true;
    }

    /// <summary>
    /// Everything that happens once this file's content has a home in the cloud, whether it got there by being
    /// uploaded or by being recognised as already present. Split out because those two answers arrive in different
    /// stages of the pipeline — a dedup or resume hit is settled by the probe and never reaches the compressor —
    /// and both have to record exactly the same things.
    /// </summary>
    private async Task FinishBlobAsync(
        BackupRequest request, PlannedFile file, BlobPlacement placement,
        ConcurrentDictionary<string, StorageRef> storageByPath, ConcurrentDictionary<string, string> tailByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, Action<long> onItem,
        BackupRunControl? control, CancellationToken ct)
    {
        // The content actually stored is not the same as what the diff saw: override the index entry with the
        // former, so that fullHash/length/head-and-tail hash match the bytes inside data/{hash}. All these values
        // come from the read pass just performed — **the source file is not reopened**.
        // When file.FullHash is empty (the diff deferred the full hash to this read pass) they necessarily differ,
        // so an override is written as usual — which is why the hash in the index always comes from "the bytes that
        // actually went into the archive" rather than the ones the diff saw.
        var content = placement.Content;

        // Journal: the upload (or the if-missing hit) has confirmed and returned, this block really is in the cloud
        // now, and only now do we dare record it.
        // The order cannot move — recording before uploading would record a block that does not exist, the next
        // recovery would skip it outright, and that is data loss.
        // Placed **before** the collision warning below: the warning hits the database and a webhook, an I/O
        // unrelated to this record, and its failure must not take the journal down with it — a journal append is a
        // local write of a few dozen bytes, far cheaper than the warning, and it is what the next run genuinely
        // relies on to decide "does this block need re-uploading", so it must not be lost to an unrelated failure.
        // CancellationToken.None is passed, not this run's ct: Task 9 cancels that same ct to suspend/cancel a run,
        // but at this moment the upload is long confirmed and the block is already in the cloud — cancelling this
        // write undoes nothing, it only makes the next recovery think the block was never uploaded and re-upload it
        // for nothing. The torn-write risk is the same: a cancelled write may truncate this line, and splicing it
        // into a new journal next time would corrupt the parse of it together with the following record.
        // A Resumed one is reused from an old volume, which is kept until this run succeeds, so there is no need to copy it again.
        if (control is not null && !placement.Resumed)
            await control.RecordBlobAsync(
                file.Path, placement.Ref, content.FullHash, content.HeadHash, content.TailHash, content.Length,
                content.Mtime, Math.Max(1, placement.Volumes), content.Raw, [.. placement.VolumeSizes],
                CancellationToken.None);

        // The collision warning is an after-the-fact report once the content has been processed/uploaded
        // successfully, and it never touches the source file again — which is why this whole method sits **outside**
        // every caller's unreadable-source catch: otherwise a failure of this notification (or of its internal log
        // write) would be misread as "the file cannot be read", causing content that was already uploaded
        // successfully to be carried forward as the old entry or dropped entirely from the index, while the cloud in
        // fact already holds that data.
        if (placement.Collision)
            await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Account.Id}/{request.Container}",
                $"Hash collision avoided: {file.Path}",
                $"Different content shares hash {content.FullHash}; stored at {placement.Ref}", ct);

        if (content.FullHash != file.FullHash)
            overrides[file.Path] = new EntryOverride(
                content.FullHash, content.HeadHash, content.Length, content.Mtime);

        storageByPath[file.Path] = new StorageRef
        {
            Kind = "blob", Ref = placement.Ref, Volumes = Math.Max(1, placement.Volumes), Raw = content.Raw,
            VolumeSizes = [.. placement.VolumeSizes],
        };
        tailByPath[file.Path] = content.TailHash;

        await LogFileAsync(request, file.Path, ct);
        onItem(file.Length);
    }

    /// <summary>Decide which blob this content finally lands on: probe through the pre-filter first (on a dedup hit
    /// nothing is compressed at all), otherwise do hashing + compression in one read pass, then use the resulting
    /// hash to decide dedup/collision avoidance and upload.
    /// <para>
    /// Kept as the single-worker composition of the three halves: the pipeline calls
    /// <see cref="ProbeAndResumeAsync"/>, <see cref="StageBlobAsync"/> and <see cref="UploadStagedBlobItemAsync"/>
    /// from three different loops, while the retry inside a pack demotion (see ProcessPackAsync) still wants "do
    /// the whole thing here and now", so this composition stays around as that caller's single-item entry point.
    /// </para>
    /// </summary>
    private async Task<BlobPlacement> PlaceBlobAsync(
        BackupRequest request, PlannedFile file, string localPath, bool storeOnly, bool bypassQuota,
        BlobAddressScheme addressing, LocalDedupResolver localResolver,
        VolumeUploadScope uploadScope, StageTracker uploadTracker, RunState state, BackupRunControl? control,
        CancellationToken ct)
    {
        // 1. Pre-filter + probe. On a hit it ends right here: not one byte is compressed or uploaded.
        if (await ProbeAndResumeAsync(request, file, localPath, localResolver, uploadTracker, control, ct) is { } hit)
            return hit;

        // 2 + 3. Compress (the future compress half) and upload (the future upload half), still called back to back
        // by this one worker — see the cut rationale on StageBlobAsync.
        var stagedBlob = await StageBlobAsync(
            request, file, localPath, storeOnly, bypassQuota, allowInPlace: true, uploadTracker, state, ct);
        return await UploadStagedBlobItemAsync(
            request, file, stagedBlob, addressing, localResolver, uploadScope, uploadTracker, state, control, ct);
    }

    /// <summary>Dedup/resume pre-filter for the single-file path: all three tiers settle the item without compressing a
    /// single byte, so this runs before anything is staged. Returns null when no tier matched, meaning the
    /// caller must fall through to <see cref="StageBlobAsync"/>.</summary>
    private async Task<BlobPlacement?> ProbeAndResumeAsync(
        BackupRequest request, PlannedFile file, string localPath, LocalDedupResolver localResolver,
        StageTracker uploadTracker, BackupRunControl? control, CancellationToken ct)
    {
        var headBytes = request.Options.Diff.HeadHashBytes;

        // Zeroth tier, and the only one that costs no read at all: the previous run uploaded this path and the file has
        // not been touched since. It is asked **first**, in front of the probe, because everything below it opens the
        // file — the pre-filter reads the head, and a candidate escalates to reading the whole thing to get a content
        // identity. That is the read this tier exists to avoid, and asking afterwards would avoid nothing.
        // The judgement it makes is the diff's own (length + mtime), not a relaxation of the content test below; see
        // JournalResume.FindUntouchedBlob for why that is sound and where its boundary lies.
        // The empty table is checked before the stat, not after: a run with no journal adopted (every ordinary backup,
        // a first one most of all) would otherwise pay one more stat per file to be told there is nothing to resume.
        // Exists is checked because FileInfo.Length throws on a file that has gone away, and where "the source
        // disappeared" gets settled is the existing route below, unchanged.
        if (control?.Resume is { IsEmpty: false } resume && new FileInfo(localPath) is { Exists: true } info)
        {
            var mtime = new DateTimeOffset(info.LastWriteTimeUtc);
            // The identity comes from the record, which is the identity of the bytes that were actually uploaded — the
            // same thing the tier below hands over, just without having re-derived it from the file. A record missing
            // any of the three hashes cannot supply one, and falls through to the content test like any other miss.
            if (resume.FindUntouchedBlob(file.Path, mtime, info.Length)
                is { FullHash: { } full, HeadHash: { } head, TailHash: { } tail } untouched)
                return new BlobPlacement(
                    untouched.Ref, false, Math.Max(1, untouched.Volumes), [.. untouched.VolumeSizes],
                    new BlobContent(full, head, tail, untouched.Length, mtime, untouched.Raw),
                    Resumed: true);
        }

        if (await ProbeForDedupAsync(localPath, headBytes, localResolver, uploadTracker, ct) is { } p)
        {
            // First tier: the copy the previous run already confirmed as uploaded. Both path **and** content must
            // match — after an interruption the file may well have been modified, and reusing on path alone would
            // write old content into the index as if it were new. (The tier above is not that: it reuses on path plus
            // metadata, and what it accepts is exactly what the diff accepts as unchanged. Anything it declines,
            // including every record written before the mtime field existed, arrives here.)
            if (control?.Resume.FindBlob(file.Path, p.FullHash, p.Length, p.HeadHash, p.TailHash) is { } done)
                return new BlobPlacement(
                    done.Ref, false, Math.Max(1, done.Volumes), [.. done.VolumeSizes], p with { Raw = done.Raw },
                    Resumed: true);

            // Second tier: an existing blob from another version (the original behavior, unchanged).
            if (localResolver.TryFindExisting(p.FullHash, p.Length, p.HeadHash, p.TailHash) is { } prior)
                return new BlobPlacement(prior.Ref, false, prior.Volumes, prior.VolumeSizes, p with { Raw = prior.Raw });
        }

        return null;
    }

    /// <summary>
    /// A raw item whose bytes are sent straight from the source file rather than from a staged copy, plus the
    /// metadata that has to still be true afterwards for that to have been safe.
    /// <para>
    /// The copy this replaces was not there to save a read. It was there to **fix the content**: the hash was
    /// computed while copying (<see cref="HashingStream"/>), so the bytes hashed and the bytes stored were the same
    /// set by construction — and that matters because a data blob's address *is* its content. It lives at
    /// <c>data/{fullHash}</c>, and if the bytes that reach the cloud are not the bytes that produced that hash, the
    /// container holds an object whose name contradicts its content. Nothing downstream would notice: dedup,
    /// restore and check all read the index, and the index would agree with the name.
    /// </para>
    /// <para>
    /// What replaces it is a bracket: this pair is stat'ed before the hashing read begins and again once the upload
    /// is over, however it ended, and any movement in either <see cref="Length"/> or <see cref="MtimeUtc"/> means
    /// the upload did not necessarily send what was hashed. That is the same test the diff trusts to call a file
    /// unchanged (see BackupDiffer's compare of length + last write time), applied to this item's own hash, its wait
    /// for an uploader, and its upload.
    /// </para>
    /// <para>
    /// That stretch is **not** short, and the bracket does not assume it is. It compares two stats taken around the
    /// whole of it rather than racing it: a file that has not moved between them did not move, however long it took.
    /// The length of the stretch changes only how often this trips — and on a store-only run it is long, because a
    /// raw item charges nothing to the staging pool and so nothing bounds how far the compressor may run ahead of
    /// the uploaders (see the note on stagedQueue in RunCoreAsync); for the tail of a large one it is hours. What
    /// that costs when it does trip is one wasted upload, a take-back and a retry through the copying route.
    /// </para>
    /// <para>
    /// It is not absolute, and deliberately so: a file rewritten during its own upload **and** restored to exactly
    /// its previous length and mtime would pass. That is the boundary the diff has lived with since the beginning.
    /// Paying a full copy of every raw file to pre-empt it is not worth it; never noticing would be unacceptable,
    /// which is what the undo-and-retry on the other side of the bracket is for.
    /// </para>
    /// </summary>
    /// <param name="StoreOnly">Carried so the fallback can re-stage this item the pessimistic way. It cannot be
    /// re-derived here — the rule matches by path and the caller that owns it is several stages upstream.</param>
    private sealed record InPlaceSource(string Path, long Length, DateTime MtimeUtc, bool StoreOnly)
    {
        /// <summary>Has the file moved under us since it was hashed? Returns the description of what moved, or null
        /// when it did not. A file that cannot be stat'ed at all counts as moved: the upload's bytes cannot be
        /// vouched for either way, and the safe reading of "I cannot tell" is the same as "it changed".</summary>
        public string? Moved()
        {
            var now = new FileInfo(Path);
            if (!now.Exists)
                return "it no longer exists";
            if (now.Length != Length)
                return $"length {Length} -> {now.Length}";
            return now.LastWriteTimeUtc != MtimeUtc
                ? $"last write {MtimeUtc:O} -> {now.LastWriteTimeUtc:O}"
                : null;
        }
    }

    /// <summary>
    /// The source file moved while it was being uploaded in place, so the object written under its content address
    /// may not hold the content that address names. Thrown **after** that object has been taken back — or, in the
    /// one case where it could not be, after that has been reported (see <see cref="UndoInPlaceUploadAsync"/>).
    /// <para>
    /// The item that hit it never gets to see it twice: its own caller answers it by re-staging through the copying
    /// route, which uploads a snapshot and is therefore immune, so a retry cannot throw it again. It does travel one
    /// step further in one case — a same-content peer waiting on this item's dedup reservation is failed with it,
    /// exactly as it would be by any other upload failure, because the alternative is letting it point its index
    /// entry at a blob that was just deleted.
    /// </para>
    /// <para>
    /// That peer is why this type is <c>internal</c> rather than private: it reaches the peer at
    /// <see cref="LocalDedupResolver.ResolveAsync"/>, which is **outside** the catch that answers it, so the only
    /// thing standing between it and a dead run is <see cref="TransientErrors.IsTransient"/> — and that judgement is
    /// deliberately made in one place for the whole codebase, which has to be able to name the type.
    /// </para>
    /// </summary>
    internal sealed class SourceMovedDuringUploadException(string path, string what)
        : Exception($"'{path}' changed while it was being uploaded ({what}); the upload was undone.");

    /// <summary>What <see cref="UploadStagedBlobItemAsync"/> needs to finish an item, and everything that must be
    /// released exactly once if it never gets that far.
    /// <para>
    /// Exactly one of the two is set. <see cref="Handoff"/> is the archive on disk plus the ownership of its pool
    /// quota — the copying route. <see cref="Source"/> is the raw route, which stages nothing and therefore owns no
    /// quota and no temp files, so it travels with a null handoff: the same shape a probe hit already uses.
    /// </para>
    /// </summary>
    private sealed record StagedBlob(BlobContent Content, StagedHandoff? Handoff, InPlaceSource? Source);

    /// <summary>Compress half of the single-file path: one read pass computes the three-segment hash while feeding
    /// the bytes into 7z (or copying them straight into a raw temp file).
    /// <para>
    /// Deliberately takes **no** <see cref="LocalDedupResolver"/> and **no** <see cref="VolumeUploadScope"/> —
    /// that is the whole point of the cut. <see cref="LocalDedupResolver.ResolveAsync"/> can block this call
    /// waiting on <c>Reservation.Completion</c> when another item in the same batch is uploading identical
    /// content, which is a wait on someone else's network; keeping that call (and the volume gate it feeds) out of
    /// this method is what lets compression and upload run as two independently-paced loops instead of one worker
    /// doing both in sequence.
    /// </para>
    /// </summary>
    /// <param name="bypassQuota">True when this call runs on an <b>uploader</b> — a thread the staging quota depends
    /// on to come back, and which therefore must never wait for it. See
    /// <see cref="StagingArea.StageWithoutBackpressureAsync"/> for what happens when it does.</param>
    /// <param name="allowInPlace">Passed straight through to <see cref="StreamAndStageAsync"/>; see there.</param>
    private async Task<StagedBlob> StageBlobAsync(
        BackupRequest request, PlannedFile file, string localPath, bool storeOnly, bool bypassQuota,
        bool allowInPlace, StageTracker uploadTracker, RunState state, CancellationToken ct)
    {
        var headBytes = request.Options.Diff.HeadHashBytes;
        var (content, staged, source) = await StreamAndStageAsync(
            request, localPath, file.Path, storeOnly, bypassQuota, headBytes, allowInPlace, uploadTracker, state, ct);
        // A raw item owns no archive and no quota, so it gets no handoff at all rather than an empty one: there is
        // nothing for a Dispose to hand back, and a handoff that owns nothing is one more thing to reason about on
        // every path that has to release one.
        return source is not null
            ? new StagedBlob(content, Handoff: null, source)
            : new StagedBlob(content, new StagedHandoff(staging, staged), Source: null);
    }

    /// <summary>Upload half of the single-file path: the name is only known once compression finishes, so dedup and
    /// collision avoidance are decided here, followed by the actual upload (or, on a dedup hit, no upload at all).
    /// </summary>
    private async Task<BlobPlacement> UploadStagedBlobItemAsync(
        BackupRequest request, PlannedFile file, StagedBlob stagedBlob, BlobAddressScheme addressing,
        LocalDedupResolver localResolver, VolumeUploadScope uploadScope, StageTracker uploadTracker, RunState state,
        BackupRunControl? control, CancellationToken ct)
    {
        var content = stagedBlob.Content;
        // Null on the raw route, which owns no archive — the same shape a probe hit's entry already travels with.
        var handoff = stagedBlob.Handoff;
        try
        {
            // Purely local decision: cross-version lookups against the map, and within this batch a reservation
            // coordinates things (same content shares ref/raw/volume count, different content steps aside). No cloud reads.
            var res = await localResolver.ResolveAsync(
                content.FullHash, content.Length, content.HeadHash, content.TailHash, uploadTracker);
            if (res.Exists)
            {
                var existing = res.Existing!;
                // Compressed for nothing, but the pool quota must go back immediately all the same.
                handoff?.MarkSettled();
                return new BlobPlacement(res.Ref, res.Collision, existing.Volumes, existing.VolumeSizes,
                    content with { Raw = existing.Raw }); // the existing blob's actual raw flag wins
            }
            try
            {
                // The raw route names the source file; every other route names the volumes in staged-temp.
                var files = stagedBlob.Source is { } src ? [src.Path] : stagedBlob.Handoff!.Staged!.Files;
                var bytes = stagedBlob.Source?.Length ?? stagedBlob.Handoff!.Staged!.Bytes;
                var (volumes, sizes) = await UploadStagedBlobAsync(
                    request, res.Ref, files, bytes, stagedBlob.Source, content, addressing, uploadScope,
                    uploadTracker, state, file.Path, control, ct);
                res.Complete(content.Raw, volumes, sizes); // wake the later arrivals with the same content in this batch and hand them the same storage info
                handoff?.MarkSettled();
                return new BlobPlacement(res.Ref, res.Collision, volumes, sizes, content);
            }
            catch (SourceMovedDuringUploadException ex) when (stagedBlob.Source is { } moved)
            {
                // The object that upload wrote has already been deleted (see UploadStagedBlobAsync) — it was named
                // for a hash its content may no longer have matched, and leaving it would be worse than never
                // having uploaded at all: the next run to produce that same hash would find it, skip its own
                // if-missing upload, and index somebody else's bytes.
                //
                // The claim on that address has to go back with it, and failing it is the only way this table
                // offers to do that. Fail does two things and both are wanted here: it wakes any same-content peer
                // in this run that is waiting on the address (they must never deduplicate onto a blob that has just
                // been deleted — that half is the existing contract for any failed upload), and it withdraws the
                // claim so the retry below, or a later file with the same content, can take the address afresh.
                res.Fail(ex);
                handoff?.MarkSettled(); // null here, but the pairing is stated once so it cannot drift
                //
                // One retry, through the copying route. It is immune by construction: it uploads a snapshot taken
                // under the compression lock, so whatever the source does next cannot reach the bytes in flight.
                // The asymmetry is the design's: the fast path is optimistic and verified, the fallback is
                // pessimistic and unconditional. Past this, an item that keeps failing is the existing
                // transient-error machinery's problem, not this method's.
                //
                // bypassQuota, because this always runs on the thread that just finished an upload. In the pipeline
                // that is an uploader, and everything in the staging pool is released by an uploader — one that
                // parks on the quota is waiting for itself (see StagingArea.StageWithoutBackpressureAsync). The
                // overshoot it permits is one archive, the same bound the transient retry beside it already accepts.
                var copied = await StageBlobAsync(
                    request, file, moved.Path, moved.StoreOnly, bypassQuota: true, allowInPlace: false,
                    uploadTracker, state, ct);
                // Terminates after exactly one level: `copied` carries a handoff and no source, so the branch above
                // it cannot be reached a second time.
                return await UploadStagedBlobItemAsync(
                    request, file, copied, addressing, localResolver, uploadScope, uploadTracker, state, control, ct);
            }
            catch (Exception ex)
            {
                res.Fail(ex);           // fail the waiters along with it; never dedup onto a blob that was not uploaded successfully
                handoff?.MarkSettled(); // the waiters have been answered; Dispose must not answer them twice
                throw;
            }
        }
        finally
        {
            // On a dedup hit this archive was compressed for nothing, but it still has to go back to the staging
            // area immediately — it occupies backpressure quota. Routed through the handoff (not a bare
            // staging.Release(staged)) so that the same call also covers the discarded-before-upload case once a
            // pipeline queues StagedBlob values between two independently-paced loops.
            handoff?.Dispose();
        }
    }

    /// <summary>
    /// Dedup pre-filter: read only the file head to compute the head hash, and if not even (length + head) matches
    /// anything in the local index return null so the caller takes the single-pass streaming fast path; only when
    /// there is a candidate do we read the whole file to derive the full content identity.
    /// </summary>
    /// <remarks>
    /// The whole stretch registers as "checking on disk" (<see cref="StageProgress.Checking"/>): when a candidate
    /// hits, the whole file gets read here, which for a few-GB file on a NAS is tens of seconds during which nothing
    /// is pushed and nothing is being waited on — without reporting it, the screen shows a motionless
    /// "1 object starting upload" while compression has not even begun.
    /// </remarks>
    private async Task<BlobContent?> ProbeForDedupAsync(
        string localPath, int headBytes, LocalDedupResolver localResolver,
        StageTracker uploadTracker, CancellationToken ct)
    {
        uploadTracker.BeginChecking();
        try
        {
            var length = new FileInfo(localPath).Length;
            var head = await hasher.HeadHashAsync(localPath, headBytes, ct);
            // The journal takes part in the pre-filter too: the adopted confirmed blocks were already folded into
            // localResolver's pre-filter set inside LocalDedupResolver.Build (see JournalResume.ConfirmedBlobs), so
            // asking it alone is enough here.
            var may = localResolver.MayDeduplicate(length, head);
            localResolver.NoteInFlight(length, head);
            return may ? await ReadContentIdentityAsync(localPath, headBytes, ct) : null;
        }
        finally
        {
            // Must be a finally: this path throws (unreadable file, cancellation), and one missed pairing leaves
            // this column stuck at an inflated number for the rest of the run — which is exactly how preparing got
            // burned once in this project (see StagingArea).
            uploadTracker.EndChecking();
        }
    }

    /// <summary>Read the file through once, computing the head/full/tail hashes and the length in a single pass
    /// (doing it in three separate reads would pay for two extra I/O passes for nothing).</summary>
    private static async Task<BlobContent> ReadContentIdentityAsync(
        string localPath, int segmentBytes, CancellationToken ct)
    {
        var mtime = new FileInfo(localPath).LastWriteTimeUtc;
        var streaming = new StreamingHasher(segmentBytes, segmentBytes);
        await HashOnlyAsync(localPath, streaming, ct);
        return Identity(streaming, mtime, raw: false);
    }

    /// <summary>Read the file through once, feeding every byte to the hasher and writing nothing anywhere. The one
    /// way this project opens a source file (<see cref="FileHasher.OpenRead"/>) — a FIFO at this path must fail
    /// rather than block the whole run inside open().</summary>
    private static async Task HashOnlyAsync(
        string localPath, StreamingHasher streaming, CancellationToken ct)
    {
        await using var source = FileHasher.OpenRead(localPath);
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
            streaming.Append(buffer.AsSpan(0, read));
    }

    /// <summary>One read pass: the source file's bytes stream into the hasher and the archive (or raw temp file) at
    /// the same time. So "the bytes that were hashed" and "the bytes that were stored" are the same set by construction.
    /// <para>
    /// The raw route is the exception, and <paramref name="allowInPlace"/> is what turns it on: there the stored
    /// bytes **are** the source bytes, so nothing is produced at all — the read only hashes, and the caller uploads
    /// the source file itself (see <see cref="InPlaceSource"/> for how the property above is preserved without a copy).
    /// </para>
    /// </summary>
    /// <param name="allowInPlace">False forces the copying route even where the raw predicate holds. The fallback
    /// after the in-place guard trips passes false: it needs a snapshot precisely because the source moved, and an
    /// in-place retry of an in-place failure could only trip the same guard again.</param>
    private async Task<(BlobContent Content, StagedItem? Staged, InPlaceSource? Source)> StreamAndStageAsync(
        BackupRequest request, string localPath, string entryName, bool storeOnly, bool bypassQuota, int segmentBytes,
        bool allowInPlace, StageTracker uploadTracker, RunState state, CancellationToken ct)
    {
        // Grab the metadata once before starting to read. The length decides raw; the mtime must be **the one from
        // before the read**: if the file is rewritten during the read, recording the earlier mtime makes the next
        // diff consider it changed and re-check it (the safe direction), while recording the later one would mean
        // that newer content never gets backed up again (the dangerous direction).
        // On the in-place route this pair is load-bearing for a second reason as well: it is the "before" half of
        // the bracket the upload is checked against, and taking it before the read (rather than after it) means the
        // bracket covers the hashing read too, not just the upload.
        var before = new FileInfo(localPath);
        var mtime = before.LastWriteTimeUtc;

        // Raw direct upload (PRD 3.3.2): store-only + unencrypted + no volume splitting needed → the source bytes
        // are the stored bytes, so no 7z wrapping is needed around them.
        var raw = storeOnly && string.IsNullOrEmpty(request.Password)
            && (request.Options.VolumeBytes is not { } vb || before.Length <= vb);

        var streaming = new StreamingHasher(segmentBytes, segmentBytes);

        if (raw && allowInPlace)
        {
            // No copy, no staging quota, no compression lock. What used to happen here was a byte-for-byte
            // duplicate of the source written into compress-temp, moved into staged-temp and uploaded from there:
            // two reads and a write for content identical to the source still sitting on disk, with the whole of
            // it booked against a quota every other backup on the machine shares.
            //
            // The compression lock is deliberately **not** taken. It serialises production into the temp area, and
            // this route produces nothing; holding it would make every raw file block every other run's compression
            // for the length of a full-file read, buying nothing. (The compress stage is a single worker, so at
            // most one of these reads is in flight per run regardless.)
            //
            // It is reported as Checking, the same column the dedup probe's whole-file read uses: this is an item
            // reading from disk, pushing no bytes and waiting on nothing, and for a few-GB file on a NAS it is tens
            // of seconds during which the screen would otherwise show nothing at all. Checking is a subdivision of
            // Uploading (= inWork - inStaging), and this route touches neither staging counter, so the item ledger
            // identity holds with the item counted exactly once.
            uploadTracker.BeginChecking();
            try
            {
                await HashOnlyAsync(localPath, streaming, ct);
            }
            finally
            {
                uploadTracker.EndChecking();
            }
            return (Identity(streaming, mtime, raw: true), null,
                new InPlaceSource(localPath, before.Length, mtime, storeOnly));
        }

        var name = StagedName(entryName);
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> produce = async (compressTemp, token) => raw
            ? [await CopyRawStreamingAsync(localPath, compressTemp, name, streaming, token)]
            : await CompressStreamingAsync(
                request, compressTemp, name, entryName, localPath, storeOnly, before.Length, streaming, token);
        var staged = bypassQuota
            ? await staging.StageWithoutBackpressureAsync(produce, state.Staging, ct, uploadTracker)
            : await staging.StageAsync(produce, state.Staging, ct, uploadTracker);

        return (Identity(streaming, mtime, raw), staged, null);
    }

    private static BlobContent Identity(StreamingHasher streaming, DateTime mtimeUtc, bool raw) => new(
        streaming.FullHash, streaming.HeadHash, streaming.TailHash, streaming.Length,
        new DateTimeOffset(mtimeUtc), raw);

    /// <summary>The file name inside the compression temp area. Before streaming it was the content hash, but now
    /// the hash is only known once compression finishes — this name lives for a few seconds in the temp area and its
    /// only requirement is not colliding at the same instant (compression is globally serial and the lock is only
    /// released after the output has been moved out).</summary>
    private static string StagedName(string entryPath) =>
        "b" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(entryPath))).ToLowerInvariant()[..16];

    private static async Task<string> CopyRawStreamingAsync(
        string localPath, string compressTemp, string name, StreamingHasher streaming, CancellationToken ct)
    {
        var dest = Path.Combine(compressTemp, name);
        await using var source = FileHasher.OpenRead(localPath);
        await using var file = File.Create(dest);
        await using var sink = new HashingStream(streaming, file);
        await source.CopyToAsync(sink, ct);
        return dest;
    }

    private async Task<IReadOnlyList<string>> CompressStreamingAsync(
        BackupRequest request, string compressTemp, string archiveName, string entryName, string localPath,
        bool storeOnly, long expectedBytes, StreamingHasher streaming, CancellationToken ct)
    {
        var output = Path.Combine(compressTemp, archiveName + ".7z");
        var result = await compressor.CompressStreamAsync(
            new StreamCompressionRequest(entryName, output, request.Password,
                VolumeBytes: request.Options.VolumeBytes, StoreOnly: storeOnly, ExpectedBytes: expectedBytes),
            async (stdin, token) =>
            {
                await using var source = FileHasher.OpenRead(localPath);
                await using var sink = new HashingStream(streaming, stdin);
                await source.CopyToAsync(sink, token);
                return streaming.Length;
            }, ct);
        return result.VolumeFiles;
    }

    /// <param name="files">What to send: the volumes in staged-temp, or — on the raw route — the one source file
    /// itself, which is uploaded from where it already sits.</param>
    /// <param name="inPlace">Non-null exactly when <paramref name="files"/> is the source file. It carries the
    /// metadata the upload is bracketed against; see <see cref="InPlaceSource"/> for why the bracket is there.</param>
    /// <returns>The blob's volume count and the byte size of each volume.</returns>
    private async Task<(int Volumes, IReadOnlyList<long> Sizes)> UploadStagedBlobAsync(
        BackupRequest request, string blobRef, IReadOnlyList<string> files, long fileBytes, InPlaceSource? inPlace,
        BlobContent content, BlobAddressScheme addressing, VolumeUploadScope uploadScope, StageTracker uploadTracker,
        RunState state, string sourceLabel, BackupRunControl? control, CancellationToken ct)
    {
        var sizes = files.Select(f => new FileInfo(f).Length).ToList();
        // The gate and the in-flight registration are both pushed down to each volume (VolumeUploadScope); this
        // only marks "this item has entered the upload phase" so it can be told apart from the ones still compressing.
        uploadTracker.BeginUpload(blobRef);
        try
        {
            var meta = new Dictionary<string, string>(
                addressing.Metadata(content.FullHash, content.Length, content.HeadHash, content.TailHash));
            if (content.Raw)
                meta["raw"] = "1";
            await ClearLeftoverVolumesAsync(request, blobRef, files.Count, fileBytes, uploadTracker, ct);
            control?.TrackInFlight(blobRef);
            await VolumeBlobIO.UploadAsync(
                uploader, request.Account, request.Container, blobRef, files,
                request.DataTier, request.Options.Upload, ct, meta, uploadScope,
                // Drop each volume from the temp disk as soon as it is uploaded. Never on the raw route: what was
                // "uploaded" there is the user's own file. (ReleaseFile ignores a path it never staged, so this is
                // belt as well as braces — but a callback that deletes files should not be handed a path it has no
                // business deleting on the strength of a lookup miss.)
                onVolumeUploaded: inPlace is null ? staging.ReleaseFile : null,
                label: sourceLabel);                     // show the source file path in the UI, not the content-addressed blob name

            // The other half of the raw route's bracket. The source was stat'ed before it was hashed; if either
            // half of that pair has moved since, the bytes just written under this content address are not
            // guaranteed to be the bytes that produced it — and an object whose name contradicts its content is a
            // corruption no restore would detect until it failed. So it is taken back, before anything is
            // recorded, and the caller retries the item through the copying route.
            if (inPlace?.Moved() is { } what)
            {
                await UndoInPlaceUploadAsync(request, blobRef, inPlace, what, control);
                // Thrown before AddUploaded/ConfirmUpload, like any other failed upload: these bytes are on their
                // way back off the wire, and the retry that follows will count its own.
                throw new SourceMovedDuringUploadException(inPlace.Path, what);
            }

            // Only settle once it has confirmed and returned. On an exception it is deliberately **not** settled:
            // that leftover is exactly what Stop now has to clear.
            control?.ClearInFlight(blobRef);
            // Recorded after the whole item is uploaded: a failure midway fails the entire run, at which point this
            // number is never used anyway, while recording volume by volume would make one retry count the same
            // bytes twice.
            state.AddUploaded(sizes.Sum());
            uploadTracker.ConfirmUpload(blobRef);
            return (files.Count, sizes);
        }
        // The same bracket, on the ending where the upload does **not** return. It is not an edge case: "the
        // upload threw" does not mean "nothing was committed". Azure acknowledges a commit over a connection that
        // can die on the way back, which is the routine NAS-to-Azure blip this whole codebase is built around —
        // RetryPolicy exhausting on status 0 comes out here with the object already in the container. A Stop now
        // landing between the commit and the stat arrives here as well, and it is answered the same way.
        //
        // Left to the success path alone, every one of those endings kept the object **and** sent the item round
        // again: the retry re-hashes the moved source, lands on a different address, and the first object is
        // orphaned rather than overwritten. Nothing removes it afterwards — PurgeInFlightAsync runs for Stop now
        // alone, and the closing orphan sweep only for a round that adopted or voided a journal or is the first on
        // its container — and a later run that legitimately produces that hash claims the address, finds the
        // single-volume path clearing nothing, and is told "already there" by the if-missing upload without a byte
        // being read. That is the one failure this codebase cannot detect after the fact.
        //
        // Filtered on inPlace because only the raw route can put bytes it did not produce at a content address:
        // every other route uploads a snapshot taken under the compression lock, whose content cannot move.
        // SourceMovedDuringUploadException is excluded because the branch above has already done exactly this.
        catch (Exception ex) when (inPlace is not null && ex is not SourceMovedDuringUploadException)
        {
            // Conditional on the same test the success path uses, not unconditional. An upload that failed with
            // the source still exactly as it was hashed leaves nothing this run cannot vouch for: whatever the
            // server committed, it committed from a file that has not changed, so it holds the content its address
            // names. Deleting it regardless would throw away a correct object that the retry's if-missing upload
            // would otherwise skip — on a multi-GB raw file, a full re-upload bought for nothing, on the failure
            // that happens most often.
            if (inPlace.Moved() is { } what)
                await UndoInPlaceUploadAsync(request, blobRef, inPlace, what, control);
            throw;
        }
        finally
        {
            uploadTracker.EndUpload(blobRef);
        }
    }

    /// <summary>
    /// Take back the object an in-place upload may have committed at <paramref name="blobRef"/>, the source having
    /// moved while it was in flight.
    /// <para>
    /// Deleting is safe precisely here and nowhere else: this run holds the exclusive in-run claim on this address
    /// (<see cref="LocalDedupResolver.ResolveAsync"/> hands out one at a time), and an address that any retained
    /// version index or any adopted journal already referenced would have been resolved as a dedup hit instead of
    /// being claimed — so nothing in this backup points at what is being removed. The only other thing that could
    /// be sitting here is an orphan of an interrupted earlier run, which no index and no adopted journal names
    /// either, and which the next run re-uploads if it is ever wanted again. That argument does not depend on how
    /// the upload ended, which is why the failing path can use it too.
    /// </para>
    /// <para>
    /// It runs on <see cref="CancellationToken.None"/> and clears the in-flight registration only after the delete
    /// returns: a cancelled cleanup would leave exactly the object this exists to remove, and until the delete
    /// returns, the registration is what makes "Stop now" clear it (see <see cref="PurgeInFlightAsync"/>). Clearing
    /// it afterwards is not optional either — a stale registration on an address a **later** item may legitimately
    /// claim and upload would have Stop now delete that one's good blob. A hard crash inside that window still
    /// leaves the object behind; it is the same window <see cref="ClearLeftoverVolumesAsync"/> has always had
    /// between its delete and its re-upload.
    /// </para>
    /// <para>
    /// The delete is retried, because the failure it is most likely to meet is the same blip that brought the
    /// caller here — under the same policy the cleanup path uses for its point operations (see RetentionCleaner),
    /// which is bounded by attempt count rather than by the upload path's two hours: this runs in the tear-down of
    /// one item, and a run that spends hours failing to remove one object helps nobody. Concretely that is
    /// <see cref="RetryOptions"/>'s defaults — five attempts, 200 ms doubling, so about three seconds of backoff
    /// plus whatever the SDK spends inside each attempt. An outage longer than that is answered by the report
    /// below rather than by waiting it out.
    /// </para>
    /// <para>
    /// A delete that ultimately fails is **said out loud** rather than swallowed. What is left behind is an object
    /// whose name may contradict its content, and no later check can tell: check and restore both read the index,
    /// and the index agrees with the name. So it goes into the operation log and out through the same
    /// <see cref="NotificationEvents.UnrecoverableError"/> channel as a hash collision, naming the address, because
    /// an operator with the address can delete it in one command and nothing else in this system will.
    /// </para>
    /// </summary>
    private async Task UndoInPlaceUploadAsync(
        BackupRequest request, string blobRef, InPlaceSource inPlace, string what, BackupRunControl? control)
    {
        try
        {
            await RetryPolicy.ExecuteAsync(
                t => factory.CreateServiceClient(request.Account)
                    .GetBlobContainerClient(request.Container)
                    .GetBlobClient(blobRef)
                    .DeleteIfExistsAsync(cancellationToken: t),
                options: null, ex => TransientErrors.IsTransient(ex), CancellationToken.None);
            control?.ClearInFlight(blobRef);
        }
        catch (Exception ex)
        {
            // The registration is deliberately left standing: it is the only thing that will still clear this
            // address if the operator answers with Stop now.
            try
            {
                await Record(
                    NotificationEvents.UnrecoverableError, $"backup:{request.Account.Id}/{request.Container}",
                    $"Could not remove a blob that may hold the wrong content: {blobRef}",
                    $"'{inPlace.Path}' changed while it was being uploaded ({what}), and deleting {blobRef} failed "
                    + $"({ex.Message}). Delete it from the container by hand: while it is there, any later backup that "
                    + "produces that same content will be told the address is already taken and will record it without "
                    + "reading a byte of it.",
                    CancellationToken.None);
            }
            catch
            {
                // This method must not throw, and this is the one statement in it that can: Record goes through
                // SQLite and a webhook, and this repository has had "database is locked" out of exactly that path.
                //
                // Both callers are on their way to an exception of their own, and a throw from here replaces it.
                // On the failing path that costs the diagnosis — the blip that ended the upload is what explains
                // the run, and a logging error in its place explains nothing. On the success path it costs more
                // than the message: the replacement escapes before `throw new SourceMovedDuringUploadException`
                // runs, and then satisfies the very filter of the catch below, so the take-back runs a second
                // time; and because what finally surfaces is not that exception, the catch that re-queues the item
                // through the copying route never matches and the file loses its retry altogether.
                //
                // So it is swallowed. The alternative is not "report it somewhere else": the operation log and the
                // notifier are the two channels this application has, and this line only runs when they are both
                // in the hands of something that just failed.
            }
        }
    }

    /// <summary>
    /// Before uploading a multi-volume archive, wipe any old volumes that may be left at this address.
    /// <para>
    /// Getting here means the local authority decided "there should be nothing at this ref". The cloud may still
    /// have something: a previous run died halfway through uploading (writing the index/info file is the very last
    /// step of the wrap-up, so the volumes that did land are in no index and in no local state), or **this run's**
    /// own item hit a transient error, waited a round in front of the suspend gate and came back for another try.
    /// The upload is if-missing (<see cref="IBlobUploader.UploadIfMissingAsync"/> hands the decision to the server
    /// via If-None-Match), so volumes that are already there get skipped — leaving that family of volumes in the
    /// cloud as **a mixture of two different compressions**.
    /// </para>
    /// <para>
    /// This used to return early for unencrypted backups, on the grounds that "the same input with the same
    /// parameters makes 7z produce byte-identical volumes". That premise **does not hold** for the single-file blob
    /// path, as measured (7-Zip 26.00):
    /// </para>
    /// <para>
    /// A single file goes through <c>-si</c>, reading from stdin (<see cref="CompressStreamingAsync"/>), and the
    /// stdin we feed it is **a pipe**. 7z cannot get the source file's mtime, so it writes the archive member's
    /// kMTime attribute as the time **at the moment of compression**. Two compressions therefore differ in: the
    /// 8-byte FILETIME in the last volume, and the two CRCs in the first volume's 32-byte signature header that
    /// cover the trailing header. The compressed data itself is byte-identical — but precisely because the first
    /// volume's CRC covers the last volume's header, splicing the first attempt's first volume onto the second
    /// attempt's last volume makes 7z fail outright with <c>Headers Error / Can't open as archive</c>.
    /// And the index claims this blob is perfectly fine: silent data corruption, not one missing upload.
    /// (Control group: the pack path compresses **by file name**, taking the mtime from the file on disk, and two
    /// runs really do produce byte-identical output — see SevenZipDeterminismTests. With encryption neither path is
    /// deterministic: AES picks a fresh random salt/IV every time.)
    /// </para>
    /// <para>
    /// So the only criterion left is "is it multi-volume", no longer whether it is encrypted. A single volume needs
    /// no clearing: it is a complete, self-consistent archive, and skipping it gives the same result as uploading a
    /// fresh one. Only multi-volume archives have that unsplice-able "half old, half new" shape, and multi-volume
    /// means large files, where one listing plus a few deletes is negligible against the bytes to be uploaded —
    /// whereas listing once for every new blob would mean hundreds of thousands of pointless round trips on a first backup.
    /// </para>
    /// </summary>
    private async Task ClearLeftoverVolumesAsync(
        BackupRequest request, string blobRef, int volumeCount, long archiveBytes,
        StageTracker uploadTracker, CancellationToken ct)
    {
        if (volumeCount <= 1)
            return;

        // Registered after the early return: with a single volume this does nothing, and flashing a column on the
        // screen for that case is pure noise.
        // Strictly speaking this stretch inspects volumes in the cloud rather than local files, but it still counts
        // under the "checking" column — giving it a column of its own is not worth it, and there is only one thing
        // to say: this item is checking, not transferring.
        // Pass the archive bytes along: this family of volumes is all sitting on disk waiting, and not one of them
        // can set off until this listing-plus-deletes round trip completes.
        uploadTracker.BeginChecking(archiveBytes);
        try
        {
            var cc = factory.CreateServiceClient(request.Account).GetBlobContainerClient(request.Container);
            await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, blobRef, ct))
            {
                // Listing by prefix also picks up collision-avoidance siblings (data/{hash}~1 and its volumes),
                // which are **different content** referenced by other index entries, and deleting them by mistake
                // is real data loss. IsVolumeOf only accepts this archive's own volumes.
                if (VolumeBlobIO.IsVolumeOf(blobRef, b.Name))
                    await cc.GetBlobClient(b.Name).DeleteIfExistsAsync(cancellationToken: ct);
            }
        }
        finally
        {
            uploadTracker.EndChecking(archiveBytes);
        }
    }

    /// <summary>
    /// The Stop now wrap-up: delete the content still registered as in flight, along with all of its volumes.
    /// The registration is only settled once an upload confirms and returns, so whatever is left in it is the
    /// "half uploaded, claimed by nobody" residue.
    /// Fully uploaded blocks are not included — they are kept for the next run to reuse, which is exactly what the user asked for.
    /// </summary>
    private async Task PurgeInFlightAsync(BackupRequest request, BackupRunControl control)
    {
        var container = factory.CreateServiceClient(request.Account).GetBlobContainerClient(request.Container);
        foreach (var blobRef in control.InFlight)
        {
            await foreach (var b in container.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, blobRef, CancellationToken.None))
            {
                // Listing by prefix also picks up collision-avoidance siblings (data/{hash}~1 and its volumes),
                // which are **different content** referenced by other index entries, and deleting them by mistake
                // is real data loss. IsVolumeOf only accepts this archive's own volumes.
                if (VolumeBlobIO.IsVolumeOf(blobRef, b.Name))
                    await container.GetBlobClient(b.Name).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
            }
        }
    }

    private async Task<EntryOverride> BuildOverrideAsync(
        string localPath, string fullHash, int headBytes, CancellationToken ct)
    {
        var info = new FileInfo(localPath);
        var head = await hasher.HeadHashAsync(localPath, headBytes, ct);
        return new EntryOverride(fullHash, head, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
    }

    /// <summary>
    /// Run a piece of work behind the suspend gate: on a transient error it waits in front of the gate, and once
    /// let through it repeats the whole thing verbatim, until it succeeds or the gate runs out of patience and
    /// demotes this run to suspended.
    /// <para>
    /// <paramref name="body"/> is the **unit** of retry, so it has to be re-entrant as a whole: a repeat must leave
    /// behind none of the previous attempt's half-finished output, and must not record the same thing twice. The
    /// single-file blob path passes a whole item, the pack path passes one **group** — so what the two call sites
    /// hand in differs by an order of magnitude in size, while the gate's waiting logic is exactly the same and
    /// should not be written out a second time.
    /// </para>
    /// </summary>
    /// <param name="ct">**The run's own** cancellation token, nothing else. The transient check uses it to tell
    /// "the network hiccuped" apart from "the user pressed cancel" — pass the wrong one and the cancel gets
    /// swallowed as a hiccup, silently making the button do nothing.</param>
    private static async Task WithPauseAsync(BackupRunControl? control, Func<Task> body, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await body();
                // A completed piece of work resets the consecutive-failure count: the gate's patience means
                // "nothing has gone right since the first hiccup", and without resetting after an intervening
                // success, a few scattered hiccups over several hours would add up to enough to declare the run suspended.
                control?.Gate.ReportSuccess();
                return;
            }
            catch (Exception ex) when (control is not null && TransientErrors.IsTransient(ex, ct))
            {
                if (!await control.Gate.WaitAsync(ex, ct))
                    throw new BackupSuspendedException(SuspendReason.AutoSuspended, ex.Message);
            }
        }
    }

    /// <summary>
    /// Handle **one sealed box** of groupable small files (§6/§9): compress + re-verify after compression, with
    /// members that changed during compression re-queued under their settled new hash (naturally landing in the
    /// next box) rather than pulled out as single files; only members that grow past the threshold, or that keep
    /// changing up to the attempt limit, are demoted to single files (the latter raises a warning).
    /// <para>
    /// The moment of sealing moved over to the diff side (see <see cref="GroupingPlanner.Classify"/> and the
    /// pipeline): this method used to receive "all groupable files of one directory" and pack them itself as it
    /// went; now it receives an already-packed box, so boxes can run concurrently instead of waiting for the
    /// previous box of the same directory to finish uploading.
    /// </para>
    /// </summary>
    /// <param name="storeOnly">This box's compression mode, fixed by compressibility at packing time and carried
    /// along with the box to here (see <see cref="WorkItem"/>).
    /// It is **not re-derived** here: the rule matches by path, an incoming box is homogeneous by definition, and
    /// re-deriving would only add one more place that could drift from the planner. Every sub-group produced by
    /// splitting, and the group recompressed after a member changed, all reuse this same value.</param>
    /// <param name="dispatch">How each of this pool's groups gets from "here are its members" to "its archive is on
    /// its way to the cloud". Null = do it here and now, one group at a time, which is what a caller that owns a
    /// whole worker wants (the dangling-alias re-run, and the follow-up a recompression can leave behind). The
    /// pipeline hands in a version that compresses the group, puts the archive on the staged queue and returns
    /// immediately, so that this loop can get on with the next group while uploaders drain the previous ones.
    /// Everything else stays on this side either way: the group split, the attempt counter, re-queueing a member
    /// that changed under 7z and demoting one that grew past the threshold are all decided before the upload and
    /// all mutate state this single invocation owns.</param>
    /// <param name="bypassQuota">True when this whole invocation runs on an uploader — the stranded-member tail is
    /// the one caller that does — so that everything it compresses here (a group of its own, or a member demoted to
    /// a single file) skips the staging-quota wait. An uploader parked on that quota is waiting for a release only an
    /// uploader can make; see <see cref="StagingArea.StageWithoutBackpressureAsync"/>. Every other caller runs on the
    /// compression stage or on the run's own thread, where waiting for room is exactly right.</param>
    private async Task ProcessPackAsync(
        BackupRequest request, IReadOnlyList<PlannedFile> pool, bool storeOnly,
        BlobAddressScheme addressing, LocalDedupResolver localResolver,
        BackupInfoFile info, ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, string> tailByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, ConcurrentDictionary<string, string> postDiffUnreadable,
        VolumeUploadScope uploadScope, Action<long> onItem, StageTracker uploadTracker,
        RunState state, BackupRunControl? control, CancellationToken ct, bool bypassQuota = false,
        GroupDispatch? dispatch = null)
    {
        var plan = request.Options.Plan;
        var threshold = plan.SingleFileThresholdBytes;
        var headBytes = request.Options.Diff.HeadHashBytes;
        var maxAttempts = Math.Max(1, request.Options.ProcessingMaxAttempts);
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new List<PlannedFile>(pool);

        while (queue.Count > 0)
        {
            // Take one group of unprocessed, within-limits files from the directory (at least one). All three
            // limits share GroupIsFull — this is the last check before handing off to 7z, and the MaxPackPathBytes
            // one directly decides whether argv blows up (E2BIG).
            var group = new List<PlannedFile>();
            long bytes = 0;
            long pathBytes = 0;
            var take = 0;
            while (take < queue.Count)
            {
                var f = queue[take];
                if (group.Count > 0 && GroupingPlanner.GroupIsFull(group.Count, bytes, pathBytes, f, plan)) break;
                group.Add(f); bytes += f.Length; pathBytes += GroupingPlanner.EntryArgBytes(f.Path); take++;
            }
            queue.RemoveRange(0, group.Count);

            // The pack id is taken **outside** the retry, one per group, and never changes once taken. Inside the
            // retry, the group would take a new id every time the gate let it through: the volumes the previous
            // attempt already uploaded would be reachable from no index at all, just occupying space in the
            // container, with a record in info.Packs pointing at each orphan.
            var packId = state.NextPackId();
            // These PlannedFiles all come from ToPlannedFile(PackEntry), so FullHash is non-null by construction —
            // deferred computation only happens for single-file blobs, and that path produces no packs.
            var members = group.Select(f => new PackEntry(f.Path, f.Path, f.FullHash!, f.Length)).ToList();

            // Resume: this whole box was already confirmed as uploaded by the previous run. The member sets have to
            // match one for one — RecordPackAsync below takes **this run's** member list for the group to write
            // PackInfo.Members/OriginalBytes and writes one index entry per member pointing at this pack. A superset
            // claims the archive holds members it simply does not have (restore cannot extract them, check reports
            // them missing, while the index insists they are there); a subset undercounts OriginalBytes, from which
            // dead-weight compaction misjudges how much live flesh is left in the box.
            //
            // RecordPackAsync still has to run (only the upload is skipped): this run's cross-box dedup wrap-up uses
            // storageByPath[leaderPath] to decide whether the leader went astray, and simply continuing here would
            // leave every alias hanging off this leader dangling and re-run.
            //
            // control is passed as null: this record still lives in the adopted journal and stays there until this
            // run successfully commits the index, so there is no need to copy it again.
            var journalMembers = members
                .Select(m => new JournalMember(m.Path, m.EntryName, m.FullHash, m.Length)).ToList();
            if (control?.Resume.FindPack(journalMembers) is { } donePack)
            {
                await RecordPackAsync(
                    request, donePack.Ref, members, donePack.VolumeSizes, donePack.StoreOnly, info,
                    storageByPath, control: null, ct);
                foreach (var m in members) await LogFileAsync(request, m.Path, ct);
                onItem(bytes);   // settle this group's slot and bytes as usual, otherwise progress never catches up with total
                continue;
            }

            // "Compress this group + upload it" is the retry unit (see RunGroupAsync): one hiccup redoes this group
            // from scratch and leaves the earlier groups alone. The journal append and the oplog write are **not**
            // inside it (see SettlePackAsync): they happen after the cloud has confirmed, and repeating them would
            // only double-count/miscount the uploaded bytes and the index member list, not make "this group"
            // re-entrant again. Re-queueing changed members stays outside too: it mutates queue and attempts, so a
            // repeat would queue the same member twice and double-count its attempts.
            //
            // Nothing is precompressed on this branch: this caller is the single worker that runs both halves back
            // to back, so there is never an archive already waiting when the group gets its turn. The dispatched
            // branch is the one that has one — it compressed the archive itself a moment ago.
            IReadOnlyList<PackEntry> changedMembers;
            if (dispatch is not null)
            {
                changedMembers = await dispatch(packId, members, bytes, ct);
            }
            else
            {
                var (changed, recordedMembers, recordedVolumes) = await RunGroupAsync(
                    request, packId, members, storeOnly, bypassQuota, precompressed: null,
                    uploadScope, uploadTracker, state, control, ct);
                await SettlePackAsync(
                    request, packId, recordedMembers, recordedVolumes, storeOnly, info, storageByPath, onItem,
                    bytes, control, ct);
                changedMembers = changed;
            }

            foreach (var m in changedMembers)
            {
                var local = Local(request, m.Path);
                string newHash;
                long newLen;
                try
                {
                    newHash = await hasher.FullHashAsync(local, ct);
                    newLen = new FileInfo(local).Length;
                    // The content changed (≠ the diff-time fullHash): write an index override so that fullHash/name/metadata match the new content.
                    overrides[m.Path] = await BuildOverrideAsync(local, newHash, headBytes, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The member was already excluded from this archive above (content changed or unreadable, same
                    // treatment); if this second attempt to confirm the new content still cannot read it (not a
                    // transient hiccup but genuinely locked / permissions revoked), stop pretending it can be
                    // re-queued for processing — degrade it to "unreadable" right here: no blob is produced, it
                    // enters no pack, and the index carries the old entry forward or omits it entirely.
                    // When a whole directory is unreadable at once, this step keeps the first member that hits it
                    // from denying the rest of the directory their chance to be processed.
                    // Do not call onItem() here: this group's slot was already reported once above, and reporting
                    // again would double-count.
                    await MarkPostDiffUnreadableAsync(request, m.Path, ex.Message, postDiffUnreadable, ct);
                    continue;
                }

                var n = attempts[m.Path] = attempts.GetValueOrDefault(m.Path) + 1;
                if (newLen >= threshold || n >= maxAttempts)
                {
                    // Grew past the threshold, or kept changing up to the attempt limit → single file (the latter raises a warning).
                    if (n >= maxAttempts)
                        await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Account.Id}/{request.Container}",
                            $"File kept changing during grouping: {m.Path}",
                            $"Stored as single file after {n} attempts", ct);
                    // Do not wrap the whole call in another try: HandleBlobAsync has its own correctly scoped catch
                    // around source reading / processing / upload (the post-successful-upload wrap-up is outside it),
                    // and the job here is simply "do not add another layer", not to re-catch failures it has already
                    // handled (Finding 1: a caller's catch should not enclose all of the callee's work).
                    // The gate is a different matter: it swallows nothing, it merely waits on a transient error and
                    // then repeats **the same** item. This item is a single file that fell out of the pool, the same
                    // shape as the single-file path the uploader's closure runs, so it uses the same retry unit —
                    // without putting it behind the gate, one hiccup on this single item could take the whole run down.
                    await WithPauseAsync(control, () => HandleBlobAsync(
                        request, new PlannedFile(m.Path, newLen, newHash), bypassQuota, addressing, localResolver,
                        storageByPath, tailByPath, overrides, postDiffUnreadable, uploadScope, static _ => { },
                        uploadTracker, state, control, ct), ct);
                }
                else
                {
                    queue.Add(new PlannedFile(m.Path, newLen, newHash)); // naturally lands in the next group
                }
            }
        }
    }

    /// <summary>
    /// How one group of a pack pool gets from "here are its members" to "its archive is on its way to the cloud".
    /// Whatever it does with the archive, what it owes back is the compression's verdict on the members — the ones
    /// that archive could not carry, which <see cref="ProcessPackAsync"/> then re-queues under their new hash or
    /// demotes to single files.
    /// </summary>
    /// <param name="bytes">The group's raw member bytes: the workload the settle has to clear, exactly once per
    /// group, or the remaining-time estimate never reaches zero.</param>
    private delegate Task<IReadOnlyList<PackEntry>> GroupDispatch(
        string packId, IReadOnlyList<PackEntry> members, long bytes, CancellationToken ct);

    /// <summary>
    /// One group's books, closed once the cloud has confirmed its archive: the index rows and the journal append
    /// for the members that really travelled in it, the verbose log line for each, and this group's one slot and
    /// workload in the progress ledger.
    /// <para>
    /// This sits **outside** the retry unit deliberately. Once <see cref="RunGroupAsync"/> returns successfully the
    /// cloud has confirmed the upload and the gate will not let this group run again, so it runs exactly once —
    /// whereas inside, a transient error thrown by the recording itself (a local-disk IOException, say) would
    /// trigger a recompression of the whole group: that recompression would count the already-uploaded bytes a
    /// second time in state.AddUploaded (distorting speed and ETA), and a single-volume pack would additionally be
    /// skipped by UploadIfMissing as "already exists", so the newly recompressed Members/VolumeSizes would go into
    /// the index while the container still holds the previous archive — the two disagree from then on, and only
    /// check/repair would ever find out.
    /// </para>
    /// <para>
    /// <paramref name="onItem"/> fires even when nothing was recorded. However many members were excluded from the
    /// stable pack (content changed, or unreadable), this grouping iteration corresponds to one slot reserved in
    /// total and must be reported **exactly once** — even when the whole group turned out unreadable together,
    /// otherwise uploaded never catches up with total and completion cannot show 100%. Reporting per group rather
    /// than per member is the other half of that: several members of one group failing together must not each claim
    /// a slot. The workload is settled the same way, in one go, or it hangs there forever and the remaining time
    /// never reaches 0. And precisely because this is outside the retry, however many times the group hiccuped and
    /// was recompressed the books are settled once.
    /// </para>
    /// </summary>
    private async Task SettlePackAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> recorded, IReadOnlyList<long> volumes,
        bool storeOnly, BackupInfoFile info, ConcurrentDictionary<string, StorageRef> storageByPath,
        Action<long> onItem, long bytes, BackupRunControl? control, CancellationToken ct)
    {
        // When every member of the group is unreadable there is nothing to record, and it is skipped naturally with
        // no special case.
        if (recorded.Count > 0)
        {
            await RecordPackAsync(request, packId, recorded, volumes, storeOnly, info, storageByPath, control, ct);
            foreach (var m in recorded) await LogFileAsync(request, m.Path, ct);
        }
        onItem(bytes);
    }

    /// <summary>
    /// One group's retry unit — "compress this group + upload it" — run behind the suspend gate.
    /// <para>
    /// The whole thing is re-entrant, and that is what keeps a hiccup from corrupting the container: the pack id is
    /// taken by the caller **outside** this method and never changes, so a repeat's output overwrites the same
    /// family of volumes (leftovers are cleared before uploading, see <see cref="UploadStagedPackAsync"/>). The
    /// repeat recompresses rather than reusing the failed attempt's archive — see the comment on emptying the slot
    /// below for why reuse would be wrong even if the archive were still on disk.
    /// </para>
    /// </summary>
    /// <param name="members">This group's members, as the caller cut them. Only a pass that compresses here uses the
    /// list, and then only through <paramref name="precompressed"/>'s filter — see <c>retryMembers</c> below.</param>
    /// <param name="precompressed">An archive the compression side already produced for this group, consumed by the
    /// first pass and by that pass alone. Null means "compress it here", which is what the single-worker caller
    /// wants; the pipelined caller compresses in one loop and uploads in another, and hands the result of the
    /// first loop in here.</param>
    /// <param name="bypassQuota">True when this runs on an uploader. Only a **retry** compresses in here — the first
    /// pass consumes <paramref name="precompressed"/> — and a retrying uploader that waits for staging room waits on
    /// a pool only an uploader can drain (see <see cref="StagingArea.StageWithoutBackpressureAsync"/>).</param>
    /// <returns>The members left out of the pack (changed or unreadable, for the caller to re-queue under their new
    /// hash), the members that really went into it, and that pack's volume sizes.</returns>
    private async Task<(List<PackEntry> Changed, IReadOnlyList<PackEntry> Recorded, IReadOnlyList<long> Volumes)>
        RunGroupAsync(
            BackupRequest request, string packId, IReadOnlyList<PackEntry> members, bool storeOnly, bool bypassQuota,
            GroupAttempt? precompressed, VolumeUploadScope uploadScope, StageTracker uploadTracker, RunState state,
            BackupRunControl? control, CancellationToken ct)
    {
        // The archive the next pass should upload instead of compressing one, and nothing more: the first pass takes
        // whatever the compression side handed in, every later one finds the slot empty and compresses afresh.
        var pending = precompressed;

        // What a recompression is allowed to put in this pack, and the answer is not always "this group's members".
        //
        // A caller that hands in an archive has **already acted on that pass's verdict**: by the time this method is
        // entered, ProcessPackAsync has re-read every member that pass excluded under its new hash and either queued
        // it into a later group or demoted it to a single file. Re-judging the original list would let a member that
        // was excluded then and looks stable now travel in this archive **as well as** in wherever the compressor
        // has meanwhile sent it. The same content stored twice, and worse: two threads writing storageByPath[path]
        // with nothing ordering them, so which row survives is a coin toss, and the losing shape is an index entry
        // carrying the member's new hash while pointing at a pack that recorded it under the stale diff-time one —
        // restore then pulls out content that does not match the entry beside it.
        //
        // Confining the retry to that pass's stable set makes it impossible by construction rather than by a check
        // after the fact. It is also the shape the code already trusts: the changed-member branch inside
        // CompressGroupAsync recompresses a subset under the same pack id for the same reason.
        //
        // The other direction — a member that pass kept and this one excludes — cannot be settled here, because only
        // the caller knows what it did with the verdict it was given; it is handled by the stranded-member tail in
        // RunCoreAsync's dispatch. Null precompressed has neither problem: that caller runs both halves back to back
        // and consumes the final pass's verdict, so the group's own member list is exactly right.
        var retryMembers = precompressed?.Stable ?? members;

        async Task<(List<PackEntry> Changed, IReadOnlyList<PackEntry> Recorded, IReadOnlyList<long> Volumes)> AttemptAsync()
        {
            var attempt = pending
                ?? await CompressGroupAsync(
                    request, packId, retryMembers, storeOnly, bypassQuota, uploadTracker, state, ct);
            // Emptied before the upload starts rather than after it ends, because this archive does not outlive the
            // pass either way — uploaded on success, dead on failure, disposed by the finally below in both cases.
            // Leaving the slot pointing at it would let a later pass "upload" an archive whose volume files the
            // staging area has already deleted; and even with the files still there, an archive whose members were
            // never re-verified against the source is not one to send.
            pending = null;
            try
            {
                var vols = await UploadGroupAsync(
                    request, packId, attempt, uploadScope, uploadTracker, state, control, ct);
                // The cloud confirmed, so nothing is left for Dispose to turn away. The pack path registers no dedup
                // reservation and so has no waiters to answer either way, but saying which of the two endings this
                // is stays the handoff's contract, and the pipeline queues these values between two loops where
                // the difference is what a discarded entry has to act on.
                attempt.Handoff.MarkSettled();
                return (attempt.Changed, attempt.Stable, vols);
            }
            finally
            {
                // Hand the quota back here, on every path — but there is exactly one path that has nothing else to
                // hand it back, and it is worth naming precisely, because a list of cases that are already covered
                // elsewhere reads as though this line could be dropped once any of them changes.
                //
                // That path is an upload that throws BEFORE its own try: UploadStagedPackAsync sizes the volumes
                // and calls BeginUpload above its try, and a throw from either skips the finally that would have
                // released the archive. Everything else is already accounted for. An upload that got as far as
                // sending releases in that same finally, cancellation included. A group 7z emptied carries either
                // a null archive (nothing to release) or a zero-file, zero-byte one. And a throw out of the
                // compression itself never reaches here at all — CompressGroupAsync runs above this try and cleans
                // up in its own catch.
                //
                // What the debt costs is why the one path is enough to keep it: it lives on a singleton shared by
                // every run and is at the same time the backpressure gate on output, so leaking it throttles
                // compression process-wide until a restart.
                attempt.Handoff.Dispose();
            }
        }

        (List<PackEntry> Changed, IReadOnlyList<PackEntry> Recorded, IReadOnlyList<long> Volumes) result = ([], [], []);
        await WithPauseAsync(control, async () => result = await AttemptAsync(), ct);
        return result;
    }

    /// <summary>
    /// What the compression half of the pack path produces for one group: the archive, wrapped in the
    /// <see cref="StagedHandoff"/> that owns its pool quota, plus the member split that goes with it — <c>Changed</c>
    /// are the members that may not travel in it (7z dropped them, or they changed under it while it was being
    /// written) and <c>Stable</c> are the ones it holds. That split is decided before anything is uploaded and does
    /// not depend on the upload, which is why it can be settled on this side of the seam and simply carried across.
    /// </summary>
    private sealed record GroupAttempt(
        StagedHandoff Handoff, List<PackEntry> Changed, IReadOnlyList<PackEntry> Stable);

    /// <summary>
    /// Compress half of the pack path: the pre-compression stat snapshot, the compression itself, and the
    /// post-compression re-verification that decides which members may not travel with the archive.
    /// <para>
    /// Deliberately takes **no** <see cref="VolumeUploadScope"/> and speaks to no blob client — that is the whole
    /// point of the cut. Nothing in here waits on the network, so this half runs as its own loop, paced only by
    /// the staging quota, while the uploaders work through what it has already produced.
    /// </para>
    /// <para>
    /// The archive comes back owned by the caller through the handoff, which is why the <c>using</c>-scoped guard
    /// that used to hold this stretch is gone (<c>StagingArea.Hold</c>, deleted with its last caller): the scope it
    /// guarded no longer ends inside this method. The hazard it existed for has not gone away — see the catch below.
    /// </para>
    /// </summary>
    /// <param name="bypassQuota">True when this runs on an uploader recompressing a group it has to resend, which
    /// must not wait on the staging quota only an uploader hands back (see
    /// <see cref="StagingArea.StageWithoutBackpressureAsync"/>). False on the compression stage, where waiting for
    /// room is the backpressure this whole design is built around.</param>
    private async Task<GroupAttempt> CompressGroupAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, bool storeOnly, bool bypassQuota,
        StageTracker uploadTracker, RunState state, CancellationToken ct)
    {
        // This snapshot may be hours removed from the diff: after sealing, the pack still queues in a bounded
        // queue, and how much work is stacked ahead of it and how many consumers there are is none of its business.
        // In the meantime a member may well be deleted (a build artifact) or have its permissions revoked, and Stat
        // would throw right there, taking the whole run down in exactly the shape this branch fixes. No new
        // mechanism: if it cannot be read, record the snapshot as null and let the existing "exclude the member"
        // path below handle it (the same path as "the content changed during compression": exclude it from the
        // archive → re-read the new content → if still unreadable, demote it).
        // Stat per member: a box has hundreds of members, and on a NAS that is not free. Reported under
        // "checking on disk", same as the post-compression pass.
        uploadTracker.BeginChecking();
        Dictionary<string, (long Mtime, long Length, int Mode)?> before;
        try
        {
            before = members.ToDictionary(m => m.Path, m => TryStat(Local(request, m.Path)));
        }
        finally
        {
            uploadTracker.EndChecking();
        }
        var (staged, missing) = await CompressPackTolerantAsync(
            request, packId, members, storeOnly, bypassQuota, uploadTracker, state, ct);
        try
        {
            // Members that 7z dropped from the archive must be marked excluded **directly**; the comparison
            // below cannot be relied on to catch them: that comparison looks at metadata and the content hash,
            // and revoking permissions changes neither mtime nor length — so the comparison would say "this
            // member did not change", a pack missing a member would be uploaded as-is, and the index would
            // claim it is in there.
            var changed = members.Where(m => missing.Contains(m.EntryName)).ToList();

            // Post-compression re-verification: metadata changed **and** the content hash changed → that member
            // changed during compression.
            //
            // The whole stretch registers as "checking on disk": the per-member stat is already not cheap, and
            // hitting one large changed member means reading it end to end to recompute the hash. This runs
            // after leaving the staging phase and before registering any in-flight volume, so it emits not a
            // single progress event — without reporting it, the screen shows a motionless "1 object starting
            // upload" for tens of seconds.
            // Pass this archive's bytes along: it is already compressed onto disk and counted in the
            // backpressure books, yet if the stretch below finds any member changed, the whole thing is thrown
            // away and recompressed (see the changed.Count branch below) — not one byte of it goes out.
            // So the UI subtracts it from "pending upload" and lists it separately as "checking".
            var checkingBytes = staged?.Bytes ?? 0;
            uploadTracker.BeginChecking(checkingBytes);
            try
            {
                foreach (var m in members)
                {
                    if (missing.Contains(m.EntryName))
                        continue;

                    var local = Local(request, m.Path);
                    bool exclude;
                    try
                    {
                        // Unreadable and content-changed have the same consequence for this pack: neither may
                        // stay in the archive to be uploaded.
                        // Already unreadable at snapshot time (before is null) falls into the same class, with
                        // no need for a second read to confirm.
                        exclude = before[m.Path] is not { } snapshot
                            || (Stat(local) != snapshot && await hasher.FullHashAsync(local, ct) != m.FullHash);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        exclude = true;
                    }
                    if (exclude)
                        changed.Add(m);
                }
            }
            finally
            {
                uploadTracker.EndChecking(checkingBytes);
            }

            // An empty `changed` is the good ending, and the one the caller reads it as: nothing was dropped and
            // nothing moved under the compression, so this group cleanly became one pack and every member of it
            // travels in the archive being handed over. (The two lists are not interchangeable even here — see
            // GroupAttempt: `members` is what was asked for, and it is only equal to what went in on this path.)
            if (changed.Count == 0)
                return new GroupAttempt(new StagedHandoff(staging, staged), changed, members);

            // Discard this archive; the stable members still become a pack; the changed ones are handled under
            // their new hash.
            // staged can only be null when 7z dropped every member of the group (not even an empty archive was
            // left), in which case there is nothing to release.
            if (staged is not null)
                staging.Release(staged);
            var stable = members.Where(m => !changed.Contains(m)).ToList();
            // Every member of the group was judged changed/unreadable: no stable members to record, and no second
            // archive to compress or to own — the handoff carries nothing, which is exactly the state
            // UploadGroupAsync reads as "there is no pack to send".
            if (stable.Count == 0)
                return new GroupAttempt(new StagedHandoff(staging, staged: null), changed, []);

            var staged2 = await CompressPackAsync(
                request, packId, stable, storeOnly, bypassQuota, uploadTracker, state, ct);
            return new GroupAttempt(new StagedHandoff(staging, staged2), changed, stable);
        }
        catch
        {
            // Between the compression above and the returns below lies a stretch that can throw: the hash
            // recomputation inside the re-verification, and the OperationCanceledException a cancel throws from
            // anywhere in it. Let that escape with the archive unreleased and its debt hangs on the staging
            // singleton forever (only a restart clears it) — and that singleton is the backpressure gate on output,
            // so enough of them stall compression for every run at once. This is the exception path only: once the
            // archive is wrapped in a StagedHandoff and returned, the caller owns it and releases it there.
            // Releasing twice is safe (ReleaseFile removes by path), so the changed-member branch above having
            // already released it before recompressing is not a problem.
            if (staged is not null)
                staging.Release(staged);
            throw;
        }
    }

    /// <summary>
    /// Upload half of the pack path: send this group's archive and report its volume sizes.
    /// <para>
    /// An empty <c>Stable</c> means every member of the group was judged changed or unreadable, so there is no pack
    /// to send — and that is also the only case in which the handoff carries no archive, which is what makes the
    /// <c>!</c> below safe rather than hopeful.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<long>> UploadGroupAsync(
        BackupRequest request, string packId, GroupAttempt attempt, VolumeUploadScope uploadScope,
        StageTracker uploadTracker, RunState state, BackupRunControl? control, CancellationToken ct)
    {
        if (attempt.Stable.Count == 0)
            return [];
        return await UploadStagedPackAsync(
            request, packId, attempt.Handoff.Staged!, uploadScope, uploadTracker, state,
            attempt.Stable.Count, control, ct);
    }

    /// <summary>
    /// Compress a group of members, tolerating 7z silently dropping members it cannot read: remove the dropped ones
    /// and recompress until the archive agrees with the member set, or the members run out. Returns the archive
    /// (null when the entire group is unreadable) and the names of the dropped entries.
    /// <see cref="ArchiveMembersMissingException"/> is not allowed to bubble straight out — in this module's
    /// established semantics an unreadable member means "exclude that member", not "fail the whole run".
    /// </summary>
    private async Task<(StagedItem? Staged, IReadOnlySet<string> Missing)> CompressPackTolerantAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, bool storeOnly, bool bypassQuota,
        StageTracker uploadTracker, RunState state, CancellationToken ct)
    {
        var remaining = members.ToList();
        var missing = new HashSet<string>(StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            try
            {
                return (await CompressPackAsync(
                    request, packId, remaining, storeOnly, bypassQuota, uploadTracker, state, ct), missing);
            }
            catch (ArchiveMembersMissingException ex)
            {
                var dropped = new HashSet<string>(ex.MissingEntries, StringComparer.Ordinal);
                // Not a single member could be removed, which means the reported names do not match the member
                // names (should not happen). Continuing the loop would spin forever, and rather than spin silently
                // it should fail loudly.
                if (remaining.RemoveAll(m => dropped.Contains(m.EntryName)) == 0)
                    throw;
                missing.UnionWith(dropped);
            }
        }
        return (null, missing);
    }

    /// <summary>Degrade under "this run failed to store this file": the index carries the old entry forward or omits it entirely, and one warning is pushed.</summary>
    private async Task MarkPostDiffUnreadableAsync(
        BackupRequest request, string path, string reason,
        ConcurrentDictionary<string, string> postDiffUnreadable, CancellationToken ct)
    {
        postDiffUnreadable[path] = reason;
        await RecordPostDiffUnreadableAsync(request, path, reason, ct);
    }

    private Task<StagedItem> CompressPackAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, bool storeOnly, bool bypassQuota,
        StageTracker uploadTracker, RunState state, CancellationToken ct)
    {
        var entries = members.Select(m => m.EntryName).ToList();
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> produce = (compressTemp, token) => CompressAsync(
            request, compressTemp, packId, entries, storeOnly, token);
        // An uploader recompressing a group it has to resend must not wait on the pool it is itself the only source
        // of releases for — see StagingArea.StageWithoutBackpressureAsync.
        return bypassQuota
            ? staging.StageWithoutBackpressureAsync(produce, state.Staging, ct, uploadTracker)
            : staging.StageAsync(produce, state.Staging, ct, uploadTracker);
    }

    /// <returns>The byte size of each of this pack's volumes (in .001..N order; recorded for verifying volume completeness/size).</returns>
    private async Task<IReadOnlyList<long>> UploadStagedPackAsync(
        BackupRequest request, string packId, StagedItem staged, VolumeUploadScope uploadScope,
        StageTracker uploadTracker, RunState state, int memberCount, BackupRunControl? control,
        CancellationToken ct)
    {
        var sizes = staged.Files.Select(f => new FileInfo(f).Length).ToList(); // grab the sizes before Release
        var blobName = $"packs/{packId}.7z";
        uploadTracker.BeginUpload(blobName);   // for the gate and the in-flight registration see VolumeUploadScope; both live at the per-volume level
        try
        {
            // Same discipline as for single-file blobs (see ClearLeftoverVolumesAsync): only for multi-volume, and
            // what it does is keep this family of volumes from mixing in the previous attempt's output. Pack ids are
            // unique within a run, so leftovers can only come from **this run's own** retry — and a retry is exactly
            // the path taken every time the suspend gate lets something through. A pack's members are compressed by
            // file name, so two runs usually produce byte-identical output, but "usually" is not something to gamble
            // data on: a member's mtime changing between the two attempts (with the content unchanged, so
            // re-verification does not exclude it) is enough to make the archive headers differ, and the spliced
            // result is just as unopenable.
            await ClearLeftoverVolumesAsync(request, blobName, staged.Files.Count, staged.Bytes, uploadTracker, ct);
            control?.TrackInFlight(blobName);
            await VolumeBlobIO.UploadAsync(
                uploader, request.Account, request.Container, blobName, staged.Files,
                request.DataTier, request.Options.Upload, ct, scope: uploadScope,
                onVolumeUploaded: staging.ReleaseFile,   // drop each volume from the temp disk as soon as it is uploaded
                // A box holds hundreds of files, too many to list — report the pack id and the member count.
                label: $"pack {packId} ({memberCount} files)");
            // Only settle once it has confirmed and returned. On an exception it is deliberately **not** settled:
            // that leftover is exactly what Stop now has to clear.
            control?.ClearInFlight(blobName);
            state.AddUploaded(sizes.Sum());   // same timing as the single-file path: recorded only when the whole item is uploaded
            uploadTracker.ConfirmUpload(blobName);
        }
        finally
        {
            uploadTracker.EndUpload(blobName);
            staging.Release(staged);
        }
        return sizes;
    }

    /// <param name="storeOnly">This box's compression mode, recorded into <see cref="PackInfo.StoreOnly"/>.
    /// Dead-weight compaction and repair recompression rewrite the archive under the same packId, and at that point
    /// all they hold is the surviving members and a pack id, not the original rule — without recording it on the
    /// pack, a store-only pack that outlives one version retirement gets recompressed under the default mode, with
    /// no sign of it whatsoever.</param>
    private static async Task RecordPackAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, IReadOnlyList<long> volumeSizes,
        bool storeOnly, BackupInfoFile info, ConcurrentDictionary<string, StorageRef> storageByPath,
        BackupRunControl? control, CancellationToken ct)
    {
        foreach (var m in members)
            storageByPath[m.Path] = new StorageRef { Kind = "pack", Ref = packId, EntryName = m.EntryName };

        var packInfo = new PackInfo
        {
            Blob = $"packs/{packId}.7z",
            Members = members.Select(m => m.FullHash).ToList(),
            OriginalBytes = members.Sum(m => m.Length),
            DeadBytes = 0,
            Volumes = Math.Max(1, volumeSizes.Count),
            VolumeSizes = [.. volumeSizes],
            StoreOnly = storeOnly,
        };
        lock (info.Packs)
            info.Packs[packId] = packInfo;

        // Journal: the pack is uploaded and confirmed. The member list has to be recorded in full, because recovery
        // relies on it to rebuild PackInfo — the info file is committed last, so after a crash it does not contain
        // this pack at all.
        // CancellationToken.None again: this run's ct is the one Task 9 cancels to suspend/cancel a run, but by now
        // the whole box is confirmed in the cloud, so cancelling this write rescues nothing and only makes the next
        // recovery think the box was never uploaded and re-upload it for nothing; a cancel midway through the write
        // can also leave a torn line that drags down the following record.
        if (control is not null)
            await control.RecordPackAsync(
                packId,
                [.. members.Select(m => new JournalMember(m.Path, m.EntryName, m.FullHash, m.Length))],
                volumeSizes, storeOnly, CancellationToken.None);
    }

    private static string Local(BackupRequest request, string relPath) =>
        Path.Combine(request.LocalRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The immediate parent directory (without the file name); the empty string for the root. Used for grouping by directory.</summary>
    private static string DirectoryOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    private static (long Mtime, long Length, int Mode) Stat(string path)
    {
        var info = new FileInfo(path);
        var mode = OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(path);
        return (info.LastWriteTimeUtc.Ticks, info.Length, mode);
    }

    /// <summary>Returns null when the metadata cannot be obtained (file gone, permissions revoked), which the caller handles as "this member must be excluded".</summary>
    private static (long Mtime, long Length, int Mode)? TryStat(string path)
    {
        try
        {
            return Stat(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the source file really is unreadable right now. Used to tell "the file cannot be read" apart
    /// from failures in the compression/staging/upload stack that also surface as IOException — the network above
    /// all: BlobUploader treats IOException as a retryable network error and rethrows it verbatim once the retry
    /// budget runs out, in exactly the same shape as "the file cannot be read". Opening successfully is not enough,
    /// one byte must actually be read: permission/media errors may only surface on the first real read. FileShare is
    /// set as wide as possible, so this only decides "can we read it" and does not judge on some other writer's
    /// behalf whether it should be writing.</summary>
    private static bool SourceUnreadable(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.ReadByte();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private async Task<IReadOnlyList<string>> CompressAsync(
        BackupRequest request, string compressTemp, string archiveName,
        IReadOnlyList<string> entries, bool storeOnly, CancellationToken ct)
    {
        var output = Path.Combine(compressTemp, archiveName + ".7z");
        var result = await compressor.CompressAsync(
            new CompressionRequest(request.LocalRoot, entries, output, request.Password,
                VolumeBytes: request.Options.VolumeBytes, StoreOnly: storeOnly), ct);
        return result.VolumeFiles;
    }

    /// <summary>A file whose content changed during processing: the settled hash/metadata override the diff-time index entry (§9).</summary>
    private sealed record EntryOverride(string FullHash, string? HeadHash, long Length, DateTimeOffset Mtime);

    private static List<IndexEntry> BuildEntries(
        DiffResult diff, IReadOnlyDictionary<string, StorageRef> storageByPath,
        IReadOnlyDictionary<string, string> tailByPath,
        IReadOnlyDictionary<string, EntryOverride> overrides,
        IReadOnlyDictionary<string, string> postDiffUnreadable)
    {
        var entries = new List<IndexEntry>();
        foreach (var c in diff.Changes)
        {
            // Unreadable: carry the previous version's entry forward (including Storage, so nothing is re-uploaded
            // and dedup is unaffected), appending only UnreadableAt. When the previous version does not have the
            // file, the entry is skipped entirely — there is no content to point at.
            // Readable at diff time but unreadable when the compress/upload stage reopens it (postDiffUnreadable)
            // gets exactly the same treatment: as far as the index is concerned, "this run failed to store the
            // content" is one and the same thing and should not grow a second set of rules.
            // This block must come before the Current is null check: entries derived from an unreadable directory
            // have **no** Current (the whole subtree was never scanned at all), and placed after it they would be
            // skipped as "no current state" and vanish from the new index — which is precisely the silent data loss
            // this change fixes.
            if (c.Kind == ChangeKind.Unreadable || postDiffUnreadable.ContainsKey(c.Path))
            {
                if (c.Previous is not null)
                    // Entries that already carry an UnreadableAt keep their original value: this field answers
                    // "since when has this content been unable to update", and refreshing it to UtcNow every run
                    // erases that answer, leaving only "it wasn't readable just now" either.
                    // Once some run reads it again, the entry is rebuilt normally and the field returns to null.
                    entries.Add(c.Previous with { UnreadableAt = c.Previous.UnreadableAt ?? DateTimeOffset.UtcNow });
                continue;
            }

            if (c.Kind == ChangeKind.Deleted || c.Current is null)
                continue;

            var ov = overrides.GetValueOrDefault(c.Path);
            var kind = c.Current.Kind == EntryKind.File ? "file" : "symlink";
            // Judge by the length **that finally goes into the index**, not the one the diff saw: when the content
            // shrinks to an empty file during processing, the override is the truth for this entry.
            var length = ov?.Length ?? c.Current.Length;
            entries.Add(new IndexEntry
            {
                Path = c.Path,
                Kind = kind,
                Length = length,
                Mtime = ov?.Mtime ?? c.Current.ModifiedAt,
                Permissions = c.Current.Permissions,
                HeadHash = ov?.HeadHash ?? c.HeadHash,
                // Tail hash precedence: for single-file blobs uploaded this run, use the value computed during the
                // compression pass (the most authoritative — those are the bytes that actually went into the
                // archive); otherwise use what the diff computed; otherwise inherit the previous version's entry.
                // The middle tier is new: pack members used to have none of these, so they could only dedup on three
                // criteria, inconsistent with the four used on the single-file blob path. The diff now computes it
                // for unchanged files too (see BackupDiffer.UnchangedAsync), so one run fills this in for old backups.
                TailHash = tailByPath.GetValueOrDefault(c.Path) ?? c.TailHash ?? c.Previous?.TailHash,
                FullHash = ov?.FullHash ?? c.FullHash,
                Target = c.Current.Target,
                // Zero-length regular files never carry a storage reference — including the ones **carried forward
                // from the previous version**.
                // Empty files in old backups were compressed and uploaded like everything else, and an empty file
                // that never changes (.gitkeep, __init__.py, lock files, …) is judged Unchanged every run, so
                // CarriedStorage passes that old reference down generation after generation: if it recorded the
                // wrong raw flag back then, the user has no reason whatsoever to touch that file and it would never
                // get better. Cutting it off here makes the next backup self-heal, and the old blob is subsequently
                // reclaimed by retention cleanup.
                Storage = kind == "file" && length == 0
                    ? null
                    : storageByPath.GetValueOrDefault(c.Path) ?? c.CarriedStorage,
            });
        }
        return entries;
    }


    private static BackupInfoFile NewInfo(BackupRequest request)
    {
        var encrypted = !string.IsNullOrEmpty(request.Password);
        return new BackupInfoFile
        {
            Backup = new BackupMeta
            {
                Name = request.Name,
                Description = request.Description,
                SourceRootHint = request.LocalRoot,
                Encrypted = encrypted,
                CreatedAt = DateTimeOffset.UtcNow,
                // Encrypted backup: the random salt is used for keyed addressing of data blobs (to prevent fingerprinting).
                KdfSalt = encrypted ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(16) : null,
            },
        };
    }

}
