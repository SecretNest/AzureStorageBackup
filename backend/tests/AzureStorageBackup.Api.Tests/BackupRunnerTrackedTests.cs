using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// RunTrackedAsync is for the scheduler: the caller already holds the busy lock, and this method must not grab it again.
/// If it grabbed the lock the way Start does, every scheduled backup would fail immediately — which is exactly the defect this round fixes.
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

        // Simulate the scheduler: the caller takes the lock first.
        Assert.True(busy.TryAcquire(accountId, container, "BackingUp"));
        try
        {
            var state = await runner.RunTrackedAsync(configId, CancellationToken.None);

            // The local root does not exist, so the backup will most likely fail — that is fine. What is being
            // asserted is that it did not fail on "could not get the busy lock", because that would mean it grabbed a
            // lock it had no business grabbing.
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

        // This pins down that the UI can see scheduled backups: the state has to stay in the runner for the GET endpoint to query.
        Assert.NotNull(runner.Get(configId));
    }

    [Fact]
    public async Task RunTrackedAsync_Waits_For_A_Concurrent_Start_To_Reach_A_Terminal_State()
    {
        var (_, configId, _) = await SeedAsync();
        var runner = factory.Services.GetRequiredService<BackupRunner>();

        // StartAsync takes the lock, registers into _runs, then throws the execution body into the background and
        // returns; calling RunTrackedAsync right afterwards will most likely run into that still-Running state, and it
        // must wait for that state to reach a terminal one before returning, rather than handing the caller back an
        // old state that is still Running.
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

        // Someone else already holds the lock → StartAsync must fail and say it is busy, exactly as it behaved before the change.
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
        // Pins down the core invariant of this round's fix: _runs is written only after the busy lock is in hand.
        // Revert to the old ordering (register first, grab the lock second) and this test fails on the Get(configId)
        // assertion — the caller has not got the lock yet, and yet a Running entry has already appeared in _runs.
        var (accountId, configId, container) = await SeedAsync();
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();

        // Simulate the scheduler: the caller takes the lock first, so StartAsync is bound to miss out.
        Assert.True(busy.TryAcquire(accountId, container, "BackingUp"));
        try
        {
            var state = await runner.StartAsync(configId);

            Assert.Equal(RunStatus.Failed, state.Status);
            Assert.Contains("busy", state.Error ?? "", StringComparison.OrdinalIgnoreCase);

            // No ghostly "Running" entry is left behind in _runs pretending to be a backup that is really running.
            var registered = runner.Get(configId);
            Assert.True(registered is null || registered.Status != RunStatus.Running);
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }
}
