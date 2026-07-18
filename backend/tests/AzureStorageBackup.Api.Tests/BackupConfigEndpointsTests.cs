using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class BackupConfigEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static BackupConfigRequest SampleRequest(string name = "photos") => new(
        AccountId: 1,
        ContainerName: "photos",
        Name: name,
        Description: "family",
        LocalRoot: "/data/photos",
        Password: "s3cret",
        IndexTier: StorageTier.Hot,
        DataTier: StorageTier.Cool,
        IgnoreRules: "*.tmp",
        DontCompressRules: null,
        DontGroupRules: null,
        IncludeSymlinks: false,
        MaxVersions: 50,
        MaxAgeDays: 180,
        RetentionMode: RetentionMode.EitherTriggers,
        SingleFileThresholdBytes: 5_000_000,
        GroupCapBytes: 100_000_000);

    [Fact]
    public async Task Post_Creates_Config_And_Hides_Password()
    {
        var res = await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest());

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.True(created!.Id > 0);
        Assert.Equal("photos", created.Name);
        Assert.True(created.HasPassword); // 有加密密码，但不回传明文

        var body = await (await _client.GetAsync($"/api/backup-configs/{created.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret", body);
    }

    [Fact]
    public async Task Post_Without_LocalRoot_Returns_400()
    {
        var res = await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest() with { LocalRoot = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_With_Empty_Password_Keeps_Existing()
    {
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("keep-pw")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PutAsJsonAsync($"/api/backup-configs/{created!.Id}",
            SampleRequest("keep-pw") with { Password = null, Name = "renamed" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var updated = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal("renamed", updated!.Name);
        Assert.True(updated.HasPassword); // 密码保留
    }

    [Fact]
    public async Task Delete_Removes_Config()
    {
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("del")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/backup-configs/{created!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{created.Id}")).StatusCode);
    }

    // Azurite 的 well-known 账户与密钥（与其它集成测试一致，见 BackupRunEndpointsTests）。
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Delete_Config_Optionally_Deletes_Cloud_Container()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var containerName = "del-" + Guid.NewGuid().ToString("N")[..8];

        var accountReq = new AccountRequest("azurite", null, AzuriteEndpoint, AzureRegion.Global,
            AzuriteKey, false, ProxyMode.Independent, null, null, null, null);
        var account = await (await _client.PostAsJsonAsync("/api/accounts", accountReq))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var factoryClient = new BlobClientFactory();
        var azuriteAccount = new Account { BlobEndpoint = AzuriteEndpoint, AccountKey = AzuriteKey, Region = AzureRegion.Global };
        var cc = factoryClient.CreateServiceClient(azuriteAccount).GetBlobContainerClient(containerName);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // deleteContainer=false（默认）：本地配置删除，云端 container 仍在。
            var config1 = await (await _client.PostAsJsonAsync("/api/backup-configs",
                    SampleRequest("del-keep") with { AccountId = account!.Id, ContainerName = containerName }))
                .Content.ReadFromJsonAsync<BackupConfigResponse>();

            Assert.Equal(HttpStatusCode.NoContent,
                (await _client.DeleteAsync($"/api/backup-configs/{config1!.Id}?deleteContainer=false")).StatusCode);
            Assert.True((await cc.ExistsAsync()).Value);

            // deleteContainer=true：另建一个指向同 container 的配置，删除时云端 container 一并被删。
            var config2 = await (await _client.PostAsJsonAsync("/api/backup-configs",
                    SampleRequest("del-purge") with { AccountId = account.Id, ContainerName = containerName }))
                .Content.ReadFromJsonAsync<BackupConfigResponse>();

            Assert.Equal(HttpStatusCode.NoContent,
                (await _client.DeleteAsync($"/api/backup-configs/{config2!.Id}?deleteContainer=true")).StatusCode);
            Assert.False((await cc.ExistsAsync()).Value);
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }

    [Fact]
    public async Task New_Config_Reports_Normal_Status_And_Idle_Activity()
    {
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("status-idle")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        Assert.Equal(BackupStatus.Normal, created!.Status);
        Assert.Null(created.LastError);
        Assert.Equal("Idle", created.Activity);

        var fetched = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal("Idle", fetched!.Activity);
    }

    [Fact]
    public async Task Busy_Config_Reports_Checking_Activity_In_List_And_Detail()
    {
        var req = SampleRequest("status-busy") with { ContainerName = "busy-container" };
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", req))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        // 模拟 /check 端点持有的忙碌锁（无专属 runner，靠 BackupBusyTracker 兜底判定 Checking）。
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(created!.AccountId, created.ContainerName));
        try
        {
            var list = await (await _client.GetAsync("/api/backup-configs"))
                .Content.ReadFromJsonAsync<List<BackupConfigResponse>>();
            Assert.Equal("Checking", list!.Single(c => c.Id == created.Id).Activity);

            var single = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
                .Content.ReadFromJsonAsync<BackupConfigResponse>();
            Assert.Equal("Checking", single!.Activity);
        }
        finally
        {
            busy.Release(created.AccountId, created.ContainerName);
        }

        var afterRelease = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal("Idle", afterRelease!.Activity);
    }

    [Fact]
    public async Task Failed_Operation_Sets_Error_And_Reset_Status_Clears_It()
    {
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("status-error")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        // 模拟某个 runner 写状态点因操作失败落库 Error（决策 2）。
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .SetErrorAsync(created!.Id, "simulated failure");

        var afterFailure = await (await _client.GetAsync($"/api/backup-configs/{created!.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(BackupStatus.Error, afterFailure!.Status);
        Assert.Equal("simulated failure", afterFailure.LastError);
        Assert.NotNull(afterFailure.LastErrorAt);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PostAsync($"/api/backup-configs/{created.Id}/reset-status", null)).StatusCode);

        var afterReset = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(BackupStatus.Normal, afterReset!.Status);
        Assert.Null(afterReset.LastError);
    }

    [Fact]
    public async Task Reset_Status_On_Missing_Config_Returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsync("/api/backup-configs/999999/reset-status", null)).StatusCode);
    }
}
