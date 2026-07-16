namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 常驻调度器（M6、PRD 2.2/2.3）：每分钟检查启用的计划任务，到期即触发（fire-and-forget，
/// 组内由 TaskDispatcher 依次执行）。触发前先记录 LastRunAt，避免同一时刻重复触发或重启后重放。
/// MVP 用 UTC 求值 cron。
/// </summary>
public sealed class SchedulerService(
    IServiceScopeFactory scopes, TaskDispatcher dispatcher, ILogger<SchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduler tick failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<IScheduledTaskService>();

        var now = DateTimeOffset.UtcNow;
        foreach (var task in await tasks.ListAsync(ct))
        {
            if (!SchedulerPlanner.IsDue(task, now, TimeZoneInfo.Utc))
                continue;

            await tasks.SetLastRunAsync(task.Id, now, ct); // 先记录，防止重复触发
            _ = dispatcher.DispatchAsync(task, ct);         // 后台执行；不阻塞下一个任务
        }
    }
}
