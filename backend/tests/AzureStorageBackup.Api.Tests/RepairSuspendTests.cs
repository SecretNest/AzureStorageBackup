using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The suspended repair's persistence contract (volume-identity.md § repair is a run): only the intent is
/// stored — the labels in the cloud are the actual resume state — and its existence outranks automation.
/// </summary>
public class RepairSuspendTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<int> ConfigAsync(string name)
    {
        var account = await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "acct-" + name + "-" + Guid.NewGuid().ToString("N")[..6], Description: null,
            BlobEndpoint: "https://t" + Guid.NewGuid().ToString("N")[..12] + ".blob.core.windows.net", Region: AzureRegion.Global,
            AccountKey: "dGVzdGtleQ==", UseProxy: false, ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null));
        var accountId = (await account.Content.ReadFromJsonAsync<AccountResponse>())!.Id;
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
                AccountId: accountId, ContainerName: name + "-container", Name: name, Description: null,
                LocalRoot: "/data/" + name, Password: null, IndexTier: StorageTier.Hot, DataTier: StorageTier.Cool)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        return created!.Id;
    }

    /// <summary>A restart must not turn a suspended repair into "never happened": with no run in this process's
    /// memory (this fresh host has never run one), the persisted intent alone makes GET answer Suspended — the
    /// resume button comes back with the process.</summary>
    [Fact]
    public async Task A_Persisted_Suspension_Survives_A_Restart_As_A_Suspended_State()
    {
        var configId = await ConfigAsync("susp-restart");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SuspendedRepairs.Add(new SuspendedRepair
            {
                BackupConfigId = configId, PathsJson = """["a.bin"]""",
                Cloud = CloudCheckLevel.ExistenceSize, SuspendedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var run = await (await _client.GetAsync($"/api/backup-configs/{configId}/repair"))
            .Content.ReadFromJsonAsync<RepairRunResponse>();
        Assert.Equal("Suspended", run!.Status);
    }

    /// <summary>Suspending when nothing runs is a 409, not a silent no-op: the user asked to preserve a run that
    /// does not exist, and pretending success would leave them believing an intent was stored.</summary>
    [Fact]
    public async Task Suspending_Nothing_Is_A_Conflict()
    {
        var configId = await ConfigAsync("susp-nothing");
        var res = await _client.PostAsync($"/api/backup-configs/{configId}/repair/suspend", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    /// <summary>Resuming without a persisted intent is likewise a 409 — there is nothing to replay.</summary>
    [Fact]
    public async Task Resuming_Nothing_Is_A_Conflict()
    {
        var configId = await ConfigAsync("resume-nothing");
        var res = await _client.PostAsync($"/api/backup-configs/{configId}/repair/resume", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    /// <summary>A suspended user repair outranks automation: the deferred-repair trigger must not start a run of
    /// its own while the intent row exists — a suspension is explicit intent, and its resume re-derives
    /// everything the trigger would have found.</summary>
    [Fact]
    public async Task The_Deferred_Trigger_Defers_To_A_Suspended_Repair()
    {
        var configId = await ConfigAsync("susp-defer");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SuspendedRepairs.Add(new SuspendedRepair
            {
                BackupConfigId = configId, PathsJson = "[]",
                Cloud = CloudCheckLevel.ExistenceSize, SuspendedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await factory.Services.GetRequiredService<DeferredRepairs>().TryStartAsync(configId);

        // No run was started for this config — the runner's memory holds nothing but the synthesized suspension.
        var runner = factory.Services.GetRequiredService<RepairRunner>();
        Assert.Null(runner.Get(configId));
    }
    /// <summary>A successful repair leaves two durable facts: the reconciled check gate (Repaired, history
    /// line, button back to Check) and the retired suspension row. Written as two separate commits, a crash
    /// between them left the gate already saying Repaired while the row still synthesized a Suspended state
    /// with a Resume button on the next start — and DeferredRepairs deferring to a run that had finished.
    /// The two facts commit as one transaction; the runner's post-flip clear stays as the ghost-row backstop.</summary>
    [Fact]
    public async Task The_Gate_Reconciliation_And_The_Suspension_Retirement_Commit_As_One()
    {
        var configId = await ConfigAsync("susp-atomic");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SuspendedRepairs.Add(new SuspendedRepair
            {
                BackupConfigId = configId, PathsJson = """["a.bin"]""",
                Cloud = CloudCheckLevel.ExistenceSize, SuspendedAt = DateTimeOffset.UtcNow,
            });
            db.LastCheckRuns.Add(new LastCheckRun
            {
                BackupConfigId = configId, ReportJson = "{}", FinishedAt = DateTimeOffset.UtcNow,
                Resolution = CheckResolution.Pending, UnrepairedCount = 1,
            });
            await db.SaveChangesAsync();
        }

        await factory.Services.GetRequiredService<CheckRunner>()
            .ResolveAfterRepairAsync(configId, unrecoverable: 0, clearSuspendedRepair: true);

        using var verify = factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var gate = await vdb.LastCheckRuns.AsNoTracking().SingleAsync(x => x.BackupConfigId == configId);
        Assert.Equal(CheckResolution.Repaired, gate.Resolution);
        Assert.False(await vdb.SuspendedRepairs.AsNoTracking().AnyAsync(x => x.BackupConfigId == configId));
    }

    /// <summary>The pause gate's three-way contract: it holds while paused, a lift releases the waiter, and a
    /// cancellation (stop or suspend) tears through it — a paused run must still be stoppable and suspendable,
    /// or Pause becomes a trap the operator can only escape by restarting the container.</summary>
    [Fact]
    public async Task The_Pause_Gate_Holds_Releases_And_Yields_To_Cancellation()
    {
        var state = new RepairRunState();

        // Not paused: passes straight through.
        await state.WaitWhilePausedAsync(CancellationToken.None);

        state.Pause();
        var waiting = state.WaitWhilePausedAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted); // held
        state.Unpause();
        await waiting; // released

        state.Pause();
        using var cts = new CancellationTokenSource();
        var held = state.WaitWhilePausedAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
    }

}
