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
    private DateTimeOffset? _troubleSince;          // null = no trouble at the moment (a success clears it)
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
    /// the workers stay parked and the UI goes back to reporting the trouble, which is correct — the run is
    /// not ready to proceed just because the operator is.
    /// </summary>
    public void ResumeByUser()
    {
        lock (_lock)
        {
            if (!_pausedByUser)
                return;
            _pausedByUser = false;
            // _timer is non-null exactly while a transient-error pause is running its backoff.
            if (_timer is null)
                ReleaseLocked(true);
            else
                _current = _current! with { Source = PauseSource.TransientError };
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

    private PauseInfo UserPauseInfo() =>
        new("Paused by the user.", _userPausedSince, NextRetryAt: null, Failures: 0, PauseSource.User);

    private Task<bool> OpenLocked(Exception cause)
    {
        var now = DateTimeOffset.UtcNow;
        _troubleSince ??= now;
        _failures++;

        if (now - _troubleSince.Value >= _patience)
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
                if (_troubleSince is { } since && DateTimeOffset.UtcNow - since >= _patience)
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
