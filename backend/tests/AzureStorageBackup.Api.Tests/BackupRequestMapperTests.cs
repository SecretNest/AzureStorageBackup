using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class BackupRequestMapperTests
{
    private static BackupConfig Config() => new()
    {
        AccountId = 1,
        ContainerName = "c",
        Name = "n",
        LocalRoot = "/data",
    };

    private static Account Account() => new() { Name = "a", BlobEndpoint = "http://x", AccountKeyProtected = TestSecrets.Protect("k") };

    [Fact]
    public void Maps_Upload_Concurrency_From_Settings()
    {
        var settings = new GlobalSettings { UploadConcurrency = 9 };

        var request = BackupRequestMapper.From(Config(), Account(), password: null, settings);

        Assert.Equal(9, request.Options.UploadConcurrency);
    }

    [Fact]
    public void Maps_Retry_Backoff_Sequence_From_Settings()
    {
        var settings = new GlobalSettings { RetryBackoffSeconds = "5,30,90,300", RetryMaxTotalMinutes = 120 };

        var request = BackupRequestMapper.From(Config(), Account(), password: null, settings);

        var schedule = RetryPolicy.DelaySchedule(request.Options.Upload).ToList();
        Assert.Equal(TimeSpan.FromSeconds(5), schedule[0]);
        Assert.Equal(TimeSpan.FromSeconds(300), schedule[3]);
        Assert.Equal(TimeSpan.FromSeconds(300), schedule[4]); // 之后每 300s
        var total = schedule.Aggregate(TimeSpan.Zero, (a, d) => a + d);
        Assert.True(total <= TimeSpan.FromHours(2));
    }

    [Fact]
    public void Without_Settings_Uses_Defaults()
    {
        var request = BackupRequestMapper.From(Config(), Account(), password: null);

        Assert.Equal(5, request.Options.UploadConcurrency);
    }

    [Fact]
    public void Blank_Backoff_Falls_Back_To_Count_Based_Retry()
    {
        var settings = new GlobalSettings { RetryBackoffSeconds = "   " };

        var request = BackupRequestMapper.From(Config(), Account(), password: null, settings);

        // 空序列 → 计数模式（Backoff 为空），DelaySchedule 产出 MaxAttempts-1 项。
        Assert.Null(request.Options.Upload.Backoff);
    }

    [Fact]
    public void Maps_Scope_Rules_Into_Scan_Options()
    {
        var config = Config();
        config.ScopeRules = "-\n+ photos";

        var request = BackupRequestMapper.From(config, Account(), password: null);

        Assert.True(request.Options.Scan.Scope.IsInScope("photos/a.jpg"));
        Assert.False(request.Options.Scan.Scope.IsInScope("music/b.mp3"));
    }

    [Fact]
    public void Scope_Rules_Are_Not_Inheritable_So_Null_Means_Everything()
    {
        // 其它规则字段的 null = 「继承全局默认」，这个字段的 null = 「全部包含」。
        // 这处不同是故意的（设计 §1），别顺手把它塞进 ResolvedBackupSettings。
        var config = Config();
        config.ScopeRules = null;

        var request = BackupRequestMapper.From(
            config, Account(), password: null,
            settings: new GlobalSettings { DefaultIgnoreRules = "*.tmp" });

        Assert.True(request.Options.Scan.Scope.IsAll);
    }
}
