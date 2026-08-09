using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class KeyringStatusEndpointTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record KeyringStatusResponse(string Status, int AccountsPending, int BackupConfigsPending);

    private IKeyringHealth Keyring => factory.Services.GetRequiredService<IKeyringHealth>();

    [Fact]
    public async Task Reports_Healthy_On_A_Fresh_Database()
    {
        var res = await _client.GetAsync("/api/system/keyring");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<KeyringStatusResponse>();
        Assert.Equal("Healthy", body!.Status);
        Assert.Equal(0, body.AccountsPending);
        // Both counts must be 0 — assert only one and a bug in the backup-side count still passes.
        Assert.Equal(0, body.BackupConfigsPending);
    }

    private static AccountRequest SampleAccount(string name) => new(
        Name: name,
        Description: null,
        BlobEndpoint: "https://" + name + ".blob.core.windows.net",
        Region: AzureRegion.Global,
        AccountKey: "dGVzdGtleQ==",
        UseProxy: false,
        ProxyMode: ProxyMode.Independent,
        ProxyHost: null,
        ProxyPort: null,
        ProxyUsername: null,
        ProxyPassword: null);

    private static BackupConfigRequest SampleConfig(int accountId, string name, string? password) => new(
        AccountId: accountId,
        ContainerName: name + "-container",
        Name: name,
        Description: null,
        LocalRoot: "/data/" + name,
        Password: password,
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

    /// <summary>
    /// Covers both asymmetric sides of the counting/flagging rules: the account key is mandatory → every account is counted and flagged;
    /// the backup password is optional → only records that really carry a password are counted and flagged, and an unencrypted backup is unaffected even while the keyring is Lost.
    /// If the counting rule gets inverted (forgetting to filter out unencrypted configs, say, or forgetting to count all accounts), this test should fail.
    /// Note: the stored ciphertext must be replaced with the output of a different keyring; flipping IKeyringHealth alone is not enough —
    /// the counts are decided by per-record decryptability (design §3.3).
    /// </summary>
    [Fact]
    public async Task Reports_Lost_With_Correct_Counts_And_Per_Record_Flags()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", SampleAccount("keyring-status-acct")))
            .Content.ReadFromJsonAsync<AccountResponse>())!;

        var encrypted = (await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleConfig(account.Id, "keyring-status-encrypted", "s3cret")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>())!;
        var plain = (await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleConfig(account.Id, "keyring-status-plain", null)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>())!;

        var accountsBefore = await (await _client.GetAsync("/api/accounts"))
            .Content.ReadFromJsonAsync<List<AccountResponse>>();
        var configsBefore = await (await _client.GetAsync("/api/backup-configs"))
            .Content.ReadFromJsonAsync<List<BackupConfigResponse>>();
        Assert.All(accountsBefore!, a => Assert.False(a.SecretsUnavailable));
        Assert.All(configsBefore!, c => Assert.False(c.SecretsUnavailable));

        // Simulate /keys being lost: replace every stored ciphertext with the output of a different keyring.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var a in await db.Accounts.ToListAsync())
                a.AccountKeyProtected = TestSecrets.Stale("account-key");
            foreach (var c in await db.BackupConfigs.Where(c => c.PasswordProtected != null).ToListAsync())
                c.PasswordProtected = TestSecrets.Stale("backup-password");
            await db.SaveChangesAsync();
        }

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var status = await (await _client.GetAsync("/api/system/keyring"))
                .Content.ReadFromJsonAsync<KeyringStatusResponse>();
            Assert.Equal("Lost", status!.Status);
            Assert.Equal(accountsBefore!.Count, status.AccountsPending);
            // Only encrypted configs count: a plaintext config has no ciphertext to lose and must not be included.
            Assert.Equal(1, status.BackupConfigsPending);

            var accounts = await (await _client.GetAsync("/api/accounts"))
                .Content.ReadFromJsonAsync<List<AccountResponse>>();
            Assert.All(accounts!, a => Assert.True(a.SecretsUnavailable));

            var configs = await (await _client.GetAsync("/api/backup-configs"))
                .Content.ReadFromJsonAsync<List<BackupConfigResponse>>();
            Assert.True(configs!.Single(c => c.Id == encrypted.Id).SecretsUnavailable);
            Assert.False(configs!.Single(c => c.Id == plain.Id).SecretsUnavailable);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }
}
