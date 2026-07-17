using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>全局设置（单例）的读取与更新。</summary>
public interface IGlobalSettingsService
{
    Task<GlobalSettings> GetAsync(CancellationToken ct = default);
    Task<GlobalSettings> UpsertAsync(GlobalSettings settings, CancellationToken ct = default);
}

public class GlobalSettingsService(AppDbContext db) : IGlobalSettingsService
{
    public async Task<GlobalSettings> GetAsync(CancellationToken ct = default) =>
        await db.GlobalSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new GlobalSettings();

    public async Task<GlobalSettings> UpsertAsync(GlobalSettings s, CancellationToken ct = default)
    {
        var existing = await db.GlobalSettings.FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            db.GlobalSettings.Add(s);
            await db.SaveChangesAsync(ct);
            return s;
        }

        existing.DefaultIndexTier = s.DefaultIndexTier;
        existing.DefaultDataTier = s.DefaultDataTier;
        existing.DefaultMaxVersions = s.DefaultMaxVersions;
        existing.DefaultMaxAgeDays = s.DefaultMaxAgeDays;
        existing.DefaultRetentionMode = s.DefaultRetentionMode;
        existing.DefaultSingleFileThresholdBytes = s.DefaultSingleFileThresholdBytes;
        existing.DefaultGroupCapBytes = s.DefaultGroupCapBytes;
        existing.DefaultVolumeBytes = s.DefaultVolumeBytes;
        existing.DefaultIncludeSymlinks = s.DefaultIncludeSymlinks;
        existing.DefaultIgnoreRules = s.DefaultIgnoreRules;
        existing.DefaultDontCompressRules = s.DefaultDontCompressRules;
        existing.DefaultDontGroupRules = s.DefaultDontGroupRules;
        existing.UploadConcurrency = s.UploadConcurrency;
        existing.LogMaxEntries = s.LogMaxEntries;
        existing.LogMaxAgeDays = s.LogMaxAgeDays;
        existing.RetryBackoffSeconds = s.RetryBackoffSeconds;
        existing.RetryMaxTotalMinutes = s.RetryMaxTotalMinutes;
        existing.DeadWeightThresholdPercent = s.DeadWeightThresholdPercent;
        await db.SaveChangesAsync(ct);
        return existing;
    }
}
