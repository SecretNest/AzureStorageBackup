using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 执行一个计划任务（M6）：解析目标（单备份或组成员），组内**依次**执行；
/// 按类型分发到 备份/检查/清理。每个引擎调用在独立 scope 中解析 scoped 服务。
/// </summary>
public sealed class TaskDispatcher(IServiceScopeFactory scopes, ILogger<TaskDispatcher> logger, BackupBusyTracker busy)
{
    public async Task DispatchAsync(ScheduledTask task, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;

        foreach (var (accountId, container) in await ResolveTargetsAsync(sp, task, ct))
        {
            ct.ThrowIfCancellationRequested();

            // 目标忙碌（正在备份/检查/还原/清理）→ 记报警并跳过，不打断在执行的任务（用户要求）。
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

        var password = BackupRequestMapper.Password(config);
        try
        {
            switch (task.TaskType)
            {
                case ScheduledTaskType.Backup:
                    var settings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
                    await sp.GetRequiredService<BackupOrchestrator>()
                        .RunAsync(BackupRequestMapper.From(config, account, settings), null, ct);
                    break;

                case ScheduledTaskType.Check:
                    var checkSettings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
                    var options = new CheckOptions
                    {
                        Cloud = task.CheckCloudLevel,
                        Local = task.CheckLocalLevel,
                        // 显式转为 AccessTier?：见 BackupConfigEndpoints.cs /check 端点同处注释（真实生产 bug 修复）。
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
                    var cleanupSettings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
                    await sp.GetRequiredService<RetentionCleaner>()
                        .CleanupAsync(account, container, password, BackupRequestMapper.CleanupOf(config, cleanupSettings), ct);
                    break;
            }
        }
        catch (Exception ex)
        {
            // 落库 Error（决策 2），best-effort：写状态失败不应掩盖原始异常。
            // 外层 DispatchAsync 的 catch 负责记日志，这里用 `throw;` 保留原始异常与调用栈。
            await configs.WriteStatusAsync(config.Id, ex.Message, logger, ct);
            throw;
        }

        // 成功落库 Normal（决策 2），best-effort：写状态失败不应把已成功的运行误判为失败。
        await configs.WriteStatusAsync(config.Id, error: null, logger, ct);
    }
}
