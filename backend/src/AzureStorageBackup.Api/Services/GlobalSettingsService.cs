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
    public async Task<GlobalSettings> GetAsync(CancellationToken ct = default)
    {
        // OrderBy 不是多余的：单例表也得给 First 一个确定的顺序，否则 EF 每次调用都记一条
        // 10103 警告（"First without OrderBy may lead to unpredictable results"）。
        // 这个方法几乎每个请求都会走一遍，docker logs 里刷屏的就是它。
        var s = await db.GlobalSettings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is null)
            return new GlobalSettings();

        // 迁移新列（StagedLimitBytes/ProcessingMaxAttempts）对既有行 SQL 默认 0，
        // 规范化为模型默认，使 GET /settings 与引擎行为（>0?:回退）一致，避免 UI 显示 0。
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
        existing.UploadConcurrency = s.UploadConcurrency;
        existing.DownloadConcurrency = s.DownloadConcurrency;
        existing.LogEphemeralMaxAgeDays = s.LogEphemeralMaxAgeDays;
        existing.DefaultVerboseLogging = s.DefaultVerboseLogging;
        existing.RetryBackoffSeconds = s.RetryBackoffSeconds;
        existing.RetryMaxTotalMinutes = s.RetryMaxTotalMinutes;
        existing.DeadWeightThresholdPercent = s.DeadWeightThresholdPercent;
        existing.StagedLimitBytes = s.StagedLimitBytes;
        existing.ProcessingMaxAttempts = s.ProcessingMaxAttempts;
        existing.OverlapDiffAndUpload = s.OverlapDiffAndUpload;
        existing.SevenZipPriority = s.SevenZipPriority;
        await db.SaveChangesAsync(ct);
        return existing;
    }
}
