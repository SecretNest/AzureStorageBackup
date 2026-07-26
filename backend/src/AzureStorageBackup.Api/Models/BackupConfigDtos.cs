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
    bool? IncludeSymlinks,
    int? MaxVersions,
    int? MaxAgeDays,
    RetentionMode? RetentionMode,
    long? SingleFileThresholdBytes,
    long? GroupCapBytes,
    long? VolumeBytes,
    bool? VerboseLogging,
    DateTimeOffset CreatedAt,
    BackupStatus Status,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    string Activity,
    bool SecretsUnavailable,
    ResolvedBackupSettings Effective)
{
    /// <summary>
    /// <paramref name="secretsUnavailable"/> 必须按该配置密文的实际可解性传入
    /// （见 <see cref="SecretAvailability"/>），不能直接传全局 Lost 状态——恢复中间态里
    /// 已重设成功的备份必须停止显示「待重设」。无密码的备份没有密文可丢，恒为 false。
    ///
    /// <paramref name="settings"/> 是必填而非可选：界面要靠 Effective 显示继承字段的当前生效值，
    /// 少传一处就会显示成 GlobalSettings 的编译期默认而不是用户实际配置的默认。设为必填，
    /// 让编译器把每个调用点都找出来。
    /// </summary>
    public static BackupConfigResponse From(
        BackupConfig c, GlobalSettings? settings, string activity = "Idle", bool secretsUnavailable = false) => new(
        c.Id, c.AccountId, c.ContainerName, c.Name, c.Description, c.LocalRoot,
        !string.IsNullOrEmpty(c.PasswordProtected), c.IndexTier, c.DataTier,
        c.IgnoreRules, c.DontCompressRules, c.DontGroupRules, c.IncludeSymlinks,
        c.MaxVersions, c.MaxAgeDays, c.RetentionMode,
        c.SingleFileThresholdBytes, c.GroupCapBytes, c.VolumeBytes, c.VerboseLogging, c.CreatedAt,
        c.Status, c.LastError, c.LastErrorAt, activity,
        secretsUnavailable && !string.IsNullOrEmpty(c.PasswordProtected),
        ResolvedBackupSettings.From(c, settings));
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

/// <summary>备份密码重设请求。必须是当初加密云端包的那个密码——不支持更改密码（设计决策 6、8）。</summary>
public record ResetBackupPasswordRequest(string Password);

/// <summary>创建/更新备份配置请求体。更新时 Password 为空表示保留原值。
/// 11 个可继承字段为 null 表示「使用默认」——落库即 null，运行时经
/// <see cref="ResolvedBackupSettings"/> 解析。IndexTier/DataTier 不可继承，保持必填。</summary>
public record BackupConfigRequest(
    int AccountId,
    string ContainerName,
    string Name,
    string? Description,
    string LocalRoot,
    string? Password,
    StorageTier IndexTier,
    StorageTier DataTier,
    string? IgnoreRules = null,
    string? DontCompressRules = null,
    string? DontGroupRules = null,
    bool? IncludeSymlinks = null,
    int? MaxVersions = null,
    int? MaxAgeDays = null,
    RetentionMode? RetentionMode = null,
    long? SingleFileThresholdBytes = null,
    long? GroupCapBytes = null,
    long? VolumeBytes = null,
    bool? VerboseLogging = null)
{
    /// <summary>请求体里的 Password 是明文；落到实体上时立即加密（设计 §3.1：实体只持密文）。</summary>
    public BackupConfig ToConfig(IEncryptionService encryption) => new()
    {
        VolumeBytes = VolumeBytes,
        VerboseLogging = VerboseLogging,
        AccountId = AccountId,
        ContainerName = ContainerName,
        Name = Name,
        Description = Description,
        LocalRoot = LocalRoot,
        PasswordProtected = string.IsNullOrEmpty(Password) ? null : encryption.Encrypt(Password),
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
