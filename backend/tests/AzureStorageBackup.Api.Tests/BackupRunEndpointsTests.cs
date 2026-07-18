using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupRunEndpointsTests(TestWebAppFactory factory)
    : IClassFixture<TestWebAppFactory>, IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly string _localRoot = Path.Combine(Path.GetTempPath(), "asb-run-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_localRoot, recursive: true); } catch { /* best effort */ }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    [SkippableFact]
    public async Task Run_Backup_Endpoint_Produces_A_Version()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        // 本地根 + 文件
        Directory.CreateDirectory(_localRoot);
        await File.WriteAllTextAsync(Path.Combine(_localRoot, "a.txt"), "alpha");

        var containerName = "run-" + Guid.NewGuid().ToString("N")[..8];

        // 建账户（Azurite）
        var accountReq = new AccountRequest("azurite", null, AzuriteEndpoint, AzureRegion.Global,
            AzuriteKey, false, ProxyMode.Independent, null, null, null, null);
        var account = await (await _client.PostAsJsonAsync("/api/accounts", accountReq))
            .Content.ReadFromJsonAsync<AccountResponse>();

        // 建备份配置
        var configReq = new BackupConfigRequest(account!.Id, containerName, "photos", null, _localRoot,
            null, StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000);
        var config = await (await _client.PostAsJsonAsync("/api/backup-configs", configReq))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var factoryClient = new BlobClientFactory();
        var azuriteAccount = new Account { BlobEndpoint = AzuriteEndpoint, AccountKey = AzuriteKey, Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azuriteAccount).GetBlobContainerClient(containerName);

        try
        {
            // 启动
            var start = await _client.PostAsync($"/api/backup-configs/{config!.Id}/run", null);
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

            // 轮询到完成
            BackupRunResponse? status = null;
            for (var i = 0; i < 600; i++) // 宽松：并发集成测试在少核机器上会拖慢后台 job
            {
                status = await (await _client.GetAsync($"/api/backup-configs/{config.Id}/run"))
                    .Content.ReadFromJsonAsync<BackupRunResponse>();
                if (status!.Status != "Running")
                    break;
                await Task.Delay(200);
            }

            Assert.True(status!.Status == "Completed",
                $"Expected Completed but was '{status.Status}'. Error: {status.Error ?? "(none)"}");
            Assert.Equal(1, status.Version);
            Assert.True(await container.GetBlobClient(BackupDiscovery.IndexBlobName).ExistsAsync());

            // 操作日志已记录本次备份（M8 接线验证）
            var logs = await _client.GetFromJsonAsync<LogRow[]>($"/api/logs?source=backup:{account.Id}/{containerName}");
            Assert.Contains(logs!, l => l.message.Contains("Backup succeeded"));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private sealed record LogRow(int id, string source, string message);
}
