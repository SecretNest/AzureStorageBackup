using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Models;

/// <summary>Blob 访问层。索引 Tier 应为 Hot/Cool/Cold；数据 Tier 可含 Archive（M4 §13）。</summary>
public enum StorageTier
{
    Hot = 0,
    Cool = 1,
    Cold = 2,
    Archive = 3,
}

/// <summary>
/// 一个备份的本地配置（PRD §11 新建备份向导产物）。
/// 记录设备本地的根路径与设置；加密密码在应用层与库中均为密文，解密只经 ISecretReader（设计 §3.1）。
/// </summary>
public class BackupConfig
{
    public int Id { get; set; }

    public int AccountId { get; set; }
    public string ContainerName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>本地根路径（设备本地；跨设备恢复时重新指定）。</summary>
    public string LocalRoot { get; set; } = string.Empty;

    /// <summary>加密密码密文。空 = 不加密。取明文用 ISecretReader.RevealBackupPassword。</summary>
    public string? PasswordProtected { get; set; }

    // Tier 创建后锁定（BackupConfigService.UpdateAsync），因此**不可继承**——
    // 继承意味着随全局设置变化，那正是一次创建后的变更。新建时由前端以全局默认预填。
    public StorageTier IndexTier { get; set; } = StorageTier.Hot;
    public StorageTier DataTier { get; set; } = StorageTier.Archive;

    // 以下字段：null = 继承全局设置（PRD §3「使用默认」），非 null = 本配置自己的覆盖值。
    // 三个规则字段的 "" 表示「明确没有规则」，与继承区分开。
    // 规则（gitignore 语法，每行一条）
    public string? IgnoreRules { get; set; }
    public string? DontCompressRules { get; set; }
    public string? DontGroupRules { get; set; }

    /// <summary>命中者允许跨目录装箱；null = 用全局默认。</summary>
    public string? CrossDirGroupRules { get; set; }

    public bool? IncludeSymlinks { get; set; }

    // 版本保留（§10）
    public int? MaxVersions { get; set; }
    public int? MaxAgeDays { get; set; }
    public RetentionMode? RetentionMode { get; set; }

    // 分组（§6）
    public long? SingleFileThresholdBytes { get; set; }
    public long? GroupCapBytes { get; set; }

    // null = 继承；0 = 明确关闭分卷；>0 = 分卷大小。
    // 「关闭」从 null 挪到 0，好让 null 在所有可继承字段上含义一致（Settings 页本就写着 0=off）。
    public long? VolumeBytes { get; set; }

    /// <summary>是否写 debug 级日志（含操作文件名，短存 14 天）。默认关。</summary>
    public bool? VerboseLogging { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // 持久状态（§4.2 决策 2）：仅 Normal/Error。瞬时态（备份中/还原中…）不落库，DTO 时派生。
    public BackupStatus Status { get; set; } = BackupStatus.Normal;
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
}
