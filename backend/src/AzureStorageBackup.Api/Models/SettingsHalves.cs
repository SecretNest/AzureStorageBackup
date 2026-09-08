using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Models;

// The settings row is one table but two resources on the API, each the shape of one settings page: a page saves its
// own half and cannot carry the other page's fields along — which the whole-object PUT used to do, so that whichever
// page saved last overwrote the other with values it had read before. SettingsPartitionTests holds the two to a
// partition of GlobalSettings: every column in exactly one of them.

/// <summary>Settings → Backup defaults: what a new backup inherits (tiers, retention, packing thresholds, rules) and the
/// per-tier repack switches. <c>GET/PUT /api/settings/defaults</c>.</summary>
public sealed record BackupDefaultsSettings
{
    public StorageTier DefaultIndexTier { get; init; }
    public StorageTier DefaultDataTier { get; init; }
    public int DefaultMaxVersions { get; init; }
    public int DefaultMaxAgeDays { get; init; }
    public RetentionMode DefaultRetentionMode { get; init; }
    public long DefaultSingleFileThresholdBytes { get; init; }
    public long DefaultGroupCapBytes { get; init; }
    public long? DefaultVolumeBytes { get; init; }
    public bool RepackDownloadHot { get; init; }
    public bool RepackDownloadCool { get; init; }
    public bool RepackDownloadCold { get; init; }
    public bool RepackDownloadArchive { get; init; }
    public bool DefaultIncludeSymlinks { get; init; }
    public string? DefaultIgnoreRules { get; init; }
    public string? DefaultDontCompressRules { get; init; }
    public string? DefaultDontGroupRules { get; init; }
    public string? DefaultCrossDirGroupRules { get; init; }
    public string? DefaultIgnoreRulesCaseInsensitive { get; init; }
    public string? DefaultDontCompressRulesCaseInsensitive { get; init; }
    public string? DefaultDontGroupRulesCaseInsensitive { get; init; }
    public string? DefaultCrossDirGroupRulesCaseInsensitive { get; init; }

    public static BackupDefaultsSettings From(GlobalSettings row) => new()
    {
        DefaultIndexTier = row.DefaultIndexTier,
        DefaultDataTier = row.DefaultDataTier,
        DefaultMaxVersions = row.DefaultMaxVersions,
        DefaultMaxAgeDays = row.DefaultMaxAgeDays,
        DefaultRetentionMode = row.DefaultRetentionMode,
        DefaultSingleFileThresholdBytes = row.DefaultSingleFileThresholdBytes,
        DefaultGroupCapBytes = row.DefaultGroupCapBytes,
        DefaultVolumeBytes = row.DefaultVolumeBytes,
        RepackDownloadHot = row.RepackDownloadHot,
        RepackDownloadCool = row.RepackDownloadCool,
        RepackDownloadCold = row.RepackDownloadCold,
        RepackDownloadArchive = row.RepackDownloadArchive,
        DefaultIncludeSymlinks = row.DefaultIncludeSymlinks,
        DefaultIgnoreRules = row.DefaultIgnoreRules,
        DefaultDontCompressRules = row.DefaultDontCompressRules,
        DefaultDontGroupRules = row.DefaultDontGroupRules,
        DefaultCrossDirGroupRules = row.DefaultCrossDirGroupRules,
        DefaultIgnoreRulesCaseInsensitive = row.DefaultIgnoreRulesCaseInsensitive,
        DefaultDontCompressRulesCaseInsensitive = row.DefaultDontCompressRulesCaseInsensitive,
        DefaultDontGroupRulesCaseInsensitive = row.DefaultDontGroupRulesCaseInsensitive,
        DefaultCrossDirGroupRulesCaseInsensitive = row.DefaultCrossDirGroupRulesCaseInsensitive,
    };

