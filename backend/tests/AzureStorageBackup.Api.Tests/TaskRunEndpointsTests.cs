using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class TaskRunEndpointsTests(TestWebAppFactory factory)
    : IClassFixture<TestWebAppFactory>, IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private readonly HttpClient _client = factory.CreateClient();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-taskrun-" + Guid.NewGuid().ToString("N"));

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
    public async Task Manual_Run_Dispatches_A_Backup_Task()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "alpha");
        var containerName = "taskrun-" + Guid.NewGuid().ToString("N")[..8];

        var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            "azurite", null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>();

        await _client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
            account!.Id, containerName, "photos", null, _root, null,
            StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000));

        // 计划任务：备份目标 (account, container)
        var task = await (await _client.PostAsJsonAsync("/api/tasks", new TaskRequest(
            TaskTargetKind.Backup, account.Id, containerName, null,
            ScheduledTaskType.Backup, "0 3 * * *", true)))
            .Content.ReadFromJsonAsync<TaskResponse>();

        var factoryClient = new BlobClientFactory();
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKey = AzuriteKey, Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);

        try
        {
            var res = await _client.PostAsync($"/api/tasks/{task!.id}/run", null);
            res.EnsureSuccessStatusCode();

            // dispatcher 已 await 完成整个备份
            Assert.True(await container.GetBlobClient(BackupDiscovery.IndexBlobName).ExistsAsync());

            var after = await res.Content.ReadFromJsonAsync<TaskResponse>();
            Assert.NotNull(after!.lastRunAt);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    // 后端 camelCase JSON 子集
    private sealed record TaskResponse(int id, DateTimeOffset? lastRunAt);
}
