using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>把持久化的 BackupConfig 映射为引擎的 BackupRequest（BackupRunner 与调度器共用）。</summary>
public static class BackupRequestMapper
{
    public static BackupRequest From(BackupConfig config, Account account, GlobalSettings? settings = null) => new()
    {
        Account = account,
        Container = config.ContainerName,
        LocalRoot = config.LocalRoot,
        Name = config.Name,
        Description = config.Description,
        Password = Password(config),
        IndexTier = MapTier(config.IndexTier),
        DataTier = MapTier(config.DataTier),
        Options = new BackupEngineOptions
        {
            Ignore = new IgnoreRuleSet(SplitLines(config.IgnoreRules)),
            DontCompress = OptionalRules(config.DontCompressRules),
            DontGroup = OptionalRules(config.DontGroupRules),
            Scan = new ScanOptions { IncludeSymlinks = config.IncludeSymlinks },
            Plan = new PlanOptions
            {
                SingleFileThresholdBytes = config.SingleFileThresholdBytes,
                GroupCapBytes = config.GroupCapBytes,
            },
            VolumeBytes = config.VolumeBytes is > 0 ? config.VolumeBytes : null,
            Retention = RetentionOf(config),
            UploadConcurrency = settings is { UploadConcurrency: > 0 } ? settings.UploadConcurrency : 5,
            Upload = RetryOf(settings),
            DeadWeightThreshold = settings is { DeadWeightThresholdPercent: > 0 }
                ? settings.DeadWeightThresholdPercent / 100.0 : 0.30,
            AllowRepackDownload = settings?.RepackDownloadAllowed(config.DataTier) ?? true,
            VerboseLogging = config.VerboseLogging,
            ProcessingMaxAttempts = settings is { ProcessingMaxAttempts: > 0 } ? settings.ProcessingMaxAttempts : 5,
        },
    };

    /// <summary>把全局设置的网络重试退避（PRD 4.1）映射为上传路径的 RetryOptions。</summary>
    public static RetryOptions RetryOf(GlobalSettings? settings)
    {
        if (settings is null)
            return new RetryOptions();

        var sequence = ParseSeconds(settings.RetryBackoffSeconds);
        if (sequence.Count == 0)
            return new RetryOptions();

        return new RetryOptions
        {
            Backoff = sequence,
            SteadyInterval = sequence[^1],
            MaxTotalDelay = TimeSpan.FromMinutes(Math.Max(1, settings.RetryMaxTotalMinutes)),
        };
    }

    private static IReadOnlyList<TimeSpan> ParseSeconds(string? text) =>
        (text ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.TryParse(s, out var v) && v > 0 ? v : -1)
            .Where(v => v > 0)
            .Select(TimeSpan.FromSeconds)
            .ToList();

    public static RetentionPolicy RetentionOf(BackupConfig config) => new()
    {
        MaxVersions = config.MaxVersions,
        MaxAgeDays = config.MaxAgeDays,
        Mode = config.RetentionMode,
    };

    /// <summary>清理选项（保留 + 死重压实所需 tier/分卷/阈值），调度器 Cleanup 任务用。</summary>
    public static CleanupOptions CleanupOf(BackupConfig config, GlobalSettings? settings = null) => new()
    {
        Retention = RetentionOf(config),
        DataTier = MapTier(config.DataTier),
        VolumeBytes = config.VolumeBytes is > 0 ? config.VolumeBytes : null,
        DeadWeightThreshold = settings is { DeadWeightThresholdPercent: > 0 }
            ? settings.DeadWeightThresholdPercent / 100.0 : 0.30,
        LocalRoot = config.LocalRoot,
        AllowRepackDownload = settings?.RepackDownloadAllowed(config.DataTier) ?? true,
    };

    public static string? Password(BackupConfig config) =>
        string.IsNullOrEmpty(config.Password) ? null : config.Password;

    public static AccessTier MapTier(StorageTier tier) => tier switch
    {
        StorageTier.Cool => AccessTier.Cool,
        StorageTier.Cold => AccessTier.Cold,
        StorageTier.Archive => AccessTier.Archive,
        _ => AccessTier.Hot,
    };

    private static IgnoreRuleSet? OptionalRules(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new IgnoreRuleSet(SplitLines(text));

    private static IEnumerable<string> SplitLines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
