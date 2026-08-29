using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The global upload slot gate. The capacity is exactly the "upload concurrency" from the settings; the only
/// difference is **who gets one first**: not first-come-first-served, but **by item age** — the volume family
/// that started uploading earliest wins, and only the surplus it cannot use goes to later items.
/// <para>
/// First-come-first-served spreads the slots thin across every item in flight. Compression is globally serial,
/// so the steady state is "1 item compressing + N items uploading" (N = the concurrency), each of those N
/// getting roughly one stream, which means **N items half-done at once**, every one of them crawling.
/// The cost is not just ugliness: the journal is written and the in-flight ledger cleared only after the whole
/// volume family is uploaded and the cloud has confirmed, so "how many items are half-done at once" is exactly
/// how much work an interruption throws away — <c>Stop now</c> deletes the leftover volumes of every in-flight
/// item, and suspend/crash makes them start the whole item over. Arbitrating by item age drops that number from
/// N to typically 1~2 items.
/// </para>
/// <para>
/// Throughput is unaffected: the slots stay fully loaded. When an older item's sliding window cannot fill them
/// (say it has only one volume left to send), the freed slot lands on the next item right away rather than
/// sitting idle.
/// </para>
/// </summary>
public sealed class VolumeUploadGate
{
    /// <summary>Sort key <c>(ticket, volume)</c>: item age first, then ascending volume number within one item.
    /// The latter is not optional tidiness — the in-flight list on screen reads in this order, and it only reads
    /// sensibly when each item advances one volume after another.</summary>
    private readonly PriorityQueue<TaskCompletionSource, (long Ticket, int Volume)> _waiters = new();
    private readonly Lock _lock = new();
    private readonly Func<int> _capacity;
    private long _nextTicket;

    /// <summary>Slots handed out and not yet returned. The bookkeeping is deliberately "in use" rather than the
    /// "free" it replaced: free had to be recomputed whenever the capacity moved, while in-use is independent of it
    /// and every question is answered by comparing the two.</summary>
    private int _inUse;

    /// <param name="capacity">Read **live**, on every admission decision, so a change to the upload-concurrency
    /// setting reaches a run already going — the same treatment the staging limit gets (see StagingArea's
    /// <c>stagedLimit</c>, and "decision 4" at its wiring in Program.cs). Raising it lets the extra waiters
    /// straight through on the next pump; lowering it hands out nothing more until enough volumes have finished,
    /// because a transfer already on the wire is not something to interrupt over a settings change.</param>
    public VolumeUploadGate(Func<int> capacity) => _capacity = capacity;

    public VolumeUploadGate(int capacity) : this(() => capacity) { }

    public int Capacity => Math.Max(1, _capacity());

    /// <summary>How many slots are free right now. For tests and diagnostics — this number is the only way to tell whether a slot has leaked.</summary>
    public int Free { get { lock (_lock) return Math.Max(0, Capacity - _inUse); } }

    /// <summary>Take a ticket. **One per volume family**, i.e. "the moment this archive started uploading".</summary>
    public long NextTicket() => Interlocked.Increment(ref _nextTicket);

    /// <summary>
    /// Ask for a slot. A returned Task that is **already completed** means the gate was free at the time and
    /// nothing queued at all — the caller uses that to decide whether to report "waiting for a slot"
    /// (see <see cref="VolumeUploadScope.RunAsync"/>).
    /// </summary>
    public Task AcquireAsync(long ticket, int volume, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);

