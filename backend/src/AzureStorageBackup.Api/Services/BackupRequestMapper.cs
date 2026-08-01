using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 装箱那两条**按机器**（而不是按备份）定的界，由环境变量给：<c>Backup__MaxPackMembers</c> 与
/// <c>Backup__MaxPackPathBytes</c>。第三条 <c>GroupCapBytes</c> 是每个备份自己的设置，不在这里。
/// <para>
/// 之所以按机器给：这两条约束的是**这台机器上 7z 进程的内存与 argv 上限**，
/// 与"这份备份想把包切多大"无关。同一份配置搬到另一台机器上，合适的值可能完全不同。
/// </para>
/// </summary>
public sealed record PackLimits(int MaxPackMembers = 20_000, long MaxPackPathBytes = 1_000_000)
{
    public static readonly PackLimits Default = new();
}

/// <summary>把持久化的 BackupConfig 映射为引擎的 BackupRequest（BackupRunner 与调度器共用）。</summary>
public static class BackupRequestMapper
{
    /// <summary>
    /// <paramref name="password"/> 是**明文**，由调用方经 ISecretReader.RevealBackupPassword 取得
    /// （设计 §3.1：解密只在咽喉处；映射器是静态的，拿不到 ISecretReader）。
    /// </summary>
    public static BackupRequest From(
        BackupConfig config, Account account, string? password, GlobalSettings? settings = null,
        PackLimits? packLimits = null)
    {
        var r = ResolvedBackupSettings.From(config, settings);
        var limits = packLimits ?? PackLimits.Default;
        return new BackupRequest
        {
            Account = account,
            Container = config.ContainerName,
            LocalRoot = config.LocalRoot,
            Name = config.Name,
            Description = config.Description,
            Password = password,
            IndexTier = MapTier(config.IndexTier),
            DataTier = MapTier(config.DataTier),
            Options = new BackupEngineOptions
            {
                Ignore = new IgnoreRuleSet(SplitLines(r.IgnoreRules)),
                DontCompress = OptionalRules(r.DontCompressRules),
                DontGroup = OptionalRules(r.DontGroupRules),
                CrossDirGroup = OptionalRules(r.CrossDirGroupRules),
                Scan = new ScanOptions { IncludeSymlinks = r.IncludeSymlinks },
                Plan = new PlanOptions
                {
                    SingleFileThresholdBytes = r.SingleFileThresholdBytes,
                    GroupCapBytes = r.GroupCapBytes,
                    MaxPackMembers = limits.MaxPackMembers,
                    MaxPackPathBytes = limits.MaxPackPathBytes,
                },
                VolumeBytes = r.VolumeBytes is > 0 ? r.VolumeBytes : null,
                Retention = RetentionOf(config, settings),
                UploadConcurrency = settings is { UploadConcurrency: > 0 } ? settings.UploadConcurrency : 5,
                Upload = RetryOf(settings),
                DeadWeightThreshold = settings is { DeadWeightThresholdPercent: > 0 }
                    ? settings.DeadWeightThresholdPercent / 100.0 : 0.30,
                AllowRepackDownload = settings?.RepackDownloadAllowed(config.DataTier) ?? true,
                VerboseLogging = r.VerboseLogging,
                ProcessingMaxAttempts = settings is { ProcessingMaxAttempts: > 0 } ? settings.ProcessingMaxAttempts : 5,
                OverlapDiffAndUpload = settings?.OverlapDiffAndUpload ?? true,
            },
        };
    }

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

    public static RetentionPolicy RetentionOf(BackupConfig config, GlobalSettings? settings)
    {
        var r = ResolvedBackupSettings.From(config, settings);
        return new RetentionPolicy
        {
            MaxVersions = r.MaxVersions,
            MaxAgeDays = r.MaxAgeDays,
            Mode = r.RetentionMode,
        };
    }

    /// <summary>清理选项（保留 + 死重压实所需 tier/分卷/阈值），调度器 Cleanup 任务用。</summary>
    public static CleanupOptions CleanupOf(BackupConfig config, GlobalSettings? settings = null)
    {
        var r = ResolvedBackupSettings.From(config, settings);
        return new CleanupOptions
        {
            Retention = RetentionOf(config, settings),
            DataTier = MapTier(config.DataTier),
            VolumeBytes = r.VolumeBytes is > 0 ? r.VolumeBytes : null,
            DeadWeightThreshold = settings is { DeadWeightThresholdPercent: > 0 }
                ? settings.DeadWeightThresholdPercent / 100.0 : 0.30,
            LocalRoot = config.LocalRoot,
            AllowRepackDownload = settings?.RepackDownloadAllowed(config.DataTier) ?? true,
        };
    }

    // 备份密码改由 ISecretReader.RevealBackupPassword 提供（设计 §3.1），此处不再暴露。

    public static AccessTier MapTier(StorageTier tier) => tier switch
    {
        StorageTier.Cool => AccessTier.Cool,
        StorageTier.Cold => AccessTier.Cold,
        StorageTier.Archive => AccessTier.Archive,
        _ => AccessTier.Hot,
    };

    /// <summary>把一段可选的规则文本映射为规则集（空/空白 → null，表示「没有规则」）。
    /// 公开是因为修复路径（RepairRunner → BackupRepairer）也要按同一套 DontCompress 规则决定是否只存不压，
    /// 否则修好的归档与全新备份写出的压缩方式不一致。</summary>
    public static IgnoreRuleSet? OptionalRules(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new IgnoreRuleSet(SplitLines(text));

    private static IEnumerable<string> SplitLines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
