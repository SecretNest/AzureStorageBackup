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
/// 记录设备本地的根路径与设置；加密密码在应用层为明文，落库经 ValueConverter 加密（仿 M1）。
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

    /// <summary>加密密码（应用态明文；落库加密）。空 = 不加密。</summary>
    public string? Password { get; set; }

    public StorageTier IndexTier { get; set; } = StorageTier.Hot;
    public StorageTier DataTier { get; set; } = StorageTier.Hot;

    // 规则（gitignore 语法，每行一条）
    public string? IgnoreRules { get; set; }
    public string? DontCompressRules { get; set; }
    public string? DontGroupRules { get; set; }

    public bool IncludeSymlinks { get; set; }

    // 版本保留（§10）
    public int MaxVersions { get; set; } = 100;
    public int MaxAgeDays { get; set; } = 180;
    public RetentionMode RetentionMode { get; set; } = RetentionMode.EitherTriggers;

    // 分组（§6）
    public long SingleFileThresholdBytes { get; set; } = 5 * 1024 * 1024;
    public long GroupCapBytes { get; set; } = 100 * 1024 * 1024;

    public DateTimeOffset CreatedAt { get; set; }
}
