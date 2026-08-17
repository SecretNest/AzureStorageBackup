namespace AzureStorageBackup.Api.Services;

public enum RunStatus
{
    Running,
    Completed,
    Failed,

    /// <summary>The user pressed stop. Neither a failure nor a success: **no Error status is written** (otherwise this
    /// backup would carry a red Error from then on, clearable only by a manual Reset), and it is not recorded as a successful run.</summary>
    Canceled,

    /// <summary>
    /// The scene was preserved, the work was not finished. The difference from Failed is concrete: the journal is
    /// still on disk, and the next round (the user clicking Resume, or the next scheduled task) accepts what was
    /// already uploaded as it stands and does not re-upload it.
    /// <para>
    /// Note this is used **only** at the moment the run has genuinely exited. While waiting to retry a transient
    /// error the status is still Running (see <see cref="BackupRunState.Pause"/>) — the Task is alive and the seat is
    /// still taken there, so reporting a terminal state would make the scheduler think this round is over and start
    /// another one that displaces it.
    /// </para>
    /// </summary>
    Suspended,
}

/// <summary>In-memory state of one backup run (polled by the frontend).</summary>
public sealed class BackupRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;
    public BackupProgress? Progress { get; set; }
    public int? Version { get; set; }

    /// <summary>Number of files this round could not read and therefore carried the old index entry forward for. A
    /// "successful" backup may have stored nothing at all; leave this number off the UI and the operator has only the
    /// notification to go on — and notifications drown in other messages.</summary>
    public int? UnreadableFiles { get; set; }

    /// <summary>
    /// What this round changed and what it cost, copied off <see cref="BackupRunResult"/> when the run finishes.
    /// The same figures <see cref="BackupSummary"/> puts in the operation log and the webhook notification —
    /// but the page the operator is actually looking at when a backup ends polls this state, and until these
    /// were carried here the only way to learn what a round did was to go and open the log.
    /// <para>
    /// Null while the run is going, deliberately not 0: zero would read as "this round changed nothing",
    /// which nobody is in a position to claim before the diff has finished.
    /// </para>
    /// </summary>
    public int? NewFiles { get; set; }

    /// <inheritdoc cref="NewFiles"/>
    public int? ModifiedFiles { get; set; }

    /// <inheritdoc cref="NewFiles"/>
    public int? DeletedFiles { get; set; }

    /// <summary>Source-side raw size of the deleted files, read off the previous version's index. **Not** the space
    /// the cloud gave back — see <see cref="BackupRunResult.DeletedBytes"/>. Nullable for the reason in <see cref="NewFiles"/>,
    /// and here that matters twice over: an older backend sends no such field, and rendering it as 0 would state
    /// something about those files that nobody knows.</summary>
    public long? DeletedBytes { get; set; }

    /// <summary>Source-side raw bytes of the changed files, before compression and dedup. See <see cref="NewFiles"/> for why it is nullable.</summary>
    public long? ChangedBytes { get; set; }

    /// <summary>Bytes actually pushed to the cloud; content that hit dedup counts zero. Read together with
    /// <see cref="ChangedBytes"/> to see what compression and dedup each saved. See <see cref="NewFiles"/> for why it is nullable.</summary>
    public long? UploadedBytes { get; set; }

    public string? Error { get; set; }

    /// <summary>Start and finish moments of this backup, taken from the version record (see <see cref="BackupRunResult.CompletedAt"/>).
    /// Null until it completes.</summary>
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Identifier of this run. The journal file is named after it, and resume matches on it.</summary>
    public string RunId { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Why the run was suspended; null when it is not suspended.</summary>
    public SuspendReason? SuspendReason { get; set; }

    /// <summary>Internal machinery, not part of the HTTP contract: the handle on this run — Suspend / Retry now reach the gate through it.</summary>
    internal BackupRunControl? Control { get; set; }

    /// <summary>
    /// Whether it is currently stuck on a transient error waiting to retry. **This is not a status value**: Status is
    /// still Running, because the Task is alive and the seat is still taken; reporting a terminal state would make
    /// the scheduler start another round that displaces it.
    /// </summary>
    public PauseInfo? Pause => Control?.Gate.Current;

    /// <summary>
    /// Whether the operator's own hold is standing right now. Deliberately not read off <see cref="Pause"/>:
    /// <see cref="PauseInfo.Source"/> reports only whichever reason most recently closed the gate, so a Pause
    /// pressed while a transient-error backoff is already running leaves <c>Pause.Source</c> saying
    /// <c>TransientError</c> — countdown and Retry-now affordance included — until that backoff's own timer
    /// fires, up to one steady interval (five minutes by default). A UI rendering paused-ness from <c>Pause</c>
    /// alone would show such a run as merely stuck and retrying, as though the operator's Pause had done
    /// nothing. Reading <see cref="PauseGate.IsPausedByUser"/> live, instead of a value baked into a stored
    /// <see cref="PauseInfo"/>, is what lets this stay true for as long as the hold does, independent of which
    /// reason <c>Pause.Source</c> happens to be reporting at the moment.
    /// </summary>
    public bool PausedByUser => Control?.Gate.IsPausedByUser ?? false;

    /// <summary>
    /// Internal machinery, not part of the HTTP contract: the original exception on failure. Set alongside Error in
    /// RunCoreAsync's catch so TaskDispatcher can attach it as the InnerException when it rethrows — the container log
    /// therefore keeps the status code, request id and real stack that the Azure exception carries, instead of being
    /// left with just a one-line message and a stack that starts at the throw site (Fix 4).
    /// </summary>
    internal Exception? Failure { get; set; }

    /// <summary>
    /// Internal machinery, not part of the HTTP contract: fires once when this run reaches a terminal state
    /// (Completed/Failed/Canceled). Awaited by RunTrackedAsync's short-circuit branch; not for frontend polling.
    /// </summary>
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Internal machinery, not part of the HTTP contract: this run's cancellation source, used by the /cancel endpoint.
    /// Before it existed, the only way to stop a backup that had been running for hours was to restart the container — and
    /// the user runs on a NAS, where that takes other services down with it; "no deleting a config while it is busy" closed off the delete escape hatch too.</summary>
    internal CancellationTokenSource Cancellation { get; } = new();
}

