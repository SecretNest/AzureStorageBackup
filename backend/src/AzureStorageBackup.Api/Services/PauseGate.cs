using System.Diagnostics;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Why the gate is closed. The two compose; see PauseGate's remarks.
/// <para>
/// The numbers are the wire contract and are mirrored in <c>frontend/src/api/backupConfigs.ts</c>: PauseInfo goes
/// to the browser as itself, with no DTO in between to project the enum into a string the way BackupRunResponse
/// does for Status and SuspendReason, and nothing in this application registers a JsonStringEnumConverter — so
/// this serialises as a number, exactly like CloudState/LocalState, which cross the boundary the same way. Pinning
/// the values keeps a later reordering from silently redefining what the browser already believes, and putting
/// TransientError at 0 means the default value is the behaviour that predates the user's pause.
/// </para>
/// </summary>
public enum PauseSource
{
    /// <summary>A worker hit a transient error. Self-heals on a timer, and downgrades if patience runs out.</summary>
    TransientError = 0,

    /// <summary>The user pressed Pause. No timer, no patience, and it never downgrades on its own.</summary>
    User = 1,
}

/// <summary>The pause currently in effect, for the frontend to look at.</summary>
/// <param name="Reason">The error message that triggered the pause.</param>
/// <param name="Since">When this round of pausing started.</param>
/// <param name="NextRetryAt">The instant the self-heal timer will next let waiters through.</param>
/// <param name="Failures">How many consecutive failures so far (one success resets it to zero).</param>
/// <param name="Source">Which reason is being reported — see <see cref="PauseSource"/>.</param>
public sealed record PauseInfo(
    string Reason, DateTimeOffset Since, DateTimeOffset? NextRetryAt, int Failures, PauseSource Source);

/// <summary>
/// The pause gate for transient errors. A worker that hits network/cloud flakiness waits here in place, instead of
/// condemning the whole backup round.
/// <para>
/// The first worker to hit trouble opens the gate and starts the self-heal timer; later arrivals all wait on the same
/// signal. When the timer fires, or the user clicks <c>Retry now</c>, every waiter is released to retry together.
/// </para>
/// <para>
/// <see cref="ReportSuccess"/> is the crucial ingredient: as long as some worker is still getting work done, the
/// network is up, so the failure count and the patience clock both reset. Otherwise one unlucky file that never
/// uploads would drag a perfectly healthy round into a downgrade.
/// </para>
/// <para>
/// Patience running out means downgrade: <see cref="WaitAsync"/> returns false and the caller takes the "suspend and
/// exit" path — flush the journal, release the staging seat and the production lock. Without that, a suspended run
/// sits on the global staging quota forever and blocks every parallel backup **completely** (StagingArea's quota gate
/// is global, it does not go per-seat).
/// </para>
/// </summary>
public sealed class PauseGate : IDisposable
{
    private static readonly TimeSpan[] DefaultSchedule =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private readonly IReadOnlyList<TimeSpan> _schedule;
    private readonly TimeSpan _steady;
    private readonly TimeSpan _patience;
    private readonly Lock _lock = new();

    /// <summary>The lifetime of the whole gate. A pending 5-minute Task.Delay must never outlive the run.</summary>
    private readonly CancellationTokenSource _life = new();

    private TaskCompletionSource<bool>? _release;   // non-null = paused right now
    private CancellationTokenSource? _timer;
    private int _failures;
    // null = no trouble at the moment. A success clears it, and so do Retry now and Resume — all three mean
    // "the run is starting over from here", which is what the patience clock is entitled to measure from.
    private DateTimeOffset? _troubleSince;
    private PauseInfo? _current;
    private bool _downgraded;

    /// <summary>Held closed by the user, independently of any trouble. See the remarks on ReleaseLocked.</summary>
    private bool _pausedByUser;
    private DateTimeOffset _userPausedSince;

    /// <summary>
    /// How many pieces of work are past a gate and still moving: a volume on the wire, the file under 7z, the item
    /// the prober is hashing, the diff between two of its callbacks. See <see cref="BeginWork"/>.
    /// </summary>
    private int _inHand;

    /// <summary>
    /// The child processes the user's hold freezes — see <see cref="Processes"/>. Shares this gate's lock, so a 7z
    /// stopping or leaving is seen here in the same breath as any other change to what is in hand.
    /// </summary>
    private readonly ProcessHold _processes;

