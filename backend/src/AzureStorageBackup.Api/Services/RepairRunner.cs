using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>In-memory state of one repair run.</summary>
public sealed class RepairRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;
    public RepairReport? Report { get; set; }
    public string? Error { get; set; }

    /// <summary>Internal machinery, not part of the HTTP contract: this run's cancellation source, used by the /cancel endpoint.</summary>
    internal CancellationTokenSource Cancellation { get; } = new();
}

public sealed record RepairRunResponse(
    string Status, IReadOnlyList<string>? Repaired, IReadOnlyList<string>? Unrecoverable,
    IReadOnlyList<string>? DeletedOrphans, string? Error)
{
    public static RepairRunResponse From(RepairRunState s) => new(
        s.Status.ToString(), s.Report?.Repaired, s.Report?.Unrecoverable, s.Report?.DeletedOrphans, s.Error);
}

/// <summary>
/// Background repair runner: runs <see cref="BackupRepairer"/> and keeps the state in memory for polling.
/// It **holds <see cref="BackupBusyTracker"/> until completion** — a repair rewrites blobs/indexes and touches
/// dedup-shared objects, so it must be exclusive: while it runs, that backup can do no backup, check or other repair
/// (a user requirement). Fails outright when the target is busy.
/// </summary>
public sealed class RepairRunner(IServiceScopeFactory scopes, BackupBusyTracker busy)
{
    private readonly Dictionary<int, RepairRunState> _runs = [];
    private readonly Lock _lock = new();

    public RepairRunState Start(int configId, int? version, CloudCheckLevel cloud, StorageTier? rehydrate, bool cleanupOrphans)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new RepairRunState();
            _runs[configId] = state;
            _ = Task.Run(() => RunAsync(configId, version, cloud, rehydrate, cleanupOrphans, state));
            return state;
        }
    }

    public RepairRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
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

    private async Task RunAsync(int configId, int? version, CloudCheckLevel cloud, StorageTier? rehydrate, bool cleanupOrphans, RepairRunState state)
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
                    // Takes the same rule set as BackupRequestMapper.From: the repaired archive must use the same
                    // compression mode a fresh backup writes.
                    BackupRequestMapper.OptionalRules(resolved.DontCompressRules), state.Cancellation.Token);
                state.Status = RunStatus.Completed;
            }
            finally
            {
                busy.Release(account.Id, config.ContainerName);
            }
            await configs.WriteStatusAsync(configId, error: null, sp.GetService<ILogger<RepairRunner>>());
        }
        catch (OperationCanceledException)
        {
            // The user pressed stop: not a failure, so no Error state is written (the same convention as BackupRunner).
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
                .WriteStatusAsync(configId, ex.Message, scope.ServiceProvider.GetService<ILogger<RepairRunner>>());
        }
    }
}
