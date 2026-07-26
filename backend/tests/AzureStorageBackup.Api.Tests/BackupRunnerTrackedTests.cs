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
    public async Task RunTrackedAsync_Waits_For_A_Concurrent_Start_To_Reach_A_Terminal_State()
    {
        var (_, configId, _) = await SeedAsync();
        var runner = factory.Services.GetRequiredService<BackupRunner>();

        // StartAsync 拿到锁、登记完 _runs 后就把执行体丢进后台并返回；这里紧接着调用
        // RunTrackedAsync，大概率会撞上那个仍是 Running 的 state：它必须等到该 state
        // 跑到终态再返回，而不是把仍在 Running 的旧 state 原样递给调用方。
        await runner.StartAsync(configId);
        var state = await runner.RunTrackedAsync(configId, CancellationToken.None);

        Assert.NotEqual(RunStatus.Running, state.Status);
    }

    [Fact]
    public async Task Start_Still_Acquires_The_Busy_Lock()
    {
        var (accountId, configId, container) = await SeedAsync();
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();

        // 别人已持锁 → StartAsync 必须失败并说明忙碌，行为与改动前一致。
        Assert.True(busy.TryAcquire(accountId, container, "Checking"));
        try
        {
            var state = await runner.StartAsync(configId);
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

    [Fact]
    public async Task StartAsync_Does_Not_Register_A_Running_Entry_While_Locked_Out()
    {
        // 钉住本轮修复的核心不变式：_runs 只在已经拿到忙碌锁之后才写入。
        // 若倒回旧顺序（先登记、后抢锁），本测试会在 Get(configId) 那句断言上失败——
        // 调用方还没拿到锁，_runs 里却已经出现一条 Running 记录。
        var (accountId, configId, container) = await SeedAsync();
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();

        // 模拟调度器：调用方先持锁，StartAsync 必然抢不到。
        Assert.True(busy.TryAcquire(accountId, container, "BackingUp"));
        try
        {
            var state = await runner.StartAsync(configId);

            Assert.Equal(RunStatus.Failed, state.Status);
            Assert.Contains("busy", state.Error ?? "", StringComparison.OrdinalIgnoreCase);

            // 没有幽灵般的「Running」记录留在 _runs 里冒充一次真正在跑的备份。
            var registered = runner.Get(configId);
            Assert.True(registered is null || registered.Status != RunStatus.Running);
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }
}