    /// <summary>
    /// Time the run has spent held with nothing moving — the stretch the row reads "Paused" for — up to the last
    /// transition, and when that stretch started if it is still going (-1 when it is not). See <see cref="HeldMs"/>.
    /// </summary>
    private long _heldMs;
    private long _settledSince = -1;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public PauseGate(
        IReadOnlyList<TimeSpan>? schedule = null, TimeSpan? steady = null, TimeSpan? patience = null)
    {
        _schedule = schedule is { Count: > 0 } ? schedule : DefaultSchedule;
        _steady = steady ?? TimeSpan.FromMinutes(5);
        _patience = patience ?? TimeSpan.FromMinutes(10);
        _processes = new ProcessHold(_lock, TrackSettledLocked);
    }

    /// <summary>Millisecond time source injected by tests, as on <c>StageTracker</c>; null in production, which
    /// falls through to the internal <see cref="Stopwatch"/>.</summary>
    internal Func<long>? Clock { get; init; }

    private long NowMs() => Clock?.Invoke() ?? _clock.ElapsedMilliseconds;

    /// <summary>
    /// Where the run's 7z processes register themselves (<c>SevenZipCli</c>, through the compression request).
    /// The user's hold stops every process attached here, and stops each one attached while it stands, the
    /// instant it is attached; Resume and a downgrade both let them run again. A stopped process steps out of
    /// the in-hand count for as long as it is stopped, the way a parked worker does (<see cref="Idle"/>): the
    /// loop holding it is still inside its <see cref="BeginWork"/> scope, but nothing in that scope is moving.
    /// <para>
    /// The downgrade half is not a courtesy: <c>RequestStop</c> downgrades before it cancels, and the compressor
    /// feeding a stopped 7z is blocked on a pipe that only that process drains. Released first, the feed sees the
    /// cancellation on its next write and takes the kill-the-tree path it always took; still stopped, the run
    /// would wind down into a process that never reads and a writer that never returns.
    /// </para>
    /// </summary>
    public ProcessHold Processes => _processes;

    /// <summary>
    /// How long, in all, this run has stood held with nothing in hand — the "Paused" stretches, not the
    /// "Pausing…" ones, since during those bytes are still landing and the time they take is real work time.
    /// Read fresh on every publish by the upload stage's tracker and subtracted from the elapsed time its
    /// estimate extrapolates from: a whole-run average that counted an overnight pause as time spent moving bytes
    /// would print a remaining time stretched by the whole night, and shrink it back only as the run wore the
    /// night down.
    /// </summary>
    public long HeldMs
    {
        get
        {
            lock (_lock)
                return _heldMs + (_settledSince >= 0 ? NowMs() - _settledSince : 0);
        }
    }

    /// <summary>The pause in effect right now; null when nothing is paused.</summary>
    public PauseInfo? Current { get { lock (_lock) return _current; } }

    public bool IsDowngraded { get { lock (_lock) return _downgraded; } }

    /// <summary>
    /// Is the gate closed right now, for either reason — the question a worker asks at a point where it cannot
    /// afford to wait: an uploader that has just been handed an upload slot must not park while holding it
    /// (the slot gate is shared by every run on the machine), so it asks, and if held gives the slot back before
    /// it parks. See <c>VolumeUploadScope.RunAsync</c>.
    /// </summary>
    public bool IsHeld { get { lock (_lock) return _release is not null; } }

    /// <summary>
    /// Is the user's own hold standing? Ask this rather than reading <c>Current.Source</c>, which cannot answer it
    /// on its own: a pause pressed while a transient-error backoff is running leaves <see cref="Current"/>
    /// reporting the backoff (with its countdown and its Retry-now affordance) until that backoff's timer fires,
    /// up to one steady interval — five minutes by default. A pause the operator can neither see nor distinguish
    /// from "stuck, retrying shortly" is a pause that looks like it did nothing.
    /// <para>
    /// The two facts are deliberately kept separate instead of having the pause overwrite the trouble: a run can
    /// genuinely be both paused and mid-backoff, and callers that must know which to show would have no way back
    /// to the discarded half.
    /// </para>
    /// </summary>
    public bool IsPausedByUser { get { lock (_lock) return _pausedByUser; } }

