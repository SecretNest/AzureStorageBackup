using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class AccountEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private IKeyringHealth Keyring => factory.Services.GetRequiredService<IKeyringHealth>();

    private static AccountRequest SampleRequest(string name = "prod") => new(
        Name: name,
        Description: "primary",
        BlobEndpoint: "https://prod.blob.core.windows.net",
        Region: AzureRegion.Global,
        AccountKey: "dGVzdGtleQ==",
        UseProxy: false,
        ProxyMode: ProxyMode.Independent,
        ProxyHost: null,
        ProxyPort: null,
        ProxyUsername: null,
        ProxyPassword: null);

    [Fact]
    public async Task Post_Creates_Account_And_Returns_201()
    {
        var res = await _client.PostAsJsonAsync("/api/accounts", SampleRequest());

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.NotNull(created);
        Assert.True(created!.Id > 0);
        Assert.Equal("prod", created.Name);
    }

    [Fact]
    public async Task Post_Then_Get_Returns_Account()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("get-test"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        var get = await _client.GetAsync($"/api/accounts/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal("get-test", fetched!.Name);
    }

    [Fact]
    public async Task Response_Does_Not_Expose_Secrets()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("secret-test"));
        var body = await post.Content.ReadAsStringAsync();

        Assert.DoesNotContain("dGVzdGtleQ==", body);
        Assert.DoesNotContain("accountKey", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// F5：/test-connection 缺了 POST / 的空 key 校验。空 key → AccountKeyProtected = ""，
    /// 到解密咽喉处抛 SecretUnavailableException，被深度防御映射成 409 keyring_lost——
    /// 用户看到的是「密钥环解不开」，而真实原因只是没填 key。必须是 400 + 明确文案。
    /// </summary>
    [Fact]
    public async Task TestConnection_Rejects_Empty_Key_Instead_Of_Blaming_The_Keyring()
    {
        foreach (var key in new[] { "", "   " })
        {
            var res = await _client.PostAsJsonAsync(
                "/api/accounts/test-connection", SampleRequest("test-conn-empty-key") with { AccountKey = key });

            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.Contains("AccountKey is required.", body);
            Assert.DoesNotContain("keyring", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Get_Missing_Returns_404()
    {
        var res = await _client.GetAsync("/api/accounts/999999");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Delete_Removes_Account()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("del-test"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        var del = await _client.DeleteAsync($"/api/accounts/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await _client.GetAsync($"/api/accounts/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    /// <summary>
    /// 还被备份占用的账户不能删。库里 <c>BackupConfig.AccountId</c> 没有外键约束，删了不会报错，
    /// 只会留下一批指向空号的孤儿配置——而它们要到下次真跑起来才炸（<c>BackupRunner</c>/
    /// <c>CheckRunner</c>/<c>RestoreRunner</c> 三处的 "Account {id} not found"）。定时任务的话
    /// 就是半夜失败、第二天才看见；还原那条更糟，等到真要恢复数据时才发现配置是坏的。
    /// </summary>
    [Fact]
    public async Task Delete_Is_Refused_While_A_Backup_Still_Uses_The_Account()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("del-in-use"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BackupConfigs.Add(new BackupConfig
            {
                AccountId = created!.Id, ContainerName = "photos", Name = "Photos", LocalRoot = "/data/photos",
            });
            db.BackupConfigs.Add(new BackupConfig
            {
                AccountId = created.Id, ContainerName = "docs", Name = "Documents", LocalRoot = "/data/docs",
            });
            await db.SaveChangesAsync();
        }

        var del = await _client.DeleteAsync($"/api/accounts/{created!.Id}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);

        // 占用者的名字要说出来——只说"删不了"，用户还得自己一个个翻备份去找是谁占着。
        var body = await del.Content.ReadAsStringAsync();
        Assert.Contains("Documents", body);
        Assert.Contains("Photos", body);

        // 而且真的没删。
        var get = await _client.GetAsync($"/api/accounts/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    /// <summary>占用信息要随账户一起下发，界面才能在渲染那一刻就把删除按钮禁掉并说明原因。</summary>
    [Fact]
    public async Task Get_Reports_Which_Backups_Use_The_Account()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("usage-report"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        // 刚建出来的账户没人用。
        Assert.Empty(created!.UsedByBackups);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 故意按倒序插入：响应里必须是排好序的，否则同一个页面每刷新一次悬浮提示就换个样。
            db.BackupConfigs.Add(new BackupConfig
            {
                AccountId = created.Id, ContainerName = "z", Name = "Zeta", LocalRoot = "/data/z",
            });
            db.BackupConfigs.Add(new BackupConfig
            {
                AccountId = created.Id, ContainerName = "a", Name = "Alpha", LocalRoot = "/data/a",
            });
            await db.SaveChangesAsync();
        }

        var get = await _client.GetAsync($"/api/accounts/{created.Id}");
        var fetched = await get.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal(["Alpha", "Zeta"], fetched!.UsedByBackups);

        // 列表页走的是另一条（批量）路径，同样要带上。
        var list = await _client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts");
        Assert.Equal(["Alpha", "Zeta"], list!.Single(a => a.Id == created.Id).UsedByBackups);
    }

    /// <summary>
    /// 编辑态的连通测试：Key 框留空表示"沿用现有凭据"，此时不能像不带 id 的那个端点一样
    /// 甩一个 400 回来——"改了 endpoint 或代理，想先测一下现有 key 还连不连得上"正是编辑时
    /// 最该能做的事。
    /// </summary>
    [Fact]
    public async Task TestConnection_For_An_Existing_Account_Accepts_A_Blank_Key()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("test-conn-by-id"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        var res = await _client.PostAsJsonAsync(
            $"/api/accounts/{created!.Id}/test-connection", SampleRequest("test-conn-by-id") with { AccountKey = "" });

        // 连不连得上取决于这个假 endpoint（连不上是必然的），但**不能**是"你没填 key"那种拒绝：
        // 走到这一步就说明库里的密文被正确地搬了过来。
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("AccountKey is required.", body);
    }

    [Fact]
    public async Task TestConnection_For_A_Missing_Account_Returns_404()
    {
        var res = await _client.PostAsJsonAsync(
            "/api/accounts/999999/test-connection", SampleRequest() with { AccountKey = "" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Put_Updates_Name()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("before"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        var update = SampleRequest("after") with { AccountKey = null }; // 不改 key
        var put = await _client.PutAsJsonAsync($"/api/accounts/{created!.Id}", update);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await _client.GetAsync($"/api/accounts/{created.Id}");
        var fetched = await get.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal("after", fetched!.Name);
    }

    /// <summary>
    /// 密钥环丢失时 PUT 一个不改 AccountKey 的账户，会走"保留原密文"分支——
    /// 而那份密文恰恰是密钥环丢失时解不开的。响应必须如实标 SecretsUnavailable=true，
    /// 否则 UI 会看起来一切正常，而 /api/system/keyring 却同时把它计入待处理，自相矛盾。
    /// </summary>
    [Fact]
    public async Task Put_While_Keyring_Lost_Reports_SecretsUnavailable_True()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("keyring-lost-put"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        // /keys 丢失：库里的密文换成另一套密钥环的产物。标记按逐条实际可解性判定（设计 §3.3），
        // 只翻转 IKeyringHealth 而不动密文，是「密钥环还在、状态被误设」而非真的丢失。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.FirstAsync(a => a.Id == created!.Id)).AccountKeyProtected = TestSecrets.Stale("old-key");
            await db.SaveChangesAsync();
        }

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var update = SampleRequest("keyring-lost-put-renamed") with { AccountKey = null }; // 留空，触发保留原密文分支
            var put = await _client.PutAsJsonAsync($"/api/accounts/{created!.Id}", update);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var body = await put.Content.ReadFromJsonAsync<AccountResponse>();
            Assert.True(body!.SecretsUnavailable);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }
}
