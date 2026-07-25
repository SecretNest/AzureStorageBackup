using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Task 5 复审 Finding 1/3：密钥环丢失时，真正状态变更/依赖凭据的三个触发点
/// （/run、/restore、/repair）必须在入口即 409，而只读的列表端点仍须可达
/// （否则整个「恢复模式」功能失去意义）。KeyringGuardTests 只测了静态方法本身，
/// 这里驱动真实 HTTP 请求，防止有人漏挂闸门却测试仍然全绿。
/// </summary>
public class KeyringGateEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record KeyringLostError(string error, string code);

    private static BackupConfigRequest SampleRequest(string name) => new(
        AccountId: 1,
        ContainerName: name + "-container",
        Name: name,
        Description: null,
        LocalRoot: "/data/" + name,
        Password: "s3cret",
        IndexTier: StorageTier.Hot,
        DataTier: StorageTier.Cool,
        IgnoreRules: null,
        DontCompressRules: null,
        DontGroupRules: null,
        IncludeSymlinks: false,
        MaxVersions: 50,
        MaxAgeDays: 180,
        RetentionMode: RetentionMode.EitherTriggers,
        SingleFileThresholdBytes: 5_000_000,
        GroupCapBytes: 100_000_000);

    private async Task<BackupConfigResponse> CreateConfigAsync(string name)
        => (await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest(name)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>())!;

    private IKeyringHealth Keyring => factory.Services.GetRequiredService<IKeyringHealth>();

    [Fact]
    public async Task Run_Restore_Repair_Return_409_KeyringLost_When_Keyring_Is_Lost()
    {
        var created = await CreateConfigAsync("gate-run-restore-repair");

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var run = await _client.PostAsync($"/api/backup-configs/{created.Id}/run", null);
            Assert.Equal(HttpStatusCode.Conflict, run.StatusCode);
            var runBody = await run.Content.ReadFromJsonAsync<KeyringLostError>();
            Assert.Equal("keyring_lost", runBody!.code);

            var restore = await _client.PostAsJsonAsync($"/api/backup-configs/{created.Id}/restore",
                new RestoreRequestBody(null, null));
            Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
            var restoreBody = await restore.Content.ReadFromJsonAsync<KeyringLostError>();
            Assert.Equal("keyring_lost", restoreBody!.code);

            var repair = await _client.PostAsync($"/api/backup-configs/{created.Id}/repair", null);
            Assert.Equal(HttpStatusCode.Conflict, repair.StatusCode);
            var repairBody = await repair.Content.ReadFromJsonAsync<KeyringLostError>();
            Assert.Equal("keyring_lost", repairBody!.code);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }

    /// <summary>
    /// 全分支复审 Finding 2：计划任务的手动触发（"Run now"）是备份/检查/清理的入口之一，
    /// 与 /backup-configs/{id}/run 同性质。缺闸门时 dispatcher 内解密备份密码抛出、被吞成日志，
    /// 端点仍推进 LastRunAt 并返回 200——UI 显示成功而实际什么都没做。
    /// 必须 409，且 LastRunAt 必须保持未推进。
    /// </summary>
    [Fact]
    public async Task Manual_Task_Run_Returns_409_KeyringLost_And_Does_Not_Advance_LastRunAt()
    {
        var task = (await (await _client.PostAsJsonAsync("/api/tasks", new TaskRequest(
                TaskTargetKind.Backup, 1, "gate-task-run-container", null,
                ScheduledTaskType.Backup, "0 3 * * *", true)))
            .Content.ReadFromJsonAsync<TaskResponse>())!;
        Assert.Null(task.LastRunAt);

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var res = await _client.PostAsync($"/api/tasks/{task.Id}/run", null);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("keyring_lost", (await res.Content.ReadFromJsonAsync<KeyringLostError>())!.code);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }

        var after = await (await _client.GetAsync($"/api/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<TaskResponse>();
        Assert.Null(after!.LastRunAt);
    }

    /// <summary>
    /// 全分支复审 Finding 4/5：列容器在设计 §3.1 里就被点名为「需要凭据的动作」，
    /// 而连删云端 container 的删除分支同样需要账户密钥。二者此前都没有闸门，密钥环丢失时
    /// 一路走到 SecretReader 抛异常，客户端拿到裸 500。必须 409 keyring_lost。
    /// </summary>
    [Fact]
    public async Task Container_Listing_And_Cloud_Deleting_Delete_Return_409_KeyringLost()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
                "gate-containers", null, "https://gate.blob.core.windows.net", AzureRegion.Global,
                "dGVzdGtleQ==", false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>())!;
        var config = await CreateConfigAsync("gate-delete-container");

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var list = await _client.GetAsync($"/api/accounts/{account.Id}/containers");
            Assert.Equal(HttpStatusCode.Conflict, list.StatusCode);
            Assert.Equal("keyring_lost", (await list.Content.ReadFromJsonAsync<KeyringLostError>())!.code);

            var create = await _client.PostAsJsonAsync($"/api/accounts/{account.Id}/containers", new { name = "x" });
            Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);

            var dropContainer = await _client.DeleteAsync($"/api/accounts/{account.Id}/containers/x");
            Assert.Equal(HttpStatusCode.Conflict, dropContainer.StatusCode);

            var dropCloud = await _client.DeleteAsync($"/api/backup-configs/{config.Id}?deleteContainer=true");
            Assert.Equal(HttpStatusCode.Conflict, dropCloud.StatusCode);
            Assert.Equal("keyring_lost", (await dropCloud.Content.ReadFromJsonAsync<KeyringLostError>())!.code);

            // 纯本地删除必须仍然放行：决策 6 下这是「想不起备份密码」的唯一出口。
            var dropLocal = await _client.DeleteAsync($"/api/backup-configs/{config.Id}");
            Assert.Equal(HttpStatusCode.NoContent, dropLocal.StatusCode);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }

    /// <summary>
    /// 深度防御（设计 §3.1）：密钥环在进程运行期间被换掉，canary 还没重新判定，
    /// 于是全局状态仍是 Healthy、闸门放行，解密在咽喉处才失败。没有映射时客户端拿到裸 500
    /// （Program.cs 未注册任何异常处理中间件）。必须仍是 409 keyring_lost。
    /// 这条同时把闸门与映射区分开：状态是 Healthy，KeyringGuard 根本不会触发。
    /// </summary>
    [Fact]
    public async Task Undecryptable_Secret_Maps_To_409_Even_While_Status_Is_Healthy()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
                "gate-deep-defence", null, "https://deep.blob.core.windows.net", AzureRegion.Global,
                "dGVzdGtleQ==", false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>())!;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.FirstAsync(a => a.Id == account.Id)).AccountKeyProtected = TestSecrets.Stale("swapped-out");
            await db.SaveChangesAsync();
        }

        Assert.Equal(KeyringStatus.Healthy, Keyring.Status);

        var res = await _client.GetAsync($"/api/accounts/{account.Id}/containers");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("keyring_lost", (await res.Content.ReadFromJsonAsync<KeyringLostError>())!.code);
    }

    /// <summary>
    /// 全分支复审 Finding 5：/import 的宽泛 catch 把「读不了账户密钥」说成
    /// "Could not read info file (wrong password?)"，把密钥环问题赖到用户输的密码头上。
    /// 必须给出指向账户凭据的提示。
    /// </summary>
    [Fact]
    public async Task Import_Blames_Account_Credentials_Not_The_Password_When_The_Key_Is_Undecryptable()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
                "gate-import", null, "https://import.blob.core.windows.net", AzureRegion.Global,
                "dGVzdGtleQ==", false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>())!;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.FirstAsync(a => a.Id == account.Id)).AccountKeyProtected = TestSecrets.Stale("lost-key");
            await db.SaveChangesAsync();
        }

        var res = await _client.PostAsJsonAsync("/api/backup-configs/import",
            new ImportRequest(account.Id, "gate-import-container", "some-password"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Re-enter this account's credentials first.", body!["error"]);
    }

    /// <summary>整个恢复模式的存在意义：即便密钥环丢失，只读列表端点也不能跟着一起 409。</summary>
    [Fact]
    public async Task List_Endpoint_Still_Returns_200_When_Keyring_Is_Lost()
    {
        await CreateConfigAsync("gate-list-still-works");

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var res = await _client.GetAsync("/api/backup-configs");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var list = await res.Content.ReadFromJsonAsync<List<BackupConfigResponse>>();
            Assert.NotEmpty(list!);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }
}
