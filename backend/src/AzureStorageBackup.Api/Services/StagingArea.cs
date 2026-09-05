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
/// When several runs share the pool, each holds a <see cref="StagingLease"/> and the ceiling is split evenly across the seats
/// (<see cref="HasRoom"/>). The compression lock is not first-come: <see cref="Dispatch"/> hands it to the eligible seat holding
/// the <b>least</b>, so fairness is judged on what each run holds, not on whose turn it happens to be. And the seat that has
/// just handed the lock back stays in that comparison for a short grace (<see cref="ReleaseCompressLock"/>): a compressor
/// is a loop that comes straight back, and judging only whoever happened to be parked at the instant of hand-back would
/// let a neighbour holding far more take every other turn regardless.
/// </para>
/// <para>
/// Release granularity is a **single volume**, not the whole family: a large file splits into thousands of volumes, and deleting only
/// after the whole family is uploaded makes peak usage equal the entire archive (a 100 GB file needs 100 GB of temp space — that one has
/// already crashed a backup once), and the watermark stays pinned at the ceiling the whole time, with compression jammed behind
/// backpressure. Delete each volume as it goes up and the peak shrinks to just "the few volumes not yet uploaded".
/// </para>
/// </summary>
public sealed class StagingArea(string compressTempDir, string stagedTempDir, Func<long> stagedLimit, TimeSpan? handBackGrace = null) : IDisposable
{
    // Bytes each staged file occupies, together with whose account it is charged to. Per-volume release must debit exactly,
    // and it must be **idempotent** — the same volume is released once by the upload path volume by volume, then again by the
    // whole-family backstop at the tail; double-debiting drives the watermark negative, and after that backpressure never blocks compression again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (StagingLease? Lease, long Bytes)> _staged =
        new(StringComparer.Ordinal);
    private long _stagedBytes;

    /// <summary>The runs currently in flight: their count is the denominator of the share, and their per-seat holdings
    /// feed both the share-capped ceiling and the least-holdings-first lock (see <see cref="HasRoom"/> and
    /// <see cref="Dispatch"/>). Configured-but-idle backups hold no seat.</summary>
    private readonly HashSet<StagingLease> _leaseSet = [];

    /// <summary>One gate over everything the dispatcher reads together: the seat set, the two queues, and whether the
    /// compression lock is out. Byte counters stay interlocked so the hot paths (release, progress) need not take it.</summary>
    private readonly Lock _gate = new();

    /// <summary>Whether the compression lock is out. It is <b>granted</b> by <see cref="Dispatch"/>, never raced for:
    /// a semaphore wakes whoever registered first, and arrival order says nothing about who is behind.</summary>
    private bool _compressing;

    /// <summary>Callers wanting the compression lock (every staging). Ordered by arrival, but <see cref="Dispatch"/> picks by holdings.</summary>
    private readonly List<Waiter> _lockQueue = [];

    /// <summary>
    /// The seat the free lock is being kept for, or null. Set by <see cref="Dispatch"/> when the seat handing the lock
    /// back holds less than the best parked waiter, and cleared when that seat comes back and takes it, when anyone
    /// holding even less takes it, or when <see cref="_handBackGrace"/> runs out (see <see cref="OnGraceExpired"/>).
    /// </summary>
    private StagingLease? _heldFor;

    /// <summary>Identifies the grace in force, so a timer from an earlier grace that has since been cut short does nothing.</summary>
    private int _graceSeq;

    /// <summary>
    /// How long the seat that let go is still counted as a candidate. It covers the compressor loop's round trip between
    /// two stagings — hand the archive to the upload queue, take the next item, one stat of the source file — which is a
    /// few milliseconds locally and a few tens on a busy NAS share. It is only ever paid when the seat does <b>not</b> come
    /// back (its next item is a dedup hit, an in-place raw file, or there is none), and then at most once per archive that
    /// seat produced, by a neighbour that was holding more; a single run never waits on it, having nobody to defer to.
    /// </summary>
    private readonly TimeSpan _handBackGrace = handBackGrace ?? TimeSpan.FromMilliseconds(250);

