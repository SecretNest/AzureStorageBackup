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

    // false 与 0 是合法的覆盖值，不能被当成「未设置」而回落到全局。
    // 这正是用 null 而非哨兵值的理由，值得单独钉住。
    [Fact]
    public void False_And_Zero_Are_Overrides_Not_Absence()
    {
        var config = new BackupConfig { IncludeSymlinks = false, VerboseLogging = false, MaxVersions = 0 };

        var r = ResolvedBackupSettings.From(config, Globals());

        Assert.False(r.IncludeSymlinks);
        Assert.False(r.VerboseLogging);
        Assert.Equal(0, r.MaxVersions);
    }

    // VolumeBytes 三态：null = 继承，0 = 明确关闭分卷，正数 = 分卷大小。
    [Theory]
    [InlineData(null, 333L)]
    [InlineData(0L, 0L)]
    [InlineData(64L, 64L)]
    public void VolumeBytes_Has_Three_States(long? configured, long expected)
    {
        var r = ResolvedBackupSettings.From(new BackupConfig { VolumeBytes = configured }, Globals());
        Assert.Equal(expected, r.VolumeBytes);
    }

    // 规则字段三态：null = 继承，"" = 明确没有规则，有内容 = 覆盖。
    // 空串必须活下来，否则「我不要任何忽略规则」就无法表达。
    [Theory]
    [InlineData(null, "global-ignore")]
    [InlineData("", "")]
    [InlineData("x", "x")]
    public void Rules_Have_Three_States(string? configured, string expected)
    {
        var r = ResolvedBackupSettings.From(new BackupConfig { IgnoreRules = configured }, Globals());
        Assert.Equal(expected, r.IgnoreRules);
    }

    [Fact]
    public void Null_Settings_Falls_Back_To_GlobalSettings_Defaults()
    {
        var defaults = new GlobalSettings();
        var r = ResolvedBackupSettings.From(new BackupConfig(), null);
        Assert.Equal(defaults.DefaultMaxVersions, r.MaxVersions);
    }
}
