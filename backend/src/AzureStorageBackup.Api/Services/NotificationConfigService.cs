using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>Reads and updates the global notification configuration (a singleton).</summary>
public interface INotificationConfigService
{
    Task<NotificationConfig> GetAsync(CancellationToken ct = default);
    Task<NotificationConfig> UpsertAsync(NotificationConfig config, CancellationToken ct = default);
}

public class NotificationConfigService(AppDbContext db) : INotificationConfigService
{
    // The OrderBy is not redundant: even a singleton table has to give First a determinate order, or EF
    // logs a 10103 warning ("First without OrderBy may lead to unpredictable results") on every call and
    // floods docker logs — this table is read every time a notification is sent. Handled the same way in
    // GlobalSettingsService.
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
