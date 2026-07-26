using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 执行一个计划任务（M6）：解析目标（单备份或组成员），组内**依次**执行；
/// 按类型分发到 备份/检查/清理。每个引擎调用在独立 scope 中解析 scoped 服务。
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

        if (!boundary.IsInside(config.LocalRoot))
        {
            // 有意比上方忙碌跳过（LogWarning）高一级：忙碌是瞬态，下一轮调度大概率自愈；
            // 根越界是一个持续到操作员改配置为止的站定问题，每一轮调度都会再跳过一次，
            // 值得用 Error 让它在容器日志聚合/告警里更显眼，而不是被当成普通噪音过滤掉。
            logger.LogError(
                "Scheduled task skipped: local root '{Root}' is outside the configured Backup__Root.",
                config.LocalRoot);
            // 与忙碌跳过分支同形（上方 TryAcquire 失败处）：把「这个计划任务没跑」写进操作员能看见的
            // 操作日志，而不是只留一条容器日志里的 LogError——单用户无人值守部署下没人会去翻它。
            // 配置本身按设计保留、不删（越界即拒跑，直到操作员修正 LocalRoot 或 Backup__Root），
            // 消息里带上违规的本地根与当前配置的根，让操作员一眼知道改哪个。
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
                    // 与界面按钮走同一条执行体，这样定时备份也有进度可查。
                    // 用 RunTrackedAsync 而非 Start：DispatchAsync 已为该目标持有忙碌锁，
                    // Start 会再抢一次并必然失败，把每一次定时备份都变成「busy」。
                    var backupState = await sp.GetRequiredService<BackupRunner>()
                        .RunTrackedAsync(config.Id, ct);
                    // 执行体吞掉异常、只把失败写进 state，所以这里必须显式抛出，
                    // 否则下方 catch 不会触发，失败会被 WriteStatusAsync(null) 记成成功。
                    // 把原始异常挂作 InnerException（Fix 4）：外层 DispatchAsync 的
                    // LogError(ex, …) 原本收到的是编排器的真实异常——Azure 失败时带着状态码、
                    // 请求 id 和有用的堆栈；只传消息的话，容器日志里就只剩一个从这里 throw
                    // 开始的空壳堆栈，对无人值守部署这是最后一道诊断线索，不该丢。
                    if (backupState.Status == RunStatus.Failed)
                        throw new InvalidOperationException(backupState.Error ?? "Backup failed.", backupState.Failure);
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
