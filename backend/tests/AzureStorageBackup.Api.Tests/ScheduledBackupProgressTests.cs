using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 定时备份此前绕开 BackupRunner 直接调 BackupOrchestrator，进度回调传的是 null，
/// 因此界面永远查不到它的状态。而定时备份恰恰是常态。
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
        acctRes.EnsureSuccessStatusCode();
        var acct = await acctRes.Content.ReadFromJsonAsync<AccountResponse>();

        var cfgRes = await _client.PostAsJsonAsync("/api/backup-configs", new
        {
            AccountId = acct!.Id,
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
            AccountId = acct.Id,
            ContainerName = container,
            GroupId = (int?)null,
            TaskType = ScheduledTaskType.Backup,
            CronExpression = "0 2 * * *",
            Enabled = true,
        });
        taskRes.EnsureSuccessStatusCode();
        var task = await taskRes.Content.ReadFromJsonAsync<TaskResponse>();

        // 立即执行该计划任务，走的是调度器的分发路径。端点内部 await 完整个 DispatchAsync
        // 才返回，但仍按指示轮询，防止将来该端点改成异步触发。
        (await _client.PostAsync($"/api/tasks/{task!.Id}/run", null)).EnsureSuccessStatusCode();

        // 备份多半会失败（本地根不存在），但**必须留下可轮询的状态**。
        // 修复前这里是 null：调度器根本没经过 BackupRunner。
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