        // Continuations must run asynchronously: Pump sets the result while holding the lock, so a synchronous
        // continuation would run straight into the caller's code inside that lock.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            // **Always enqueue**; no fast path for "the gate is free, just grab one". That fast path is exactly
            // the behaviour being fixed: it lets a freshly arrived new item bypass the queue and cut in front of
            // an older item already waiting there.
            _waiters.Enqueue(tcs, (ticket, volume));
            Pump();
        }
        return tcs.Task.IsCompletedSuccessfully ? tcs.Task : WaitAsync(tcs, ct);
    }

    private static async Task WaitAsync(TaskCompletionSource tcs, CancellationToken ct)
    {
        // Cancellation and Pump race over the same TCS: whoever sets it first wins, and the loser gets nothing.
        // Cancellation wins → this waiter becomes a corpse in the queue; the next time Pump pops it, TrySetResult
        // fails and it is skipped, so no slot is charged to it. Pump wins → the slot is already its own, the
        // await returns normally, the caller's own upload then throws because the token is already broken, and
        // the finally returns the slot as usual. Neither path leaks a slot.
        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
    }

    public void Release()
    {
        lock (_lock)
        {
            // The SemaphoreSlim this replaced threw SemaphoreFullException on an over-release; this line wires
            // that safety net back up. A double release is not a small thing: slots appear out of nowhere and the
            // in-flight stream count silently exceeds the concurrency the user set, and it fails silently — all
            // you see is a backup mysteriously eating more bandwidth than configured.
            // Asked of the in-use count, not of the free one: "more released than taken" is a statement about this
            // gate's own bookkeeping and must stay true across a capacity change, which the old comparison against
            // Capacity did not — lower the setting mid-run and free legitimately exceeds it while volumes drain.
            if (_inUse <= 0)
                throw new InvalidOperationException("Upload slot released more times than acquired.");
            _inUse--;
            Pump();
        }
    }

    /// <summary>Hand the free slots to the highest-priority **live** waiter. Must be called under the lock.</summary>
    private void Pump()
    {
        // A loop rather than a single pop: what comes out may be a cancelled corpse, in which case the slot has
        // not been handed over yet and the search must continue. And because the loop always re-tests the
        // condition, there is no "queue full of corpses + free slots = nobody gets one" deadlock — the corpses
        // get cleared out by subsequent Pumps.
        // The capacity is re-read every turn of this loop, which is what makes a raise land immediately: the
        // waiters queued under the old, smaller value are already sitting here, and the next release — or any
        // later acquisition, since Acquire always enqueues and pumps — lets as many of them through as the new
        // value allows, in ticket order.
        while (_inUse < Capacity && _waiters.TryDequeue(out var waiter, out _))
            if (waiter.TrySetResult())
                _inUse++;
    }
}

/// <summary>
/// The wrapper around every volume upload: ask the global gate for one **stream** slot, register the volume as
/// in-flight, and give it its own progress sink.
/// <para>
/// Slots are counted per **volume**, not per **item**. Counted per item, the thousands of volumes a 100 GB file
/// splits into occupy a single stream for the whole stretch — the "concurrency 5" set in the UI is meaningless
/// while a large file uploads, measured at 4–6 MB/s, which is exactly the ceiling of one TCP connection to Azure.
/// Counted per volume, the in-flight stream count always equals the configured value, no matter whether the queue
/// holds one huge file or ten thousand small ones.
/// </para>
/// <para>
/// The SDK's <c>TransferOptions.MaximumConcurrency</c> (block-level concurrency inside a blob) is deliberately
/// **left alone**: that layer would multiply with the slots here, and the 5 that was configured would no longer
/// equal any number anyone can explain. And the default volume size of 100 MB is below the SDK's 256 MB
/// single-shot threshold, so one volume is one PUT on one connection — "one volume = one stream" is exact, not
/// an approximation.
/// </para>
/// </summary>
public sealed class VolumeUploadScope(VolumeUploadGate gate, StageTracker tracker, Func<int> maxParallelPerItem)
{
    public VolumeUploadScope(VolumeUploadGate gate, StageTracker tracker, int maxParallelPerItem)
        : this(gate, tracker, () => maxParallelPerItem) { }

    /// <summary>How many volumes of a single item may be pushed up at once. This window is **not** here to let
    /// later small items squeeze in — slots are already arbitrated by item age (see <see cref="VolumeUploadGate"/>),
    /// and keeping latecomers out is exactly the intent.
    /// It guards something else: do not shove all thousand-odd volumes of one large file into the waiting queue
    /// at once, which wastes memory, and their staging files cannot be torn down until each has finished.</summary>
    /// <remarks>Read live, for the same reason the gate's capacity is: a window frozen at the value the run started
    /// with would cap one item at the old concurrency however many slots the gate had been raised to offer.</remarks>
    public int MaxParallelPerItem => Math.Max(1, maxParallelPerItem());

