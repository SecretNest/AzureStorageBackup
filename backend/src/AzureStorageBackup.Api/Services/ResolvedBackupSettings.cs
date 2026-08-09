using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The effective values of a backup config. A null field on the config means "inherit the global setting" (PRD §3 "use default").
///
/// Resolution must happen **at the point of use**; it must not be filled in inside BackupConfigService.GetAsync. Once filled in,
/// the edit screen can no longer tell "an inherited 100" from "a 100 I typed myself", and saving quietly freezes the inheritance
/// into an override — the feature would defeat itself.
///
/// IndexTier / DataTier are not on this list — they are locked after creation (BackupConfigService.UpdateAsync),
/// and inheriting means following the global setting, which is exactly a change after creation.
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
    /// <summary>When <paramref name="settings"/> is null, fall back to GlobalSettings' compile-time defaults,
    /// consistent with how existing callers handle <c>GlobalSettings?</c>.</summary>
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
