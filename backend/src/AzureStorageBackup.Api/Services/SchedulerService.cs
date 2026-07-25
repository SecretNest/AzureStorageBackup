namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 常驻调度器（M6、PRD 2.2/2.3）：每分钟检查启用的计划任务，到期即触发（fire-and-forget，
/// 组内由 TaskDispatcher 依次执行）。触发前先记录 LastRunAt，避免同一时刻重复触发或重启后重放。
/// cron 时区由 Scheduler:TimeZone（IANA id）配置，缺省/非法则 UTC。
/// </summary>
public sealed class SchedulerService(
    IServiceScopeFactory scopes, TaskDispatcher dispatcher, IConfiguration config, ILogger<SchedulerService> logger,
    VerboseFileLog verboseLog, IKeyringHealth keyring)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly TimeZoneInfo _tz = ResolveTimeZone(config["Scheduler:TimeZone"]);

    /// <summary>解析 IANA/系统时区 id；空或非法回退 UTC。</summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }

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
        if (keyring.Status == KeyringStatus.Lost)
        {
            // 每 tick 只记一条汇总，不逐任务记——否则日志会被刷爆（设计 §3.3）
            logger.LogWarning("Keyring lost; skipping all scheduled tasks until credentials are re-entered.");
            return;
        }

        using var scope = scopes.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<IScheduledTaskService>();

        var now = DateTimeOffset.UtcNow;

        // 短存日志保留清理（PRD 3.6），天数取自全局设置
        var settings = await scope.ServiceProvider.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
        await scope.ServiceProvider.GetRequiredService<IOperationLog>().TrimAsync(
            settings.LogEphemeralMaxAgeDays, now, ct);
        verboseLog.Trim(settings.LogEphemeralMaxAgeDays, now); // verbose 文本日志同窗口按日期删旧文件

        foreach (var task in await tasks.ListAsync(ct))
        {
            if (!SchedulerPlanner.IsDue(task, now, _tz))
                continue;

            await tasks.SetLastRunAsync(task.Id, now, ct); // 先记录，防止重复触发
            _ = dispatcher.DispatchAsync(task, ct);         // 后台执行；不阻塞下一个任务
        }
    }
}
