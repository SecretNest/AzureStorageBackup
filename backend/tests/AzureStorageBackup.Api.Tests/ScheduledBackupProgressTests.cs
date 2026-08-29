using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Scheduled backups used to go around BackupRunner and call BackupOrchestrator directly, passing null for the progress callback,
/// so the UI could never look up their state. And a scheduled backup is precisely the normal case.
/// </summary>
[Trait("Category", "Integration")]
public class ScheduledBackupProgressTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task A_Scheduled_Backup_Leaves_State_The_UI_Can_Poll()
    {
        var container = "sched-" + Guid.NewGuid().ToString("N")[..8];
        var acctRes = await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "sched-" + Guid.NewGuid().ToString("N")[..8],
            Description: null,
            BlobEndpoint: "http://127.0.0.1:10000/devstoreaccount1",
            Region: AzureRegion.Global,
            AccountKey: "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null));
        // One endpoint, one record now: adopt the account an earlier test already registered for Azurite.
        var acctId = await TestAccounts.EnsureFromAsync(_client, acctRes, "http://127.0.0.1:10000/devstoreaccount1");

        var cfgRes = await _client.PostAsJsonAsync("/api/backup-configs", new
        {
            AccountId = acctId,
            ContainerName = container,
            Name = "sched-test",
            LocalRoot = Path.Combine(Path.GetTempPath(), "asb-sched-" + Guid.NewGuid().ToString("N")[..8]),
            IndexTier = StorageTier.Hot,
            DataTier = StorageTier.Hot,
        });
        cfgRes.EnsureSuccessStatusCode();
        var cfg = await cfgRes.Content.ReadFromJsonAsync<BackupConfigResponse>();

        var taskRes = await _client.PostAsJsonAsync("/api/tasks", new
        {
            TargetKind = TaskTargetKind.Backup,
            AccountId = acctId,
            ContainerName = container,
            GroupId = (int?)null,
            TaskType = ScheduledTaskType.Backup,
            CronExpression = "0 2 * * *",
            Enabled = true,
        });
        taskRes.EnsureSuccessStatusCode();
        var task = await taskRes.Content.ReadFromJsonAsync<TaskResponse>();

        // Run the scheduled task right now, going through the scheduler's dispatch path. The endpoint awaits the whole DispatchAsync
        // before it returns, but we still poll as instructed, in case the endpoint is ever changed to fire asynchronously.
        (await _client.PostAsync($"/api/tasks/{task!.Id}/run", null)).EnsureSuccessStatusCode();

        // The backup will most likely fail (the local root does not exist), but it **must leave behind a state the UI can poll**.
        // Before the fix this was null: the scheduler never went through BackupRunner at all.
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        BackupRunState? state = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            state = runner.Get(cfg!.Id);
            if (state is not null)
                break;
            await Task.Delay(100);
        }
        Assert.NotNull(state);
    }
}
