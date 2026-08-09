using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>The event notification entry point. The engine calls it at each trigger; unconfigured, disabled or unsubscribed events are skipped silently.</summary>
public interface INotifier
{
    Task NotifyAsync(NotificationEvents evt, string title, string body, CancellationToken ct = default);
}

public sealed class NotificationService(
    INotificationConfigService configs, INotificationSender sender, ILogger<NotificationService> logger) : INotifier
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
        }
    }
}