    /// <summary>Callers wanting room only (<see cref="ReserveAsync"/>): they manage their own temp space and take no lock.</summary>
    private readonly List<Waiter> _roomQueue = [];

    private sealed class Waiter(StagingLease? lease, bool needsRoom)
    {
        public readonly StagingLease? Lease = lease;
        /// <summary>False only for <see cref="StageWithoutBackpressureAsync"/>: it queues for the lock alone.</summary>
        public readonly bool NeedsRoom = needsRoom;
        public readonly TaskCompletionSource Granted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>What this caller's seat holds right now — the dispatch key. A caller without a seat counts as holding
        /// nothing: it is outside the split, and the only such callers are tests.</summary>
        public long Holdings => Lease?.Bytes ?? 0;
    }

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
        private int _files;
        private int _disposed;

        internal StagingLease(StagingArea area) => _area = area;

        /// <summary>Staged bytes this run currently occupies.</summary>
        public long Bytes => Interlocked.Read(ref _bytes);

        /// <summary>
        /// How many **volume files** those bytes are spread across. The UI reports the two side by side
        /// ("N objects waiting for uploading (M volumes on the staging disk, X GB)"), and neither can be derived from
        /// the other: volumes of one archive are uniform but the last one is a remainder, and a run mixes archives of
        /// wildly different sizes.
        /// <para>
        /// <see cref="ReserveAsync"/> deliberately books bytes with **no** files — a reservation is temp space the
        /// caller manages itself (repair's compose directory, compaction's unpacked members), not volumes waiting to
        /// travel. Those callers run outside a backup, so a run's own lease never mixes the two; if one ever did, its
        /// bytes would show up against no volumes.
        /// </para>
        /// </summary>
        public int Files => Volatile.Read(ref _files);

