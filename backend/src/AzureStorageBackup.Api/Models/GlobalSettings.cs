using System.Diagnostics;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Models;

/// <summary>
/// 7z 进程的 CPU 优先级档位。
/// <para>
/// <b>Lowest 必须是 0。</b>加列时 EF 给既有行填的就是 0，这样老库升级后天然落在"最低"上，
/// 与新库的默认值一致。反面教材是 StagedLimitBytes / ProcessingMaxAttempts：它们的合法默认
/// 不是 0，于是 <see cref="Services.GlobalSettingsService"/> 里至今留着一段"读到 0 就换回默认值"
/// 的补丁。把 Lowest 定成 0 就不必再欠这笔账。
/// </para>
/// <para>不提供"高于正常"：Linux 上提升优先级要特权，而让压缩抢在 Web 界面前面，
/// 对一个背景备份程序来说只有坏处。</para>
/// </summary>
public enum SevenZipCpuPriority
{
    /// <summary>Linux nice 19。只吃别人不要的那部分 CPU。</summary>
    Lowest = 0,
    /// <summary>Linux nice 10。</summary>
    BelowNormal = 1,
    /// <summary>Linux nice 0，与其它进程平等争抢。</summary>
    Normal = 2,
}

public static class SevenZipCpuPriorityExtensions
{
    /// <summary>映射到进程优先级。落到 default 的（数据库里存了个不认识的值）一律按最低走：
    /// 认不出来时压慢一点是小事，把机器卡住不是。</summary>
    public static ProcessPriorityClass ToProcessPriorityClass(this SevenZipCpuPriority priority) => priority switch
    {
        SevenZipCpuPriority.Normal => ProcessPriorityClass.Normal,
        SevenZipCpuPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
        _ => ProcessPriorityClass.Idle,
    };
}

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

    /// <summary>跨路径打包规则的全局默认（gitignore 语法）。空 = 全部按目录打包。</summary>
    public string? DefaultCrossDirGroupRules { get; set; }

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

    /// <summary>压缩后重校验中，同一个成员反复变化时的重处理次数上限（PRD §5.1，M4 §9，默认 5）。</summary>
    public int ProcessingMaxAttempts { get; set; } = 5;

    /// <summary>
    /// 备份时差分与「压缩+上传」是否重叠跑（默认开）。开着时网络不必等哈希全部跑完；
    /// 代价是差分的读与压缩的读同时压在一块盘上。机械盘的 NAS 上两股读可能互相拖慢到得不偿失，
    /// 那种情况下关掉它，回到"先全部判完再传"。
    /// </summary>
    public bool OverlapDiffAndUpload { get; set; } = true;

    /// <summary>
    /// 7z 进程的 CPU 优先级，默认最低。压缩与解压是这个程序唯一会把 CPU 吃满的动作，
    /// 而它跑在一台还有别的东西在跑的机器上——备份慢一点没人会注意，机器卡住会。
    /// <para>
    /// 与 <c>Backup__SevenZipMethodArgs</c> 里的 <c>-mmt=N</c> 是两件事：限线程降的是并行度，
    /// 这里降的是争抢时的排队权重。单线程满载一样能让界面卡顿，那种情况只有优先级救得了。
    /// </para>
    /// </summary>
    public SevenZipCpuPriority SevenZipPriority { get; set; } = SevenZipCpuPriority.Lowest;
}
