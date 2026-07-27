using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 备份配置的生效值。配置字段为 null 表示继承全局设置（PRD §3「使用默认」）。
///
/// 解析必须发生在**使用时**，不能在 BackupConfigService.GetAsync 里就地填充：一旦填充，
/// 编辑界面就分不清「继承来的 100」和「自己填的 100」，保存时会把继承悄悄固化成覆盖，
/// 这个功能就自己废了自己。
///
/// IndexTier / DataTier 不在此列——它们创建后锁定（BackupConfigService.UpdateAsync），
/// 而继承意味着随全局变化，即一次创建后的变更。
/// </summary>
public sealed record ResolvedBackupSettings(
    string? IgnoreRules,
    string? DontCompressRules,
    string? DontGroupRules,
    string? CrossDirGroupRules,
    bool IncludeSymlinks,
    int MaxVersions,
    int MaxAgeDays,
    RetentionMode RetentionMode,
    long SingleFileThresholdBytes,
    long GroupCapBytes,
    long? VolumeBytes,
    bool VerboseLogging)
{
    /// <summary><paramref name="settings"/> 为 null 时用 GlobalSettings 的编译期默认值，
    /// 与既有调用方对 <c>GlobalSettings?</c> 的处理保持一致。</summary>
    public static ResolvedBackupSettings From(BackupConfig config, GlobalSettings? settings)
    {
        var s = settings ?? new GlobalSettings();
        return new ResolvedBackupSettings(
            config.IgnoreRules ?? s.DefaultIgnoreRules,
            config.DontCompressRules ?? s.DefaultDontCompressRules,
            config.DontGroupRules ?? s.DefaultDontGroupRules,
            config.CrossDirGroupRules ?? s.DefaultCrossDirGroupRules,
            config.IncludeSymlinks ?? s.DefaultIncludeSymlinks,
            config.MaxVersions ?? s.DefaultMaxVersions,
            config.MaxAgeDays ?? s.DefaultMaxAgeDays,
            config.RetentionMode ?? s.DefaultRetentionMode,
            config.SingleFileThresholdBytes ?? s.DefaultSingleFileThresholdBytes,
            config.GroupCapBytes ?? s.DefaultGroupCapBytes,
            config.VolumeBytes ?? s.DefaultVolumeBytes,
            config.VerboseLogging ?? s.DefaultVerboseLogging);
    }
}
