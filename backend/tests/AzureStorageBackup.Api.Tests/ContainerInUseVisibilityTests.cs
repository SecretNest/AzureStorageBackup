using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 「这个 container 上已经有备份了」的判定，从前**只**看云端信息文件在不在
/// （<see cref="ContainerService"/> → <see cref="BackupDiscovery"/>）。而那个文件是备份的最后一步
/// 才写的（BackupOrchestrator 的 Finalize），于是首次备份跑到一半时，容器列表把它报成空容器——
/// 用户照着这份列表把同一个 container 又配给了第二条备份，两边各写各的索引互相覆盖。
/// <para>
/// 占用的权威在本地：库里那条 <see cref="BackupConfig"/> 从创建的那一刻就存在，不必等任何云端产物。
/// 备份进行中、备份失败留下半成品、云端一时读不到——三种情况一并被它覆盖。
/// </para>
/// </summary>
public class ContainerInUseVisibilityTests
{
    /// <summary>按名字给出预设的云端存在情况，不碰真的 Azure。</summary>
    private sealed class StubContainerService(params ContainerInfo[] listed) : IContainerService
    {
        public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(Account a, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContainerInfo>>(listed);

        public Task CreateContainerAsync(Account a, string name, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteContainerAsync(Account a, string name, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static async Task<int> CreateAccountAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "inuse-" + Guid.NewGuid().ToString("N")[..8],
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

    private static (TestWebAppFactory Factory, HttpClient Client) Rig(params ContainerInfo[] listed)
    {
        var factory = new TestWebAppFactory();
        var configured = factory.WithWebHostBuilder(b => b.ConfigureServices(
            s => s.AddSingleton<IContainerService>(new StubContainerService(listed))));
        return (factory, configured.CreateClient());
    }

    private static async Task AddConfigAsync(TestWebAppFactory factory, int accountId, string container, string name)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().CreateAsync(new BackupConfig
        {
            AccountId = accountId,
            ContainerName = container,
            Name = name,
            LocalRoot = "/data/" + container,
        });
    }

    /// <summary>用户实际踩到的那一幕：首次备份还没写出信息文件，容器却已经装着这一轮上传的数据。</summary>
    [Fact]
    public async Task A_Container_Held_By_A_Local_Config_Is_In_Use_Even_With_No_Cloud_Index_Yet()
    {
        var (factory, client) = Rig(new ContainerInfo("mid-backup", BackupPresence.None));
        using var _ = factory;

        var accountId = await CreateAccountAsync(client);
        await AddConfigAsync(factory, accountId, "mid-backup", "Photos");

        var list = await client.GetFromJsonAsync<List<ContainerInfo>>($"/api/accounts/{accountId}/containers");

        var row = Assert.Single(list!);
        // 点名是谁占着：光说"不能用"，用户不知道该去动哪条备份。
        Assert.Equal("Photos", row.InUseBy);
    }

    /// <summary>占用按 (账户, container) 精确限定，不同账户下的同名容器互不干涉。</summary>
    [Fact]
    public async Task A_Config_In_Another_Account_Does_Not_Mark_The_Container()
    {
        var (factory, client) = Rig(new ContainerInfo("shared-name", BackupPresence.None));
        using var _ = factory;

        var held = await CreateAccountAsync(client);
        var other = await CreateAccountAsync(client);
        await AddConfigAsync(factory, held, "shared-name", "Held");

        var list = await client.GetFromJsonAsync<List<ContainerInfo>>($"/api/accounts/{other}/containers");

        Assert.Null(Assert.Single(list!).InUseBy);
    }

    /// <summary>云端判定原样保留：本地没有配置的容器，该是什么 presence 还是什么 presence。</summary>
    [Fact]
    public async Task Cloud_Presence_Still_Reported_For_Containers_No_Config_Holds()
    {
        var (factory, client) = Rig(
            new ContainerInfo("orphan-backup", BackupPresence.Plain),
            new ContainerInfo("really-empty", BackupPresence.None));
        using var _ = factory;

        var accountId = await CreateAccountAsync(client);

        var list = await client.GetFromJsonAsync<List<ContainerInfo>>($"/api/accounts/{accountId}/containers");

        var orphan = list!.Single(c => c.Name == "orphan-backup");
        Assert.Equal(BackupPresence.Plain, orphan.Backup);
        Assert.Null(orphan.InUseBy);
        Assert.Null(list!.Single(c => c.Name == "really-empty").InUseBy);
    }
}
