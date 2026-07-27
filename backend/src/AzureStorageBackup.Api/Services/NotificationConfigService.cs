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
    // OrderBy 不是多余的：单例表也得给 First 一个确定的顺序，否则 EF 每次调用都记一条
    // 10103 警告（"First without OrderBy may lead to unpredictable results"），在 docker logs
    // 里刷屏——每次发通知都会读一遍这张表。与 GlobalSettingsService 同一处理。
    public async Task<NotificationConfig> GetAsync(CancellationToken ct = default) =>
        await db.NotificationConfigs.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct) ?? new NotificationConfig();

    public async Task<NotificationConfig> UpsertAsync(NotificationConfig config, CancellationToken ct = default)
    {
        var existing = await db.NotificationConfigs.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
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
