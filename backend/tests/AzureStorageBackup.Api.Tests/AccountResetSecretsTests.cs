using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AzureStorageBackup.Api.Tests;

public class AccountResetSecretsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    // Azurite 默认账户密钥（devstoreaccount1），与 BackupOrchestratorTests 一致。
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    // 格式合法（base64、同长度）但不是 Azurite 真实密钥——用于触发"验证失败"路径，
    // 而不是"格式错误"路径。
    private const string WrongButValidKey =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    private readonly HttpClient _client = factory.CreateClient();

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    /// <summary>连通验证恒通过的桩：把「要连云」那一步跨过去，专注断言代理密码的清空语义
    /// （真连云的两条用例在下面，各自打真实 Azurite）。</summary>
    private sealed class AlwaysConnects : IBlobClientFactory
    {
        public BlobServiceClient CreateServiceClient(Account account) => throw new NotSupportedException();

        public Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default)
            => Task.FromResult(new ConnectionResult(true, null));
    }

    private sealed class StubbedFactory(Action<IServiceCollection> configure) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(configure); // 在 Program.cs 的注册之后执行，故能覆盖
        }
    }

    private static AccountRequest SampleRequest(string name, string blobEndpoint, string accountKey) => new(
        Name: name,
        Description: "reset-secrets test",
        BlobEndpoint: blobEndpoint,
        Region: AzureRegion.Global,
        AccountKey: accountKey,
        UseProxy: false,
        ProxyMode: ProxyMode.Independent,
        ProxyHost: null,
        ProxyPort: null,
        ProxyUsername: null,
        ProxyPassword: null);

    [Fact]
    public async Task Rejects_Empty_Key()
    {
        var res = await _client.PostAsJsonAsync(
            "/api/accounts/1/reset-secrets", new { accountKey = "", proxyPassword = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Returns_404_For_Unknown_Account()
    {
        var res = await _client.PostAsJsonAsync(
            "/api/accounts/999999/reset-secrets", new { accountKey = "dGVzdA==", proxyPassword = (string?)null });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    /// <summary>
    /// 设计决策 5：新凭据必须先连云验证通过才落库。这里对真实 Azurite 发起验证，
    /// 断言密文确实被替换，并且新密文用应用的加密服务能解出真实密钥——
    /// 如果实现跳过验证直接写入，这条测试依然会通过（因为凭据本来就对），
    /// 所以配合下面"验证失败"的用例一起才能证明验证真的挡在写库之前。
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Reset_Succeeds_And_Persists_New_Ciphertext_When_Verification_Passes()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var post = await _client.PostAsJsonAsync(
            "/api/accounts",
            SampleRequest("reset-ok", "http://127.0.0.1:10000/devstoreaccount1", "placeholder-not-yet-verified"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        var reset = await _client.PostAsJsonAsync(
            $"/api/accounts/{created!.Id}/reset-secrets",
            new { accountKey = AzuriteKey, proxyPassword = (string?)null });

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var row = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == created.Id);

        Assert.Equal(AzuriteKey, TestSecrets.Reveal(encryption, row.AccountKeyProtected));
    }

    /// <summary>
    /// 验证失败必须是"不落库"，而不是"落库了错误值"。用一个格式合法但错误的密钥
    /// 打真实 Azurite，断言 400 且原密文原封不动（仍能解出创建时的值）。
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Reset_Fails_And_Leaves_Stored_Ciphertext_Untouched_When_Verification_Fails()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var post = await _client.PostAsJsonAsync(
            "/api/accounts",
            SampleRequest("reset-fail", "http://127.0.0.1:10000/devstoreaccount1", "original-key-value"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        var reset = await _client.PostAsJsonAsync(
            $"/api/accounts/{created!.Id}/reset-secrets",
            new { accountKey = WrongButValidKey, proxyPassword = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var row = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == created.Id);

        Assert.Equal("original-key-value", TestSecrets.Reveal(encryption, row.AccountKeyProtected));
    }

    /// <summary>
    /// 重设时代理密码留空 = **清空**（端点里 `string.IsNullOrEmpty(req.ProxyPassword) ? null : Encrypt(...)`），
    /// 而 UseProxy 与 ProxyUsername 是从原账户搬过来的、不受影响。于是会留下「用代理 + 有用户名 + 无密码」
    /// 这一组合——它必须是可用状态：解密咽喉处 RevealProxyPassword 对空密文返回 null（而不是抛
    /// SecretUnavailableException），代理凭据退化成空密码而不是让整个账户从此连不上云。
    /// 这条组合此前无人覆盖：现有代理用例走的是「压根没有用户名」那条分支。
    /// </summary>
    [Fact]
    public async Task Reset_With_Blank_Proxy_Password_Clears_It_And_Leaves_The_Proxied_Account_Usable()
    {
        using var factory = new StubbedFactory(services =>
        {
            services.RemoveAll<IBlobClientFactory>();
            services.AddSingleton<IBlobClientFactory, AlwaysConnects>();
        });
        var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "proxied",
            Description: null,
            BlobEndpoint: "https://proxied.blob.core.windows.net",
            Region: AzureRegion.Global,
            AccountKey: "dGVzdGtleQ==",
            UseProxy: true,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: "proxy.local",
            ProxyPort: 8080,
            ProxyUsername: "u",
            ProxyPassword: "old-proxy-password")))
            .Content.ReadFromJsonAsync<AccountResponse>();
        Assert.NotNull(created);

        var reset = await client.PostAsJsonAsync(
            $"/api/accounts/{created!.Id}/reset-secrets", new ResetAccountSecretsRequest("dGVzdGtleTI=", null));
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var row = await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == created.Id);

        Assert.Equal("dGVzdGtleTI=", TestSecrets.Reveal(encryption, row.AccountKeyProtected));
        Assert.True(string.IsNullOrEmpty(row.ProxyPasswordProtected)); // 清空，不是保留旧值
        Assert.True(row.UseProxy);                                     // 代理设置本身没被动
        Assert.Equal("u", row.ProxyUsername);

        // 关键：清空后该账户仍能构出 HTTP 管道——空密码，而不是一路抛到 409 keyring_lost。
        var handler = new BlobClientFactory(new SecretReader(encryption)).CreateProxyHandler(row);
        var proxy = Assert.IsType<System.Net.WebProxy>(handler.Proxy);
        var cred = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal("u", cred.UserName);
        Assert.Equal("", cred.Password);
    }
}
