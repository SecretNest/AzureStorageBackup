using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 两条备份配置指到同一个 (账户, container) 上，就是两套互不知情的版本号与索引写在同一个地方：
/// 后跑的那一条读到的云端信息文件要么还没写出来、要么是别人的，于是从 version 1 重新开始，
/// 把对方的 index.json 覆盖掉，对方的数据 blob 变成孤儿、下一轮保留清理就把它们删了。
/// <para>
/// 所以创建与导入都必须在**写库之前**拒绝，库上再加唯一索引兜住绕过端点的那条路。
/// </para>
/// </summary>
public class DuplicateBackupConfigTests
{
    private sealed record ErrorBody(string error);

    private static async Task<int> CreateAccountAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "dup-" + Guid.NewGuid().ToString("N")[..8],
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

    private static BackupConfigRequest Request(int accountId, string container, string name, string localRoot) =>
        new(accountId, container, name, null, localRoot, null,
            StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000);

    [Fact]
    public async Task A_Second_Backup_On_The_Same_Container_Is_Refused()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var accountId = await CreateAccountAsync(client);

        var first = await client.PostAsJsonAsync("/api/backup-configs",
            Request(accountId, "one-container", "Photos", "/data/photos"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/backup-configs",
            Request(accountId, "one-container", "Documents", "/data/documents"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<ErrorBody>();
        // 点名占着这个 container 的是谁——否则用户只知道"不让建"，不知道该去看哪条备份。
        Assert.Contains("Photos", body!.error);

        // 要害：拒绝必须发生在写库之前，库里只能留下第一条。
        using var scope = factory.Services.CreateScope();
        var configs = await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().ListAsync();
        Assert.Equal(["Photos"], configs.Select(c => c.Name));
    }

    /// <summary>导入同样要挡，而且判定要排在读云之前：本地就能回答的问题，不该先花一趟网络。</summary>
    [Fact]
    public async Task Importing_Into_A_Container_A_Config_Already_Holds_Is_Refused()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var accountId = await CreateAccountAsync(client);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/backup-configs",
            Request(accountId, "taken-container", "Photos", "/data/photos"))).StatusCode);

        var res = await client.PostAsJsonAsync("/api/backup-configs/import",
            new ImportRequest(accountId, "taken-container", null));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("Photos", (await res.Content.ReadFromJsonAsync<ErrorBody>())!.error);
    }

    [Fact]
    public async Task The_Same_Container_Name_Under_A_Different_Account_Is_Allowed()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var one = await CreateAccountAsync(client);
        var two = await CreateAccountAsync(client);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/backup-configs",
            Request(one, "shared-name", "First", "/data/first"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/backup-configs",
            Request(two, "shared-name", "Second", "/data/second"))).StatusCode);
    }

    /// <summary>
    /// 端点的判定是「先查再写」，两次请求撞在一起时中间有窗口。库上的唯一索引是兜底：
    /// 绕过端点直接写也好、并发挤进窗口也好，第二条都落不了地。
    /// </summary>
    [Fact]
    public async Task The_Database_Itself_Rejects_A_Duplicate_Written_Behind_The_Service()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var accountId = await CreateAccountAsync(client);

        using var scope = factory.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        await configs.CreateAsync(new BackupConfig
        {
            AccountId = accountId, ContainerName = "sealed-container", Name = "First", LocalRoot = "/data/first",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => configs.CreateAsync(new BackupConfig
        {
            AccountId = accountId, ContainerName = "sealed-container", Name = "Second", LocalRoot = "/data/second",
        }));
    }
}
