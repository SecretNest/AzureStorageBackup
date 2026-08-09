namespace AzureStorageBackup.Api.Services;

/// <summary>In-memory state of a single restore run.</summary>
public sealed class RestoreRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;
    public RestoreResult? Result { get; set; }
    public string? Error { get; set; }

    /// <summary>Description of the current phase (e.g. "waiting for rehydration…"), so the frontend can show why a long wait is happening. The most recent one only.</summary>
    public string? Phase { get; set; }

    /// <summary>The most recent handful of events. Phase is a single value and each new one overwrites the previous — when dozens of files are skipped/failed,
    /// only the last one survives the run and the rest show up as a bare count. This keeps them.</summary>
    public RecentEvents Events { get; } = new();

    /// <summary>What the current stage is doing (which pack is being restored, how many groups are done, how fast).</summary>
    public StageProgress? Detail { get; set; }

    /// <summary>Internal machinery, not part of the HTTP contract: this run's cancellation source, used by the /cancel endpoint.
    /// Restore needs it especially — waiting for Archive rehydration can take hours, and changing your mind midway would otherwise mean just sitting there.</summary>
    internal CancellationTokenSource Cancellation { get; } = new();
}

public sealed record RestoreRunResponse(
    string Status, int? Version, int? RestoredFiles, int? SkippedFiles, int? FailedFiles, string? Error, string? Phase,
    StageProgress? Detail = null, IReadOnlyList<string>? Events = null)
{
    public static RestoreRunResponse From(RestoreRunState s) => new(
        s.Status.ToString(), s.Result?.Version, s.Result?.RestoredFiles, s.Result?.SkippedFiles,
        s.Result?.FailedFiles, s.Error, s.Phase, s.Detail, s.Events.Snapshot());
}

/// <summary>
/// Background restore runner: runs RestoreOrchestrator in the background per config id, keeping state in memory for polling.
/// **Does not take BackupBusyTracker** — restore only reads from the cloud and can run alongside a backup; even a long one (e.g. waiting for Archive rehydration) must not block backups (user's requirement).
/// Limited to one restore per config at a time (to avoid concurrent writes to the same target).
/// </summary>
public sealed class RestoreRunner(IServiceScopeFactory scopes)
{
    private readonly Dictionary<int, RestoreRunState> _runs = [];
    private readonly Lock _lock = new();

    public RestoreRunState Start(int configId, string targetRoot, int? version,
        IReadOnlyDictionary<string, int>? substitutions = null,
        IReadOnlyList<string>? selectedPaths = null,
        RestoreConflictMode conflict = RestoreConflictMode.OverwriteIfChanged,
        RestoreRehydratePriority rehydratePriority = RestoreRehydratePriority.Standard)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new RestoreRunState();
            _runs[configId] = state;
            _ = Task.Run(() => RunAsync(configId, targetRoot, version, substitutions, selectedPaths, conflict, rehydratePriority, state));
            return state;
        }
    }

    public RestoreRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    /// <summary>Stops the restore that is currently running. Returns false = there is no restore running right now.
    /// Cancel() runs its callbacks synchronously on the current thread, so the lock is taken to look the record up but not to cancel (see the matching comment on BackupRunner.Cancel).</summary>
    public bool Cancel(int configId)
    {
        RestoreRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running })
            return false;
        state.Cancellation.Cancel();
        return true;
    }

    private async Task RunAsync(int configId, string targetRoot, int? version,
        IReadOnlyDictionary<string, int>? substitutions,
        IReadOnlyList<string>? selectedPaths, RestoreConflictMode conflict, RestoreRehydratePriority rehydratePriority,
        RestoreRunState state)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var configs = sp.GetRequiredService<IBackupConfigService>();
            var accounts = sp.GetRequiredService<IAccountService>();
            var settingsSvc = sp.GetRequiredService<IGlobalSettingsService>();
            var orchestrator = sp.GetRequiredService<RestoreOrchestrator>();

            var config = await configs.GetAsync(configId)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            var account = await accounts.GetAsync(config.AccountId)
                ?? throw new InvalidOperationException($"Account {config.AccountId} not found.");
            var settings = await settingsSvc.GetAsync();

            // No busy lock: restore and backup can run in parallel. On Archive, rehydration is started and polled automatically (possibly a long wait), then re-archived once done.
            // Phase keeps "the most recent one" for the single-line summary; the same message also goes into the ring buffer, so skipped/failed entries
            // don't get flushed away by the next one — those are exactly what you most want to read one by one after a restore.
            var progress = new Progress<string>(p =>
            {
                state.Phase = p;
                state.Events.Add(p);
            });
            state.Result = await orchestrator.RunAsync(new RestoreRequest
            {
                Account = account,
                Container = config.ContainerName,
                TargetRoot = targetRoot,
                Password = sp.GetRequiredService<ISecretReader>().RevealBackupPassword(config),
                Version = version,
                DownloadConcurrency = settings.DownloadConcurrency > 0 ? settings.DownloadConcurrency : 5,
                Substitutions = substitutions ?? new Dictionary<string, int>(StringComparer.Ordinal),
                SelectedPaths = selectedPaths,
                Conflict = conflict,
                RehydratePriority = rehydratePriority,
            }, ct: state.Cancellation.Token, phase: progress, onProgress: d => state.Detail = d);
            state.Phase = null;
            state.Status = RunStatus.Completed;
            await configs.WriteStatusAsync(configId, error: null, sp.GetService<ILogger<RestoreRunner>>());
        }
        catch (OperationCanceledException)
        {
            // The user hit stop: this is not a failure, so don't write an Error status (same convention as BackupRunner).
            // Files already written to disk stay — restore writes file by file, there is no such thing as a "rollback".
            state.Phase = null;
            state.Status = RunStatus.Canceled;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
            // The original scope may already have been disposed along with the exception (`using var scope` disposes when the try block exits): open another one to write the status.
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .WriteStatusAsync(configId, ex.Message, scope.ServiceProvider.GetService<ILogger<RestoreRunner>>());
        }
    }
}
