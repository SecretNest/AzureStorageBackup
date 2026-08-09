using AzureStorageBackup.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// After startup, continues the backups that a **planned exit** interrupted last time (governed by <c>GlobalSettings.AutoResumeInterruptedRuns</c>).
/// <para>
/// The precondition is that a journal is still on disk — a run that finished deletes its own volume, so one still lying there means "did not finish".
/// But "did not finish" falls far short of "we should restart it on his behalf"; for the criteria see <see cref="PickResumableAsync"/>.
/// </para>
/// </summary>
public sealed class AutoResumeService(
    IServiceScopeFactory scopes, BackupJournalStore journals, BackupRunner runner,
    ILogger<AutoResumeService> logger) : BackgroundService
{
    /// <summary>
    /// Wait this long before starting work: let the web port come up and the scheduler take its first tick before going after the output lock.
    /// <para>
    /// Writable (rather than <c>const</c>/<c>readonly</c>) purely for the tests: nobody is willing to run a test that waits 15 seconds, and this if
    /// ("with the setting off, really start nothing") is exactly the one sentence about this feature that most needs pinning down — the way it breaks is **silent**:
    /// the operator unticks the box, the UI still reports saved successfully, and then one day after a restart a backup he did not want starts by itself.
    /// The precedent is <see cref="BackupRunner.SuspendWaitCap"/>.
    /// </para>
    /// <para>
    /// What makes a writable static field safe here is **not** "there is no second instance in the tests" — that statement is false:
    /// the SchedulerOnFactory in AutoResumeTests and the one in GracefulSuspendTests both set
    /// <c>Scheduler:Enabled</c> to true and really do start this service, and xUnit is allowed to run those classes in parallel.
    /// What actually holds it up is something else: every <see cref="TestWebAppFactory"/> host uses its own SQLite file, the BackupConfigs table on those two
    /// hosts is empty, <see cref="PickResumableAsync"/> gets back an empty list, and so it changes no behavior at all whether they
    /// read 50 milliseconds or 15 seconds.
    /// So the premise is "the test hosts running in parallel have no backup configs", not "there is no other instance". The day a test host starts
    /// this service with configs in place, this field has to become an injected option — at that point, of two parallel tests, one setting it to 50 milliseconds
    /// and one waiting for it not to start work, the latter gets skewered by the former's value.
    /// </para>
    /// </summary>
    internal static TimeSpan Delay = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Picks from disk the configIds that should be resumed automatically. A pure function (disk reads only), so unit tests call it directly.
    /// <para>
    /// There is exactly one criterion: this config left **at least one** journal volume, and the mark beside **every** volume reads
    /// <see cref="SuspendReason.ShuttingDown"/>. Nothing else is touched; here is why, case by case:
    /// </para>
    /// <list type="bullet">
    /// <item><b>ShuttingDown</b> — a planned process exit stopped it here, and it was flushed through
    /// <c>SettleStopAsync</c> (journal fsync first, mark afterwards). This is the only kind of interruption "caused by this process itself
    /// with the scene left intact", and therefore the only kind we may resume without asking.</item>
    /// <item><b>UserRequested</b> — a pause the operator pressed himself. Restarting it for him erases the intent behind that press.</item>
    /// <item><b>AutoSuspended</b> — the gate ran out of patience and stepped down. That transient error is most likely still there (the cable is still unplugged,
    /// the far end is still returning 503), so resuming right away would just hit the same wall and suspend again, burning a run for nothing.</item>
    /// <item><b>No mark</b> — indeterminate. A crash, a kill, a shutdown flush that timed out and left it halfway, the operator pressing Cancel
    /// (both kinds of cancel still flush, and both deliberately write no mark), or the mark write itself having failed — on disk they all look exactly the same.
    /// At least one of them (Cancel) is the user having said "stop" in so many words, so this whole class is left untouched.</item>
    /// </list>
    /// <para>
    /// Requiring **every** volume to be ShuttingDown, rather than letting the newest volume decide: marks are recorded per volume, and one config can perfectly well
    /// end up with several volumes whose values disagree (pause pressed → Run pressed again → the new run adopts the old volume → a shutdown stops the new run as
    /// ShuttingDown). And the resuming run adopts every still-valid volume when it opens its own, so if one volume should have been left alone,
    /// touching anything means touching that one too. A unanimous vote saves us from inventing an arbitration scheme for "which volume is newer and gets the say".
    /// </para>
    /// <para>However many volumes one backup left, it counts once: resuming is **a new run**, and it adopts all the volumes itself.</para>
    /// </summary>
    /// <param name="logger">
    /// Logs one line for each config that was **declined**. Optional; the pure-function unit tests do not pass it.
    /// <para>
    /// That line is not decorative: the deployment shape here is an appliance on a NAS, and the operator has neither a shell nor any tool for looking at mark
    /// files. Without it, the question "why was my backup not picked up after the restart" leaves him with **no lead whatsoever** —
    /// the switch in the UI is on, the log says not a word, and the real reason (some volume stopped for a different reason) exists only on disk.
    /// </para>
    /// </param>
    public static async Task<IReadOnlyList<int>> PickResumableAsync(
        BackupJournalStore journals,
        IReadOnlyList<(int ConfigId, int AccountId, string Container)> configs,
        CancellationToken ct,
        ILogger? logger = null)
    {
        var picked = new List<int>();
        foreach (var (configId, accountId, container) in configs)
        {
            // PeekAsync rather than ListAsync: all we want here is each volume's runId, whereas ListAsync deserializes
            // **every single record** of every volume. The volume that stopped halfway may well hold hundreds of thousands of them (this repo has measured
            // a scan of 200k entries), and this code runs on the startup path — parsing hundreds of MB of JSON to obtain a list of file names
            // is wildly out of proportion to what it buys. PeekAsync reads only the first line and counts the rest.
            var volumes = await journals.PeekAsync(accountId, container, ct);
            if (volumes.Count == 0)
                continue;

            // The first disqualifying volume is enough to decline the whole config, and it is exactly the one the log should name.
            var blocker = volumes.FirstOrDefault(x =>
                journals.ReadSuspendMark(accountId, container, x.RunId) != SuspendReason.ShuttingDown);
            if (blocker is null)
            {
                picked.Add(configId);
                continue;
            }

            var mark = journals.ReadSuspendMark(accountId, container, blocker.RunId);
            logger?.LogInformation(
                "Not resuming backup config {ConfigId} on startup: its journal {RunId} is marked "
                + "'{Mark}', and only runs left behind by a planned shutdown are resumed automatically. "
                + "Press Run to continue this backup.",
                configId, blocker.RunId, mark?.ToString() ?? "none");
        }
        return picked;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Delay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            using var scope = scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IGlobalSettingsService>();
            if (!(await settings.GetAsync(stoppingToken)).AutoResumeInterruptedRuns)
                return;

            // Every config is a candidate: BackupConfig has no such thing as enabled/disabled — a config existing means it is meant to be backed up.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var configs = await db.BackupConfigs.AsNoTracking()
                .Select(c => new { c.Id, c.AccountId, c.ContainerName })
                .ToListAsync(stoppingToken);

            var resumable = await PickResumableAsync(
                journals,
                [.. configs.Select(c => (c.Id, c.AccountId, c.ContainerName))],
                stoppingToken,
                logger);

            // Start them one at a time, **and wait for the previous one to finish before starting the next**: the output lock is global, so rushing in together only queues up,
            // with no way to see who is waiting on whom (concurrent backups are in fact slower, which this repo has measured).
            //
            // A bare foreach is not enough: StartAsync throws the work into Task.Run and returns, so calling it several times in a row
            // means several runs going at once. What actually serializes them is the await Completion below.
            foreach (var configId in resumable)
            {
                if (stoppingToken.IsCancellationRequested)
                    return;
                var state = await runner.StartAsync(configId);

                // StartAsync has two short circuits that **return a terminal state on the spot** (config not found, busy lock held by someone else),
                // and in those cases no run was started at all, so state.RunId names a run that does not exist.
                // Neither should be reported as "already resumed", or the log would lie to you with a RunId no run answers to.
                if (state.Status != RunStatus.Running)
                {
                    logger.LogWarning(
                        "Could not auto-resume interrupted backup {ConfigId}: {Error}",
                        configId, state.Error ?? state.Status.ToString());
                    continue;
                }

                logger.LogInformation(
                    "Auto-resuming interrupted backup {ConfigId} (run {RunId})", configId, state.RunId);

                // Wait for it to reach a terminal state. At shutdown this wait does not hold the host up: GracefulSuspendService is registered after this service
                // and therefore stops before it, and it suspends and flushes this run, which ends the wait here.
                //
                // This wait **deliberately has no cap**: serialization is mandatory (see above), and a cap would simply let concurrency in once it expires.
                // But the cost has to be stated: a run parked on PauseGate waiting for a transient error to heal is by design still Running (it keeps its seat;
                // reporting a terminal state would let the scheduler start another run and displace it), so its Completion can stay unsettled for a very long time —
                // the gate patiently waits up to 10 minutes before stepping down, and every resumable config behind it queues the whole while.
                // This is not a deadlock (the gate either steps down or succeeds, and both roads lead to a terminal state); it is a queue that can be long.
                await state.Completion.Task.WaitAsync(stoppingToken);

                // A failed auto-resume has to be one notch louder than a successful one: it is "a run the system decided to start by itself" with nobody watching the outcome,
                // and logging it as Information would bury it in the normal stream.
                if (state.Status == RunStatus.Failed)
                    logger.LogWarning(
                        "Auto-resumed backup {ConfigId} failed: {Error}", configId, state.Error);
                else
                    logger.LogInformation(
                        "Auto-resumed backup {ConfigId} ended as {Status}", configId, state.Status);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // A failed auto-resume must not stop the process from coming up: the user can still go and press the button himself.
            logger.LogError(ex, "Auto-resume of interrupted backups failed");
        }
    }
}