    /// <summary>
    /// Both halves of "why is this run paused" as of one instant. Anything that reports the two together must
    /// come through here rather than reading <see cref="Current"/> and <see cref="IsPausedByUser"/> in turn: those
    /// take the lock separately, so a Pause or a Resume landing between them yields a pair that never existed.
    /// The UI renders from exactly that pair, and both mixtures are wrong in a way the operator can see — a null
    /// <see cref="PauseInfo"/> beside a standing hold draws no pause label at all, and a
    /// <see cref="PauseSource.User"/> info beside <c>false</c> draws the transient-error branch, countdown and
    /// Retry-now button included, on a run the operator has simply paused.
    /// </summary>
    public (PauseInfo? Current, bool ByUser, bool Settled) Snapshot()
    {
        lock (_lock)
            return (_current, _pausedByUser, SettledLocked());
    }

    /// <summary>
    /// Has the user's hold taken effect: it stands, and nothing is in hand any more, so nothing will move until
    /// Resume.
    /// <para>
    /// The hold goes up the instant Pause is pressed, but it holds only what has not started. Every producing
    /// loop finishes the piece in its hands first — the volume on the wire, the item under the prober — and on a
    /// slow link that is minutes. (The file under 7z is the exception: the hold stops its process where it is,
    /// see <see cref="Processes"/>, and a stopped process is not in hand.) For as long as it lasts the run is
    /// <b>pausing</b>, not paused, and the screen has to say which: "Paused" over a run visibly uploading was
    /// read as the button having done nothing.
    /// </para>
    /// </summary>
    public bool IsSettled { get { lock (_lock) return SettledLocked(); } }

    // A stopped process is in some loop's hand and moving nowhere, so it counts against what is in hand rather
    // than for it. The difference can only reach zero, not cross it: a process is attached from inside a BeginWork
    // scope, and stopping it takes out at most the one count that scope put in.
    private bool SettledLocked() => _pausedByUser && _inHand - _processes.Stopped <= 0;

    /// <summary>
    /// Bring the held-time clock up to date with <see cref="SettledLocked"/>. Called after every change that can
    /// move the answer — the hold, what is in hand, and the stopped processes — so the clock runs exactly while
    /// the row reads "Paused".
    /// </summary>
    private void TrackSettledLocked()
    {
        var settled = SettledLocked();
        if (settled && _settledSince < 0)
            _settledSince = NowMs();
        else if (!settled && _settledSince >= 0)
        {
            _heldMs += NowMs() - _settledSince;
            _settledSince = -1;
        }
    }

    /// <summary>
    /// A piece of work has passed a gate and is moving. Dispose when it is done — or, for a worker that will go
    /// on to wait for something the pause itself prevents, wrap the wait in <see cref="Idle"/>. A 7z attached to
    /// <see cref="Processes"/> steps out on its own for as long as the hold keeps it stopped.
    /// <para>
    /// The unit is deliberately "what will produce bytes or CPU on its own", not "the item a loop holds": an
    /// uploader holding a hundred-volume file has one item in hand and, once the hold is up, at most a handful of
    /// volumes still moving. Counting the item would keep the run "pausing" until the whole file had gone up,
    /// which is exactly the wait the per-volume gate exists to avoid; counting the volumes (each one takes its
    /// own <see cref="BeginWork"/> after passing <see cref="WaitIfPausedAsync"/>, while the family-level stretch
    /// around them sits in <see cref="Idle"/>) reports the truth: pausing while any is on the wire, paused once
    /// the last one lands.
    /// </para>
    /// </summary>
    public IDisposable BeginWork()
    {
        lock (_lock)
        {
            _inHand++;
            TrackSettledLocked();
        }
        return new Counted(this, +1);
    }

    /// <summary>
    /// Step out of the in-hand count for a wait the pause itself may make endless: the compressor waiting for
    /// staging room that only an upload can free, and no upload is coming while the hold stands; the prober
    /// writing into a full probed queue whose only consumer is that compressor. Dispose on the way back into work. Only meaningful inside a <see cref="BeginWork"/> scope; outside one the count is left
    /// alone rather than driven negative.
    /// </summary>
    public IDisposable Idle()
    {
        lock (_lock)
        {
            if (_inHand == 0)
                return Counted.None;
            _inHand--;
            TrackSettledLocked();
        }
        return new Counted(this, -1);
    }

