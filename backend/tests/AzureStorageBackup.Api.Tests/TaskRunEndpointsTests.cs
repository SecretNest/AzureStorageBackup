using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

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

        var config = await (await _client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
            account!.Id, containerName, "photos", null, _root, null,
            StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000)))
            .Content.ReadFromJsonAsync<ConfigResponse>();

        // Scheduled task: backup target (account, container)
        var task = await (await _client.PostAsJsonAsync("/api/tasks", new TaskRequest(
            TaskTargetKind.Backup, account.Id, containerName, null,
            ScheduledTaskType.Backup, "0 3 * * *", true)))
            .Content.ReadFromJsonAsync<TaskResponse>();

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);

        try
        {
            // The request is answered as soon as the dispatch is under way — 202, not 200 — and the work goes
            // on without it. It used to await the whole backup inside the request, which the web client's
            // one-minute deadline then aborted, cancelling the backup with it (see the endpoint's header).
            using var abandoned = new CancellationTokenSource();
            var res = await _client.PostAsync($"/api/tasks/{task!.id}/run", null, abandoned.Token);
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
            var after = await res.Content.ReadFromJsonAsync<TaskResponse>();
            Assert.NotNull(after!.lastRunAt);
            // The caller walking away must not reach the backup: it finishes, and leaves an index behind.
            abandoned.Cancel();

            var state = await WaitForRunAsync(config!.id);
            await state.Completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(RunStatus.Completed, state.Status);
            Assert.True(await container.GetBlobClient(BackupDiscovery.IndexBlobName).ExistsAsync());
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Scheduled_Task_Skips_And_Warns_When_Backup_Busy()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "alpha");
        var containerName = "taskbusy-" + Guid.NewGuid().ToString("N")[..8];

        var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            "azurite", null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>();

        await _client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
            account!.Id, containerName, "photos", null, _root, null,
            StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000));
        var task = await (await _client.PostAsJsonAsync("/api/tasks", new TaskRequest(
            TaskTargetKind.Backup, account.Id, containerName, null,
            ScheduledTaskType.Backup, "0 3 * * *", true)))
            .Content.ReadFromJsonAsync<TaskResponse>();

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);

        // Mark this backup busy up front (simulating another operation already running).
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(account.Id, containerName));
        try
        {
            var res = await _client.PostAsync($"/api/tasks/{task!.id}/run", null);
            res.EnsureSuccessStatusCode();

            // The dispatch runs on after the response, so the Warning it writes is awaited, not assumed.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            List<LogRow>? logs = null;
            while (DateTime.UtcNow < deadline)
            {
                logs = await _client.GetFromJsonAsync<List<LogRow>>("/api/logs?minLevel=1&limit=20");
                if (logs!.Any(l => l.message.Contains("busy", StringComparison.OrdinalIgnoreCase)))
                    break;
                await Task.Delay(100);
            }
            Assert.Contains(logs!, l => l.message.Contains("busy", StringComparison.OrdinalIgnoreCase));

            // Busy → skipped: no backup should be produced (the info file does not exist).
            Assert.False(await container.GetBlobClient(BackupDiscovery.IndexBlobName).ExistsAsync());
        }
        finally
        {
            busy.Release(account.Id, containerName);
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>The run state the detached dispatch registers for the UI to poll; it appears a moment after the response.</summary>
    private async Task<BackupRunState> WaitForRunAsync(int configId)
    {
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (runner.Get(configId) is { } state)
                return state;
            await Task.Delay(50);
        }
        throw new TimeoutException($"No run was registered for config {configId}.");
    }

    // Subset of the backend's camelCase JSON
    private sealed record TaskResponse(int id, DateTimeOffset? lastRunAt);
    private sealed record ConfigResponse(int id);
    private sealed record LogRow(int level, string message);
}
