namespace AzureStorageBackup.Api.Services;

/// <summary>A group of volume files already moved into staged-temp, waiting to be uploaded.</summary>
public sealed record StagedItem(IReadOnlyList<string> Files, long Bytes);

/// <summary>
/// Temp-area state machine (M4 design §7).
/// Compression is globally non-concurrent (one compression lock); output is written to compress-temp first, then the whole set moves into staged-temp.
/// staged-temp has a byte ceiling: the next compression only starts while we are below it (a single result is allowed to overshoot temporarily);
/// once over it, new compressions block until an upload calls <see cref="ReleaseFile"/> / <see cref="Release"/> and frees space.
/// The one exception is <see cref="StageWithoutBackpressureAsync"/>, for callers the release itself depends on — see the reasoning there.
/// <para>
/// Release granularity is a **single volume**, not the whole family: a large file splits into thousands of volumes, and deleting only
/// after the whole family is uploaded makes peak usage equal the entire archive (a 100 GB file needs 100 GB of temp space — that one has
/// already crashed a backup once), and the watermark stays pinned at the ceiling the whole time, with compression jammed behind
/// backpressure. Delete each volume as it goes up and the peak shrinks to just "the few volumes not yet uploaded".
/// </para>
/// </summary>
public sealed class StagingArea(string compressTempDir, string stagedTempDir, Func<long> stagedLimit) : IDisposable
{
    private readonly SemaphoreSlim _compressLock = new(1, 1);
    private readonly SemaphoreSlim _releaseSignal = new(0);
    // Bytes each staged file occupies, together with whose account it is charged to. Per-volume release must debit exactly,
    // and it must be **idempotent** — the same volume is released once by the upload path volume by volume, then again by the
    // whole-family backstop at the tail; double-debiting drives the watermark negative, and after that backpressure never blocks compression again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (StagingLease? Lease, long Bytes)> _staged =
        new(StringComparer.Ordinal);
    private long _stagedBytes;

    /// <summary>Number of runs currently in flight (= the denominator of the quota). Configured-but-idle backups hold no seat.</summary>
    private int _leases;

    /// <summary>Number of callers waiting for space to open up. A release must wake **all** of them, see <see cref="SignalRelease"/>.</summary>
    private int _waiting;

    public long StagedBytes => Interlocked.Read(ref _stagedBytes);

    /// <summary>
    /// Clear the compression/staging leftovers of the previous process at process startup.
    /// <para>
    /// It must be cleared at **process startup**, not at the start of every backup: several backups can be running at once,
    /// and clearing per run would delete files somebody else is currently writing. When the process has just come up no run
    /// is alive, so everything we see here is garbage from the last abnormal exit (container killed, power cut).
    /// </para>
    /// <para>
    /// Resume does not reuse these staged files — recompressing is cheaper and far safer than validating a pile of half-finished output of unknown provenance.
    /// </para>
    /// </summary>
    public static void ClearStale(string compressTempDir, string stagedTempDir)
    {
        foreach (var dir in new[] { compressTempDir, stagedTempDir })
        {
            try
            {
                if (!Directory.Exists(dir))
                    continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    try { Directory.Delete(sub, recursive: true); } catch { /* can't delete it, never mind; try again next time */ }
                foreach (var file in Directory.EnumerateFiles(dir))
                    try { File.Delete(file); } catch { /* same as above */ }
            }
            catch { /* same as above */ }
        }
    }

    /// <summary>
    /// One run's seat in the staging area. The staging disk's allowance is split evenly across **the runs currently holding
    /// a seat**, so a seat must be taken when a run starts and handed back when it ends — configured-but-idle backups must not
    /// hold a share, otherwise with ten backups configured and only one running, that one would get a tenth of the disk too.
    /// </summary>
    public sealed class StagingLease : IDisposable
    {
        private readonly StagingArea _area;
        private long _bytes;
        private int _disposed;

        internal StagingLease(StagingArea area) => _area = area;

        /// <summary>Staged bytes this run currently occupies.</summary>
        public long Bytes => Interlocked.Read(ref _bytes);

