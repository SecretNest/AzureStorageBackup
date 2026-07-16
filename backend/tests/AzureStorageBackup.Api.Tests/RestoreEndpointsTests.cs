using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class RestoreEndpointsTests(TestWebAppFactory factory)
    : IClassFixture<TestWebAppFactory>, IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly string _base = Path.Combine(Path.GetTempPath(), "asb-rst-ep-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    private async Task<T?> PollUntilDone<T>(string url, Func<T, bool> done) where T : class
    {
        for (var i = 0; i < 600; i++) // 宽松：并发集成测试在少核机器上会拖慢后台 job
        {
            var s = await (await _client.GetAsync(url)).Content.ReadFromJsonAsync<T>();
            if (s is not null && done(s))
                return s;
            await Task.Delay(200);
        }
        return null;
    }

    [SkippableFact]
    public async Task Backup_Then_Restore_Via_Http_Restores_Files()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var src = Path.Combine(_base, "src");
        var dst = Path.Combine(_base, "dst");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(Path.Combine(src, "a.txt"), "alpha");

        var containerName = "rstep-" + Guid.NewGuid().ToString("N")[..8];

        var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            "azurite", null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var config = await (await _client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
            account!.Id, containerName, "photos", null, src, null,
            StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var factoryClient = new BlobClientFactory();
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKey = AzuriteKey, Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);

        try
        {
            // 备份
            await _client.PostAsync($"/api/backup-configs/{config!.Id}/run", null);
            var backup = await PollUntilDone<BackupRunResponse>(
                $"/api/backup-configs/{config.Id}/run", s => s.status != "Running");
            Assert.Equal("Completed", backup!.status);

            // 还原到新目录
            var start = await _client.PostAsJsonAsync(
                $"/api/backup-configs/{config.Id}/restore", new RestoreRequestBody(dst, null));
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

            var restore = await PollUntilDone<RestoreRunResponse>(
                $"/api/backup-configs/{config.Id}/restore", s => s.status != "Running");

            Assert.Equal("Completed", restore!.status);
            Assert.Equal(1, restore.restoredFiles);
            Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(dst, "a.txt")));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    // 与后端 camelCase JSON 对应
    private sealed record BackupRunResponse(string status);
    private sealed record RestoreRunResponse(string status, int? version, int? restoredFiles, int? skippedFiles, string? error);
}
