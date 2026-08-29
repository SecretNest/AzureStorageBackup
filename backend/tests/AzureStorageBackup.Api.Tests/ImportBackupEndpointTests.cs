using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class ImportBackupEndpointTests(TestWebAppFactory factory)
    : IClassFixture<TestWebAppFactory>, IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-import-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    [SkippableFact]
    public async Task Import_Recreates_Config_From_Info_File()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "alpha");
        var containerName = "imp-" + Guid.NewGuid().ToString("N")[..8];

        // One endpoint, one record: adopt the Azurite account an earlier test already registered.
        var accountId = await TestAccounts.EnsureAsync(_client, new AccountRequest(
            "azurite-" + Guid.NewGuid().ToString("N")[..6], null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null));
        var account = (await _client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts"))!.Single(a => a.Id == accountId);

        var config = await (await _client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
            account!.Id, containerName, "family-photos", "desc", _root, null,
            StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);

        try
        {
            // Run a backup once to write the info file
            await _client.PostAsync($"/api/backup-configs/{config!.Id}/run", null);
            for (var i = 0; i < 600; i++)
            {
                var s = await (await _client.GetAsync($"/api/backup-configs/{config.Id}/run")).Content.ReadFromJsonAsync<RunRow>();
                if (s!.status != "Running") break;
                await Task.Delay(200);
            }

            // Delete the local config, simulating "new device / lost config"
            await _client.DeleteAsync($"/api/backup-configs/{config.Id}");

            // Import: restore the config from the info file
            var res = await _client.PostAsJsonAsync("/api/backup-configs/import",
                new ImportRequest(account.Id, containerName, null, CheckAfterImport: false));
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);

            var imported = (await res.Content.ReadFromJsonAsync<ImportResponse>())!.Config;
            Assert.Equal("family-photos", imported.Name);
            Assert.Equal(containerName, imported.ContainerName);
            Assert.Equal(_root, imported.LocalRoot); // sourceRootHint
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    // Importing an encrypted backup: the password in the request body is plaintext and must be persisted as ciphertext
    // (design §3.1). Store it wrong and /versions (which fetches the password via ISecretReader before reading the info
    // file) throws SecretUnavailableException or fails to decrypt the info file.
    [SkippableFact]
    public async Task Import_Encrypted_Backup_Stores_Password_So_Versions_Still_Readable()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "alpha");
        var containerName = "impenc-" + Guid.NewGuid().ToString("N")[..8];

        // One endpoint, one record: adopt the Azurite account an earlier test already registered.
        var accountId = await TestAccounts.EnsureAsync(_client, new AccountRequest(
            "azurite-" + Guid.NewGuid().ToString("N")[..6], null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null));
        var account = (await _client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts"))!.Single(a => a.Id == accountId);

        var config = await (await _client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
            account!.Id, containerName, "secret-photos", null, _root, "pw",
            StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.True(config!.HasPassword);

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);

        try
        {
            await _client.PostAsync($"/api/backup-configs/{config.Id}/run", null);
            for (var i = 0; i < 600; i++)
            {
                var s = await (await _client.GetAsync($"/api/backup-configs/{config.Id}/run")).Content.ReadFromJsonAsync<RunRow>();
                if (s!.status != "Running") break;
                await Task.Delay(200);
            }

            await _client.DeleteAsync($"/api/backup-configs/{config.Id}");

            var res = await _client.PostAsJsonAsync("/api/backup-configs/import",
                new ImportRequest(account.Id, containerName, "pw", CheckAfterImport: false));
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);

            var imported = (await res.Content.ReadFromJsonAsync<ImportResponse>())!.Config;
            Assert.True(imported.HasPassword);

            var versions = await _client.GetAsync($"/api/backup-configs/{imported.Id}/versions");
            Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
            Assert.Single((await versions.Content.ReadFromJsonAsync<List<VersionRow>>())!);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Import_From_Empty_Container_Is_404()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        // One endpoint, one record: adopt the Azurite account an earlier test already registered.
        var accountId = await TestAccounts.EnsureAsync(_client, new AccountRequest(
            "azurite-" + Guid.NewGuid().ToString("N")[..6], null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null));
        var account = (await _client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts"))!.Single(a => a.Id == accountId);

        var empty = "empty-" + Guid.NewGuid().ToString("N")[..8];
        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(empty);
        await container.CreateIfNotExistsAsync();
        try
        {
            var res = await _client.PostAsJsonAsync("/api/backup-configs/import",
                new ImportRequest(account!.Id, empty, null, CheckAfterImport: false));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    // B2: importing a container whose info file carries no SourceRootHint (e.g. written by
    // a pre-Backup__Root version, or by hand) leaves LocalRoot = "". From then on every guarded
    // endpoint 409s it with path_outside_root — indistinguishable, from the response alone, from a
    // config whose LocalRoot genuinely points outside the configured root. The import handler must
    // surface the real cause at import time, in the operation log, so the operator isn't left
    // guessing between "fix Backup__Root" and "set Local Root".
    [SkippableFact]
    public async Task Import_Without_A_Source_Root_Hint_Logs_Why_The_Config_Will_Be_Unrunnable()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var containerName = "imp-nohint-" + Guid.NewGuid().ToString("N")[..8];
        // One endpoint, one record: adopt the Azurite account an earlier test already registered.
        var accountId = await TestAccounts.EnsureAsync(_client, new AccountRequest(
            "azurite-" + Guid.NewGuid().ToString("N")[..6], null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null));
        var account = (await _client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts"))!.Single(a => a.Id == accountId);

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Hand-write an info file with no SourceRootHint — simulating an artifact from an older version / a hand-edited
            // database, rather than going through this app's backup flow (that path always fills in SourceRootHint = config.LocalRoot).
            // Write it directly with a standalone BackupInfoStore (wired to TestSecrets.Reader, the same key material used to
            // hand-encrypt azurite.AccountKeyProtected above), bypassing the DI container's ISecretReader that uses the real
            // keyring — that one cannot decrypt the ciphertext constructed by hand here.
            var infoStore = new BackupInfoStore(factoryClient, new SevenZipArchiveCodec());
            await infoStore.WriteInfoAsync(azurite, containerName, new BackupInfoFile
            {
                Backup = new BackupMeta { Name = "no-hint-backup", CreatedAt = DateTimeOffset.UtcNow },
            }, password: null);

            var res = await _client.PostAsJsonAsync("/api/backup-configs/import",
                new ImportRequest(account!.Id, containerName, null, CheckAfterImport: false));
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);

            var imported = (await res.Content.ReadFromJsonAsync<ImportResponse>())!.Config;
            Assert.Equal(string.Empty, imported.LocalRoot);

            using var scope = factory.Services.CreateScope();
            var log = scope.ServiceProvider.GetRequiredService<IOperationLog>();
            var entries = await log.QueryAsync(null, null, null, null, 100, CancellationToken.None);
            Assert.Contains(entries, e =>
                e.Source == $"import:{account.Id}/{containerName}" &&
                e.Level == OperationLogLevel.Warning &&
                e.Message.Contains("without a local root hint", StringComparison.Ordinal));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private sealed record RunRow(string status);

    private sealed record VersionRow(int version);
}
