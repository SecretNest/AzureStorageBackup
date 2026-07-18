using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Models;

/// <summary>
/// 备份配置响应体。刻意不含 Password，仅暴露 HasPassword（是否加密）。
/// <c>Status</c>/<c>LastError</c>/<c>LastErrorAt</c> 为持久状态（§4.2 决策 2）；
/// <c>Activity</c> 为派生瞬时态（Idle/BackingUp/Restoring/Checking/Repairing），不落库，调用方按需计算后传入。
/// </summary>
public record BackupConfigResponse(
    int Id,
    int AccountId,
    string ContainerName,
    string Name,
    string? Description,
    string LocalRoot,
    bool HasPassword,
    StorageTier IndexTier,
    StorageTier DataTier,
    string? IgnoreRules,
    string? DontCompressRules,
    string? DontGroupRules,
    bool IncludeSymlinks,
    int MaxVersions,
    int MaxAgeDays,
    RetentionMode RetentionMode,
    long SingleFileThresholdBytes,
    long GroupCapBytes,
    long? VolumeBytes,
    bool VerboseLogging,
    DateTimeOffset CreatedAt,
    BackupStatus Status,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    string Activity)
{
    public static BackupConfigResponse From(BackupConfig c, string activity = "Idle") => new(
        c.Id, c.AccountId, c.ContainerName, c.Name, c.Description, c.LocalRoot,
        !string.IsNullOrEmpty(c.Password), c.IndexTier, c.DataTier,
        c.IgnoreRules, c.DontCompressRules, c.DontGroupRules, c.IncludeSymlinks,
        c.MaxVersions, c.MaxAgeDays, c.RetentionMode,
        c.SingleFileThresholdBytes, c.GroupCapBytes, c.VolumeBytes, c.VerboseLogging, c.CreatedAt,
        c.Status, c.LastError, c.LastErrorAt, activity);
}

/// <summary>还原请求体。TargetRoot 为空则用配置的本地根；Version 为空则还原最新版本。
/// SelectedPaths 为空则还原整版本；非空则只还原恰好这些路径（需求 B，pack 只下一次、只写选中成员）。
/// Conflict 为冲突模式（决策 3）；RehydratePriority 为 Archive 活化优先级。</summary>
public record RestoreRequestBody(
    string? TargetRoot,
    int? Version,
    Dictionary<string, int>? Substitutions = null,
    List<string>? SelectedPaths = null,
    RestoreConflictMode Conflict = RestoreConflictMode.OverwriteIfChanged,
    RestoreRehydratePriority RehydratePriority = RestoreRehydratePriority.Standard);

/// <summary>还原量估算请求体（§4.1b，需求 A）：选中路径的下载/解压量预估。Version 为空则用最新版本。</summary>
public record RestoreEstimateRequestBody(int? Version, List<string> Paths);

/// <summary>导入已有备份请求：读 container 的信息文件恢复配置（roadmap，PRD 1.5）。加密备份需提供密码。</summary>
public record ImportRequest(int AccountId, string ContainerName, string? Password);

/// <summary>创建/更新备份配置请求体。更新时 Password 为空表示保留原值。</summary>
public record BackupConfigRequest(
    int AccountId,
    string ContainerName,
    string Name,
    string? Description,
    string LocalRoot,
    string? Password,
    StorageTier IndexTier,
    StorageTier DataTier,
    string? IgnoreRules,
    string? DontCompressRules,
    string? DontGroupRules,
    bool IncludeSymlinks,
    int MaxVersions,
    int MaxAgeDays,
    RetentionMode RetentionMode,
    long SingleFileThresholdBytes,
    long GroupCapBytes,
    long? VolumeBytes = null,
    bool VerboseLogging = false)
{
    public BackupConfig ToConfig() => new()
    {
        VolumeBytes = VolumeBytes,
        VerboseLogging = VerboseLogging,
        AccountId = AccountId,
        ContainerName = ContainerName,
        Name = Name,
        Description = Description,
        LocalRoot = LocalRoot,
        Password = Password,
        IndexTier = IndexTier,
        DataTier = DataTier,
        IgnoreRules = IgnoreRules,
        DontCompressRules = DontCompressRules,
        DontGroupRules = DontGroupRules,
        IncludeSymlinks = IncludeSymlinks,
        MaxVersions = MaxVersions,
        MaxAgeDays = MaxAgeDays,
        RetentionMode = RetentionMode,
        SingleFileThresholdBytes = SingleFileThresholdBytes,
        GroupCapBytes = GroupCapBytes,
    };
}
