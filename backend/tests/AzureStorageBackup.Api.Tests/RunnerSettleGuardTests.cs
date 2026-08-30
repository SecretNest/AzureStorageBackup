using System.Net;
using System.Reflection;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// A run flips to Completed while its settlement writes (suspension clearing, report reconciliation, the
/// busy release itself) are still in flight. Start()'s only guard used to be `Status == Running` — so a
/// second Start landing in that window replaced `_runs[configId]` with a fresh state whose TryAcquire then
/// failed against the still-held busy lock: a repair that actually SUCCEEDED got reported as
/// "Failed — busy", its report unreachable, while the DB quietly recorded success. The guard has to be
/// "the previous run has fully exited", not "the previous run is no longer Running".
/// Same window, both runners; the DELETE /check endpoint's repair guard is pinned here too.
/// </summary>
public sealed class RunnerSettleGuardTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private static Dictionary<int, TState> RunsOf<TRunner, TState>(TRunner runner) where TRunner : class =>
        (Dictionary<int, TState>)typeof(TRunner)
            .GetField("_runs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runner)!;

    [Fact]
    public void Repair_Start_Returns_The_Completed_But_Unsettled_Run_Instead_Of_Clobbering_It()
    {
        var runner = factory.Services.GetRequiredService<RepairRunner>();
        var runs = RunsOf<RepairRunner, RepairRunState>(runner);
        const int configId = 91001;
        var completing = new RepairRunState { Status = RunStatus.Completed }; // settlement still in flight
        runs[configId] = completing;
        try
        {
            var again = runner.Start(configId, null, CloudCheckLevel.ExistenceSize, null, cleanupOrphans: false);
            Assert.Same(completing, again);

            completing.Settled = true;
            var fresh = runner.Start(configId, null, CloudCheckLevel.ExistenceSize, null, cleanupOrphans: false);
            Assert.NotSame(completing, fresh);
        }
        finally
        {
            runs.Remove(configId);
        }
    }

    [Fact]
    public void Check_Start_Returns_The_Completed_But_Unsettled_Run_Instead_Of_Clobbering_It()
    {
        var runner = factory.Services.GetRequiredService<CheckRunner>();
        var runs = RunsOf<CheckRunner, CheckRunState>(runner);
        const int configId = 91002;
        var completing = new CheckRunState { Status = RunStatus.Completed };
        runs[configId] = completing;
        try
        {
            var again = runner.Start(configId, null, new CheckOptions());
            Assert.Same(completing, again);

            completing.Settled = true;
            var fresh = runner.Start(configId, null, new CheckOptions());
            Assert.NotSame(completing, fresh);
        }
        finally
        {
            runs.Remove(configId);
        }
    }

    [Fact]
    public async Task Drop_Keeps_A_Row_That_A_Repair_Settled_Between_Observation_And_Write()
    {
        // The DELETE /check handler reads "is the row Pending?" and then awaits before DropAsync touches the
        // row. A deferred auto-repair can finish inside that gap and flip Pending → Repaired; acting on the
        // stale observation, DropAsync's else-branch would REMOVE the just-written "Repaired" history line.
        // When the caller observed Pending but the row has since settled, the drop must leave it alone —
        // the gate the user wanted open is open, and the history is the feature.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        const int configId = 91004;
        db.LastCheckRuns.Add(new LastCheckRun
        {
            BackupConfigId = configId,
            ReportJson = "{}",
            FinishedAt = DateTimeOffset.UtcNow,
            Resolution = CheckResolution.Repaired, // a repair settled it after the caller saw Pending
        });
        await db.SaveChangesAsync();
        try
        {
            var runner = factory.Services.GetRequiredService<CheckRunner>();
            Assert.True(await runner.DropAsync(configId, observedPending: true));

            var row = await db.LastCheckRuns.AsNoTracking()
                .FirstOrDefaultAsync(x => x.BackupConfigId == configId);
            Assert.NotNull(row); // the history line survives
            Assert.Equal(CheckResolution.Repaired, row!.Resolution);

            // Dismissing history knowingly (no Pending observation) still removes it.
            Assert.True(await runner.DropAsync(configId, observedPending: false));
            Assert.False(await db.LastCheckRuns.AsNoTracking().AnyAsync(x => x.BackupConfigId == configId));
        }
        finally
        {
            await db.LastCheckRuns.Where(x => x.BackupConfigId == configId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Dropping_A_Check_Report_Is_Refused_While_A_Repair_Is_Running()
    {
        // While a repair runs, its completion path is about to reconcile this config's LastCheckRun row
        // (ResolveAfterRepairAsync). LastCheckRun has no concurrency token, so a drop landing mid-repair
        // sets up last-write-wins: the resolve can overwrite the Dropped resolution and resurrect the
        // report the user just dismissed. The endpoint must refuse, exactly as it does for a running check.
        var client = factory.CreateClient();
        var runner = factory.Services.GetRequiredService<RepairRunner>();
        var runs = RunsOf<RepairRunner, RepairRunState>(runner);
        const int configId = 91003;
        runs[configId] = new RepairRunState { Status = RunStatus.Running };
        try
        {
            var response = await client.DeleteAsync($"/api/backup-configs/{configId}/check");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            runs.Remove(configId);
        }
    }
}