    private sealed class Counted(PauseGate gate, int sign) : IDisposable
    {
        public static readonly Counted None = new(null!, 0);
        private int _disposed;

        public void Dispose()
        {
            if (sign == 0 || Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            lock (gate._lock)
            {
                gate._inHand -= sign;
                gate.TrackSettledLocked();
            }
        }
    }

    /// <summary>
    /// Wait at the gate.
    /// </summary>
    /// <returns>true = released, go retry; false = already downgraded, the caller should take the suspend-and-exit path.</returns>
    /// <exception cref="OperationCanceledException">The user canceled the run. Cancellation always wins.</exception>
    public async Task<bool> WaitAsync(Exception cause, CancellationToken ct)
    {
        // The cancellation check has to come first: a worker on its way out must not open the gate, nor join
        // someone else's wait — not even briefly publishing a phantom pause state for the UI and the other workers to see.
        ct.ThrowIfCancellationRequested();

        Task<bool> release;
        lock (_lock)
        {
            if (_downgraded)
                return false;
            // _timer, not _release, is what says "a trouble backoff is already running": a user pause can have
            // _release set with no timer at all. Trouble arriving under a standing user pause must still start
            // its own backoff (reusing that same _release — see OpenLocked) so the two reasons compose instead
            // of the second one riding free on the first one's coattails.
            release = _timer is null ? OpenLocked(cause) : _release!.Task;
        }
        // Parked is parked: a worker waiting here under a standing user hold is as still as one parked at
        // WaitIfPausedAsync, and must not keep the run reading "pausing" for as long as it waits. The failure
        // path is only ever reached from inside a BeginWork scope (a failure is something a piece of work did),
        // which is what makes stepping out here sound.
        using (Idle())
            return await release.WaitAsync(ct);
    }

    /// <summary>The user clicked <c>Retry now</c>: don't wait for the timer, release now, and treat it as a fresh start (backoff and patience both reset).</summary>
    public void ReleaseNow()
    {
        lock (_lock)
        {
            _failures = 0;
            _troubleSince = null;
            ReleaseLocked(true);
        }
    }

    /// <summary>Some worker got a piece of work done. The network is up, so reset the failure count and the patience clock.</summary>
    public void ReportSuccess()
    {
        lock (_lock)
        {
            _failures = 0;
            _troubleSince = null;
        }
    }

    /// <summary>
    /// Downgrade: the user clicked Suspend, patience ran out, or the run is tearing down after a fault. Every
    /// waiter gets false, and no later <see cref="PauseByUser"/> can close the gate again.
    /// <para>
    /// The third caller is the one worth naming, because it is not about pausing at all: the orchestrator's error
    /// teardown waits for the producing loops to settle, and those loops park here. A user hold has no timer and
    /// no patience, so without this the failed run would wait for a pause that only the operator can lift, holding
    /// the busy lock and the process-wide staging quota the whole time.
    /// </para>
    /// </summary>
    public void Downgrade()
    {
        lock (_lock)
            DowngradeLocked();
    }

    /// <summary>
    /// The user pressed Pause: hold the gate with no timer and no patience.
    /// <para>
    /// If trouble already has the gate closed, this only records the second reason — the workers are
    /// already parked on the same signal, and the trouble's own timer must not release them while this
    /// reason stands (see <see cref="ReleaseLocked"/>).
    /// </para>
    /// </summary>
    /// <returns>
    /// Whether the hold now stands. False means this gate is downgraded — the run is winding down towards a
    /// suspend or a stop and will never wait here again, so there is nothing left to hold.
    /// <para>
    /// The answer has to come from inside the lock, and it has to come from here rather than from a caller
    /// reading <see cref="IsDowngraded"/> first: a run stays <see cref="RunStatus.Running"/> throughout a
    /// wind-down that can take minutes (Suspend waits for a multi-GB upload to finish), and after a patience
    /// downgrade nothing outside this class changes at all. A caller testing the flag separately would be
    /// answering from a reading taken before its own call, which is the same silent success by a longer route.
    /// </para>
    /// <para>
    /// Pressing Pause on a run already held by the user is true, not false: the hold the operator asked for is
    /// standing, which is what they were asking about.
    /// </para>
    /// </returns>
    public bool PauseByUser()
    {
        lock (_lock)
        {
            if (_downgraded)
                return false;
            if (_pausedByUser)
                return true;
            _pausedByUser = true;
            _userPausedSince = DateTimeOffset.UtcNow;
            if (_release is null)
            {
                _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _current = UserPauseInfo();
            }
            // The file under 7z stops here, with the hold, rather than finishing first — see Processes.
            _processes.Hold();
            TrackSettledLocked();
            return true;
        }
    }

