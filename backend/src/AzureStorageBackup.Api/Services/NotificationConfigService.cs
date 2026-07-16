using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>全局通知配置（单例）的读取与更新。</summary>
public interface INotificationConfigService
{
    Task<NotificationConfig> GetAsync(CancellationToken ct = default);
    Task<NotificationConfig> UpsertAsync(NotificationConfig config, CancellationToken ct = default);
}

public class NotificationConfigService(AppDbContext db) : INotificationConfigService
{
    public async Task<NotificationConfig> GetAsync(CancellationToken ct = default) =>
        await db.NotificationConfigs.AsNoTracking().FirstOrDefaultAsync(ct) ?? new NotificationConfig();

    public async Task<NotificationConfig> UpsertAsync(NotificationConfig config, CancellationToken ct = default)
    {
        var existing = await db.NotificationConfigs.FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            db.NotificationConfigs.Add(config);
            await db.SaveChangesAsync(ct);
            return config;
        }

        existing.Enabled = config.Enabled;
        existing.Url = config.Url;
        existing.Method = config.Method;
        existing.BodyTemplate = config.BodyTemplate;
        existing.ContentType = config.ContentType;
        existing.Events = config.Events;
        existing.ProxyUrl = config.ProxyUrl;
        await db.SaveChangesAsync(ct);
        return existing;
    }
}
