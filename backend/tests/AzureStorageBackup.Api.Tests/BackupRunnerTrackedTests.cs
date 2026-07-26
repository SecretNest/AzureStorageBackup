using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// RunTrackedAsync 供调度器使用：调用方已持有忙碌锁，本方法不得再抢。
/// 若它照 Start 那样抢锁，每一次定时备份都会立刻失败——这正是本轮要修的缺陷。
/// </summary>
[Trait("Category", "Integration")]
public class BackupRunnerTrackedTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(int AccountId, int ConfigId, string Container)> SeedAsync()
    {
        var container = "run-" + Guid.NewGuid().ToString("N")[..8];
        var acctRes = await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "runner-" + Guid.NewGuid().ToString("N")[..8],
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
            Name = "runner-test",
            LocalRoot = Path.Combine(Path.GetTempPath(), "asb-runner-" + Guid.NewGuid().ToString("N")[..8]),
            IndexTier = StorageTier.Hot,
            DataTier = StorageTier.Hot,
        });
        cfgRes.EnsureSuccessStatusCode();
        var cfg = await cfgRes.Content.ReadFromJsonAsync<BackupConfigResponse>();
        return (acct.Id, cfg!.Id, container);
    }

    [Fact]
    public async Task RunTrackedAsync_Does_Not_Acquire_The_Busy_Lock()
    {
        var (accountId, configId, container) = await SeedAsync();
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();

        // 模拟调度器：调用方先持锁。
        Assert.True(busy.TryAcquire(accountId, container, "BackingUp"));
        try
        {
            var state = await runner.RunTrackedAsync(configId, CancellationToken.None);

            // 本地根不存在，备份多半失败——那没关系。要断言的是它没有
            // 因为「抢不到忙碌锁」而失败，因为那说明它抢了本不该抢的锁。
            Assert.DoesNotContain("busy", state.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }

    [Fact]
    public async Task RunTrackedAsync_Registers_State_For_Polling()
    {
        var (accountId, configId, container) = await SeedAsync();
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();

        Assert.True(busy.TryAcquire(accountId, container, "BackingUp"));
        try
        {
            await runner.RunTrackedAsync(configId, CancellationToken.None);
        }
        finally
        {
            busy.Release(accountId, container);
        }

        // 这条钉住界面能看到定时备份：状态必须留在 runner 里供 GET 端点查询。
        Assert.NotNull(runner.Get(configId));
    }

    [Fact]
    public async Task Start_Still_Acquires_The_Busy_Lock()
    {
        var (accountId, configId, container) = await SeedAsync();
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();

        // 别人已持锁 → Start 必须失败并说明忙碌，行为与改动前一致。
        Assert.True(busy.TryAcquire(accountId, container, "Checking"));
        try
        {
            var state = runner.Start(configId);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (state.Status == RunStatus.Running && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.Equal(RunStatus.Failed, state.Status);
            Assert.Contains("busy", state.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }
}