        internal void Add(long bytes, int files = 0)
        {
            Interlocked.Add(ref _bytes, bytes);
            if (files != 0)
                Interlocked.Add(ref _files, files);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            lock (_area._gate)
            {
                _area._leaseSet.Remove(this);
                // A lock kept for a seat that has just left is kept for nobody: let the dispatch below grant it.
                if (ReferenceEquals(_area._heldFor, this))
                    _area._heldFor = null;
            }
            // The moment a seat leaves, the remaining runs' share grows — they must be re-dispatched, otherwise they keep waiting
            // against the old share until the next finished volume upload happens to do it.
            _area.Dispatch();
        }
    }

    /// <summary>Take a seat. The return value must be released when the run ends (<c>using</c>).</summary>
    public StagingLease AcquireLease()
    {
        var lease = new StagingLease(this);
        lock (_gate)
            _leaseSet.Add(lease);
        return lease;
    }

    /// <summary>
    /// Whether this caller may start compressing right now. <b>Call with <see cref="_gate"/> held.</b>
    /// <para>
    /// A caller without a seat is bounded by the raw ceiling only. A seat's share is the limit split evenly across the seats
    /// in flight, and two gates have to pass: the seat holds less than its share, and the pool — counting every seat at
    /// <b>no more than its share</b> plus, at face value, whatever belongs to no seat — is below the limit.
    /// </para>
    /// <para>
    /// The cap on what a seat contributes is what keeps one run's oversized family from freezing the others: a 100 GB
    /// family cannot be split and lands whole, but it counts against the disk as one share, so the neighbours keep exactly
    /// the share the split promised them. The disk itself can therefore be exceeded by at most one family per seat, the
    /// same overshoot the single-run rule always allowed ("let it through while current usage is below the allowance", so
    /// a file bigger than the allowance can be compressed at all).
    /// </para>
    /// <para>
    /// Seat-less bytes (a reservation with no lease, a lease-less staging) sit on the same physical disk, so they count in
    /// full — nothing caps them and nothing should.
    /// </para>
    /// </summary>
    private bool HasRoom(StagingLease? lease)
    {
        var limit = stagedLimit();
        var total = Interlocked.Read(ref _stagedBytes);
        if (lease is null)
            return total < limit;
        var share = limit / Math.Max(1, _leaseSet.Count);
        if (lease.Bytes >= share)
            return false;
        long seated = 0, counted = 0;
        foreach (var l in _leaseSet)
        {
            var held = l.Bytes;
            seated += held;
            counted += Math.Min(held, share);
        }
        counted += Math.Max(0, total - seated);
        return counted < limit;
    }

    /// <summary>Of the parked lock waiters allowed to start right now, the one whose seat holds the least (ties to the
    /// earlier arrival); null when none may. <b>Call with <see cref="_gate"/> held.</b></summary>
    private Waiter? BestParked()
    {
        Waiter? best = null;
        foreach (var w in _lockQueue)
            if ((!w.NeedsRoom || HasRoom(w.Lease)) && (best is null || w.Holdings < best.Holdings))
                best = w;
        return best;
    }

    /// <summary>
    /// Re-examine every waiter after anything that could have changed an answer: bytes released, a seat come or gone,
    /// the compression lock handed back. Room-only waiters are released together, as many as have room. The compression
    /// lock, if free, goes to <b>one</b> caller: of those allowed to start, the one whose seat holds the least (ties to the
    /// earlier arrival). Holdings are the whole point — a run that fell behind while another filled the pool is the one
    /// that catches up, however many items the other has queued ahead of it.
    /// <para>
    /// <paramref name="handBackTo"/> is the seat that has just let go of the lock, and it takes part in that comparison
    /// although it is in no queue: its compressor is on its way back, and it would be parked already if the round trip
    /// between two stagings took no time at all. When it holds <b>less</b> than the best parked waiter the lock is kept
    /// for it (<see cref="_heldFor"/>) for the grace, during which <see cref="AcquireCompressLockAsync"/> lets it — or
    /// anyone holding less than the parked seat — walk in; if nobody has by the time the grace runs out, the parked seat
    /// is granted after all. Without this, two runs alternate strictly whatever they hold: the one that parks while the
    /// other compresses is the only candidate at the instant of hand-back, every time, so a small-file run gets exactly
    /// one item per archive of a big-file run, its holdings never leave zero, and the big-file run fills its share.
    /// </para>
    /// </summary>
    private void Dispatch(StagingLease? handBackTo = null)
    {
        List<Waiter>? granted = null;
        lock (_gate)
        {
            for (var i = _roomQueue.Count - 1; i >= 0; i--)
            {
                var w = _roomQueue[i];
                if (!HasRoom(w.Lease))
                    continue;
                _roomQueue.RemoveAt(i);
                (granted ??= []).Add(w);
            }
            if (!_compressing)
            {
                var best = BestParked();
                if (best is null)
                {
                    // Nobody to defer to, so nothing to hold the lock for: whoever comes next takes the fast path.
                    _heldFor = null;
                }
                else if (_heldFor is not null)
                {
                    // Being kept for a seat on its way back; the grace timer or that seat's return decides.
                }
                else if (handBackTo is not null && handBackTo.Bytes < best.Holdings && _leaseSet.Contains(handBackTo)
                         && HasRoom(handBackTo))
                {
                    _heldFor = handBackTo;
                    var seq = ++_graceSeq;
                    _ = Task.Delay(_handBackGrace).ContinueWith(
                        static (_, state) =>
                        {
                            var (area, seq) = ((StagingArea, int))state!;
                            area.OnGraceExpired(seq);
                        }, (this, seq), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                else
                {
                    _lockQueue.Remove(best);
                    _compressing = true;
                    (granted ??= []).Add(best);
                }
            }
        }
        if (granted is null)
            return;
        foreach (var w in granted)
            w.Granted.TrySetResult();
    }

    /// <summary>The seat the lock was kept for did not come back in time: stop holding it and grant the parked seat.
    /// A stale timer — the grace it belongs to was already ended by an arrival — does nothing.</summary>
    private void OnGraceExpired(int seq)
    {
        lock (_gate)
        {
            if (seq != _graceSeq || _heldFor is null)
                return;
            _heldFor = null;
        }
        Dispatch();
    }

    /// <summary>Park in a queue until <see cref="Dispatch"/> grants, or the token pulls the waiter out. A waiter the dispatcher
    /// has already granted cannot be cancelled here — the grant stands and the caller deals with it (see <see cref="AcquireCompressLockAsync"/>).</summary>
    private async Task WaitInQueueAsync(List<Waiter> queue, Waiter w, CancellationToken ct)
    {
        using var registration = ct.Register(static (state, token) =>
        {
            var (area, queue, w) = ((StagingArea, List<Waiter>, Waiter))state!;
            bool removed;
            lock (area._gate)
                removed = queue.Remove(w);
            if (removed)
                w.Granted.TrySetCanceled(token);
        }, (this, queue, w));
        await w.Granted.Task;
    }

    /// <summary>Wait until there is space (room only, no lock). Used by reservations, which manage their own temp space.</summary>
    private async Task WaitForRoomAsync(StagingLease? lease, CancellationToken ct)
    {
        Waiter w;
        lock (_gate)
        {
            if (HasRoom(lease))
                return;
            w = new Waiter(lease, needsRoom: true);
            _roomQueue.Add(w);
        }
        await WaitInQueueAsync(_roomQueue, w, ct);
    }

    /// <summary>
    /// Take the compression lock, waiting for room first unless <paramref name="waitForRoom"/> is false. The wait
    /// <b>does not hold the lock</b> — that is the whole point, see the class remarks — and it is not first-come, see <see cref="Dispatch"/>.
    /// </summary>
    /// <param name="tracker">Optional progress accounting, for the caller's own tracker only (same rule as <see cref="StageAsync"/>).
    /// A wait that began for lack of room is reported as its own phase rather than being left to read as "queueing for the archive lock":
    /// that one ends only when an <b>upload</b> frees pool space, the lock's ends when a producer lets go, and the operator's response to
    /// the two is opposite — see <see cref="StageProgress.WaitingOnRoom"/>. The phase is judged when the wait begins; room may come and
    /// go while the lock stays busy, and re-publishing every flip would cost far more than the precision is worth.</param>
    private async Task AcquireCompressLockAsync(StagingLease? lease, bool waitForRoom, CancellationToken ct, StageTracker? tracker)
    {
        Waiter w;
        bool roomWait;
        var dispatch = false;
        lock (_gate)
        {
            var hasRoom = !waitForRoom || HasRoom(lease);
            // The overwhelmingly common case is that the lock is free and there is room, and it must stay free of progress
            // events: registering unconditionally would force two publishes per item for a wait that never happened. Anyone
            // still queued while the lock is free was found ineligible by the last dispatch, and every change since has
            // dispatched again — except while the lock is being kept for a seat on its way back (see Dispatch), when the
            // parked seat is eligible and its claim on holdings stands: only a caller holding less than it may walk in.
            // The seat the lock is kept for usually is that caller; a third seat holding even less is too, and rightly so.
            if (hasRoom && !_compressing && (BestParked() is not { } parked || (lease?.Bytes ?? 0) < parked.Holdings))
            {
                _compressing = true;
                _heldFor = null;
                return;
            }
            w = new Waiter(lease, waitForRoom);
            roomWait = !hasRoom;
            _lockQueue.Add(w);
            // The seat the lock was kept for is back but may not take it (it ran out of room meanwhile, or ties the
            // parked seat): the grace has served its purpose, so end it now rather than letting the parked seat idle
            // through the rest of it.
            if (_heldFor is not null && ReferenceEquals(_heldFor, lease))
            {
                _heldFor = null;
                dispatch = true;
            }
        }
        if (dispatch)
            Dispatch();
        try
        {
            // BeginRoomWait sits inside the try: it publishes, and publish is external code deliberately allowed to throw here.
            if (roomWait)
                tracker?.BeginRoomWait();
            try
            {
                await WaitInQueueAsync(_lockQueue, w, ct);
            }
            finally
            {
                if (roomWait)
                    tracker?.EndRoomWait();
            }
        }
        catch
        {
            // Whatever threw, leave nothing behind: a waiter still queued is withdrawn; one the dispatcher granted in the
            // meantime holds the lock, and it has to go back or compression stops for good.
            bool stillQueued;
            lock (_gate)
                stillQueued = _lockQueue.Remove(w);
            if (stillQueued)
                w.Granted.TrySetCanceled();
            else if (w.Granted.Task.IsCompletedSuccessfully)
                ReleaseCompressLock();
            throw;
        }
        // Granted and cancelled in the same instant: the grant cannot be withdrawn, so honour the token here instead of
        // letting the caller compress into a cancelled run.
        if (ct.IsCancellationRequested)
        {
            ReleaseCompressLock();
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Hand the lock back and dispatch. <paramref name="handBackTo"/> is the seat letting go when it is expected
    /// straight back (a staging that ran to completion); null when it is leaving (cancelled, faulted), so the parked
    /// seat is not kept waiting for a return that is not coming.</summary>
    private void ReleaseCompressLock(StagingLease? handBackTo = null)
    {
        lock (_gate)
            _compressing = false;
        Dispatch(handBackTo);
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
            area.Dispatch();
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
        StageTracker? tracker = null,
        string? label = null,
        long labelBytes = 0)
        => StageCoreAsync(produce, lease, ct, tracker, waitForRoom: true, label, labelBytes);

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
        StageTracker? tracker = null,
        string? label = null,
        long labelBytes = 0)
        => StageCoreAsync(produce, lease, ct, tracker, waitForRoom: false, label, labelBytes);

    private async Task<StagedItem> StageCoreAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> produce,
        StagingLease? lease,
        CancellationToken ct,
        StageTracker? tracker,
        bool waitForRoom,
        // Opaque to this class on purpose: `produce` is a closure and this area has no idea what is inside it, so
        // the only place that can name the work is the caller that built both.
        string? label = null,
        long labelBytes = 0)
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
            // Room and lock are granted together by the dispatcher, so there is no window between them for someone else to use the room up.
            await AcquireCompressLockAsync(lease, waitForRoom, ct, tracker);
            try
            {
                try
                {
                    // BeginPacking moved inside the try: it calls publish(...) under _gate, and on non-heartbeat paths we
                    // deliberately let exceptions thrown by publish propagate (see the notes on BeginPacking in StageProgress.cs).
                    // Left outside the try, one throw here would increment _inPacking with no matching EndPacking, and preparing
                    // would stay stuck at an inflated number for the rest of the run; moved inside, the finally below covers it.
                    tracker?.BeginPacking(label, labelBytes);

                    Directory.CreateDirectory(compressTempDir);
                    Directory.CreateDirectory(stagedTempDir);

                    var produced = await produce(compressTempDir, ct);
                    var item = MoveToStaged(produced, lease);
                    Interlocked.Add(ref _stagedBytes, item.Bytes);
                    // The whole archive lands at once — every volume of it, charged to the seat in one go. That is
                    // what makes the volume count worth showing: the moment 7z finishes, all N volumes are on the
                    // disk, while the uploader that receives them can only start a handful at a time.
                    lease?.Add(item.Bytes, item.Files.Count);
                    return item;
                }
                finally
                {
                    tracker?.EndPacking();
                }
            }
            finally
            {
                // Hands the lock back and dispatches: the bytes just booked are what the next decision is made on, and
                // this seat is counted in it — its compressor is about to come back for the next item.
                ReleaseCompressLock(lease);
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
        // One volume gone from the disk is one off the file count as well, and the idempotent TryRemove above is what
        // keeps that exact — the whole-family tail releases every volume a second time.
        entry.Lease?.Add(-entry.Bytes, -1);
        Dispatch();
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
        // The whole-family tail dispatches once too: when every volume was already released one by one the loop above dispatched nothing, and compressions waiting on backpressure would miss their wake-up.
        Dispatch();
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
        // Nothing unmanaged is held any more; kept so `using var area` at every construction site stays valid.
    }
}
