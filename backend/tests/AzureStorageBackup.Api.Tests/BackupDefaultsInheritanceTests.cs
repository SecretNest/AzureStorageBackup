using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>钉住「跟随而非快照」：配置字段为 null 时，改全局设置必须改变生效值。</summary>
[Trait("Category", "Integration")]
public class BackupDefaultsInheritanceTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record Effective(int MaxVersions, long? VolumeBytes, string? IgnoreRules);
    private sealed record ConfigDto(int Id, int? MaxVersions, StorageTier IndexTier, Effective Effective);

    private async Task<int> CreateAccountAsync()
    {
        var res = await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "inherit-" + Guid.NewGuid().ToString("N")[..8],
            Description: null,
            BlobEndpoint: "http://127.0.0.1:10000/devstoreaccount1",
            Region: AzureRegion.Global,
            AccountKey: "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<AccountResponse>())!.Id;
    }

    private async Task<ConfigDto> CreateConfigAsync(int accountId)
    {
        // 11 个可继承字段全部不传 = 全部继承。
        var res = await _client.PostAsJsonAsync("/api/backup-configs", new
        {
            AccountId = accountId,
            ContainerName = "c-" + Guid.NewGuid().ToString("N")[..8],
            Name = "inherit-test",
            LocalRoot = "/tmp",
            IndexTier = StorageTier.Hot,
            DataTier = StorageTier.Hot,
        });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<ConfigDto>())!;
    }

    private async Task SetGlobalMaxVersionsAsync(int value)
    {
        var current = await _client.GetFromJsonAsync<Dictionary<string, object?>>("/api/settings");
        current!["defaultMaxVersions"] = value;
        var res = await _client.PutAsJsonAsync("/api/settings", current);
        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Inherited_Field_Follows_A_Later_Change_To_The_Global_Setting()
    {
        var accountId = await CreateAccountAsync();
        var created = await CreateConfigAsync(accountId);

        Assert.Null(created.MaxVersions);              // 落库的是 null，不是快照
        await SetGlobalMaxVersionsAsync(42);

        var after = await _client.GetFromJsonAsync<ConfigDto>($"/api/backup-configs/{created.Id}");

        Assert.Null(after!.MaxVersions);               // 仍然是继承
        Assert.Equal(42, after.Effective.MaxVersions); // 生效值跟着变了
    }

    [Fact]
    public async Task Overridden_Field_Does_Not_Follow_The_Global_Setting()
    {
        var accountId = await CreateAccountAsync();
        var created = await CreateConfigAsync(accountId);

        var body = await _client.GetFromJsonAsync<Dictionary<string, object?>>(
            $"/api/backup-configs/{created.Id}");
        body!["maxVersions"] = 5;
        body["password"] = null;
        (await _client.PutAsJsonAsync($"/api/backup-configs/{created.Id}", body)).EnsureSuccessStatusCode();

        await SetGlobalMaxVersionsAsync(99);

        var after = await _client.GetFromJsonAsync<ConfigDto>($"/api/backup-configs/{created.Id}");

        Assert.Equal(5, after!.MaxVersions);
        Assert.Equal(5, after.Effective.MaxVersions);
    }

    // 本轮不得放松 tier 锁定：继承之所以不适用于 tier，正是因为这条约束。
    [Fact]
    public async Task Changing_A_Tier_After_Creation_Is_Still_Rejected()
    {
        var accountId = await CreateAccountAsync();
        var created = await CreateConfigAsync(accountId);

        var body = await _client.GetFromJsonAsync<Dictionary<string, object?>>(
            $"/api/backup-configs/{created.Id}");
        body!["dataTier"] = (int)StorageTier.Cool;
        body["password"] = null;

        var res = await _client.PutAsJsonAsync($"/api/backup-configs/{created.Id}", body);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}

/// <summary>映射器必须把解析后的值交给引擎——否则 API 显示跟随、实际备份仍用旧值。</summary>
public class BackupRequestMapperInheritanceTests
{
    private static readonly Account Account = new() { Id = 1, Name = "a", BlobEndpoint = "http://x" };

    private static GlobalSettings Globals() => new()
    {
        DefaultMaxVersions = 9,
        DefaultGroupCapBytes = 777,
        DefaultIncludeSymlinks = true,
        DefaultVerboseLogging = true,
        DefaultVolumeBytes = 555,
    };

    [Fact]
    public void From_Uses_The_Resolved_Values_For_Inherited_Fields()
    {
        var config = new BackupConfig { ContainerName = "c", LocalRoot = "/tmp", Name = "n" };

        var request = BackupRequestMapper.From(config, Account, password: null, Globals());

        Assert.Equal(777, request.Options.Plan.GroupCapBytes);
        Assert.True(request.Options.Scan.IncludeSymlinks);
        Assert.True(request.Options.VerboseLogging);
        Assert.Equal(9, request.Options.Retention.MaxVersions);
        Assert.Equal(555, request.Options.VolumeBytes);
    }

    [Fact]
    public void CleanupOf_Uses_The_Resolved_Retention()
    {
        var config = new BackupConfig { ContainerName = "c", LocalRoot = "/tmp", Name = "n" };

        var options = BackupRequestMapper.CleanupOf(config, Globals());

        Assert.Equal(9, options.Retention.MaxVersions);
        Assert.Equal(555, options.VolumeBytes);
    }

    // 0 是「明确关闭分卷」，不得回落到全局的 555。
    [Fact]
    public void VolumeBytes_Zero_Means_Off_Not_Inherit()
    {
        var config = new BackupConfig { ContainerName = "c", LocalRoot = "/tmp", Name = "n", VolumeBytes = 0 };

        Assert.Null(BackupRequestMapper.From(config, Account, null, Globals()).Options.VolumeBytes);
        Assert.Null(BackupRequestMapper.CleanupOf(config, Globals()).VolumeBytes);
    }
}
