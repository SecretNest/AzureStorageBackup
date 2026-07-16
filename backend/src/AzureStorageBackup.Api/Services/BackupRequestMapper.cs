using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>把持久化的 BackupConfig 映射为引擎的 BackupRequest（BackupRunner 与调度器共用）。</summary>
public static class BackupRequestMapper
{
    public static BackupRequest From(BackupConfig config, Account account) => new()
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
            Retention = RetentionOf(config),
        },
    };

    public static RetentionPolicy RetentionOf(BackupConfig config) => new()
    {
        MaxVersions = config.MaxVersions,
        MaxAgeDays = config.MaxAgeDays,
        Mode = config.RetentionMode,
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
