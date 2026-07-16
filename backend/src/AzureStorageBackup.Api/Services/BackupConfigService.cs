using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

public class BackupConfigService(AppDbContext db) : IBackupConfigService
{
    public async Task<IReadOnlyList<BackupConfig>> ListAsync(CancellationToken ct = default) =>
        await db.BackupConfigs.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

    public async Task<BackupConfig?> GetAsync(int id, CancellationToken ct = default) =>
        await db.BackupConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<BackupConfig> CreateAsync(BackupConfig config, CancellationToken ct = default)
    {
        if (config.CreatedAt == default)
            config.CreatedAt = DateTimeOffset.UtcNow;

        db.BackupConfigs.Add(config);
        await db.SaveChangesAsync(ct);
        return config;
    }

    public async Task<BackupConfig?> UpdateAsync(int id, BackupConfig update, CancellationToken ct = default)
    {
        var existing = await db.BackupConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (existing is null)
            return null;

        existing.AccountId = update.AccountId;
        existing.ContainerName = update.ContainerName;
        existing.Name = update.Name;
        existing.Description = update.Description;
        existing.LocalRoot = update.LocalRoot;
        existing.Password = update.Password;
        existing.IndexTier = update.IndexTier;
        existing.DataTier = update.DataTier;
        existing.IgnoreRules = update.IgnoreRules;
        existing.DontCompressRules = update.DontCompressRules;
        existing.DontGroupRules = update.DontGroupRules;
        existing.IncludeSymlinks = update.IncludeSymlinks;
        existing.MaxVersions = update.MaxVersions;
        existing.MaxAgeDays = update.MaxAgeDays;
        existing.RetentionMode = update.RetentionMode;
        existing.SingleFileThresholdBytes = update.SingleFileThresholdBytes;
        existing.GroupCapBytes = update.GroupCapBytes;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        var existing = await db.BackupConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (existing is null)
            return false;

        db.BackupConfigs.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
