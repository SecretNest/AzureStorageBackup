using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class ResolvedBackupSettingsTests
{
    private static GlobalSettings Globals() => new()
    {
        DefaultMaxVersions = 7,
        DefaultMaxAgeDays = 30,
        DefaultRetentionMode = RetentionMode.BothRequired,
        DefaultSingleFileThresholdBytes = 111,
        DefaultGroupCapBytes = 222,
        DefaultVolumeBytes = 333,
        DefaultIncludeSymlinks = true,
        DefaultVerboseLogging = true,
        DefaultIgnoreRules = "global-ignore",
        DefaultDontCompressRules = "global-dontcompress",
        DefaultDontGroupRules = "global-dontgroup",
    };

    [Fact]
    public void Null_Fields_Take_The_Global_Value()
    {
        var r = ResolvedBackupSettings.From(new BackupConfig(), Globals());

        Assert.Equal(7, r.MaxVersions);
        Assert.Equal(30, r.MaxAgeDays);
        Assert.Equal(RetentionMode.BothRequired, r.RetentionMode);
        Assert.Equal(111, r.SingleFileThresholdBytes);
        Assert.Equal(222, r.GroupCapBytes);
        Assert.Equal(333, r.VolumeBytes);
        Assert.True(r.IncludeSymlinks);
        Assert.True(r.VerboseLogging);
        Assert.Equal("global-ignore", r.IgnoreRules);
        Assert.Equal("global-dontcompress", r.DontCompressRules);
        Assert.Equal("global-dontgroup", r.DontGroupRules);
    }

    [Fact]
    public void Non_Null_Fields_Override_The_Global_Value()
    {
        var config = new BackupConfig
        {
            MaxVersions = 1,
            MaxAgeDays = 2,
            RetentionMode = RetentionMode.VersionOnly,
            SingleFileThresholdBytes = 3,
            GroupCapBytes = 4,
            VolumeBytes = 5,
            IncludeSymlinks = false,
            VerboseLogging = false,
            IgnoreRules = "mine",
            DontCompressRules = "mine-dc",
            DontGroupRules = "mine-dg",
        };

        var r = ResolvedBackupSettings.From(config, Globals());

        Assert.Equal(1, r.MaxVersions);
        Assert.Equal(2, r.MaxAgeDays);
        Assert.Equal(RetentionMode.VersionOnly, r.RetentionMode);
        Assert.Equal(3, r.SingleFileThresholdBytes);
        Assert.Equal(4, r.GroupCapBytes);
        Assert.Equal(5, r.VolumeBytes);
        Assert.False(r.IncludeSymlinks);
        Assert.False(r.VerboseLogging);
        Assert.Equal("mine", r.IgnoreRules);
        Assert.Equal("mine-dc", r.DontCompressRules);
        Assert.Equal("mine-dg", r.DontGroupRules);
    }

    // false and 0 are legitimate override values and must not be taken as "unset" and fall back to the global.
    // This is exactly why null is used rather than a sentinel value, and it is worth pinning down on its own.
    [Fact]
    public void False_And_Zero_Are_Overrides_Not_Absence()
    {
        var config = new BackupConfig { IncludeSymlinks = false, VerboseLogging = false, MaxVersions = 0 };

        var r = ResolvedBackupSettings.From(config, Globals());

        Assert.False(r.IncludeSymlinks);
        Assert.False(r.VerboseLogging);
        Assert.Equal(0, r.MaxVersions);
    }

    // VolumeBytes has three states: null = inherit, 0 = volumes explicitly off, positive = volume size.
    [Theory]
    [InlineData(null, 333L)]
    [InlineData(0L, 0L)]
    [InlineData(64L, 64L)]
    public void VolumeBytes_Has_Three_States(long? configured, long expected)
    {
        var r = ResolvedBackupSettings.From(new BackupConfig { VolumeBytes = configured }, Globals());
        Assert.Equal(expected, r.VolumeBytes);
    }

    // Rule fields have three states: null = inherit, "" = explicitly no rules, non-empty = override.
    // The empty string must survive, otherwise "I want no ignore rules at all" cannot be expressed.
    // All three rule fields migrate with identical semantics (exactly the class of bug Fix 1 missed),
    // so all three are pinned down separately; testing IgnoreRules alone is not enough.
    [Theory]
    [InlineData(null, "global-ignore")]
    [InlineData("", "")]
    [InlineData("x", "x")]
    public void Rules_Have_Three_States(string? configured, string expected)
    {
        var r = ResolvedBackupSettings.From(new BackupConfig { IgnoreRules = configured }, Globals());
        Assert.Equal(expected, r.IgnoreRules);
    }

    [Theory]
    [InlineData(null, "global-dontcompress")]
    [InlineData("", "")]
    [InlineData("x", "x")]
    public void DontCompressRules_Have_Three_States(string? configured, string expected)
    {
        var r = ResolvedBackupSettings.From(new BackupConfig { DontCompressRules = configured }, Globals());
        Assert.Equal(expected, r.DontCompressRules);
    }

    [Theory]
    [InlineData(null, "global-dontgroup")]
    [InlineData("", "")]
    [InlineData("x", "x")]
    public void DontGroupRules_Have_Three_States(string? configured, string expected)
    {
        var r = ResolvedBackupSettings.From(new BackupConfig { DontGroupRules = configured }, Globals());
        Assert.Equal(expected, r.DontGroupRules);
    }

    [Fact]
    public void Null_Settings_Falls_Back_To_GlobalSettings_Defaults()
    {
        var defaults = new GlobalSettings();
        var r = ResolvedBackupSettings.From(new BackupConfig(), null);
        Assert.Equal(defaults.DefaultMaxVersions, r.MaxVersions);
    }
    /// <summary>The repair path's field incident: the runner derived store-only from
    /// OptionalRules(DontCompressRules) — the case-SENSITIVE half alone — while the backup path joins both
    /// halves. A media rule set entered case-insensitively (*.mkv, the ordinary way) was therefore invisible
    /// to repair, which re-compressed a 113.9 GB store-only file with real compression and pegged the NAS
    /// CPU at 100%. DontCompress() is the single source both sides use now: both halves, joined the way
    /// gitignore's last-match rule requires.</summary>
    [Fact]
    public void DontCompress_Joins_Both_Halves_So_An_Insensitive_Rule_Is_Not_Lost()
    {
        var resolved = ResolvedBackupSettings.From(new BackupConfig
        {
            AccountId = 1, ContainerName = "c", Name = "n", LocalRoot = "/data",
            DontCompressRulesCaseInsensitive = "*.mkv",
        }, new GlobalSettings());

        var rules = resolved.DontCompress();
        Assert.NotNull(rules);
        Assert.True(rules!.MatchesFileOrAncestorDir("Movie/Big.MKV"),
            "a case-insensitive rule must reach the repair's store-only decision");

        // Both halves empty → null, the same "no rules" contract OptionalRules had.
        Assert.Null(ResolvedBackupSettings.From(new BackupConfig
        {
            AccountId = 1, ContainerName = "c", Name = "n", LocalRoot = "/data",
        }, new GlobalSettings { DefaultDontCompressRules = null, DefaultDontCompressRulesCaseInsensitive = null }).DontCompress());
    }

}
