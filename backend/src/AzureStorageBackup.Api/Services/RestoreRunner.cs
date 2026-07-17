namespace AzureStorageBackup.Api.Services;

/// <summary>一次还原运行的内存状态。</summary>
public sealed class RestoreRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;
    public RestoreResult? Result { get; set; }
    public string? Error { get; set; }
}

public sealed record RestoreRunResponse(
    string Status, int? Version, int? RestoredFiles, int? SkippedFiles, string? Error)
{
    public static RestoreRunResponse From(RestoreRunState s) => new(
        s.Status.ToString(), s.Result?.Version, s.Result?.RestoredFiles, s.Result?.SkippedFiles, s.Error);
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
        IReadOnlyDictionary<string, int>? substitutions = null)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new RestoreRunState();
            _runs[configId] = state;
            _ = Task.Run(() => RunAsync(configId, targetRoot, version, substitutions, state));
            return state;
        }
    }

    public RestoreRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    private async Task RunAsync(int configId, string targetRoot, int? version,
        IReadOnlyDictionary<string, int>? substitutions, RestoreRunState state)
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

            // 不占忙碌锁：还原与备份可并行。
            state.Result = await orchestrator.RunAsync(new RestoreRequest
            {
                Account = account,
                Container = config.ContainerName,
                TargetRoot = targetRoot,
                Password = string.IsNullOrEmpty(config.Password) ? null : config.Password,
                Version = version,
                DownloadConcurrency = settings.DownloadConcurrency > 0 ? settings.DownloadConcurrency : 5,
                Substitutions = substitutions ?? new Dictionary<string, int>(StringComparer.Ordinal),
            });
            state.Status = RunStatus.Completed;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
        }
    }
}
