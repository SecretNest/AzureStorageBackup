using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Models;

/// <summary>备份配置响应体。刻意不含 Password，仅暴露 HasPassword（是否加密）。</summary>
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
    DateTimeOffset CreatedAt)
{
    public static BackupConfigResponse From(BackupConfig c) => new(
        c.Id, c.AccountId, c.ContainerName, c.Name, c.Description, c.LocalRoot,
        !string.IsNullOrEmpty(c.Password), c.IndexTier, c.DataTier,
        c.IgnoreRules, c.DontCompressRules, c.DontGroupRules, c.IncludeSymlinks,
        c.MaxVersions, c.MaxAgeDays, c.RetentionMode,
        c.SingleFileThresholdBytes, c.GroupCapBytes, c.CreatedAt);
}

/// <summary>还原请求体。TargetRoot 为空则用配置的本地根；Version 为空则还原最新版本。</summary>
public record RestoreRequestBody(string? TargetRoot, int? Version);

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
    long GroupCapBytes)
{
    public BackupConfig ToConfig() => new()
    {
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
