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

    /// <summary>目标包尺寸（默认 100M）作为压缩分卷大小（PRD 3.3.2.3）；0/null=不分卷。</summary>
    public long? DefaultVolumeBytes { get; set; } = 100 * 1024 * 1024;

    // 死重压实（仅分组 pack 用到）：按数据 tier 决定重 pack 时若本地缺失成员是否允许下载云端 pack 补齐。
    // 优先用本地文件（内容一致者）；本地缺失且此开关为假则放弃该 pack 的重打包。Archive 默认 false（避免高成本取回/rehydrate）。
    public bool RepackDownloadHot { get; set; } = true;
    public bool RepackDownloadCool { get; set; } = true;
    public bool RepackDownloadCold { get; set; } = true;
    public bool RepackDownloadArchive { get; set; }

    /// <summary>某数据 tier 在死重重 pack 时是否允许下载云端 pack 补齐本地缺失成员。</summary>
    public bool RepackDownloadAllowed(StorageTier tier) => tier switch
    {
        StorageTier.Cool => RepackDownloadCool,
        StorageTier.Cold => RepackDownloadCold,
        StorageTier.Archive => RepackDownloadArchive,
        _ => RepackDownloadHot,
    };
    public bool DefaultIncludeSymlinks { get; set; }
    public string? DefaultIgnoreRules { get; set; }
    public string? DefaultDontCompressRules { get; set; }
    public string? DefaultDontGroupRules { get; set; }

    // 全局
    public int UploadConcurrency { get; set; } = 5;
    public int DownloadConcurrency { get; set; } = 5; // 还原/深度检查下载并发（PRD 3.4）

    /// <summary>短存(debug/info)日志保留天数（PRD 3.6，默认 14）。长存审计日志不受此限。</summary>
    public int LogEphemeralMaxAgeDays { get; set; } = 14;

    /// <summary>新建备份默认是否写 debug 级日志（含操作文件名）。默认关（可按备份单独开启）。</summary>
    public bool DefaultVerboseLogging { get; set; }

    // 网络重试退避（PRD 4.1）：逗号分隔的秒序列 + 总时长上限（分钟）。
    // 默认 5s、30s、90s、300s，之后每 300s（= 序列最后一项），累计上限 2h。
    public string RetryBackoffSeconds { get; set; } = "5,30,90,300";
    public int RetryMaxTotalMinutes { get; set; } = 120;

    // 死重压实阈值（PRD 3.3.3.4，M4 §6）：pack 死重比例超过此百分比时原地重压回收空间。
    public int DeadWeightThresholdPercent { get; set; } = 30;

    /// <summary>压缩临时区（staged-temp）字节上限，背压阈值（决策 4，可经 Settings 实时改）。默认 2GB。</summary>
    public long StagedLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>处理后重校验（<see cref="Services.ProcessingVerifier"/>）反复重处理上限（PRD §5.1，M4 §9，默认 5）。</summary>
    public int ProcessingMaxAttempts { get; set; } = 5;
}