    /// <summary>
    /// The width of the sliding window: <see cref="MaxParallelPerItem"/> **twice over**. One half is this family's
    /// share of the wire; the other is its **relief line** on the gate — one waiter per slot it can hold.
    /// <para>
    /// The relief line is what makes age-based arbitration survive a changeover. A finished volume calls
    /// <c>Release</c> in <c>RunAsync</c>'s finally, while this family's next volume cannot queue up until the
    /// <c>WhenAny</c> continuation gets to run. The handover inside <c>Release</c> is synchronous, so a slot freed
    /// in between is given away on the spot to whoever is on the gate — and with nothing of this family's queued,
    /// that is always a newer item. One leak per completed volume, and the older item's priority is priority in
    /// name only.
    /// </para>
    /// <para>
    /// **It has to be as deep as the wave, not one volume deep.** A single spare waiter — the "baton" this
    /// replaced — covers one release and no more, and releases do not arrive one at a time. Volumes are equal-sized
    /// (100 MB by default) and share one link, so a family's whole in-flight set starts together and finishes
    /// together: up to <see cref="MaxParallelPerItem"/> releases inside one instant, every changeover continuation
    /// still queued behind them on the thread pool. The first lands on the baton and the **rest of the wave lands
    /// on the newer item** — on its volume 1 first, since the lowest volume within a ticket wins. That was not a
    /// starved-thread-pool corner but what a saturated link does on every wave, and on screen it read as a file
    /// that had only just finished preparing pushing its <c>(1/xxx)</c> in front of everything the previous file
    /// had left to send. A wave can never exceed the slots the family holds, so a relief line that deep closes it
    /// outright rather than narrowing it.
    /// </para>
    /// <para>
    /// The cost is the memory of a few more queued waiters per item, and nothing else. The window governs which
    /// volumes are **queued**, not which exist: compression has written the family's whole volume set to the pool
    /// before any of it is uploaded, and each volume is released from the pool as it lands, whatever the window.
    /// </para>
    /// <para>
    /// The two halves line up because <c>BackupOrchestrator</c> sizes <see cref="MaxParallelPerItem"/> to the gate's
    /// own capacity: the family that owns the gate holds exactly that many slots and queues exactly that many
    /// behind them.
    /// </para>
    /// </summary>
    public int WindowPerItem => MaxParallelPerItem * 2;

    /// <summary>Take a ticket, one per volume family. See <see cref="VolumeUploadGate.NextTicket"/>.</summary>
    public long NextTicket() => gate.NextTicket();