public sealed record BackupRunResponse(
    string Status, BackupProgress? Progress, int? Version, int? UnreadableFiles, string? Error,
    DateTimeOffset? StartedAt = null, DateTimeOffset? CompletedAt = null,
    string RunId = "", PauseInfo? Pause = null, string? SuspendReason = null,
    // What the round changed and what it uploaded — see the matching properties on BackupRunState.
    int? NewFiles = null, int? ModifiedFiles = null, int? DeletedFiles = null,
    long? ChangedBytes = null, long? UploadedBytes = null, long? DeletedBytes = null,
    // Sibling of Pause, not a field on it — see BackupRunState.PausedByUser for why the two can disagree.
    bool PausedByUser = false)
{
    public static BackupRunResponse From(BackupRunState s) =>
        new(s.Status.ToString(), s.Progress, s.Version, s.UnreadableFiles, s.Error, s.StartedAt, s.CompletedAt,
            s.RunId, s.Pause, s.SuspendReason?.ToString(),
            s.NewFiles, s.ModifiedFiles, s.DeletedFiles, s.ChangedBytes, s.UploadedBytes, s.DeletedBytes,
            s.PausedByUser);
}

/// <summary>
/// Background backup runner: runs BackupOrchestrator in the background for a config id, keeping progress in memory for polling.
/// A config that is already running is not started a second time. Globally non-concurrent compression is guaranteed by the singleton StagingArea.
/// </summary>
public sealed class BackupRunner(IServiceScopeFactory scopes, BackupBusyTracker busy)
{
    private readonly Dictionary<int, BackupRunState> _runs = [];
    private readonly Lock _lock = new();