    /// <summary>
    /// The user pressed Resume. Only lifts the user's own hold: if trouble is still keeping the gate shut,
    /// the workers stay parked and the UI goes on reporting the trouble, which is correct — the run is
    /// not ready to proceed just because the operator is.
    /// </summary>
    /// <returns>
    /// Whether there was a hold to lift. False covers both "nobody ever pressed Pause" and "the hold was already
    /// ended by a downgrade" (<see cref="DowngradeLocked"/> clears the flag), and both deserve the same answer:
    /// this run is not being held for the operator, so nothing was lifted for them either.
    /// </returns>
    public bool ResumeByUser()
    {
        lock (_lock)
        {
            if (!_pausedByUser)
                return false;
            _pausedByUser = false;
            // Whatever trouble may still be holding the gate, the operator's hold is what stopped the process,
            // and lifting it lets the file finish; the loop holding it then parks at the gate like any other.
            _processes.Release();
            TrackSettledLocked();

            // The patience budget starts over, exactly as it does for Retry now (see ReleaseNow), and for the
            // same reason: the run has not been given a single chance to retry since the hold went up, so
            // whatever the clock accumulated in the meantime is evidence of nothing. Without this, pausing
            // overnight after one failure means the first retry after Resume finds the ten minutes already
            // spent and downgrades the run on the spot, with no retry window at all. Clearing _failures with
            // it restarts the backoff ladder at its first step, which is the same fresh start seen from the
            // other side: what the operator authorised is one more honest attempt, not the tail of an old one.
            _troubleSince = null;
            _failures = 0;

            // _timer is non-null exactly while a transient-error pause is running its backoff, and leaving the
            // gate shut in that case is the point: the trouble reason outlives the user's. There is nothing to
            // relabel on the way out, either — while _timer is non-null, _current is necessarily the PauseInfo
            // OpenLocked wrote next to it, because the only other writers are PauseByUser (which writes only
            // when the gate was fully open, and an open gate has no timer) and ReleaseLocked (which nulls
            // _timer first). So Current already reports TransientError.
            if (_timer is null)
                ReleaseLocked(true);
            return true;
        }
    }

    /// <summary>
    /// Pass through an open gate, park at a closed one. This is the call at the top of each producing loop.
    /// <para>
    /// Deliberately not <see cref="WaitAsync"/>: that one means "I failed, count it against the patience"
    /// and opens the gate itself. Arriving at a gate must register no failure and must never be able to
    /// trigger a downgrade — otherwise merely looping would consume the run's patience.
    /// </para>
    /// <para>
    /// The released value is ignored. A false means the gate downgraded, and the caller learns what to do
    /// about that from the stop intent it checks straight afterwards, not from here.
    /// </para>
    /// </summary>
    public async Task WaitIfPausedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task<bool> release;
        lock (_lock)
        {
            if (_release is null)
                return;
            release = _release.Task;
        }
        await release.WaitAsync(ct);
    }

