namespace AzureStorageBackup.Api.Services;

/// <summary>Why the gate is closed. The two compose; see PauseGate's remarks.</summary>
public enum PauseSource
{
    /// <summary>A worker hit a transient error. Self-heals on a timer, and downgrades if patience runs out.</summary>
    TransientError,

    /// <summary>The user pressed Pause. No timer, no patience, and it never downgrades on its own.</summary>
    User,
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

    public PauseGate(
        IReadOnlyList<TimeSpan>? schedule = null, TimeSpan? steady = null, TimeSpan? patience = null)
    {
        _schedule = schedule is { Count: > 0 } ? schedule : DefaultSchedule;
        _steady = steady ?? TimeSpan.FromMinutes(5);
        _patience = patience ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>The pause in effect right now; null when nothing is paused.</summary>
    public PauseInfo? Current { get { lock (_lock) return _current; } }

    public bool IsDowngraded { get { lock (_lock) return _downgraded; } }

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

    /// <summary>Downgrade: the user clicked Suspend, or patience ran out. Every waiter gets false.</summary>
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
    public void PauseByUser()
    {
        lock (_lock)
        {
            if (_downgraded || _pausedByUser)
                return;
            _pausedByUser = true;
            _userPausedSince = DateTimeOffset.UtcNow;
            if (_release is null)
            {
                _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _current = UserPauseInfo();
            }
        }
    }

    /// <summary>
    /// The user pressed Resume. Only lifts the user's own hold: if trouble is still keeping the gate shut,
    /// the workers stay parked and the UI goes on reporting the trouble, which is correct — the run is
    /// not ready to proceed just because the operator is.
    /// </summary>
    public void ResumeByUser()
    {
        lock (_lock)
        {
            if (!_pausedByUser)
                return;
            _pausedByUser = false;

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
