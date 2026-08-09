using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Run one scheduled task (M6): resolve the targets (a single backup, or the members of a group), executing **sequentially** within a group;
/// dispatch by type to backup/check/cleanup. Each engine call resolves its scoped services inside its own scope.
/// </summary>
public sealed class TaskDispatcher(
    IServiceScopeFactory scopes, ILogger<TaskDispatcher> logger, BackupBusyTracker busy, ISecretReader secrets, PathBoundary boundary)
{
    public async Task DispatchAsync(ScheduledTask task, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;

        foreach (var (accountId, container) in await ResolveTargetsAsync(sp, task, ct))
        {
            ct.ThrowIfCancellationRequested();

            // Target busy (backing up / checking / restoring / cleaning up) → log a warning and skip, never interrupt the operation already running (user requirement).
            var activity = task.TaskType switch
            {
                ScheduledTaskType.Backup => "BackingUp",
                ScheduledTaskType.Cleanup => "CleaningUp",
                _ => "Checking",
            };
            if (!busy.TryAcquire(accountId, container, activity))
            {
                logger.LogWarning("Backup {Account}/{Container} is busy; skipping scheduled {Type}", accountId, container, task.TaskType);
                await sp.GetRequiredService<IOperationLog>().AppendAsync(
                    OperationLogLevel.Warning, $"schedule:{accountId}/{container}",
                    $"Skipped scheduled {task.TaskType}: backup is busy with another operation", ct);
                continue;
            }
            try
            {
                await ExecuteAsync(sp, task, accountId, container, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled {Type} for {Account}/{Container} failed", task.TaskType, accountId, container);
            }
            finally
            {
                busy.Release(accountId, container);
            }
        }
    }

    private static async Task<IReadOnlyList<(int AccountId, string Container)>> ResolveTargetsAsync(
        IServiceProvider sp, ScheduledTask task, CancellationToken ct)
    {
        if (task.TargetKind == TaskTargetKind.Group && task.GroupId is { } gid)
        {
            var group = await sp.GetRequiredService<IGroupService>().GetAsync(gid, ct);
            return group?.Members.Select(m => (m.AccountId, m.ContainerName)).ToList() ?? [];
        }

        if (task.AccountId is { } aid && !string.IsNullOrEmpty(task.ContainerName))
            return [(aid, task.ContainerName)];

        return [];
    }

    private async Task ExecuteAsync(
        IServiceProvider sp, ScheduledTask task, int accountId, string container, CancellationToken ct)
    {
        var configs = sp.GetRequiredService<IBackupConfigService>();
        var config = await configs.FindAsync(accountId, container, ct);
        var account = await sp.GetRequiredService<IAccountService>().GetAsync(accountId, ct);
        if (config is null || account is null)
        {
            logger.LogWarning("No backup config for {Account}/{Container}; skipping scheduled {Type}", accountId, container, task.TaskType);
            return;
        }

        if (!boundary.IsInside(config.LocalRoot))
        {
            // Deliberately one level above the busy skip above (LogWarning): busy is transient and the next scheduling round
            // will most likely heal itself; a root out of bounds is a standing problem that lasts until the operator changes
            // the config, and every scheduling round skips again, so it deserves an Error to make it stand out in container
            // log aggregation/alerting instead of being filtered away as ordinary noise.
            logger.LogError(
                "Scheduled task skipped: local root '{Root}' is outside the configured Backup__Root.",
                config.LocalRoot);
            // Same shape as the busy-skip branch (the TryAcquire failure above): write "this scheduled task did not run" into
            // the operation log the operator can actually see, rather than leaving only a LogError in the container log — in a
            // single-user unattended deployment nobody goes digging through that.
            // The config itself is kept by design, not deleted (out of bounds means refuse to run, until the operator fixes
            // LocalRoot or Backup__Root), and the message carries both the offending local root and the currently configured root so the operator sees at a glance which one to change.
            await sp.GetRequiredService<IOperationLog>().AppendAsync(
                OperationLogLevel.Error, $"schedule:{accountId}/{container}",
                $"Skipped scheduled {task.TaskType}: local root '{config.LocalRoot}' is outside the configured root '{boundary.ConfiguredRoot}'",
                ct);
            return;
        }

        var password = secrets.RevealBackupPassword(config);
        try
        {
            switch (task.TaskType)
            {
                case ScheduledTaskType.Backup:
                    // Same execution body as the UI button, so a scheduled backup has progress to inspect too.
                    // RunTrackedAsync rather than Start: DispatchAsync already holds the busy lock for this target, and
                    // Start would grab it a second time and inevitably fail, turning every scheduled backup into a "busy".
                    var backupState = await sp.GetRequiredService<BackupRunner>()
                        .RunTrackedAsync(config.Id, ct);
                    // The execution body swallows exceptions and only writes the failure into state, so we have to throw
                    // explicitly here, or the catch below never fires and WriteStatusAsync(null) records the failure as success.
                    // Attach the original exception as InnerException (Fix 4): the outer DispatchAsync's
                    // LogError(ex, …) used to receive the orchestrator's real exception — on an Azure failure that carries the
                    // status code, request id and a useful stack; passing only the message would leave the container log with
                    // nothing but a hollow stack starting at this throw, and for an unattended deployment that is the last diagnostic clue, not something to throw away.
                    if (backupState.Status == RunStatus.Failed)
                        throw new InvalidOperationException(backupState.Error ?? "Backup failed.", backupState.Failure);
                    // The user stopped this scheduled backup from the UI (or the process is shutting down): that is neither a
                    // failure nor a successful backup. Return outright and skip the "persist Normal on success" line below —
                    // otherwise a backup that was called off gets recorded as having run fine, and the genuine earlier Error status gets wiped along with it.
                    if (backupState.Status == RunStatus.Canceled)
                        return;
                    // Suspension is not a failure: throwing would log a red error against this scheduled task, while the state
                    // is in fact safely preserved and the next round picks it back up. Treated the same as Canceled: end quietly.
                    if (backupState.Status == RunStatus.Suspended)
                        return;
                    break;

                case ScheduledTaskType.Check:
                    var checkSettings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
                    var options = new CheckOptions
                    {
                        Cloud = task.CheckCloudLevel,
                        Local = task.CheckLocalLevel,
                        // Explicit cast to AccessTier?: see the same comment at the /check endpoint in BackupConfigEndpoints.cs (a real production bug fix).
                        RehydrateTier = task.CheckRehydrateTier is { } t ? (AccessTier?)BackupRequestMapper.MapTier(t) : null,
                    };
                    var result = await sp.GetRequiredService<BackupChecker>()
                        .CheckAsync(account, container, password, null, options, config.LocalRoot, ct,
                            downloadConcurrency: checkSettings.DownloadConcurrency > 0 ? checkSettings.DownloadConcurrency : 5);
                    if (!result.Ok)
                        logger.LogWarning("Check for {Account}/{Container} found {Problems} problem(s)",
                            accountId, container, result.MissingRefs.Count);
                    break;

                case ScheduledTaskType.Cleanup:
                {
                    var cleanupSettings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
                    // A standalone cleanup takes a seat of its own: the dead-weight compaction it does along the way writes to
                    // the same temp disk, so the quota has to be shared evenly with any running backup (the backup-teardown path passes the backup's own seat).
                    using var cleanupLease = sp.GetRequiredService<StagingArea>().AcquireLease();
                    // A standalone cleanup always sweeps for orphans — that is what it is for. If the blocks left behind by a
                    // cancel/crash are not reused by the next backup, this is the only path that will ever collect them.
                    var cleanup = await sp.GetRequiredService<RetentionCleaner>().CleanupAsync(
                        account, container, password,
                        BackupRequestMapper.CleanupOf(config, cleanupSettings), ct, cleanupLease,
                        sweepOrphans: true);
                    // The cleanup at the end of a backup writes what it deleted into the success summary; there is no reason for
                    // this standalone one to be quieter — in an unattended deployment the operation log is the only place to go
                    // back and check "how much space did the retention policy actually free up".
                    // Durable: this records data being deleted, which is audit material and should not disappear along with the ephemeral logs after 14 days.
                    // Not a word when nothing was cleaned up: a nightly "retired 0 version(s)" would turn this signal into
                    // background noise, and "the task really did run" can be checked in the task run records instead.
                    if (!cleanup.IsEmpty)
                        await sp.GetRequiredService<IOperationLog>().AppendAsync(
                            OperationLogLevel.Info, $"schedule:{accountId}/{container}",
                            BackupSummary.FormatRetention(cleanup), ct, durable: true);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Persist Error (decision 2), best-effort: failing to write the status must not mask the original exception.
            // The outer DispatchAsync catch does the logging; `throw;` here preserves the original exception and call stack.
            await configs.WriteStatusAsync(config.Id, ex.Message, logger, ct);
            throw;
        }

        // Persist Normal on success (decision 2), best-effort: failing to write the status must not misjudge an already-successful run as failed.
        await configs.WriteStatusAsync(config.Id, error: null, logger, ct);
    }
}