    /// <summary>
    /// <see cref="WaitIfPausedAsync"/> for a worker that is in the middle of its item: an uploader between two
    /// volumes of one file, the pack loop between two groups, the diff between two of its callbacks. It parks the
    /// same way, and steps out of the in-hand count while it is parked — a worker that has stopped moving is not
    /// what keeps the run "pausing", however much of its item is still ahead of it.
    /// <para>
    /// The loop-top call stays <see cref="WaitIfPausedAsync"/>: a loop between two items holds nothing and is not
    /// counted, so there is nothing for it to step out of.
    /// </para>
    /// </summary>
    public async Task ParkAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task<bool> release;
        lock (_lock)
        {
            if (_release is null)
                return;
            release = _release.Task;
        }
        using (Idle())
            await release.WaitAsync(ct);
    }

    /// <summary>
    /// Reported while the user's hold is what the workers are parked on. <c>NextRetryAt</c> is null because there
    /// is nothing to count down to, and the failure count is passed through as it stands rather than zeroed: if
    /// trouble did happen (before the pause, or to a volume that was already on the wire when it was pressed),
    /// hiding that from the operator is a lie, and it is the very number the next backoff's length comes from.
    /// It carries no threat here — see <see cref="PatienceExhausted"/>, a user pause cannot downgrade — and
    /// <see cref="ResumeByUser"/> resets it the moment the hold comes down.
    /// </summary>
    private PauseInfo UserPauseInfo() =>
        new("Paused by the user.", _userPausedSince, NextRetryAt: null, _failures, PauseSource.User);

    /// <summary>
    /// Has this round of trouble outlasted our patience, i.e. is it time to downgrade the run to an auto-suspend?
    /// <para>
    /// A standing user hold makes the answer no, whatever the clock says. What patience measures is "the run kept
    /// retrying and nothing ever recovered"; while the user holds the gate shut, no worker is permitted to retry
    /// anything, so the elapsed time is not evidence of a network that will not come back — it is evidence of an
    /// operator having a coffee. Design §4 states the promise this keeps: a user pause never downgrades the run
    /// on its own, because an automatic downgrade would turn a pause into a suspend exactly when nobody is
    /// watching. <see cref="ResumeByUser"/> then clears the clock, so the run comes back with its full budget.
    /// </para>
    /// </summary>
    private bool PatienceExhausted(DateTimeOffset now) =>
        !_pausedByUser && _troubleSince is { } since && now - since >= _patience;

    private Task<bool> OpenLocked(Exception cause)
    {
        var now = DateTimeOffset.UtcNow;
        _troubleSince ??= now;
        _failures++;

        if (PatienceExhausted(now))
        {
            DowngradeLocked();
            return Task.FromResult(false);
        }

        var delay = DelayFor(_failures);
        // Reuse a TCS a standing user pause already created rather than replacing it: workers that arrived at
        // PauseByUser's gate are awaiting that exact task, and a second one here would strand them.
        _release ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _current = new PauseInfo(cause.Message, now, now + delay, _failures, PauseSource.TransientError);
        _timer = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);

        var token = _timer.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) { return; }   // early release / downgrade / gate gone
            lock (_lock)
            {
                // The timer fired, so ask first: has this round of trouble already outlasted our patience?
                // Checking only when the gate opens is not enough — the last backoff can be as long as 5 minutes.
                if (PatienceExhausted(DateTimeOffset.UtcNow))
                    DowngradeLocked();
                else
                    ReleaseLocked(true);
            }
        }, CancellationToken.None);

        return _release.Task;
    }

    /// <summary>Once the backoff schedule is used up, keep going at a fixed interval instead of doubling forever into hours.</summary>
    private TimeSpan DelayFor(int failures)
        => failures <= _schedule.Count ? _schedule[failures - 1] : _steady;

    private void ReleaseLocked(bool proceed)
    {
        _timer?.Cancel();
        _timer?.Dispose();
        _timer = null;

        // A user pause outlives the trouble that happened to coincide with it. Releasing here would let a
        // backoff timer — or a Retry now aimed at the trouble — cancel a pause the user never lifted.
        // proceed: false is a downgrade, which must pierce everything: it is how Suspend reaches a parked worker.
        if (proceed && _pausedByUser)
        {
            _current = UserPauseInfo();
            return;
        }

        _current = null;
        var tcs = _release;
        _release = null;
        tcs?.TrySetResult(proceed);
    }

    private void DowngradeLocked()
    {
        _downgraded = true;

        // The user's hold ends here with everything else: a downgraded run is suspending, not pausing, and it
        // will never wait at this gate again. Clearing the flag before ReleaseLocked also keeps the downgrade
        // from depending on that method's `proceed &&` half to pierce the hold — the release below is
        // unconditional either way, and nothing left behind can claim afterwards that the operator is holding
        // a run that has already gone.
        _pausedByUser = false;
        // Before the release below and before the caller cancels anything: a stopped process is one the
        // wind-down cannot reach. See Processes.
        _processes.Release();
        TrackSettledLocked();

        ReleaseLocked(false);
    }

    public void Dispose()
    {
        lock (_lock)
            DowngradeLocked();
        _life.Cancel();
        _life.Dispose();
    }
}
