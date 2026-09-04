using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>Reading and updating the global settings (a singleton).</summary>
public interface IGlobalSettingsService
{
    Task<GlobalSettings> GetAsync(CancellationToken ct = default);
    Task<GlobalSettings> UpsertAsync(GlobalSettings settings, CancellationToken ct = default);
}

public class GlobalSettingsService(AppDbContext db) : IGlobalSettingsService
{
    public async Task<GlobalSettings> GetAsync(CancellationToken ct = default)
    {
        // The OrderBy is not redundant: even a singleton table has to hand First a definite order, otherwise EF logs a
        // 10103 warning ("First without OrderBy may lead to unpredictable results") on every single call.
        // This method runs on very nearly every request, and it is exactly what floods docker logs.
        var s = await db.GlobalSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is null)
            return new GlobalSettings();

        // Columns added by a migration (StagedLimitBytes/ProcessingMaxAttempts) default to 0 in SQL for pre-existing rows;
        // normalize them to the model defaults so GET /settings agrees with engine behavior (the >0 ?: fallback) and the UI never shows 0.
        var defaults = new GlobalSettings();
        if (s.StagedLimitBytes <= 0)
            s.StagedLimitBytes = defaults.StagedLimitBytes;
        if (s.ProcessingMaxAttempts <= 0)
            s.ProcessingMaxAttempts = defaults.ProcessingMaxAttempts;
        return s;
    }

    public async Task<GlobalSettings> UpsertAsync(GlobalSettings s, CancellationToken ct = default)
    {
        var existing = await db.GlobalSettings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
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
        existing.RepackDownloadHot = s.RepackDownloadHot;
        existing.RepackDownloadCool = s.RepackDownloadCool;
        existing.RepackDownloadCold = s.RepackDownloadCold;
        existing.RepackDownloadArchive = s.RepackDownloadArchive;
        existing.DefaultIncludeSymlinks = s.DefaultIncludeSymlinks;
        existing.DefaultIgnoreRules = s.DefaultIgnoreRules;
        existing.DefaultDontCompressRules = s.DefaultDontCompressRules;
        existing.DefaultDontGroupRules = s.DefaultDontGroupRules;
        existing.DefaultCrossDirGroupRules = s.DefaultCrossDirGroupRules;
        existing.DefaultIgnoreRulesCaseInsensitive = s.DefaultIgnoreRulesCaseInsensitive;
        existing.DefaultDontCompressRulesCaseInsensitive = s.DefaultDontCompressRulesCaseInsensitive;
        existing.DefaultDontGroupRulesCaseInsensitive = s.DefaultDontGroupRulesCaseInsensitive;
        existing.DefaultCrossDirGroupRulesCaseInsensitive = s.DefaultCrossDirGroupRulesCaseInsensitive;
        existing.UploadConcurrency = s.UploadConcurrency;
        existing.DownloadConcurrency = s.DownloadConcurrency;
        existing.CheckHeadConcurrency = s.CheckHeadConcurrency;
        existing.LogEphemeralMaxAgeDays = s.LogEphemeralMaxAgeDays;
        existing.DefaultVerboseLogging = s.DefaultVerboseLogging;
        existing.RetryBackoffSeconds = s.RetryBackoffSeconds;
        existing.RetryMaxTotalMinutes = s.RetryMaxTotalMinutes;
        existing.DeadWeightThresholdPercent = s.DeadWeightThresholdPercent;
        existing.StagedLimitBytes = s.StagedLimitBytes;
        existing.ProcessingMaxAttempts = s.ProcessingMaxAttempts;
        existing.OverlapDiffAndUpload = s.OverlapDiffAndUpload;
        existing.AutoResumeInterruptedRuns = s.AutoResumeInterruptedRuns;
        existing.SevenZipPriority = s.SevenZipPriority;
        await db.SaveChangesAsync(ct);
        return existing;
    }
}
