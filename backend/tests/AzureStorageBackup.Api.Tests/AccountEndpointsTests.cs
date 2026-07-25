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
