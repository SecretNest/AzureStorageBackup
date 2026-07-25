using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
}
