using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Models;

/// <summary>
/// 全局设置（单例，Id=1）。新建备份的默认值（PRD §11「使用默认」）+ 全局项（日志保留、并发）。
/// </summary>
public class GlobalSettings
{
    public int Id { get; set; }

    // 新建备份默认
    public StorageTier DefaultIndexTier { get; set; } = StorageTier.Hot;
    public StorageTier DefaultDataTier { get; set; } = StorageTier.Archive;
    public int DefaultMaxVersions { get; set; } = 100;
    public int DefaultMaxAgeDays { get; set; } = 180;
    public RetentionMode DefaultRetentionMode { get; set; } = RetentionMode.EitherTriggers;
    public long DefaultSingleFileThresholdBytes { get; set; } = 5 * 1024 * 1024;
    public long DefaultGroupCapBytes { get; set; } = 100 * 1024 * 1024;
    public long? DefaultVolumeBytes { get; set; }
    public bool DefaultIncludeSymlinks { get; set; }
    public string? DefaultIgnoreRules { get; set; }
    public string? DefaultDontCompressRules { get; set; }
    public string? DefaultDontGroupRules { get; set; }

    // 全局
    public int UploadConcurrency { get; set; } = 5;
    public int LogMaxEntries { get; set; } = 10_000;
    public int LogMaxAgeDays { get; set; } = 180;

    // 网络重试退避（PRD 4.1）：逗号分隔的秒序列 + 总时长上限（分钟）。
    // 默认 5s、30s、90s、300s，之后每 300s（= 序列最后一项），累计上限 2h。
    public string RetryBackoffSeconds { get; set; } = "5,30,90,300";
    public int RetryMaxTotalMinutes { get; set; } = 120;

    // 死重压实阈值（PRD 3.3.3.4，M4 §6）：pack 死重比例超过此百分比时原地重压回收空间。
    public int DeadWeightThresholdPercent { get; set; } = 30;
}