        internal void Add(long bytes) => Interlocked.Add(ref _bytes, bytes);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.Decrement(ref _area._leases);
            // The moment a seat leaves, the remaining runs' allowance grows — they must be woken, otherwise they keep waiting
            // against the old quota until the next finished volume upload happens to wake them.
            _area.SignalRelease();
        }
    }

    /// <summary>Take a seat. The return value must be released when the run ends (<c>using</c>).</summary>
    public StagingLease AcquireLease()
    {
        var lease = new StagingLease(this);
        Interlocked.Increment(ref _leases);
        return lease;
    }

    /// <summary>The allowance available to this call. Callers without a seat take no part in the split and are bounded only by the global ceiling.</summary>
    private long QuotaFor(StagingLease? lease)
    {
        var limit = stagedLimit();
        if (lease is null)
            return limit;
        return limit / Math.Max(1, Volatile.Read(ref _leases));
    }

    /// <summary>
    /// Whether compression may start right now. Both gates have to pass: your own allowance (fairness), and the global ceiling
    /// (the staging disk is a physical disk; filling it up fails the backup outright). Both use the same test, "let it through
    /// while **current** usage is below the allowance", keeping the existing semantics — so an item starting from zero can always
    /// begin compressing even when its output is bound to exceed the allowance, otherwise a file bigger than the allowance could never be compressed at all.
    /// </summary>
    private bool HasRoom(StagingLease? lease) =>
        Interlocked.Read(ref _stagedBytes) < stagedLimit()
        && (lease is null || lease.Bytes < QuotaFor(lease));

    /// <summary>
    /// Wake **every** waiter, not one. Each is waiting on its own allowance, so releasing one means the one that wakes up may
    /// not be the one that can proceed — and it consumes the signal on its way, so the one that should have woken misses it and
    /// sits idle until the next release. Extra signals only make a later WaitAsync return once immediately; the wait loop re-tests the condition, so they are harmless.
    /// </summary>
    private void SignalRelease()
    {
        var waiters = Volatile.Read(ref _waiting);
        if (waiters > 0)
            _releaseSignal.Release(waiters);
    }

    /// <summary>
    /// Reserve a slice of the allowance for temp space **the caller manages itself** — repair and dead-weight compaction assemble
    /// members into a compose directory, and sometimes download and unpack a whole old pack; those bytes land on the same physical disk.
    /// <para>
    /// The difference from <see cref="StageAsync"/>: that one is "output waiting to be uploaded", an exact figure known only once
    /// compression finishes, and it can be released volume by volume; this one is "input waiting to be consumed", a figure that can
    /// only be estimated up front and is held in one block until the operation ends. So this only does accounting and
    /// backpressure, it moves no files — the caller guarantees it writes no more than it reserved, and Disposes when done.
    /// </para>
    /// <para>
    /// It does not grab the compression lock: during a reservation the caller is usually downloading or copying, and pinning the compression lock down waiting on the network is exactly the ailment this rework just fixed.
    /// </para>
    /// </summary>
    public async Task<IDisposable> ReserveAsync(long bytes, StagingLease? lease = null, CancellationToken ct = default)
    {
        await WaitForRoomAsync(lease, ct);
        Interlocked.Add(ref _stagedBytes, bytes);
        lease?.Add(bytes);
        return new Reservation(this, lease, bytes);
    }

    private sealed class Reservation(StagingArea area, StagingLease? lease, long bytes) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            Interlocked.Add(ref area._stagedBytes, -bytes);
            lease?.Add(-bytes);
            area.SignalRelease();
        }
    }

    /// <summary>Wait until there is space. **Does not hold the compression lock** — that is the whole point, see the class remarks.</summary>
    /// <param name="tracker">Optional progress accounting, for the caller's own tracker only (same rule as <see cref="StageAsync"/>).
    /// A real wait is reported as its own phase rather than being left to read as "queueing for the archive lock": this one ends only when
    /// an <b>upload</b> frees pool space, the lock's ends when a producer lets go, and the operator's response to the two is opposite —
    /// see <see cref="StageProgress.WaitingOnRoom"/>.</param>
    private async Task WaitForRoomAsync(StagingLease? lease, CancellationToken ct, StageTracker? tracker = null)
    {
        // The overwhelmingly common case is that there is room, and it must stay free of progress events: registering
        // unconditionally would force two publishes per item for a wait that never happened, the same trade the upload
        // gate makes when it finds itself free (see VolumeUploadScope.RunAsync).
        if (HasRoom(lease))
            return;
        // BeginRoomWait sits inside the try: it raises the counter before it publishes, and publish is external code
        // deliberately allowed to throw here, so the finally has to cover that case too.
        try
        {
            tracker?.BeginRoomWait();
            while (!HasRoom(lease))
            {
                Interlocked.Increment(ref _waiting);
                try
                {
                    if (HasRoom(lease))   // look again after registering as a waiter, so we do not miss a release that just happened
                        return;
                    await _releaseSignal.WaitAsync(ct);
                }
                finally
                {
                    Interlocked.Decrement(ref _waiting);
                }
            }
        }
        finally
        {
            tracker?.EndRoomWait();
        }
    }

    /// <param name="tracker">Optional progress accounting. <b>Only</b> pass this backup's own tracker: this class is a singleton
    /// shared across backups, and charging global state to one particular backup lets two concurrent runs pollute each other.
    /// Whoever calls does the accounting, and each sees only its own work.</param>
    /// <param name="lease">
    /// This run's seat (see <see cref="AcquireLease"/>). Pass null to opt out of the allowance split and be bounded only by the global ceiling.
    /// </param>
    public Task<StagedItem> StageAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> produce,
        StagingLease? lease = null,
        CancellationToken ct = default,
        StageTracker? tracker = null)
        => StageCoreAsync(produce, lease, ct, tracker, waitForRoom: true);

    /// <summary>
    /// Stage an archive **without waiting for room**: same files, same accounting, same global compression lock —
    /// only the backpressure wait is skipped.
    /// <para>
    /// This exists for one caller and one caller only: <b>a thread the quota depends on to come back</b>. Everything
    /// in this pool is released by an upload — volume by volume as it sends, or in one go when a dropped archive is
    /// disposed — so an uploader that parks in <see cref="WaitForRoomAsync"/> is waiting for itself. One doing it is
    /// merely slow; all of them doing it at once is permanent. That is not a hypothetical: a network outage trips
    /// every in-flight upload into the suspend gate together, the compression side meanwhile fills the pool to the
    /// ceiling exactly as it is designed to, and when the gate's timer releases every waiter in one go they all come
    /// back here to recompress what they have to resend. Nobody is left holding an archive to release, and the run
    /// freezes with no error, no progress, and no way out but "Stop now" or a restart.
    /// </para>
    /// <para>
    /// The overshoot this permits is bounded and is the same trade <see cref="HasRoom"/> already makes: it lets an
    /// item starting from zero begin even when its output is bound to exceed the allowance, because the alternative
    /// is that the work can never happen at all. Here the caller is *replacing* an archive this pool already admitted
    /// once, and at most one per uploader is in hand at a time, so the ceiling is exceeded by at most that many
    /// archives before the ordinary gate binds again.
    /// </para>
    /// <para>
    /// It is a separate entry point rather than a flag on the gate deliberately: <see cref="StageAsync"/> keeps the
    /// exact semantics every other caller already relies on, and the compression stage — the one this backpressure
    /// exists to pace — must never reach this method.
    /// </para>
    /// </summary>
    public Task<StagedItem> StageWithoutBackpressureAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> produce,
        StagingLease? lease = null,
        CancellationToken ct = default,
        StageTracker? tracker = null)
        => StageCoreAsync(produce, lease, ct, tracker, waitForRoom: false);

    private async Task<StagedItem> StageCoreAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> produce,
        StagingLease? lease,
        CancellationToken ct,
        StageTracker? tracker,
        bool waitForRoom)
    {
        // Count as "queued" the moment we enter here: the compression lock is global, so we will most likely idle a while, and
        // idling is indistinguishable to the user from "not picked up yet". Only flip to "preparing" once we hold the lock.
        tracker?.BeginStaging();
        try
        {
            // Waiting for space **does not hold the compression lock**. It used to grab the lock first and then wait for space, on
            // the grounds that "we already hold the lock, nobody else can compress anyway" — true when a single backup runs. With
            // several backups in parallel that is the root of the disease: a run blocked by staging sits idle clutching the global
            // compression lock, and other runs cannot even start compressing. Handing anyone more quota saves nobody; it just deadlocks for a different reason.
            while (true)
            {
                if (!waitForRoom)
                {
                    // The backpressure wait is the only thing skipped. The compression lock is still taken, so
                    // compression stays globally serial and this caller queues behind whoever holds it — including
                    // the compression stage, which is waiting for room outside the lock and therefore is not in the way.
                    await _compressLock.WaitAsync(ct);
                    break;
                }

                await WaitForRoomAsync(lease, ct, tracker);

                await _compressLock.WaitAsync(ct);
                // Between getting space and getting the lock there is a window in which somebody else may have used that space up.
                // Once the lock is in hand we must look again, or we blow past the ceiling — drop the lock and wait again, letting whoever really has room go first.
                if (HasRoom(lease))
                    break;
                _compressLock.Release();
            }

            try
            {
                try
                {
                    // BeginPacking moved inside the try: it calls publish(...) under _gate, and on non-heartbeat paths we
                    // deliberately let exceptions thrown by publish propagate (see the notes on BeginPacking in StageProgress.cs).
                    // Left outside the try, one throw here would increment _inPacking with no matching EndPacking, and preparing
                    // would stay stuck at an inflated number for the rest of the run; moved inside, the finally below covers it.
                    tracker?.BeginPacking();

                    Directory.CreateDirectory(compressTempDir);
                    Directory.CreateDirectory(stagedTempDir);

                    var produced = await produce(compressTempDir, ct);
                    var item = MoveToStaged(produced, lease);
                    Interlocked.Add(ref _stagedBytes, item.Bytes);
                    lease?.Add(item.Bytes);
                    return item;
                }
                finally
                {
                    tracker?.EndPacking();
                }
            }
            finally
            {
                _compressLock.Release();
            }
        }
        finally
        {
            tracker?.EndStaging();
        }
    }

    /// <summary>
    /// Called after **one volume** finishes uploading: delete that volume, debit the bytes it held, wake the waiting compressions.
    /// Idempotent — a path already released (or that never belonged to this staging area at all) is simply ignored.
    /// </summary>
    public void ReleaseFile(string file)
    {
        if (!_staged.TryRemove(file, out var entry))
            return;
        try { File.Delete(file); } catch { /* best effort */ }
        Interlocked.Add(ref _stagedBytes, -entry.Bytes);
        // Debit the seat's account too: without that this run's usage only ever grows, and its own quota is permanently full in short order.
        entry.Lease?.Add(-entry.Bytes);
        SignalRelease();
    }

    /// <summary>Whole-family tail: release everything not already released volume by volume (on a dedup hit not a single volume
    /// was uploaded, so it all comes back here), then delete the emptied GUID subdirectory. Whatever was already released per volume short-circuits idempotently inside <see cref="ReleaseFile"/>.</summary>
    public void Release(StagedItem item)
    {
        foreach (var file in item.Files)
            ReleaseFile(file);
        // Delete the emptied GUID subdirectory.
        foreach (var dir in item.Files.Select(Path.GetDirectoryName).Distinct())
        {
            try { if (dir is not null && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch { /* best effort */ }
        }
        // The whole-family tail signals once too: when every volume was already released one by one the loop above signalled nothing, and compressions waiting on backpressure would miss their wake-up.
        SignalRelease();
    }

    private StagedItem MoveToStaged(IReadOnlyList<string> producedFiles, StagingLease? lease)
    {
        if (producedFiles.Count == 0)
            return new StagedItem([], 0); // nothing produced: create no subdirectory, so no empty GUID directory is left behind

        // Every staging gets its own GUID subdirectory: different backups producing identically named files do not overwrite each other (concurrency-safe across containers).
        var subDir = Path.Combine(stagedTempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subDir);
        var staged = new List<string>(producedFiles.Count);
        long bytes = 0;
        try
        {
            foreach (var src in producedFiles)
            {
                var dest = Path.Combine(subDir, Path.GetFileName(src));
                File.Move(src, dest, overwrite: false);
                var size = new FileInfo(dest).Length;
                bytes += size;
                // Per-volume release debits against this record; we cannot stat afterwards (by then the file is gone).
                // Record whose account this volume is charged to as well, or a release has no way of knowing which seat to refund.
                _staged[dest] = (lease, size);
                staged.Add(dest);
            }
        }
        catch
        {
            // Failure partway through: clean up the already-moved files plus the subdirectory, leaking nothing. The exception propagates out of StageAsync,
            // so the caller never adds bytes to _stagedBytes — which is why this only strikes them from the ledger and does **not** debit _stagedBytes; that money was never booked.
            foreach (var f in staged)
                _staged.TryRemove(f, out _);
            try { Directory.Delete(subDir, recursive: true); } catch { /* best effort */ }
            throw;
        }
        return new StagedItem(staged, bytes);
    }

    public void Dispose()
    {
        _compressLock.Dispose();
        _releaseSignal.Dispose();
    }
}