    /// <param name="ticket">This volume family's ticket, which decides its priority at the gate.</param>
    /// <param name="volumeIndex">Which volume within the family (0-based). Within one ticket they are let through in ascending order.</param>
    /// <param name="label">The name shown in the UI — the **source file path** or a description of the pack, not
    /// the blob name. Blobs are content-addressed (an HMAC when encrypted), and <c>data/9f2a3b7c…001</c> means nothing to the person at the screen.</param>
    /// <param name="volumeBytes">How big this volume is, so the UI can show "how much uploaded / how much in total".</param>
    /// <param name="owner">This family's blobRef (<c>data/{hash}</c> or <c>packs/{packId}.7z</c>).
    /// Finished volumes are recorded under it in the "landed in the cloud, item not yet settled" ledger; it stays the same across retries, so an abandoned attempt can be wiped out in one row.
    /// **Do not use ticket instead**: a ticket is taken fresh on every <see cref="VolumeBlobIO.UploadAsync"/> call, so a retry gets a new one,
    /// and using it as the ledger key would never find the previous attempt's row again.</param>
    /// <param name="staged">Whether this volume is a file in the staging pool. It has to come out of the "waiting to upload"
    /// columns while it is on the wire, and only pool files may — the raw in-place route sends the user's own file, which
    /// was never charged to the pool (see <c>StageProgress</c>'s in-flight subtraction).</param>
    public async Task RunAsync(
        string blobName, Func<IProgress<long>, Task> upload, CancellationToken ct,
        long ticket = 0, int volumeIndex = 0, string? label = null, long volumeBytes = 0,
        string? owner = null, bool staged = false)
    {
        // When the gate is free AcquireAsync returns an already-completed Task, and in that case we do **not**
        // report "waiting for a slot": marking it there would add one forced publish per volume for nothing — a
        // big item with thousands of volumes means thousands of them. Only a real queue-up is reported, and when
        // that happens not a byte is moving on screen, so that field is the only thing that can say what is
        // being waited on.
        // This line asks about cancellation first: without it, an already-cancelled run would happily finish
        // uploading this volume while the gate is free, and only then notice it should stop.
        ct.ThrowIfCancellationRequested();
        var acquire = gate.AcquireAsync(ticket, volumeIndex, ct);
        if (!acquire.IsCompletedSuccessfully)
        {
            // Count only. This volume's bytes need no ledger entry of their own: it is a file lying in the staging pool
            // with nothing on the wire, which is exactly what the pool's own file and byte counters already say, and
            // what StageProgress.WaitingToUploadBytes is derived from. Booking them here as well only created a second
            // debt to repay on every exit path.
            tracker.BeginWait(UploadWait.Slot);
            var acquired = false;
            try
            {
                await acquire;
                acquired = true;
            }
            finally
            {
                // EndWait calls straight into the publish the caller supplied (external code: writing the
                // database, pushing SSE), which may throw, and exceptions on this path are **deliberately**
                // propagated (see StageProgress). At the moment it throws the slot is already in hand, and just
                // letting it go means that slot never comes back — see the note at Release below for the shape
                // of the leak.
                try
                {
                    tracker.EndWait(UploadWait.Slot);
                }
                catch
                {
                    if (acquired)
                        gate.Release();
                    throw;
                }
            }
        }
        try
        {
            tracker.BeginItem(blobName, label, volumeBytes, owner, staged);
            // One ItemProgress per volume: DeltaProgress's baseline is per call, so if parallel volumes share
            // one instance each other's cumulative values look like a rewind. With the key, these bytes land on
            // the account of the right stream.
            await upload(tracker.ItemProgress(blobName));
        }
        finally
        {
            // Release needs a finally of its own; it cannot simply follow EndItem in the same block: EndItem
            // also calls publish, and one throw from it skips the following statement entirely. And this leak is
            // silent — the exception travels up into the "file cannot be read" catch-all and is swallowed there
            // (MarkPostDiffUnreadableAsync catches IOException), the backup keeps running, just one stream short;
            // accumulate as many as the configured concurrency and all uploads stall at the gate forever, showing
            // "nothing is uploading while the staging pool is piled high", and it never heals itself. BeginItem
            // moved into the try as well: if it throws, EndItem short-circuits because it cannot find the stream — harmless.
            try
            {
                // The bytes were already counted report by report during transfer, so adding the total again here would double-count.
                tracker.EndItem(blobName, 0);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

/// <summary>
/// Reading and writing single/multi-volume archives on blobs (§7). A single volume uses the base name;
/// multi-volume uses baseName.001/.002...
/// Shared by data blobs and packs; restore/check reassemble their downloads by the same rules.
/// </summary>
public static class VolumeBlobIO
{
    /// <summary>
    /// Upload the volume files produced by compression. Single volume → baseRef; multi-volume → baseRef.001,
    /// baseRef.002...
    /// <para>
    /// Every volume enters the sliding window, **order irrelevant** — they queue in file order, and nothing is
    /// required about which one lands first.
    /// </para>
    /// <para>
    /// .001 used to be sent last, on its own, as the "the whole family is here" commit marker, so that a partial
    /// upload would not be mistaken for an existing one by dedup's existence check. That check (a HEAD comparison
    /// against the cloud) has been deleted — dedup always goes through the local authoritative index and never
    /// asks the cloud. And that marker was never as cheap as the old comment computed: negligible measured against
    /// a thousand volumes, yes, but with the default 100 MB volumes and concurrency 5, a 100–500 MB file splits
    /// into exactly 2–5 volumes and that final serial single-volume trip doubled the item's upload time — and
    /// that size band is the bulk of a real backup. Leftovers from interruptions are handled elsewhere: per-volume
    /// if-missing fills in what is missing, and encrypted multi-volume archives are cleared before upload
    /// (see BackupOrchestrator.ClearLeftoverVolumesAsync).
    /// </para>
    /// </summary>
    /// <param name="scope">Per-volume concurrency slots and progress registration (see <see cref="VolumeUploadScope"/>).
    /// When null it degrades to the old behaviour: serial, unthrottled, no progress reporting — for repair/replace calls that are not on the main backup path.</param>
    /// <param name="onVolumeUploaded">Called the moment a volume finishes, with its **local** file path.
    /// The backup path hangs per-volume staging release on this: if deletion waited for the whole family, the temp
    /// disk peak would equal the entire archive (a 100 GB file would need 100 GB of temp space), and the watermark would sit against the ceiling the whole time and choke compression.</param>
    public static async Task UploadAsync(
        IBlobUploader uploader, Account account, string container, string baseRef,
        IReadOnlyList<string> volumeFiles, AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null, VolumeUploadScope? scope = null,
        Action<string>? onVolumeUploaded = null, string? label = null)
    {
        // For multi-volume, mark which volume this is in the label: a large file splits into thousands of
        // volumes, and showing only the path would repeat the same line thousands of times with no sign of
        // progress.
        string LabelFor(int index) => label is null
            ? baseRef
            : volumeFiles.Count > 1 ? $"{label} ({index + 1}/{volumeFiles.Count})" : label;

        // One ticket per volume family, i.e. "the moment this archive started uploading" — the gate uses it to
        // give slots to the older family first rather than spreading them thin across every item in flight (see
        // VolumeUploadGate). Each group of a pack calls this method once and therefore takes its own ticket;
        // groups are serial with respect to each other anyway, so that is correct.
        var ticket = scope?.NextTicket() ?? 0;

        async Task One(string name, string file, int index)
        {
            if (scope is null)
                await uploader.UploadIfMissingAsync(account, container, name, file, tier, retry, ct, metadata);
            else
                await scope.RunAsync(
                    name,
                    p => uploader.UploadIfMissingAsync(account, container, name, file, tier, retry, ct, metadata, p),
                    ct, ticket, index, LabelFor(index), SizeOf(file), baseRef,
                    // The same discriminator the per-volume release uses two arguments down the call: a caller that
                    // wants its volumes released as they go is a caller whose volumes came out of the pool, and the raw
                    // in-place route — the only one that passes no callback — is uploading the user's own file.
                    staged: onVolumeUploaded is not null);
            onVolumeUploaded?.Invoke(file);
        }

        static long SizeOf(string file)
        {
            try { return new FileInfo(file).Length; } catch { return 0; }
        }

        if (volumeFiles.Count == 1)
        {
            await One(baseRef, volumeFiles[0], 0);
            return;
        }

        // Sliding window: one volume finishes, one more starts. With batched Task.WhenAll, the slowest volume in
        // a batch makes the other streams spin idle waiting for it — volumes never take the same time (retries,
        // block parallelism, server-side throttling all differ), and on screen it shows as "5 streams counting
        // down to 0 one by one, then 5 more appearing" instead of holding steadily at 5.
        // The window width is still bounded: thousands of volumes must not be shoved into the global gate's
        // waiting queue at once, which wastes memory, and the staging files of queued volumes cannot be torn
        // down until each has finished uploading (see VolumeUploadScope).
        // The width is MaxParallelPerItem * 2 (see WindowPerItem): one half equals the gate capacity and lets this
        // family eat every slot on its own, and the other half is the relief line that catches the slots back at a
        // changeover — one waiter per slot, because a whole in-flight set of equal volumes on one link finishes in
        // one wave and a single spare waiter catches only the first of them.
        var window = scope?.WindowPerItem ?? 1;
        var started = new List<Task>(volumeFiles.Count);
        var running = new List<Task>(window);
        for (var i = 0; i < volumeFiles.Count; i++)
        {
            if (running.Count >= window)
            {
                var done = await Task.WhenAny(running);
                running.Remove(done);
                // Once a volume dies, no new ones start. The ones already in flight are still awaited below —
                // letting go halfway would leave orphan tasks nobody observes, still holding gate slots and temp
                // disk. The exception itself is left for WhenAll to throw, matching the semantics of the old
                // batched version: throw after everything has settled, and throw the first one.
                if (done.IsFaulted || done.IsCanceled)
                    break;
            }
            // WhenAny hands back **one** of the tasks that finished, and when several land together which one is
            // unspecified — so a volume can fail while the loop is looking at a sibling that succeeded, and the
            // check above misses it. Sweeping what is still in flight closes that gap and makes "once a volume
            // dies, no new ones start" true as written rather than true when the scheduler cooperates.
            // In production every volume takes seconds to minutes, so the faulted one was almost always the one
            // WhenAny returned and the difference never showed; with instant uploads the ordering is arbitrary and
            // the loop would run to the end. Cheap to check: `running` never exceeds the window (a handful).
            if (running.Exists(t => t.IsFaulted || t.IsCanceled))
                break;
            var one = One(VolumeName(baseRef, i + 1), volumeFiles[i], i);
            started.Add(one);
            running.Add(one);
        }
        await Task.WhenAll(started);
    }

    /// <summary>
    /// Replace every volume of an archive: upload the new volumes with **overwrite** (single → baseRef;
    /// multi → baseRef.001..M), and only after all of them succeed delete the leftover old volumes (the tail
    /// .M+1..N, or the old base name when going from a single old volume to multiple new ones, and anything else
    /// outside the new volume set).
    /// **Upload first, delete after** — the crash window drops from "the whole blob is gone" to "old and new
    /// volumes mixed" (recoverable via check/repair).
    /// </summary>
    public static async Task ReplaceAsync(
        IBlobUploader uploader, Account account, BlobContainerClient container, string baseRef,
        IReadOnlyList<string> volumeFiles, AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var newNames = VolumeNames(baseRef, volumeFiles.Count);

        // 1) Overwrite-upload the new volumes. For a single volume the loop runs once and writes baseRef.
        for (var i = 0; i < volumeFiles.Count; i++)
            await uploader.UploadOverwriteAsync(account, container.Name, newNames[i], volumeFiles[i], tier, retry, ct, metadata);

        // 2) Delete leftover old volumes outside the new set (e.g. the tail when the old volume count > the new
        //    one, or the old naming after a single↔multi switch).
        //    Only this archive's own volumes are deleted (exactly baseRef, or the baseRef.<digits> volume suffix)
        //    — a prefix scan would also match the collision-avoidance siblings data/{hash}~N (different content,
        //    independently referenced), which must be excluded or someone else's data gets deleted by mistake.
        var keep = new HashSet<string>(newNames, StringComparer.Ordinal);
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, ct))
            if (IsVolumeOf(baseRef, b.Name) && !keep.Contains(b.Name))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync(cancellationToken: ct);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a volume of the archive <paramref name="baseRef"/> itself: equal to the base name, or shaped like baseName.NNN (a suffix of <c>.</c> plus digits only).
    /// Used to filter precisely after a prefix enumeration, excluding collision-avoidance siblings that share the prefix but hold different content (e.g. data/{hash}~1, data/{hash}~1.001).
    /// </summary>
    public static bool IsVolumeOf(string baseRef, string name)
    {
        if (name == baseRef)
            return true;
        if (!name.StartsWith(baseRef + ".", StringComparison.Ordinal))
            return false;
        var suffix = name[(baseRef.Length + 1)..];
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// All volume blob names of an archive: single volume (count ≤ 1) → [baseRef]; multi-volume → [baseRef.001..count].
    /// The single source of truth for naming — shared by upload, replace and reference-set construction, so no
    /// place builds its own names and drifts.
    /// </summary>
    public static IReadOnlyList<string> VolumeNames(string baseRef, int count)
        => count <= 1
            ? [baseRef]
            : Enumerable.Range(1, count).Select(i => VolumeName(baseRef, i)).ToList();

    /// <summary>
    /// Whether this archive has been **touched at all**: the single-volume base name exists, or the first volume
    /// of a multi-volume set exists.
    /// <para>
    /// It cannot say "the whole family is here" — volumes upload concurrently, nothing is required about which
    /// lands first, and the first volume existing only means somebody wrote to that address. To verify
    /// completeness use <see cref="VerifyVolumesAsync"/>, which checks volume by volume against the count recorded in the index.
    /// </para>
    /// </summary>
    public static async Task<bool> ExistsAsync(BlobContainerClient cc, string baseRef, CancellationToken ct)
        => (await cc.GetBlobClient(baseRef).ExistsAsync(ct)).Value
           || (await cc.GetBlobClient(VolumeName(baseRef, 1)).ExistsAsync(ct)).Value;

    /// <summary>
    /// The "existence + size" check: verify that every volume exists, and when <paramref name="expectedSizes"/> is non-empty that each volume's size matches.
    /// Only HEAD requests (GetProperties), no downloads; Archive-tier blobs can have their properties read without rehydration. When the sizes are unknown (empty), only existence is verified.
    /// Within one family the HEADs run <paramref name="concurrency"/> at a time (1 = the old serial probing); the volumes are independent, so ordering carries no meaning here.
    /// </summary>
    public static async Task<(bool Present, bool SizeOk)> VerifyVolumesAsync(
        BlobContainerClient cc, string baseRef, int expectedVolumes, IReadOnlyList<long> expectedSizes, CancellationToken ct,
        int concurrency = 1)
        => (await VerifyFamiliesAsync(cc, [(baseRef, expectedVolumes, expectedSizes)], concurrency, ct))[0];

    /// <summary>
    /// The existence+size check over many families at once, one flat pool of HEADs under a single concurrency
    /// budget. A real container is dominated by single-volume objects (one HEAD each): probed family by family they
    /// advance at one round-trip apiece no matter what any per-family budget says, so the parallelism has to span
    /// the whole worklist, not one family. Per family the result is the same tuple as
    /// <see cref="VerifyVolumesAsync"/>, in input order.
    /// <para>
    /// One missing volume settles its family's verdict, so remaining probes of a condemned family are skipped
    /// rather than cancelled — only the in-flight handful is wasted, bounded by the budget. A size mismatch does
    /// not settle: the family is bad either way, but which volumes exist is still worth knowing at HEAD prices.
    /// </para>
    /// </summary>
    /// <param name="onProbe">Called once per probe — one per volume, skips included — the progress hook.
    /// Progress is counted in probes rather than families: a thousand-volume family is a thousand round-trips of
    /// real work, and counting it as one tick freezes the bar for minutes while single-volume packs then race it
    /// forward. Called from worker threads, possibly concurrently; the index is into <paramref name="families"/>.</param>
    public static async Task<(bool Present, bool SizeOk)[]> VerifyFamiliesAsync(
        BlobContainerClient cc,
        IReadOnlyList<(string BaseRef, int Volumes, IReadOnlyList<long> Sizes)> families,
        int concurrency, CancellationToken ct, Action<int>? onProbe = null)
    {
        var missing = new bool[families.Count];   // "some volume is gone" — settles the family (§ skip above)
        var sizeBad = new bool[families.Count];   // "a volume exists at the wrong size"

        var work = families.SelectMany((f, fi) =>
            Enumerable.Range(1, Math.Max(1, f.Volumes)).Select(vi => (Family: fi, Volume: vi)));
        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct },
            async (item, token) =>
            {
                var (fi, vi) = item;
                var (baseRef, volumes, sizes) = families[fi];
                if (!Volatile.Read(ref missing[fi]))
                {
                    long? len;
                    if (volumes <= 1)
                        // Two sequential probes, not a concurrent pair: the .001 fallback is only meaningful
                        // once the base name is known to be absent.
                        len = await LengthAsync(cc.GetBlobClient(baseRef), token)
                              ?? await LengthAsync(cc.GetBlobClient(VolumeName(baseRef, 1)), token);
                    else
                        len = await LengthAsync(cc.GetBlobClient(VolumeName(baseRef, vi)), token);

                    if (len is null)
                        Volatile.Write(ref missing[fi], true);
                    else if (sizes.Count >= vi && len != sizes[vi - 1])
                        sizeBad[fi] = true;
                }
                onProbe?.Invoke(fi);
            });

        return [.. families.Select((_, i) => missing[i] ? (false, false) : (true, !sizeBad[i]))];
    }

    private static async Task<long?> LengthAsync(BlobClient blob, CancellationToken ct)
    {
        try { return (await blob.GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength; }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    /// <summary>Download the archive (single or multi-volume) into workDir and return the local path of the first volume for 7z to extract.</summary>
    /// <param name="progress">A **factory** of per-volume progress callbacks. Why it must be a factory rather than a single <see cref="IProgress{T}"/>:
    /// the SDK's <c>ProgressHandler</c> reports bytes cumulative within this one <c>DownloadToAsync</c> call, and
    /// the <c>DeltaProgress</c> returned by <see cref="StageTracker.ItemProgress"/> turns cumulative into
    /// incremental against **that one instance's own baseline** <c>_last</c>, which is updated **unconditionally**
    /// after every <c>Report</c> (see the <c>DeltaProgress</c> comment inside <see cref="StageTracker"/>).
    /// <para>
    /// If a multi-volume download shared one instance: let L be the baseline left when the previous volume ended
    /// and c₁ be volume k's first report. If c₁ ≥ L, that report is only counted as c₁ − L, after which volume k's
    /// own increments accumulate as usual, so **the volume as a whole is undercounted by L bytes** — an
    /// undercount, not an inflation, and its upper bound is the size of the previous volume. The trigger: a
    /// smaller volume followed immediately by a larger one whose first reported block exceeds the baseline the
    /// small volume left behind. (Conversely, if c₁ &lt; L, the "treat it as a fresh start" reset happens to be
    /// right for this volume and nothing is lost.) Real inflation has only one source: the cumulative value
    /// suddenly dropping within one series of <c>Report</c> calls (an SDK retry), which behaves identically
    /// whether or not instances are swapped and is deliberately allowed by design
    /// (see the comment on <c>DeltaProgress</c>).
    /// </para>
    /// Calling the factory once per volume for a brand new instance is exactly what keeps the previous volume's baseline from leaking into the next,
    /// the same reasoning as "one ItemProgress() per volume" in <see cref="VolumeUploadScope.RunAsync"/>.
    /// When null no progress callback is attached — for call paths such as repair/compaction that are not registered as in-flight.</param>
    public static async Task<string> DownloadAsync(
        BlobContainerClient cc, string baseRef, string workDir, CancellationToken ct,
        Func<IProgress<long>>? progress = null)
    {
        async Task DownloadOne(BlobClient blob, string path)
        {
            if (progress is null)
                await blob.DownloadToAsync(path, ct);
            else
                await blob.DownloadToAsync(path, new BlobDownloadToOptions { ProgressHandler = progress() }, ct);
        }

        var single = cc.GetBlobClient(baseRef);
        if ((await single.ExistsAsync(ct)).Value)
        {
            var path = Path.Combine(workDir, "arc.7z");
            await DownloadOne(single, path);
            return path;
        }

        string? first = null;
        for (var i = 1; ; i++)
        {
            var blob = cc.GetBlobClient(VolumeName(baseRef, i));
            if (!(await blob.ExistsAsync(ct)).Value)
                break;
            var local = Path.Combine(workDir, $"arc.7z.{i:D3}");
            await DownloadOne(blob, local);
            first ??= local;
        }

        return first ?? throw new InvalidOperationException($"Archive '{baseRef}' not found in container.");
    }

    private static string VolumeName(string baseRef, int index) => $"{baseRef}.{index:D3}";
}