    /// <summary>Writes this half onto the row and nothing else — the other half is another page's business.</summary>
    public void ApplyTo(GlobalSettings row)
    {
        row.DefaultIndexTier = DefaultIndexTier;
        row.DefaultDataTier = DefaultDataTier;
        row.DefaultMaxVersions = DefaultMaxVersions;
        row.DefaultMaxAgeDays = DefaultMaxAgeDays;
        row.DefaultRetentionMode = DefaultRetentionMode;
        row.DefaultSingleFileThresholdBytes = DefaultSingleFileThresholdBytes;
        row.DefaultGroupCapBytes = DefaultGroupCapBytes;
        row.DefaultVolumeBytes = DefaultVolumeBytes;
        row.RepackDownloadHot = RepackDownloadHot;
        row.RepackDownloadCool = RepackDownloadCool;
        row.RepackDownloadCold = RepackDownloadCold;
        row.RepackDownloadArchive = RepackDownloadArchive;
        row.DefaultIncludeSymlinks = DefaultIncludeSymlinks;
        row.DefaultIgnoreRules = DefaultIgnoreRules;
        row.DefaultDontCompressRules = DefaultDontCompressRules;
        row.DefaultDontGroupRules = DefaultDontGroupRules;
        row.DefaultCrossDirGroupRules = DefaultCrossDirGroupRules;
        row.DefaultIgnoreRulesCaseInsensitive = DefaultIgnoreRulesCaseInsensitive;
        row.DefaultDontCompressRulesCaseInsensitive = DefaultDontCompressRulesCaseInsensitive;
        row.DefaultDontGroupRulesCaseInsensitive = DefaultDontGroupRulesCaseInsensitive;
        row.DefaultCrossDirGroupRulesCaseInsensitive = DefaultCrossDirGroupRulesCaseInsensitive;
    }
}

/// <summary>Settings → Performance: how the server runs — concurrency, the upload memory limit, the staging area,
/// retries, compaction, the 7z priority, and the logging pair (retention and the verbose default). <c>GET/PUT /api/settings/performance</c>.</summary>
public sealed record PerformanceSettings
{
    public int UploadConcurrency { get; init; }
    public long UploadMemoryLimitBytes { get; init; }
    public int DownloadConcurrency { get; init; }
    public int CheckHeadConcurrency { get; init; }
    public int LogEphemeralMaxAgeDays { get; init; }
    public bool DefaultVerboseLogging { get; init; }
    public string RetryBackoffSeconds { get; init; }
    public int RetryMaxTotalMinutes { get; init; }
    public int DeadWeightThresholdPercent { get; init; }
    public long StagedLimitBytes { get; init; }
    public int ProcessingMaxAttempts { get; init; }
    public bool OverlapDiffAndUpload { get; init; }
    public bool AutoResumeInterruptedRuns { get; init; }
    public SevenZipCpuPriority SevenZipPriority { get; init; }

    public static PerformanceSettings From(GlobalSettings row) => new()
    {
        UploadConcurrency = row.UploadConcurrency,
        UploadMemoryLimitBytes = row.UploadMemoryLimitBytes,
        DownloadConcurrency = row.DownloadConcurrency,
        CheckHeadConcurrency = row.CheckHeadConcurrency,
        LogEphemeralMaxAgeDays = row.LogEphemeralMaxAgeDays,
        DefaultVerboseLogging = row.DefaultVerboseLogging,
        RetryBackoffSeconds = row.RetryBackoffSeconds,
        RetryMaxTotalMinutes = row.RetryMaxTotalMinutes,
        DeadWeightThresholdPercent = row.DeadWeightThresholdPercent,
        StagedLimitBytes = row.StagedLimitBytes,
        ProcessingMaxAttempts = row.ProcessingMaxAttempts,
        OverlapDiffAndUpload = row.OverlapDiffAndUpload,
        AutoResumeInterruptedRuns = row.AutoResumeInterruptedRuns,
        SevenZipPriority = row.SevenZipPriority,
    };

    /// <summary>Writes this half onto the row and nothing else — the other half is another page's business.</summary>
    public void ApplyTo(GlobalSettings row)
    {
        row.UploadConcurrency = UploadConcurrency;
        row.UploadMemoryLimitBytes = UploadMemoryLimitBytes;
        row.DownloadConcurrency = DownloadConcurrency;
        row.CheckHeadConcurrency = CheckHeadConcurrency;
        row.LogEphemeralMaxAgeDays = LogEphemeralMaxAgeDays;
        row.DefaultVerboseLogging = DefaultVerboseLogging;
        row.RetryBackoffSeconds = RetryBackoffSeconds;
        row.RetryMaxTotalMinutes = RetryMaxTotalMinutes;
        row.DeadWeightThresholdPercent = DeadWeightThresholdPercent;
        row.StagedLimitBytes = StagedLimitBytes;
        row.ProcessingMaxAttempts = ProcessingMaxAttempts;
        row.OverlapDiffAndUpload = OverlapDiffAndUpload;
        row.AutoResumeInterruptedRuns = AutoResumeInterruptedRuns;
        row.SevenZipPriority = SevenZipPriority;
    }
}
