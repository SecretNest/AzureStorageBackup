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
    // Azurite's default account key (devstoreaccount1), same as in BackupOrchestratorTests.
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    // Well-formed (base64, same length) but not Azurite's real key — used to take the "verification failed" path
    // rather than the "malformed input" path.
    private const string WrongButValidKey =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==";

    private readonly HttpClient _client = factory.CreateClient();

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    /// <summary>Stub whose connectivity check always passes: it steps over the "must reach the cloud" part so the test can focus on the clearing semantics of the proxy password
    /// (the two tests that really do reach the cloud are below, each hitting a real Azurite).</summary>
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
            builder.ConfigureServices(configure); // runs after Program.cs's registrations, so it can override them
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
    /// Design decision 5: new credentials must pass cloud verification before they are persisted. This verifies against a real Azurite,
    /// asserting that the ciphertext really was replaced and that the new ciphertext decrypts back to the real key through the app's encryption service —
    /// an implementation that skipped verification and wrote straight through would still pass this test (the credentials are correct to begin with),
    /// so only together with the "verification fails" test below does it prove verification really stands in front of the write.
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
    /// A failed verification must mean "nothing persisted", not "a wrong value persisted". Hit a real Azurite with a
    /// well-formed but incorrect key and assert a 400 with the stored ciphertext untouched (still decrypting to the value it was created with).
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
    /// Leaving the proxy password blank during a reset means **clear it** (the endpoint does `string.IsNullOrEmpty(req.ProxyPassword) ? null : Encrypt(...)`),
    /// while UseProxy and ProxyUsername are copied over from the original account and are unaffected. That leaves the combination "proxy on + username + no password" —
    /// and it must be a usable state: at the decryption choke point RevealProxyPassword returns null for empty ciphertext (instead of throwing
    /// SecretUnavailableException), so the proxy credentials degrade to an empty password rather than cutting the whole account off from the cloud for good.
    /// Nothing covered this combination before: the existing proxy tests take the "no username at all" branch.
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
        Assert.True(string.IsNullOrEmpty(row.ProxyPasswordProtected)); // cleared, not kept at the old value
        Assert.True(row.UseProxy);                                     // the proxy settings themselves were untouched
        Assert.Equal("u", row.ProxyUsername);

        // The crux: after clearing, the account can still build its HTTP pipeline — with an empty password, instead of throwing all the way out to 409 keyring_lost.
        var handler = new BlobClientFactory(new SecretReader(encryption)).CreateProxyHandler(row);
        var proxy = Assert.IsType<System.Net.WebProxy>(handler.Proxy);
        var cred = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal("u", cred.UserName);
        Assert.Equal("", cred.Password);
    }
}
