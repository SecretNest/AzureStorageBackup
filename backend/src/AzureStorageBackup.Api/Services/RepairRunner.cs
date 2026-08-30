using System.Text.Json;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>In-memory state of one repair run.</summary>
public sealed class RepairRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;
    public RepairReport? Report { get; set; }
    public string? Error { get; set; }

    /// <summary>What the current stage is doing (which file, how many, how fast) — the same shape and rendering
    /// the backup's rows use. A 100 GB file's repair has an honest floor of one full read plus one compression;
    /// this is what makes that floor look like work instead of a hang.</summary>
    public StageProgress? Detail { get; set; }

    /// <summary>The run's original request, kept so a suspension can persist the intent (only the intent — the
    /// labels in the cloud are the actual resume state; see <see cref="Models.SuspendedRepair"/>).</summary>
    internal (int? Version, CloudCheckLevel Cloud, StorageTier? Rehydrate, bool CleanupOrphans, IReadOnlyCollection<string>? OnlyPaths, IReadOnlyCollection<string>? AlsoMarkPaths) Request { get; set; }

    /// <summary>Set by <see cref="RepairRunner.Suspend"/> before cancelling, so RunAsync's cancellation handler
    /// can tell "yield to other work, keep the intent" apart from "the user gave up on this run".</summary>
    internal volatile bool SuspendRequested;

    /// <summary>Internal machinery, not part of the HTTP contract: this run's cancellation source, used by the /cancel endpoint.</summary>
    internal CancellationTokenSource Cancellation { get; } = new();

    /// <summary>The run row's Pause — in-memory only, like the backup's own: a hold is a live decision, not an
    /// intent worth surviving a restart (a restart lifts it, same as the backup's pause gate). The gate is
    /// awaited before each object and before each volume (see BackupRepairer's pauseGate), so a pause answers
    /// in seconds; whatever is already on the wire finishes its volume first.</summary>
    private volatile TaskCompletionSource? _pauseGate;

    public bool Paused => _pauseGate is not null;

    internal void Pause() => Interlocked.CompareExchange(ref _pauseGate,
        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);

    internal void Unpause() => Interlocked.Exchange(ref _pauseGate, null)?.TrySetResult();

    /// <summary>Parked while paused; a cancellation (stop or suspend) tears through it — a paused run must
    /// still be stoppable and suspendable, or Pause becomes a trap.</summary>
    internal async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (_pauseGate is { } gate)
            await gate.Task.WaitAsync(ct);
    }
}

public sealed record RepairRunResponse(
    string Status, IReadOnlyList<string>? Repaired, IReadOnlyList<string>? Unrecoverable,
    IReadOnlyList<string>? DeletedOrphans, string? Error, StageProgress? Detail, bool Paused = false)
{
    public static RepairRunResponse From(RepairRunState s) => new(
        s.Status.ToString(), s.Report?.Repaired, s.Report?.Unrecoverable, s.Report?.DeletedOrphans, s.Error, s.Detail,
        s.Paused);
}

/// <summary>
/// Background repair runner: runs <see cref="BackupRepairer"/> and keeps the state in memory for polling.
/// It **holds <see cref="BackupBusyTracker"/> until completion** — a repair rewrites blobs/indexes and touches
/// dedup-shared objects, so it must be exclusive: while it runs, that backup can do no backup, check or other repair
/// (a user requirement). Fails outright when the target is busy.
/// </summary>
public sealed class RepairRunner(IServiceScopeFactory scopes, BackupBusyTracker busy, CheckRunner? checks = null, OrphanSweeper? sweeper = null)
{
    private readonly Dictionary<int, RepairRunState> _runs = [];
    private readonly Lock _lock = new();

