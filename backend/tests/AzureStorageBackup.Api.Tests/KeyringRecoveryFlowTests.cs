using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// All-branch review Finding 1: the **intermediate state** of the recovery flow. The whole suite previously covered only the
/// two ends (everything lost / everything recovered), stepping right over the one step that can deadlock.
///
/// A standard install (≥1 account + ≥1 encrypted backup config) after /keys is lost: every account has been reset successfully, the backup password is still old ciphertext.
/// At that point the global status must still be Lost (the backup password is not fixed), while accountsPending must already be zero and
/// account rows must no longer be flagged secretsUnavailable — otherwise the frontend's ordering dependency (accounts not yet at zero → the backup password
/// "Re-enter" button stays disabled) locks the recovery flow solid: button never enabled → password never reset → status never flips.
/// </summary>
public class KeyringRecoveryFlowTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record KeyringStatusResponse(string Status, int AccountsPending, int BackupConfigsPending);

    private IKeyringHealth Keyring => factory.Services.GetRequiredService<IKeyringHealth>();

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

    [Fact]
    public async Task Accounts_Pending_Reaches_Zero_While_A_Stale_Backup_Password_Keeps_Status_Lost()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", SampleAccount("recovery-flow-acct")))
            .Content.ReadFromJsonAsync<AccountResponse>())!;
        var encrypted = (await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleConfig(account.Id, "recovery-flow-encrypted", "s3cret")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>())!;

        // /keys lost: both the account key and the backup password become old ciphertext the current keyring cannot decrypt.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.FirstAsync(a => a.Id == account.Id)).AccountKeyProtected = TestSecrets.Stale("old-key");
            (await db.BackupConfigs.FirstAsync(c => c.Id == encrypted.Id)).PasswordProtected = TestSecrets.Stale("old-pw");
            await db.SaveChangesAsync();
        }
        Keyring.Set(KeyringStatus.Lost);

        try
        {
            var before = await (await _client.GetAsync("/api/system/keyring"))
                .Content.ReadFromJsonAsync<KeyringStatusResponse>();
            Assert.Equal("Lost", before!.Status);
            Assert.Equal(1, before.AccountsPending);
            Assert.Equal(1, before.BackupConfigsPending);

            // The account reset succeeds (equivalent to reset-secrets persisting after verification, then trying to finish recovery); the backup password's turn has not come yet.
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
                (await db.Accounts.FirstAsync(a => a.Id == account.Id)).AccountKeyProtected = encryption.Encrypt("dGVzdGtleQ==");
                await db.SaveChangesAsync();

                // The backup password still will not decrypt → the completion check must refuse to flip.
                Assert.False(await scope.ServiceProvider.GetRequiredService<KeyringRecovery>().TryCompleteAsync());
            }

            var after = await (await _client.GetAsync("/api/system/keyring"))
                .Content.ReadFromJsonAsync<KeyringStatusResponse>();
            Assert.Equal("Lost", after!.Status);          // the backup password is not fixed yet, so the global status must not flip
            Assert.Equal(0, after.AccountsPending);       // but every account is fixed, so the count must be zero
            Assert.Equal(1, after.BackupConfigsPending);

            var accounts = await (await _client.GetAsync("/api/accounts"))
                .Content.ReadFromJsonAsync<List<AccountResponse>>();
            Assert.False(accounts!.Single(a => a.Id == account.Id).SecretsUnavailable);

            var configs = await (await _client.GetAsync("/api/backup-configs"))
                .Content.ReadFromJsonAsync<List<BackupConfigResponse>>();
            Assert.True(configs!.Single(c => c.Id == encrypted.Id).SecretsUnavailable);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }

    /// <summary>
    /// Follow-up review Finding 1: TryCompleteAsync used to hang off reset-secrets only. When the user gives up on recovery and simply
    /// deletes the one account that will not decrypt, a delete endpoint that does not finish recovery leaves the process stuck at "Lost with 0 pending" —
    /// the very state KeyringProbe's restart-time fallback was written to eliminate, yet the delete path still walks into it at runtime,
    /// with no way back to Healthy until the next restart. Deleting must trigger completion immediately.
    /// </summary>
    [Fact]
    public async Task Deleting_The_Last_Stale_Account_Releases_Lost_Without_Restart()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", SampleAccount("recovery-flow-del-acct")))
            .Content.ReadFromJsonAsync<AccountResponse>())!;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.FirstAsync(a => a.Id == account.Id)).AccountKeyProtected = TestSecrets.Stale("old-key");
            await db.SaveChangesAsync();
        }
        Keyring.Set(KeyringStatus.Lost);

        try
        {
            var before = await (await _client.GetAsync("/api/system/keyring"))
                .Content.ReadFromJsonAsync<KeyringStatusResponse>();
            Assert.Equal("Lost", before!.Status);
            Assert.Equal(1, before.AccountsPending);

            var del = await _client.DeleteAsync($"/api/accounts/{account.Id}");
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

            var after = await (await _client.GetAsync("/api/system/keyring"))
                .Content.ReadFromJsonAsync<KeyringStatusResponse>();
            Assert.Equal("Healthy", after!.Status);
            Assert.Equal(0, after.AccountsPending);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }

    /// <summary>Same as above, other endpoint: deleting a backup config locally (leaving the cloud container alone — decision 6's only way out).</summary>
    [Fact]
    public async Task Deleting_The_Last_Stale_Backup_Config_Releases_Lost_Without_Restart()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", SampleAccount("recovery-flow-del-cfg-acct")))
            .Content.ReadFromJsonAsync<AccountResponse>())!;
        var encrypted = (await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleConfig(account.Id, "recovery-flow-del-cfg", "s3cret")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>())!;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.BackupConfigs.FirstAsync(c => c.Id == encrypted.Id)).PasswordProtected = TestSecrets.Stale("old-pw");
            await db.SaveChangesAsync();
        }
        Keyring.Set(KeyringStatus.Lost);

        try
        {
            var before = await (await _client.GetAsync("/api/system/keyring"))
                .Content.ReadFromJsonAsync<KeyringStatusResponse>();
            Assert.Equal("Lost", before!.Status);
            Assert.Equal(0, before.AccountsPending);
            Assert.Equal(1, before.BackupConfigsPending);

            var del = await _client.DeleteAsync($"/api/backup-configs/{encrypted.Id}");
            Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

            var after = await (await _client.GetAsync("/api/system/keyring"))
                .Content.ReadFromJsonAsync<KeyringStatusResponse>();
            Assert.Equal("Healthy", after!.Status);
            Assert.Equal(0, after.BackupConfigsPending);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }
}
