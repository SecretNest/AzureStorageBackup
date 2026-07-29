using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 从 Account → Containers 那个页面删掉一个还挂着备份的 container，会让云端数据消失而本地那条
/// <see cref="BackupConfig"/> 原封不动：备份列表里于是继续显示一个后面什么都没有的备份，
/// 而点进去的每一个操作都会以各种形状失败。这是用户实际踩到的。
/// <para>
/// 删备份那条路（<c>DELETE /api/backups/{id}?deleteContainer=true</c>）本来就做对了：它连本地
/// 索引缓存、备份状态、操作日志一并清掉，还挡住"正在跑操作时删除"。所以这里不是补一套新的清理
/// 逻辑，而是把这条绕过它的近路堵上、并把用户指回正道。
/// </para>
/// </summary>
public class ContainerDeleteGuardTests
{
    private sealed record ErrorBody(string error);

    /// <summary>记录删除调用，好断言"云端根本没被碰过"。</summary>
    private sealed class RecordingContainerService : IContainerService
    {
        public List<string> Deleted { get; } = [];

        public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(Account a, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContainerInfo>>([]);

        public Task CreateContainerAsync(Account a, string name, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteContainerAsync(Account a, string name, CancellationToken ct = default)
        {
            Deleted.Add(name);
            return Task.CompletedTask;
        }
    }

    private static async Task<int> CreateAccountAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "guard-" + Guid.NewGuid().ToString("N")[..8],
            Description: null,
            BlobEndpoint: "http://127.0.0.1:10000/devstoreaccount1",
            Region: AzureRegion.Global,
            AccountKey: "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null,
            ProxyPort: null,
            ProxyUsername: null,
            ProxyPassword: null));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<AccountResponse>())!.Id;
    }

    private static (TestWebAppFactory Factory, HttpClient Client, RecordingContainerService Containers) Rig()
    {
        var factory = new TestWebAppFactory();
        var recorder = new RecordingContainerService();
        var configured = factory.WithWebHostBuilder(
            b => b.ConfigureServices(s => s.AddSingleton<IContainerService>(recorder)));
        return (factory, configured.CreateClient(), recorder);
    }

    [Fact]
    public async Task Deleting_A_Container_That_Still_Holds_A_Backup_Is_Refused_Without_Touching_Azure()
    {
        var (factory, client, containers) = Rig();
        using var _ = factory;

        var accountId = await CreateAccountAsync(client);
        const string container = "guarded-container";

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().CreateAsync(new BackupConfig
            {
                AccountId = accountId,
                ContainerName = container,
                Name = "Photos",
                LocalRoot = "/data/photos",
            });
        }

        var res = await client.DeleteAsync($"/api/accounts/{accountId}/containers/{container}");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        // 要害：护栏必须在**触云之前**生效。先删了再报错，数据已经没了，报什么都晚了。
        Assert.Empty(containers.Deleted);

        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.NotNull(body);
        // 错误信息得点名是哪个备份挡着，并指出正道——否则用户只知道"不让删"，不知道下一步该做什么。
        Assert.Contains("Photos", body!.error);
        Assert.Contains("backup", body.error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_A_Container_With_No_Backup_Still_Works()
    {
        var (factory, client, containers) = Rig();
        using var _ = factory;

        var accountId = await CreateAccountAsync(client);

        var res = await client.DeleteAsync($"/api/accounts/{accountId}/containers/spare-container");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(["spare-container"], containers.Deleted);
    }

    /// <summary>
    /// 护栏按 (account, container) 精确限定。<see cref="BackupConfig"/> 在这两列上有唯一索引，
    /// 不同账户下可以有同名 container——按名字一刀切会让 A 账户的备份挡住 B 账户里同名的空 container。
    /// </summary>
    [Fact]
    public async Task A_Backup_In_One_Account_Does_Not_Guard_The_Same_Name_In_Another()
    {
        var (factory, client, containers) = Rig();
        using var _ = factory;

        var guarded = await CreateAccountAsync(client);
        var other = await CreateAccountAsync(client);
        const string container = "shared-name";

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().CreateAsync(new BackupConfig
            {
                AccountId = guarded,
                ContainerName = container,
                Name = "Guarded",
                LocalRoot = "/data/guarded",
            });
        }

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.DeleteAsync($"/api/accounts/{guarded}/containers/{container}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/accounts/{other}/containers/{container}")).StatusCode);
        Assert.Equal([container], containers.Deleted);
    }
}
