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

    /// <summary>When the run completed — the "checked at" the dialog shows next to a clean verdict, so a
    /// report that found nothing still answers WHEN it found nothing.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>The persisted report's lifecycle state (see <see cref="Models.CheckResolution"/>); null for a
    /// live run that has not persisted yet.</summary>
    public CheckResolution? Resolution { get; set; }

    /// <summary>Problem files still unrepaired, for the "dropped (N没修好)" line.</summary>
    public int UnrepairedCount { get; set; }

    /// <summary>Internal machinery, not part of the HTTP contract: this run's cancellation source, used by the /cancel endpoint.</summary>
    internal CancellationTokenSource Cancellation { get; } = new();
}

public sealed record CheckRunResponse(string Status, CheckReport? Report, string? Error, StageProgress? Detail,
    DateTimeOffset? FinishedAt = null, string? Resolution = null, int UnrepairedCount = 0)
{
    public static CheckRunResponse From(CheckRunState s) => new(
        s.Status.ToString(), s.Report, s.Error, s.Detail, s.FinishedAt, s.Resolution?.ToString(), s.UnrepairedCount);
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
                // A resolved row is HISTORY, not a plan: the dialog shows its one-line summary and offers a
                // fresh check; handing back the stale findings table would re-open a report already dealt with.
                Report = row.Resolution == CheckResolution.Pending
                    ? JsonSerializer.Deserialize<CheckReport>(row.ReportJson)
                    : null,
                FinishedAt = row.FinishedAt,
                Resolution = row.Resolution,
                UnrepairedCount = row.UnrepairedCount,
            };
        }
        catch (JsonException)
        {
            // A row an older build cannot read (the report shape moved on) is the same as no row: the check can
            // simply be re-run, which beats a 500 on a GET that fires every time the dialog opens.
            return null;
        }
    }

    /// <summary>Whether an ACTIONABLE report is persisted for this config — the gate that refuses further
    /// checks (manual and scheduled alike) until the report is repaired away or dropped. A persisted clean
    /// report gates nothing.</summary>
    public async Task<bool> HasPersistedReportAsync(int configId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LastCheckRuns.AsNoTracking()
            .AnyAsync(x => x.BackupConfigId == configId && x.Resolution == CheckResolution.Pending, ct);
    }

    /// <summary>The configs holding an ACTIONABLE report, in one query — the list endpoint decorates every row.</summary>
    public async Task<HashSet<int>> PersistedReportIdsAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.LastCheckRuns.AsNoTracking()
            .Where(x => x.Resolution == CheckResolution.Pending).Select(x => x.BackupConfigId).ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Drop the last check result — in-memory state and persisted row both, or reopening the dialog
    /// would resurrect what the user just dismissed. Refused (false) while a check is running: the result being
    /// dropped does not exist yet, and the run owns the state. Dropping is also how a clean verdict retires
    /// itself, and how a fully-successful repair retires the report it worked from.</summary>
    public async Task<bool> DropAsync(int configId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var live) && live.Status == RunStatus.Running)
                return false;
            _runs.Remove(configId);
        }
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.LastCheckRuns.FirstOrDefaultAsync(x => x.BackupConfigId == configId, ct);
        if (row is not null)
        {
            if (row.Resolution == CheckResolution.Pending)
            {
                // Dropping a pending report keeps the HISTORY ("drop了,N个没修好"): the row flips to Dropped
                // with the unrepaired count frozen, the gate opens, and the marks keep carrying the memory.
                row.Resolution = CheckResolution.Dropped;
            }
            else
            {
                db.LastCheckRuns.Remove(row); // dismissing history removes it outright
            }
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    /// <summary>A completed repair reconciles the report it worked from: everything fixed → Repaired (the
    /// gate opens, the history line says so); anything left → the row stays Pending with the fresh unrepaired
    /// count, and only a manual Drop dismisses it.</summary>
    internal async Task ResolveAfterRepairAsync(int configId, int unrecoverable, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.LastCheckRuns.FirstOrDefaultAsync(
            x => x.BackupConfigId == configId && x.Resolution == CheckResolution.Pending, ct);
        if (row is null)
            return;
        if (unrecoverable == 0)
            row.Resolution = CheckResolution.Repaired;
        row.UnrepairedCount = unrecoverable;
        await db.SaveChangesAsync(ct);
        lock (_lock)
            _runs.Remove(configId); // the in-memory report is a stale plan now; GET reloads the resolved row
    }

    /// <summary>Persist a run's report as the config's last completed check. Best-effort: a failed write leaves
    /// the in-memory state serving this process, exactly as before persistence existed.</summary>
    internal async Task PersistAsync(int configId, CheckRunState state)
    {
        if (state.Report is null)
            return; // failed runs carry no report, and must not clobber the last real result (see LastCheckRun)
        // Every completed report persists — a clean one too, or "the last check ran on <date> and found
        // nothing" would be visible nowhere after a restart ("没地方看到这个没有错误的报告"). What differs is the
        // GATE: only an ACTIONABLE report (problems to repair, orphans to judge) refuses further checks,
        // turns the button red and holds the orphan sweep; a clean one just sits there until the next check
        // replaces it.
        var problems = state.Report.Findings.Count(f => f.Cloud == CloudState.MissingOrBad);
        var actionable = problems > 0 || state.Report.OrphanBlobs.Count > 0;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.LastCheckRuns.FirstOrDefaultAsync(x => x.BackupConfigId == configId);
            if (row is null)
                db.LastCheckRuns.Add(row = new LastCheckRun { BackupConfigId = configId });
            row.ReportJson = JsonSerializer.Serialize(state.Report);
            row.FinishedAt = state.FinishedAt ?? DateTimeOffset.UtcNow;
            row.Resolution = actionable ? CheckResolution.Pending : CheckResolution.Clean;
            row.UnrepairedCount = problems;
            state.Resolution = row.Resolution;
            state.UnrepairedCount = problems;
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
                    headConcurrency: settings.CheckHeadConcurrency > 0 ? settings.CheckHeadConcurrency : 20,
                    markFindings: true); // discovery IS the marking moment — "check出来就应该标错"
                state.Status = RunStatus.Completed;
                state.FinishedAt = DateTimeOffset.UtcNow;
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
