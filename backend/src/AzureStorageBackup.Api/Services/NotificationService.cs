using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>事件通知入口。引擎在事件触发点调用；未配置/未启用/未订阅该事件则静默跳过。</summary>
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
            // 通知失败不得影响备份/还原
            logger.LogWarning(ex, "Notification for {Event} failed", evt);
        }
    }
}
