namespace AzureStorageBackup.Api.Services;

/// <summary>一次还原运行的内存状态。</summary>
public sealed class RestoreRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;
    public RestoreResult? Result { get; set; }
    public string? Error { get; set; }

    /// <summary>当前阶段说明（如「等待活化…」），供前端显示长等待原因。最近一条。</summary>
    public string? Phase { get; set; }

    /// <summary>最近若干条事件。Phase 是单值，后一条覆盖前一条——跳过/失败了几十个文件时，
    /// 跑完只剩最后一条，其余只体现为一个计数。这里把它们留住。</summary>
    public RecentEvents Events { get; } = new();

    /// <summary>当前阶段在做什么（正在还原哪个包、已完成多少组、多快）。</summary>
    public StageProgress? Detail { get; set; }
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
/// 后台还原运行器：按配置 id 在后台跑 RestoreOrchestrator，状态存内存供轮询。
/// **不占用 BackupBusyTracker**——还原只读云端，可与备份并行；长时（如等 Archive 活化）也不挡备份（用户要求）。
/// 仅限每配置同时一个还原（避免同目标并发写）。
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

            // 不占忙碌锁：还原与备份可并行。遇 Archive 自动发起活化并轮询（可长等），完成后重新归档。
            // Phase 保留「最近一条」供一行摘要用；同一条同时进环形缓冲，这样跳过/失败的条目
            // 不会被后一条冲掉——它们恰恰是还原之后最需要逐条看的东西。
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
            }, ct: default, phase: progress, onProgress: d => state.Detail = d);
            state.Phase = null;
            state.Status = RunStatus.Completed;
            await configs.WriteStatusAsync(configId, error: null, sp.GetService<ILogger<RestoreRunner>>());
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
            // 原 scope 可能已随异常释放（`using var scope` 在 try 块退出时释放）：另开一个写状态。
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .WriteStatusAsync(configId, ex.Message, scope.ServiceProvider.GetService<ILogger<RestoreRunner>>());
        }
    }
}
