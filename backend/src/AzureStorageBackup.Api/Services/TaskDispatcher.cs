using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 执行一个计划任务（M6）：解析目标（单备份或组成员），组内**依次**执行；
/// 按类型分发到 备份/检查/清理。每个引擎调用在独立 scope 中解析 scoped 服务。
/// </summary>
public sealed class TaskDispatcher(IServiceScopeFactory scopes, ILogger<TaskDispatcher> logger)
{
    public async Task DispatchAsync(ScheduledTask task, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;

        foreach (var (accountId, container) in await ResolveTargetsAsync(sp, task, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ExecuteAsync(sp, task.TaskType, accountId, container, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled {Type} for {Account}/{Container} failed", task.TaskType, accountId, container);
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
        IServiceProvider sp, ScheduledTaskType type, int accountId, string container, CancellationToken ct)
    {
        var config = await sp.GetRequiredService<IBackupConfigService>().FindAsync(accountId, container, ct);
        var account = await sp.GetRequiredService<IAccountService>().GetAsync(accountId, ct);
        if (config is null || account is null)
        {
            logger.LogWarning("No backup config for {Account}/{Container}; skipping scheduled {Type}", accountId, container, type);
            return;
        }

        var password = BackupRequestMapper.Password(config);
        switch (type)
        {
            case ScheduledTaskType.Backup:
                var settings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
                await sp.GetRequiredService<BackupOrchestrator>()
                    .RunAsync(BackupRequestMapper.From(config, account, settings), null, ct);
                break;

            case ScheduledTaskType.Check:
                var result = await sp.GetRequiredService<BackupChecker>()
                    .CheckAsync(account, container, password, null, deep: false, ct);
                if (!result.Ok)
                    logger.LogWarning("Check for {Account}/{Container} found {Missing} missing object(s)",
                        accountId, container, result.MissingRefs.Count);
                break;

            case ScheduledTaskType.Cleanup:
                var cleanupSettings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
                await sp.GetRequiredService<RetentionCleaner>()
                    .CleanupAsync(account, container, password, BackupRequestMapper.CleanupOf(config, cleanupSettings), ct);
                break;
        }
    }
}
