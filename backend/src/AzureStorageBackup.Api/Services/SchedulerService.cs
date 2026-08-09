namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The resident scheduler (M6, PRD 2.2/2.3): checks enabled scheduled tasks every minute and fires whatever is due (fire-and-forget;
/// within a group TaskDispatcher runs them one after another). LastRunAt is recorded before firing, so the same moment cannot fire twice and a restart cannot replay it.
/// The cron time zone comes from Scheduler:TimeZone (an IANA id); missing or invalid falls back to UTC.
/// </summary>
public sealed class SchedulerService(
    IServiceScopeFactory scopes, TaskDispatcher dispatcher, IConfiguration config, ILogger<SchedulerService> logger,
    VerboseFileLog verboseLog, IKeyringHealth keyring)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly TimeZoneInfo _tz = ResolveTimeZone(config["Scheduler:TimeZone"]);

    /// <summary>Resolves an IANA/system time zone id; empty or invalid falls back to UTC.</summary>
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

    /// <summary>A single tick (internal so tests can drive it directly and cover the keyring skip branch).</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var now = DateTimeOffset.UtcNow;

        // Ephemeral log retention cleanup (PRD 3.6), with the day count taken from the global settings. It has nothing to do with the
        // keyring status: cleanup has to keep running even when the keyring is lost — a lost keyring can last a long time, and in that
        // state both the harm of log bloat and the value of logs for troubleshooting are higher, so this sits ahead of the keyring gate (unaffected by the skip).
        var settings = await scope.ServiceProvider.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
        await scope.ServiceProvider.GetRequiredService<IOperationLog>().TrimAsync(
            settings.LogEphemeralMaxAgeDays, now, ct);
        verboseLog.Trim(settings.LogEphemeralMaxAgeDays, now); // verbose text logs use the same window, deleting old files by date

        if (keyring.Status == KeyringStatus.Lost)
        {
            // One summary line per tick, not one per task — otherwise the log gets flooded (design §3.3)
            logger.LogWarning("Keyring lost; skipping all scheduled tasks until credentials are re-entered.");
            return;
        }

        var tasks = scope.ServiceProvider.GetRequiredService<IScheduledTaskService>();

        foreach (var task in await tasks.ListAsync(ct))
        {
            if (!SchedulerPlanner.IsDue(task, now, _tz))
                continue;

            await tasks.SetLastRunAsync(task.Id, now, ct); // record it first, to prevent a duplicate fire
            _ = dispatcher.DispatchAsync(task, ct);         // runs in the background; does not block the next task
        }
    }
}
