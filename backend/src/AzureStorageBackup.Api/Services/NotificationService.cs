using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>The event notification entry point. The engine calls it at each trigger; unconfigured, disabled or unsubscribed events are skipped silently.</summary>
public interface INotifier
{
    Task NotifyAsync(NotificationEvents evt, string title, string body, CancellationToken ct = default);
}

public sealed class NotificationService(
    INotificationConfigService configs, INotificationSender sender, ILogger<NotificationService> logger,
    IOperationLog? opLog = null) : INotifier
{
    public async Task NotifyAsync(NotificationEvents evt, string title, string body, CancellationToken ct = default)
    {
        try
        {
            var cfg = await configs.GetAsync(ct);
            if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Url) || (cfg.Events & evt) == 0)
                return;

            await sender.SendAsync(cfg, title, body, ct);
        }
        catch (Exception ex)
        {
            // A failed notification must not affect the backup or restore
            logger.LogWarning(ex, "Notification for {Event} failed", evt);
            // ...but it must not be invisible either. This used to go only to the container log, which on a NAS
            // deployment nobody has a shell for, so a notification the receiver rejected looked identical to one
            // never sent: the reported symptom was "the success notification never arrives", while the request had
            // in fact been made and refused every time. The operation log is the one place the operator can read.
            // CancellationToken.None because a run finishing is exactly when its own token may already be gone.
            if (opLog is not null)
            {
                try
                {
                    await opLog.AppendAsync(
                        OperationLogLevel.Warning, "notification",
                        $"Notification for {evt} was not delivered: {ex.Message}", CancellationToken.None);
                }
                catch (Exception logEx)
                {
                    // Reporting the failure must not become a second failure.
                    logger.LogWarning(logEx, "Recording the failed {Event} notification also failed", evt);
                }
            }
        }
    }
}
