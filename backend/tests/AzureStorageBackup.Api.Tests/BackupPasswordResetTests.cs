using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Backup password reset endpoint (design §3.4). Verification rests on the encrypted backup's info file itself — a 7z
/// encrypted with that very password, the smallest encrypted object in the container, so decrypting it proves the password is right.
/// This writes an encrypted info file straight to a real Azurite (no full orchestrator, no real data files needed), then hits the
/// HTTP endpoint to check that the right password is persisted and a wrong one is not.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupPasswordResetTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private readonly HttpClient _client = factory.CreateClient();

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static BackupInfoFile SampleInfo(bool encrypted = true) => new()
    {
        Backup = new BackupMeta
        {
            Name = "reset-pw-fixture",
            Encrypted = encrypted,
            CreatedAt = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
        },
        Versions =
        [
            new BackupVersion
            {
                Version = 1,
                CreatedAt = new DateTimeOffset(2026, 7, 16, 12, 5, 0, TimeSpan.Zero),
                IndexBlob = "indexes/v1.json.enc",
                Stats = new VersionStats(1, 10, 1, 10),
            },
        ],
    };

    /// <summary>Write the encrypted info file to a real Azurite through a standalone BackupInfoStore (bypassing the host's HTTP) —
    /// this is the gold-standard artifact the endpoint will use to verify the password.</summary>
    private static async Task SeedEncryptedInfoAsync(string container, string password)
    {
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(blobFactory, new SevenZipArchiveCodec());
        var account = new Account
        {
            BlobEndpoint = AzuriteEndpoint,
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };
        var cc = blobFactory.CreateServiceClient(account).GetBlobContainerClient(container);
        await cc.CreateIfNotExistsAsync();
        await store.WriteInfoAsync(account, container, SampleInfo(), password);
    }

    /// <summary>Finding 1 regression fixture: put an *unencrypted* info file in the container (written with password: null under the unencrypted blob name),
    /// simulating the mismatch where the local config believes the backup is encrypted while the cloud object is actually plaintext. ReadInfoWithETagAsync probes the unencrypted blob name first,
    /// so without checking Backup.Encrypted the endpoint would persist whatever string was submitted as the password, having verified nothing.</summary>
    private static async Task SeedPlaintextInfoAsync(string container)
    {
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(blobFactory, new SevenZipArchiveCodec());
        var account = new Account
        {
            BlobEndpoint = AzuriteEndpoint,
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };
        var cc = blobFactory.CreateServiceClient(account).GetBlobContainerClient(container);
        await cc.CreateIfNotExistsAsync();
        await store.WriteInfoAsync(account, container, SampleInfo(encrypted: false), password: null);
    }

    private async Task<(AccountResponse Account, BackupConfigResponse Config)> CreateAccountAndConfigAsync(
        string container, string initialPassword)
    {
        var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            "azurite", null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>();
        Assert.NotNull(account);

        var config = await (await _client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
            account!.Id, container, "reset-pw-fixture", null, "/some/local/root", initialPassword,
            StorageTier.Hot, StorageTier.Archive, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.NotNull(config);

        return (account, config!);
    }

    private sealed record KeyringSnapshot(IReadOnlyList<KeyringCanary> Canaries, KeyringStatus Status);

    private KeyringSnapshot SnapshotKeyring()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new KeyringSnapshot(
            db.KeyringCanaries.AsNoTracking().ToList(),
            factory.Services.GetRequiredService<IKeyringHealth>().Status);
    }

    /// <summary>
    /// Per-test cleanup. The three tests share one host (<c>IClassFixture</c>) and today stay out of each other's way through random container names,
    /// so deleting only the container would technically be correct; but the Account / BackupConfig rows left in the fixture database, plus the canary
    /// <see cref="KeyringRecovery"/> rewrites and the status bit it flips back on a successful reset, are residue visible to every other test —
    /// the moment the fixture becomes shared across test classes, those rows quietly rewrite the neighbouring tests' per-record pending counts,
    /// and the failure shows up one test class away from its actual cause. So restore everything as soon as we are done.
    /// </summary>
    private async Task CleanUpAsync(string container, int accountId, int configId, KeyringSnapshot before)
    {
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account
        { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        await blobFactory.CreateServiceClient(azurite).GetBlobContainerClient(container).DeleteIfExistsAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.BackupConfigs.Where(c => c.Id == configId).ExecuteDeleteAsync();
        await db.Accounts.Where(a => a.Id == accountId).ExecuteDeleteAsync();

        // Restore the canary by content (letting the database reassign Ids — the verdict looks only at the ciphertext of the lowest-Id row, never at the Id itself).
        await db.KeyringCanaries.ExecuteDeleteAsync();
        db.KeyringCanaries.AddRange(before.Canaries.Select(
            k => new KeyringCanary { Ciphertext = k.Ciphertext, CreatedAt = k.CreatedAt }));
        await db.SaveChangesAsync();
        factory.Services.GetRequiredService<IKeyringHealth>().Set(before.Status);
    }

    /// <summary>
    /// Right password: verification passes, the new ciphertext is persisted, and the verification path back-fills no local authoritative state
    /// (it uses the read-only ReadInfoWithETagAsync, not the seeding TrackedInfoStore.SeedFromCloudAsync).
    /// </summary>
    [SkippableFact]
    public async Task Correct_Password_Verifies_And_Is_Persisted_Without_Seeding_Local_State()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var keyringBefore = SnapshotKeyring();
        var container = RandomName("rpw-ok-");
        const string correctPassword = "the-real-password";
        await SeedEncryptedInfoAsync(container, correctPassword);

        var (account, config) = await CreateAccountAndConfigAsync(container, initialPassword: "stale-initial-value");

        try
        {
            var reset = await _client.PostAsJsonAsync(
                $"/api/backup-configs/{config.Id}/reset-password", new ResetBackupPasswordRequest(correctPassword));
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

            var row = await db.BackupConfigs.AsNoTracking().FirstAsync(c => c.Id == config.Id);
            Assert.Equal(correctPassword, TestSecrets.Reveal(encryption, row.PasswordProtected!));

            // Verification is read-only: it must leave no trace in the local authoritative state.
            Assert.Empty(await db.LocalBackupStates
                .Where(s => s.AccountId == account.Id && s.Container == container).ToListAsync());
            Assert.Empty(await db.CachedVersionIndexes
                .Where(c => c.AccountId == account.Id && c.Container == container).ToListAsync());
        }
        finally
        {
            await CleanUpAsync(container, account.Id, config.Id, keyringBefore);
        }
    }

    /// <summary>
    /// Wrong password: the outcome must be "nothing persisted", not "a wrong value persisted". Assert a 400 with the stored ciphertext untouched,
    /// and no trace left in the local authoritative state either.
    /// </summary>
    [SkippableFact]
    public async Task Wrong_Password_Is_Rejected_And_Leaves_Stored_Ciphertext_And_Local_State_Untouched()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var keyringBefore = SnapshotKeyring();
        var container = RandomName("rpw-bad-");
        const string correctPassword = "the-real-password-2";
        await SeedEncryptedInfoAsync(container, correctPassword);

        var (account, config) = await CreateAccountAndConfigAsync(container, initialPassword: "original-value-untouched");

        try
        {
            var reset = await _client.PostAsJsonAsync(
                $"/api/backup-configs/{config.Id}/reset-password", new ResetBackupPasswordRequest("totally-wrong-password"));
            Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

            var row = await db.BackupConfigs.AsNoTracking().FirstAsync(c => c.Id == config.Id);
            Assert.Equal("original-value-untouched", TestSecrets.Reveal(encryption, row.PasswordProtected!));

            Assert.Empty(await db.LocalBackupStates
                .Where(s => s.AccountId == account.Id && s.Container == container).ToListAsync());
            Assert.Empty(await db.CachedVersionIndexes
                .Where(c => c.AccountId == account.Id && c.Container == container).ToListAsync());
        }
        finally
        {
            await CleanUpAsync(container, account.Id, config.Id, keyringBefore);
        }
    }

    /// <summary>
    /// Regression for Finding 1: the local config believes the backup is encrypted (PasswordProtected is non-empty), but what
    /// actually sits in the cloud container is an *unencrypted* info file. ReadInfoWithETagAsync probes the unencrypted blob
    /// name first and reads it back successfully with password: null — so unless the endpoint checks that what came back really
    /// came from an encrypted object, it persists whatever string was submitted as the password, having never verified the real one. It must reject, leaving the stored ciphertext untouched.
    /// </summary>
    [SkippableFact]
    public async Task Plaintext_Info_Blob_In_Encrypted_Config_Container_Is_Rejected_Without_Using_Password()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var keyringBefore = SnapshotKeyring();
        var container = RandomName("rpw-plain-");
        await SeedPlaintextInfoAsync(container);

        var (account, config) = await CreateAccountAndConfigAsync(container, initialPassword: "original-value-untouched-3");

        try
        {
            var reset = await _client.PostAsJsonAsync(
                $"/api/backup-configs/{config.Id}/reset-password", new ResetBackupPasswordRequest("any-guessed-password"));
            Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

            var row = await db.BackupConfigs.AsNoTracking().FirstAsync(c => c.Id == config.Id);
            Assert.Equal("original-value-untouched-3", TestSecrets.Reveal(encryption, row.PasswordProtected!));

            Assert.Empty(await db.LocalBackupStates
                .Where(s => s.AccountId == account.Id && s.Container == container).ToListAsync());
            Assert.Empty(await db.CachedVersionIndexes
                .Where(c => c.AccountId == account.Id && c.Container == container).ToListAsync());
        }
        finally
        {
            await CleanUpAsync(container, account.Id, config.Id, keyringBefore);
        }
    }
}