    public RepairRunState Start(int configId, int? version, CloudCheckLevel cloud, StorageTier? rehydrate, bool cleanupOrphans, IReadOnlyCollection<string>? onlyPaths = null, IReadOnlyCollection<string>? alsoMarkPaths = null)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new RepairRunState
            {
                Request = (version, cloud, rehydrate, cleanupOrphans, onlyPaths, alsoMarkPaths),
            };
            _runs[configId] = state;
            _ = Task.Run(() => RunAsync(configId, version, cloud, rehydrate, cleanupOrphans, onlyPaths, alsoMarkPaths, state));
            return state;
        }
    }

    /// <summary>Resume a suspended repair from its persisted intent. Null when nothing is suspended. The
    /// selection is replayed as-is; everything else is re-derived — the pre-check runs fresh, files healed in
    /// the meantime fall out via the healed-mark clearing, and half-replaced families are salvaged volume by
    /// volume by the verified skip. The row is deleted only when the resumed run completes, so a crash between
    /// resume and completion leaves the intent intact.</summary>
    public async Task<RepairRunState?> ResumeAsync(int configId, CancellationToken ct = default)
    {
        SuspendedRepair? row;
        using (var scope = scopes.CreateScope())
        {
            row = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .SuspendedRepairs.AsNoTracking().FirstOrDefaultAsync(x => x.BackupConfigId == configId, ct);
        }
        if (row is null)
            return null;
        var paths = JsonSerializer.Deserialize<string[]>(row.PathsJson) ?? [];
        var defers = JsonSerializer.Deserialize<string[]>(row.DeferPathsJson) ?? [];
        return Start(configId, version: null, row.Cloud, row.RehydrateTier, row.CleanupOrphans, paths, defers);
    }

    /// <summary>Suspend the running repair: persist the intent, then cancel. Returns false when nothing is
    /// running. The distinction from <see cref="Cancel"/> is exactly the persisted row — and the row is written
    /// **before** the cancel, so there is no window where the run has died and the intent exists nowhere.</summary>
    /// <summary>Hold the running repair (see <see cref="RepairRunState.Pause"/>). False = nothing is running.</summary>
    public bool Pause(int configId)
    {
        RepairRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running })
            return false;
        state.Pause();
        return true;
    }

    /// <summary>Lift the operator's hold. False = the repair is not paused (or not running).</summary>
    public bool Unpause(int configId)
    {
        RepairRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running, Paused: true })
            return false;
        state.Unpause();
        return true;
    }

    public async Task<bool> SuspendAsync(int configId)
    {
        RepairRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running })
            return false;
        var (_, cloud, rehydrateTier, cleanupOrphans, onlyPaths, alsoMarkPaths) = state.Request;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SuspendedRepairs.FirstOrDefaultAsync(x => x.BackupConfigId == configId);
            if (row is null)
                db.SuspendedRepairs.Add(row = new SuspendedRepair { BackupConfigId = configId });
            row.PathsJson = JsonSerializer.Serialize(onlyPaths ?? []);
            row.DeferPathsJson = JsonSerializer.Serialize(alsoMarkPaths ?? []);
            row.Cloud = cloud;
            row.RehydrateTier = rehydrateTier;
            row.CleanupOrphans = cleanupOrphans;
            row.SuspendedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        state.SuspendRequested = true;
        state.Cancellation.Cancel();
        // The run can complete NATURALLY between the status check above and the cancel landing — its own
        // completion path sets Completed and clears any persisted row it can see, but our row may land after
        // that clear, leaving a ghost suspension: DeferredRepairs defers to it forever, and a restart offers a
        // Resume button for a run that already finished. Completion is terminal, so one re-check settles it.
        if (state.Status == RunStatus.Completed)
        {
            await ClearSuspendedAsync(configId);
            return false;
        }
        return true;
    }

    /// <summary>Whether a suspended repair's intent is persisted for this config — the deference signal
    /// <see cref="DeferredRepairs"/> checks before starting an automatic one.</summary>
    public async Task<bool> HasSuspendedAsync(int configId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .SuspendedRepairs.AsNoTracking().AnyAsync(x => x.BackupConfigId == configId, ct);
    }

    private async Task ClearSuspendedAsync(int configId)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.SuspendedRepairs.FirstOrDefaultAsync(x => x.BackupConfigId == configId);
            if (row is not null)
            {
                db.SuspendedRepairs.Remove(row);
                await db.SaveChangesAsync();
            }
        }
        catch
        {
            // Best effort: a leftover row makes the next resume re-run a repair that finds nothing to do — cheap
            // and idempotent — while failing the completed run over bookkeeping would be absurd.
        }
    }

    public RepairRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    /// <summary><see cref="Get"/>, synthesizing a Suspended state from the persisted intent when this process
    /// holds no run — a restart must not turn a suspended repair into "never happened"; the resume button has to
    /// come back with the process.</summary>
    public async Task<RepairRunState?> GetOrSuspendedAsync(int configId, CancellationToken ct = default)
    {
        if (Get(configId) is { } live)
            return live;
        return await HasSuspendedAsync(configId, ct)
            ? new RepairRunState { Status = RunStatus.Suspended }
            : null;
    }

    /// <summary>Stop the repair that is currently running. Returns false = nothing is running right now.
    /// Cancel() runs its callbacks synchronously on the calling thread, so the lock covers looking the record up but
    /// not the cancellation itself (see the same comment on BackupRunner.Cancel).</summary>
    public bool Cancel(int configId)
    {
        RepairRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running })
            return false;
        state.Cancellation.Cancel();
        return true;
    }

    private async Task RunAsync(int configId, int? version, CloudCheckLevel cloud, StorageTier? rehydrate, bool cleanupOrphans, IReadOnlyCollection<string>? onlyPaths, IReadOnlyCollection<string>? alsoMarkPaths, RepairRunState state)
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

            if (!busy.TryAcquire(account.Id, config.ContainerName, "Repairing"))
            {
                state.Error = "This backup is busy with another operation.";
                state.Status = RunStatus.Failed;
                return;
            }
            try
            {
                var options = new CheckOptions
                {
                    Cloud = cloud,
                    // Explicit cast to AccessTier?: see the same comment on the /check endpoint in
                    // BackupConfigEndpoints.cs (this fixed a real production bug).
                    RehydrateTier = rehydrate is { } t ? (AccessTier?)BackupRequestMapper.MapTier(t) : null,
                    ListOrphans = cleanupOrphans,
                };
                // Goes through the same resolver as the backup path. This used to fall back to
                // settings.DefaultVolumeBytes while BackupRequestMapper fell back to null — so for one and the same
                // backup, the volume layout a repair wrote differed from the one a new backup wrote. The resolver
                // unified the two sides.
                var resolved = ResolvedBackupSettings.From(config, settings);
                state.Report = await sp.GetRequiredService<BackupRepairer>().RepairAsync(
                    account, config.ContainerName, sp.GetRequiredService<ISecretReader>().RevealBackupPassword(config),
                    config.LocalRoot, version, options, BackupRequestMapper.MapTier(config.DataTier),
                    resolved.VolumeBytes is > 0 ? resolved.VolumeBytes : null,
                    // The same joined rule set BackupRequestMapper.From consumes: the repaired archive must use
                    // the compression mode a fresh backup writes. OptionalRules(resolved.DontCompressRules) —
                    // the case-sensitive half alone — is the field incident this line used to be.
                    resolved.DontCompress(),
                    onlyPaths: onlyPaths, alsoMarkPaths: alsoMarkPaths,
                    onProgress: d => state.Detail = d,
                    uploadConcurrency: settings.UploadConcurrency > 0 ? settings.UploadConcurrency : 5,
                    pauseGate: state.WaitWhilePausedAsync,
                    ct: state.Cancellation.Token);
                state.Status = RunStatus.Completed;
                // Completion is the only thing that retires a persisted suspension: the intent has been carried
                // out (files healed meanwhile fell out via the healed-mark clearing; the rest were handled here).
                await ClearSuspendedAsync(configId);
                // And when EVERYTHING came out whole — nothing left unrecoverable or deferred — the check
                // report the repair worked from retires with it ("所有文件都完成上传修复了,那么应该自动Drop"):
                // the row's button goes back to Check. Anything still marked keeps the report, and only a
                // manual Drop dismisses it — the marks carry the memory either way.
                if (checks is not null && state.Report is { } done)
                {
                    // Reconcile the report the repair worked from: everything fixed → Repaired (history line,
                    // gate open) and the container gets the sweep the hold deferred; anything left → the row
                    // stays Pending with the fresh unrepaired count, red button and all.
                    var unrecoverable = done.Unrecoverable.Distinct().Count();
                    await checks.ResolveAfterRepairAsync(configId, unrecoverable);
                    if (unrecoverable == 0)
                        sweeper?.Kick(configId);
                }
            }
            finally
            {
                busy.Release(account.Id, config.ContainerName);
            }
            await configs.WriteStatusAsync(configId, error: null, sp.GetService<ILogger<RepairRunner>>());
        }
        catch (OperationCanceledException)
        {
            // Suspend and stop arrive on the same token; the flag tells them apart. Suspended keeps the persisted
            // intent (written before the cancel, so it is already on disk) and the resume button; Canceled is the
            // user giving up on this run — not a failure either way, so no Error state is written (the same
            // convention as BackupRunner).
            state.Status = state.SuspendRequested ? RunStatus.Suspended : RunStatus.Canceled;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
            // The original scope may already have been disposed by the exception (`using var scope` disposes when
            // the try block exits): open another one to write the status.
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .WriteStatusAsync(configId, ex.Message, scope.ServiceProvider.GetService<ILogger<RepairRunner>>());
        }
    }
}