    /// <summary>
    /// For the UI: resolve the config → grab the busy lock → register → run in the background. Returns the existing
    /// state when the same config is already running. Resolving the config needs async I/O, so the whole method is
    /// async: the lock must be in hand before registering into _runs (see below).
    /// </summary>
    public async Task<BackupRunState> StartAsync(int configId)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;
        }

        int accountId;
        string container;
        try
        {
            using var scope = scopes.CreateScope();
            var config = await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().GetAsync(configId)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            accountId = config.AccountId;
            container = config.ContainerName;
        }
        catch (Exception ex)
        {
            var failed = new BackupRunState { Error = ex.Message, Status = RunStatus.Failed };
            failed.Completion.TrySetResult();
            return failed;
        }

        // Mark this backup busy (so scheduled tasks can detect it); if it is already busy, refuse the concurrent operation.
        if (!busy.TryAcquire(accountId, container, "BackingUp"))
        {
            var failed = new BackupRunState { Error = "This backup is busy with another operation.", Status = RunStatus.Failed };
            failed.Completion.TrySetResult();
            return failed;
        }

        // _runs is written only after the busy lock is in hand: the old implementation registered first and grabbed
        // the lock second, leaving a window between the two — a Running entry already visible in _runs with no lock
        // protecting it. The scheduler (TaskDispatcher.DispatchAsync) would grab, inside exactly that window, the
        // lock this call was supposed to hold; RunTrackedAsync then saw that "Running" entry, took this dispatch
        // round to mean "a real backup is already running" and went off to wait for it without executing anything
        // itself; meanwhile the TryAcquire here was bound to come up empty and marked that shared state Failed — so
        // the whole dispatch round ran nothing at all, yet was recorded as an error. Now the lock has to be in hand
        // before _runs is written, so a Running entry always means a run really does hold the lock, and the window
        // no longer exists.
        var state = new BackupRunState();
        lock (_lock)
            _runs[configId] = state;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunCoreAsync(configId, state, state.Cancellation.Token);
            }
            finally
            {
                busy.Release(accountId, container);
            }
        });

        return state;
    }

    /// <summary>
    /// For the scheduler: the caller **already holds** the busy lock for this (account, container)
    /// (TaskDispatcher.DispatchAsync grabs it before entering execution). This method neither acquires nor releases
    /// it; it only executes and registers the state into _runs for the GET endpoint to poll.
    ///
    /// Ownership of the lock is expressed by which method you call, not by a boolean parameter: get that boolean
    /// wrong once and either every scheduled backup refuses to run, or nobody holds the lock at all — and neither of
    /// those shows up at compile time.
    /// </summary>
    public async Task<BackupRunState> RunTrackedAsync(int configId, CancellationToken ct)
    {
        BackupRunState state;
        bool alreadyRunning;
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
            {
                state = existing;
                alreadyRunning = true;
            }
            else
            {
                state = new BackupRunState();
                _runs[configId] = state;
                alreadyRunning = false;
            }
        }

        if (alreadyRunning)
        {
            // The contract with callers is that this method only ever returns a terminal state: hand this still-
            // Running state back as it stands and the scheduler's "only Status == Failed counts as a failure" test
            // treats a backup that never ran as a silent success. Wait for it to reach a terminal state
            // (Completed/Failed), then return.
            // With the lock-before-register ordering this branch is currently unreachable and kept purely as
            // defence; but should it become reachable again, an await without a cancellation token would leave the
            // scheduler hanging on the busy lock forever, uninterruptible even by shutdown — pass ct so it can at
            // least wind down along with the shutdown (Fix 5).
            await state.Completion.Task.WaitAsync(ct);
            return state;
        }

        // Either the scheduler's ct (shutdown) or this run's own cancellation source (the user pressing stop),
        // whichever arrives first, counts as cancellation: a scheduled backup can be stopped from the UI too — it
        // runs the same execution body and can just as easily run all night.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, state.Cancellation.Token);
        await RunCoreAsync(configId, state, linked.Token);
        return state;
    }

    public BackupRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    /// <summary>Issue the intent to stop. Returns the run that was told to stop, or null when nothing is running.</summary>
    private BackupRunState? RequestStop(
        int configId, StopKind kind, SuspendReason reason = SuspendReason.UserRequested)
    {
        BackupRunState? state;
        // Cancel()/RequestStop() run the registered callbacks synchronously on **the current thread**; do that inside
        // _lock and any callback that reaches back into this runner deadlocks on itself. The lock is only there to
        // fetch that one entry.
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running })
            return null;
        if (state.Control is { } control)
        {
            // control is already disposed before the status flips to terminal (`await using` takes effect ahead of
            // the catch blocks). A stop request arriving in that instant can do nothing — this round is winding down
            // anyway, so treat it as "not running".
            try { control.RequestStop(kind, reason); }
            catch (ObjectDisposedException) { return null; }
        }
        else
            state.Cancellation.Cancel();   // hasn't reached the point where control gets built yet (config resolution stage)
        return state;
    }

    /// <summary>Stop right now (without waiting for the flush to disk). Kept so the shared /cancel endpoint has the same shape as the other runners.</summary>
    public bool Cancel(int configId) => RequestStop(configId, StopKind.StopNow) is not null;

    /// <summary>Deliberate suspend: finish the item in hand, flush to disk, exit as Suspended. Returns only once the flush is done.</summary>
    /// <param name="reason">The suspend reason written into the on-disk marker. A suspend pressed in the UI uses the
    /// default; the shutdown path passes <see cref="SuspendReason.ShuttingDown"/>.</param>
    public async Task<bool> SuspendAsync(
        int configId, SuspendReason reason = SuspendReason.UserRequested, CancellationToken ct = default)
    {
        if (RequestStop(configId, StopKind.Suspend, reason) is not { } state)
            return false;
        await state.Completion.Task.WaitAsync(ct);
        return true;
    }

    /// <summary>
    /// Cap on how long shutdown waits for every run to flush to disk. The three numbers form one chain; move one and
    /// you have to go back and look at the other two:
    /// <code>
    /// docker-compose stop_grace_period 45s  &gt;  HostOptions.ShutdownTimeout 30s  &gt;  the 20s here
    /// </code>
    /// 45 &gt; 30: once docker's grace period expires it is SIGKILL, so .NET's own timeout has to fire first for there to be any chance of getting the log out.
    /// 30 &gt; 20: the host's wait on <c>StopAsync</c> has a timeout too, and when it expires the host does not wait — it goes
    /// straight on to tearing services down, and by then nobody is left to write down "who failed to stop". The 10 seconds
    /// of headroom are there for the warning log below and for the remaining host services to wind down.
    /// <para>
    /// <c>internal</c> rather than <c>private</c>: the same precedent as <see cref="Endpoints.BackupConfigEndpoints.StopWaitCap"/>
    /// — the test project uses <c>InternalsVisibleTo</c> (see AssemblyInfo.cs) to turn the 20 seconds into milliseconds, which
    /// is the only way to afford testing the "still not flushed when the deadline hit" branch without really waiting 20 seconds.
    /// Like that precedent, it is a **process-wide shared mutable static field**, and its safety rests on two conventions that
    /// no code enforces: (1) xUnit runs the <c>[Fact]</c>s inside one class sequentially, and the test that changes the field
    /// restores it in a try/finally, so it cannot fight the other tests in its own class; (2) classes do run in parallel with
    /// each other, and **every TestWebAppFactory disposal goes through GracefulSuspendService.StopAsync, i.e. every one of them
    /// calls <c>SuspendAllAsync</c>** — so what actually holds this up is not "only one file touches it" but "during the
    /// hundred-odd milliseconds the field is turned down, none of the tests running in parallel has a backup in flight". That
    /// happens to be true today, and even on a collision the consequence is only that one shutdown waits a little less, not a
    /// wrong result.
    /// Production is always 20 seconds.
    /// </para>
    /// </summary>
    internal static TimeSpan SuspendWaitCap = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Suspend **every** run in flight and wait for them to flush their journals to disk. Returns the number of runs
    /// that really came to rest as <see cref="RunStatus.Suspended"/>.
    /// <para>
    /// Shutdown path only, and it has to be honest about what it cannot do: <see cref="StopKind.Suspend"/> deliberately
    /// does not touch the AbortToken, and each pipeline stage only exits before starting the **next** entry, so what an
    /// uploader has in hand — possibly a multi-GB upload — is left to run to completion. The wait here is therefore **capped**
    /// (<see cref="SuspendWaitCap"/>): a run that has not flushed by the deadline is abandoned mid-flight, and at the next
    /// start it is an interrupted run **with no marker**, which the operator has to Resume by hand; it is not picked up automatically.
    /// </para>
    /// </summary>
    public async Task<int> SuspendAllAsync(SuspendReason reason, CancellationToken ct)
    {
        // Copy the ids out under the lock first. _runs is a plain Dictionary; enumerate it without the lock while
        // someone is registering a new run and you get an InvalidOperationException on the spot — and that lands right
        // on the shutdown path, where there is no second chance. Release the lock as soon as the copy is made:
        // RequestStop runs the cancellation callbacks synchronously on **the current thread**, and walking in there
        // still clutching the lock is a guaranteed self-deadlock (same note as at RequestStop).
        List<int> running;
        lock (_lock)
            running = [.. _runs.Where(kv => kv.Value.Status == RunStatus.Running).Select(kv => kv.Key)];

        // Two passes: **first** send the stop intent to every run, **then** wait for all of them to flush.
        // Folding it into one pass (send one, wait for it) is fatal with concurrent backups: if the one at the head is
        // sitting on a multi-GB upload it eats the entire shutdown budget by itself, and the runs behind it never even
        // receive RequestStop — nothing flushed, no marker, straight to the axe.
        // Signalling itself is only a few assignments plus synchronous callbacks; it all goes out in an instant.
        var pending = new List<(int ConfigId, BackupRunState State)>();
        foreach (var configId in running)
        {
            try
            {
                if (RequestStop(configId, StopKind.Suspend, reason) is { } state)
                    pending.Add((configId, state));
            }
            catch (Exception ex)
            {
                // One run failing to take the order must not block the others — there is no second chance on the
                // shutdown path. The logger is fetched by opening a temporary scope, the way this class already does
                // it (it has no injected logger), and only on the occasion something went wrong: a normal shutdown
                // does not build a single scope.
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetService<ILogger<BackupRunner>>()?
                    .LogWarning(ex, "Failed to suspend backup {ConfigId} during shutdown", configId);
            }
        }
        if (pending.Count == 0)
            return 0;

        using var capped = CancellationTokenSource.CreateLinkedTokenSource(ct);
        capped.CancelAfter(SuspendWaitCap);
        // How long we actually waited. Both branches have to report it: naming "which deadline expired" is not enough,
        // because what the person reading the log really has to judge is "was it nearly enough, or hopeless from the
        // start" — and the attribution itself has a narrow crack (the cap fires first and the host token then trips
        // too, sending the if below down the neutral branch), whereas the measured duration is immune to that crack
        // and is the one number that is always right.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.WhenAll(pending.Select(p => p.State.Completion.Task)).WaitAsync(capped.Token);
        }
        catch (OperationCanceledException)
        {
            // A timeout (or the caller's ct tripping first) is not rethrown: throw and nobody writes the log below,
            // and when someone later tries to work out "why does this run have no marker", that log is the only thing
            // that can explain it. But the log itself must not misattribute the timeout — the same catch catches both
            // our own SuspendWaitCap (20s) and the caller's ct tripping first (the host's ShutdownTimeout, 30s).
            // The two are equally valuable as evidence, but their names must not be swapped: when it really comes to
            // diagnosing "why is there no marker", a log that names the wrong deadline misleads worse than no log.
            //
            // Naming the runId and not just the configId: the first question afterwards is always "which journal on
            // disk is that", and the journal file is named after the runId, so writing it down saves a lookup.
            var stuck = pending
                .Where(p => !p.State.Completion.Task.IsCompleted)
                .Select(p => $"{p.ConfigId} (run {p.State.RunId})");
            using var scope = scopes.CreateScope();
            var logger = scope.ServiceProvider.GetService<ILogger<BackupRunner>>();
            if (capped.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Our own SuspendWaitCap expired: we can name the exact number of seconds.
                logger?.LogWarning(
                    "Gave up after {Elapsed:0.0}s (cap {Seconds}s) waiting for backup(s) {ConfigIds} to "
                    + "suspend; they are left mid-flight and will come back as interrupted runs to be "
                    + "resumed by hand",
                    waited.Elapsed.TotalSeconds, SuspendWaitCap.TotalSeconds, string.Join(", ", stuck));
            }
            else
            {
                // The caller's token tripped first, not our 20 seconds. The wording has to make clear that **the
                // host's shutdown deadline expired**, not that "the shutdown was cancelled" — the latter sends people
                // looking for "who aborted this shutdown", and there is no such person.
                logger?.LogWarning(
                    "Gave up after {Elapsed:0.0}s waiting for backup(s) {ConfigIds} to suspend because the "
                    + "host's shutdown deadline (HostOptions.ShutdownTimeout) expired first; they are left "
                    + "mid-flight and will come back as interrupted runs to be resumed by hand",
                    waited.Elapsed.TotalSeconds, string.Join(", ", stuck));
            }
        }

        // Count only the ones that really came to rest as Suspended. The ones we timed out on, and the ones a
        // concurrently arriving Stop now beat us to and pressed into Canceled, have no marker on disk — count them in
        // and the shutdown log is bragging.
        return pending.Count(p => p.State.Status == RunStatus.Suspended);
    }

    /// <summary>Cancel. When <paramref name="finishCurrentFiles"/> is true, wait for the in-flight files (including all their volumes) to finish uploading.
    /// The user asked for "Cancel must not return until the flush has succeeded", so this has to wait for a terminal state.</summary>
    public async Task<bool> CancelAsync(int configId, bool finishCurrentFiles, CancellationToken ct = default)
    {
        var kind = finishCurrentFiles ? StopKind.FinishCurrentFiles : StopKind.StopNow;
        if (RequestStop(configId, kind) is not { } state)
            return false;
        await state.Completion.Task.WaitAsync(ct);
        return true;
    }

    /// <summary>The user clicked <c>Retry now</c>: don't wait for the self-healing timer, let the retry through immediately.</summary>
    public bool RetryNow(int configId)
    {
        BackupRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running } || state.Pause is null)
            return false;
        state.Control!.Gate.ReleaseNow();
        return true;
    }

    /// <summary>
    /// The user pressed Pause: hold the run where it is. Each stage finishes the item in hand and then parks at the
    /// gate, so it takes effect within one item per stage — worst case, the time to compress one large file.
    /// <para>
    /// Nothing is discarded and nothing is flushed. The run stays alive, holding its staging quota — which is booked
    /// on a process-wide singleton, so a run paused overnight makes this machine's other backups wait overnight —
    /// until <see cref="Resume"/>. That is the price of Resume being free: there is nothing to re-scan, re-diff or
    /// re-probe, because none of it was thrown away.
    /// </para>
    /// <para>
    /// A process restart loses all of it, which is why this does not replace Suspend: pause is memory state, and the
    /// shutdown path still has to suspend.
    /// </para>
    /// </summary>
    /// <returns>
    /// false when there is nothing to hold: no live run for this config, or a run whose gate is already
    /// downgraded.
    /// <para>
    /// The second case is not an edge: <see cref="RunStatus"/> stays Running for the whole wind-down after a
    /// Suspend or a Stop — which can be minutes, since a suspend waits for the volume in hand to finish
    /// uploading — and it stays Running after a patience auto-suspend too. Pressing Pause in that window used to
    /// return success while <see cref="PauseGate.PauseByUser"/> quietly did nothing, so the operator was told the
    /// run was held and it was not. The answer comes from the gate itself rather than from a
    /// <see cref="PauseGate.IsDowngraded"/> check here, so that it cannot be read before the call it describes.
    /// </para>
    /// </returns>
    public bool Pause(int configId)
    {
        BackupRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        // Unlike RetryNow this cannot lean on `state.Pause is not null` to prove the control is there — the whole
        // point is to pause a run that is not paused — so Control is checked directly. It is assigned a few awaits
        // into RunCoreAsync (config, account and settings are loaded first), and until then a run really is Running
        // with no gate to hold.
        if (state is not { Status: RunStatus.Running, Control: not null })
            return false;
        return state.Control.Gate.PauseByUser();
    }

    /// <summary>
    /// Lift a user pause. If a transient error is holding the gate as well — pressing Pause does not stop the volume
    /// already on the wire, and that upload can still fail — the run stays parked on that one and the UI goes on
    /// reporting it, which is correct: the run is not ready to proceed just because the operator is.
    /// </summary>
    /// <returns>
    /// false when there is no hold to lift: no live run, or a run nobody is holding — including one whose hold a
    /// Suspend, a Stop or a patience downgrade has already ended, which is the same window
    /// <see cref="Pause"/> describes and wants the same answer, so that the endpoint's conflict means what it
    /// says rather than reporting success for a button press that changed nothing.
    /// </returns>
    public bool Resume(int configId)
    {
        BackupRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running, Control: not null })
            return false;
        return state.Control.Gate.ResumeByUser();
    }

    /// <summary>The execution body shared by both entry points. **Does not touch the busy lock** — the lock is the caller's job.</summary>
    private async Task RunCoreAsync(int configId, BackupRunState state, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var configs = sp.GetRequiredService<IBackupConfigService>();

            var config = await configs.GetAsync(configId, ct)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            var account = await sp.GetRequiredService<IAccountService>().GetAsync(config.AccountId, ct)
                ?? throw new InvalidOperationException($"Account {config.AccountId} not found.");
            var settings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
            var password = sp.GetRequiredService<ISecretReader>().RevealBackupPassword(config);

            await using var control = new BackupRunControl(
                sp.GetRequiredService<BackupJournalStore>(), configId, state.RunId);
            state.Control = control;
            var result = await sp.GetRequiredService<BackupOrchestrator>().RunAsync(
                BackupRequestMapper.From(config, account, password, settings, sp.GetService<PackLimits>()),
                new StateProgress(state), ct, control);
            state.Version = result.Version;
            state.UnreadableFiles = result.UnreadableFiles;
            state.NewFiles = result.NewFiles;
            state.ModifiedFiles = result.ModifiedFiles;
            state.DeletedFiles = result.DeletedFiles;
            state.DeletedBytes = result.DeletedBytes;
            state.ChangedBytes = result.ChangedBytes;
            state.UploadedBytes = result.UploadedBytes;
            state.StartedAt = result.StartedAt;
            state.CompletedAt = result.CompletedAt;
            state.Status = RunStatus.Completed;

            await configs.WriteStatusAsync(configId, error: null, sp.GetService<ILogger<BackupRunner>>());
            state.Completion.TrySetResult();
        }
        catch (BackupSuspendedException ex)
        {
            // Not a failure: the journal is still on disk and no Error is written (otherwise this backup would carry
            // red text from then on, clearable only by a manual Reset); the next round accepts what was already
            // uploaded as it stands.
            state.Status = RunStatus.Suspended;
            state.SuspendReason = ex.Reason;
            // Like the other three terminal branches, release the waiters, or RunTrackedAsync hangs on Completion forever.
            state.Completion.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            // The user pressed stop (or the process is shutting down): not a failure. Neither an Error status nor a
            // Normal one is written — this round reached no conclusion at all, so the persisted state stays as it was.
            state.Status = RunStatus.Canceled;
            state.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Failure = ex;
            state.Status = RunStatus.Failed;
            // The original scope may already be disposed along with the exception (`using var scope` releases when the try block exits): open another one to write the status.
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .WriteStatusAsync(configId, ex.Message, scope.ServiceProvider.GetService<ILogger<BackupRunner>>());
            state.Completion.TrySetResult();
        }
    }

    private sealed class StateProgress(BackupRunState state) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => state.Progress = value;
    }
}
