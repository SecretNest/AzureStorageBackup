using System.Text.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>In-memory state of one check run.</summary>
public sealed class CheckRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>The report of the most recent completed run. **Kept around after the run finishes**: the user must
    /// be able to close the dialog, open it again and see the result, and re-running a content-level check means
    /// downloading the whole backup and rehashing it — the price is real egress traffic.</summary>
    public CheckReport? Report { get; set; }

    public string? Error { get; set; }

    /// <summary>What the current stage is doing (which object it is checking, how many so far, how fast).</summary>
    public StageProgress? Detail { get; set; }

    /// <summary>Internal machinery, not part of the HTTP contract: this run's cancellation source, used by the /cancel endpoint.</summary>
    internal CancellationTokenSource Cancellation { get; } = new();
}

public sealed record CheckRunResponse(string Status, CheckReport? Report, string? Error, StageProgress? Detail)
{
    public static CheckRunResponse From(CheckRunState s) => new(s.Status.ToString(), s.Report, s.Error, s.Detail);
}

/// <summary>
/// Background check runner: runs <see cref="BackupChecker"/> and keeps the state in memory for polling.
/// <para>
/// A check used to be a **synchronous endpoint**: the request hung until the check was done. A content-level check
/// downloads all the data and recomputes hashes, and a few hundred GB of backup takes hours — the browser and the
/// reverse proxy both time out first, and once the request is dropped the check ran for nothing; on top of that
/// there was no progress to look at the whole time. Moving to a background run + polling solves both at once.
/// </para>
/// Same shape as <see cref="RepairRunner"/>: it **holds <see cref="BackupBusyTracker"/> until completion** (a check
/// is an operation on that backup too, and scheduled tasks should skip it meanwhile), and fails outright when the
/// target is busy.
/// </summary>
public sealed class CheckRunner(IServiceScopeFactory scopes, BackupBusyTracker busy)
{
    private readonly Dictionary<int, CheckRunState> _runs = [];
    private readonly Lock _lock = new();

    public CheckRunState Start(int configId, int? version, CheckOptions options)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new CheckRunState();
            _runs[configId] = state;
            _ = Task.Run(() => RunAsync(configId, version, options, state));
            return state;
        }
    }

    public CheckRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    /// <summary>
    /// <see cref="Get"/>, falling back to the persisted last completed run when this process has none — the
    /// in-memory report survives closing the dialog, but not pulling a new image, and a restart must not force a
    /// re-run just to see a result that was already computed. Only the GET endpoint needs the fallback; the
    /// activity checks stay on <see cref="Get"/>, since a persisted run is by definition not Running.
    /// </summary>
    public async Task<CheckRunState?> GetOrLoadAsync(int configId, CancellationToken ct = default)
    {
        if (Get(configId) is { } live)
            return live;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.LastCheckRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.BackupConfigId == configId, ct);
        if (row is null)
            return null;
        try
        {
            return new CheckRunState
            {
                Status = RunStatus.Completed,
                Report = JsonSerializer.Deserialize<CheckReport>(row.ReportJson),
            };
        }
        catch (JsonException)
        {
            // A row an older build cannot read (the report shape moved on) is the same as no row: the check can
            // simply be re-run, which beats a 500 on a GET that fires every time the dialog opens.
            return null;
        }
    }

    /// <summary>Persist a run's report as the config's last completed check. Best-effort: a failed write leaves
    /// the in-memory state serving this process, exactly as before persistence existed.</summary>
    internal async Task PersistAsync(int configId, CheckRunState state)
    {
        if (state.Report is null)
            return; // failed runs carry no report, and must not clobber the last real result (see LastCheckRun)
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.LastCheckRuns.FirstOrDefaultAsync(x => x.BackupConfigId == configId);
            if (row is null)
                db.LastCheckRuns.Add(row = new LastCheckRun { BackupConfigId = configId });
            row.ReportJson = JsonSerializer.Serialize(state.Report);
            row.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        catch
        {
            // best effort by design
        }
    }

    /// <summary>Stop the check that is currently running. Returns false = nothing is running right now.
    /// Cancel() runs its callbacks synchronously on the calling thread, so the lock covers looking the record up but
    /// not the cancellation itself (see the same comment on BackupRunner.Cancel).</summary>
    public bool Cancel(int configId)
    {
        CheckRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running })
            return false;
        state.Cancellation.Cancel();
        return true;
    }

    private async Task RunAsync(int configId, int? version, CheckOptions options, CheckRunState state)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var configs = sp.GetRequiredService<IBackupConfigService>();
            var config = await configs.GetAsync(configId)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            var account = await sp.GetRequiredService<IAccountService>().GetAsync(config.AccountId)
                ?? throw new InvalidOperationException($"Account {config.AccountId} not found.");
            var settings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync();

            if (!busy.TryAcquire(account.Id, config.ContainerName, "Checking"))
            {
                state.Error = "This backup is busy with another operation.";
                state.Status = RunStatus.Failed;
                return;
            }
            try
            {
                state.Report = await sp.GetRequiredService<BackupChecker>().CheckAsync(
                    account, config.ContainerName, sp.GetRequiredService<ISecretReader>().RevealBackupPassword(config),
                    version, options, config.LocalRoot, config.SentinelPath, state.Cancellation.Token,
                    downloadConcurrency: settings.DownloadConcurrency > 0 ? settings.DownloadConcurrency : 5,
                    onProgress: d => state.Detail = d,
                    headConcurrency: settings.CheckHeadConcurrency > 0 ? settings.CheckHeadConcurrency : 20);
                state.Status = RunStatus.Completed;
            }
            finally
            {
                busy.Release(account.Id, config.ContainerName);
            }
            // A check that ran to completion counts as success, whether or not it found problems; only an
            // exception sets Error (decision 2).
            await configs.WriteStatusAsync(configId, error: null, sp.GetService<ILogger<CheckRunner>>());
            await PersistAsync(configId, state);
        }
        catch (OperationCanceledException)
        {
            // The user pressed stop: not a failure, so no Error state is written (the same convention as
            // BackupRunner, and consistent with the old synchronous /check endpoint's "cancel writes no Error").
            state.Status = RunStatus.Canceled;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
            // The original scope may already have been disposed by the exception (`using var scope` disposes when
            // the try block exits): open another one to write the status.
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .WriteStatusAsync(configId, ex.Message, scope.ServiceProvider.GetService<ILogger<CheckRunner>>());
        }
    }
}
