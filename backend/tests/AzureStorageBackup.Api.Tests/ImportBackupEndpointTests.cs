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

        var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            "azurite", null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>();

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
            // 跑一次备份写出信息文件
            await _client.PostAsync($"/api/backup-configs/{config!.Id}/run", null);
            for (var i = 0; i < 600; i++)
            {
                var s = await (await _client.GetAsync($"/api/backup-configs/{config.Id}/run")).Content.ReadFromJsonAsync<RunRow>();
                if (s!.status != "Running") break;
                await Task.Delay(200);
            }

            // 删掉本地配置，模拟"新设备/丢配置"
            await _client.DeleteAsync($"/api/backup-configs/{config.Id}");

            // 导入：从信息文件恢复配置
            var res = await _client.PostAsJsonAsync("/api/backup-configs/import",
                new ImportRequest(account.Id, containerName, null));
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);

            var imported = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
            Assert.Equal("family-photos", imported!.Name);
            Assert.Equal(containerName, imported.ContainerName);
            Assert.Equal(_root, imported.LocalRoot); // sourceRootHint
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    // 导入加密备份：请求体里的密码是明文，落库必须是密文（设计 §3.1）。存错了的话
    // /versions（经 ISecretReader 取密码再读信息文件）会抛 SecretUnavailableException 或解不开信息文件。
    [SkippableFact]
    public async Task Import_Encrypted_Backup_Stores_Password_So_Versions_Still_Readable()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "alpha");
        var containerName = "impenc-" + Guid.NewGuid().ToString("N")[..8];

        var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            "azurite", null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>();

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
                new ImportRequest(account.Id, containerName, "pw"));
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);

            var imported = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
            Assert.True(imported!.HasPassword);

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

        var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            "azurite", null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var empty = "empty-" + Guid.NewGuid().ToString("N")[..8];
        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(empty);
        await container.CreateIfNotExistsAsync();
        try
        {
            var res = await _client.PostAsJsonAsync("/api/backup-configs/import",
                new ImportRequest(account!.Id, empty, null));
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
        var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            "azurite", null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 手工写一份没有 SourceRootHint 的信息文件——模拟旧版本产物/手工改库，
            // 而不是经本应用的备份流程（那条路径总会填 SourceRootHint = config.LocalRoot）。
            // 用独立的 BackupInfoStore（配 TestSecrets.Reader，与上面手工加密 azurite.AccountKeyProtected
            // 用的是同一套密钥）直接写，绕开 DI 容器里那个用真实 keyring 的 ISecretReader——
            // 后者解不开这里手工构造的密文。
            var infoStore = new BackupInfoStore(factoryClient, new SevenZipArchiveCodec());
            await infoStore.WriteInfoAsync(azurite, containerName, new BackupInfoFile
            {
                Backup = new BackupMeta { Name = "no-hint-backup", CreatedAt = DateTimeOffset.UtcNow },
            }, password: null);

            var res = await _client.PostAsJsonAsync("/api/backup-configs/import",
                new ImportRequest(account!.Id, containerName, null));
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);

            var imported = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
            Assert.Equal(string.Empty, imported!.LocalRoot);

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
