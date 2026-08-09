namespace AzureStorageBackup.Api.Services;

/// <summary>
/// On a clean process exit (<c>docker stop</c>, upgrade restart), suspend the running backups and flush them to disk.
/// <para>
/// Uses <see cref="IHostedService.StopAsync"/> instead of the <c>ApplicationStopping</c> callback:
/// the former is awaited, so the host waits for it to return before tearing more services down; the latter is a
/// synchronous event and cannot wait for an async flush.
/// </para>
/// <para>
/// Registration order matters: the host stops services in **reverse** registration order, so this one has to be
/// registered **after** <c>SchedulerService</c> in order to stop before the scheduler does — otherwise the scheduler
/// could start another run halfway through the suspend, and there would be nobody left to suspend that one.
/// </para>
/// </summary>
public sealed class GracefulSuspendService(BackupRunner runner, ILogger<GracefulSuspendService> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken ct)
    {
        try
        {
            // This number counts runs that really settled as Suspended and left a mark on disk, not stop requests
            // sent — ones that timed out, and ones a concurrently arriving Stop now beat into Canceled, don't count.
            // What the log claims has to match what is on disk, otherwise going looking for a mark on the strength
            // of this log line later turns up nothing.
            var stopped = await runner.SuspendAllAsync(SuspendReason.ShuttingDown, ct);
            if (stopped > 0)
                logger.LogInformation("Suspended {Count} running backup(s) for shutdown", stopped);
        }
        catch (Exception ex)
        {
            // Throwing on the shutdown path just becomes a host error nobody ever sees, and can bury other
            // services' cleanup.
            logger.LogError(ex, "Failed to suspend running backups during shutdown");
        }
    }
}
