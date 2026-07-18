namespace AzureStorageBackup.Api.Services;

public enum RunStatus
{
    Running,
    Completed,
    Failed,
}

/// <summary>一次备份运行的内存状态（前端轮询用）。</summary>
public sealed class BackupRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;
    public BackupProgress? Progress { get; set; }
    public int? Version { get; set; }
    public string? Error { get; set; }
}

public sealed record BackupRunResponse(string Status, BackupProgress? Progress, int? Version, string? Error)
{
    public static BackupRunResponse From(BackupRunState s) =>
        new(s.Status.ToString(), s.Progress, s.Version, s.Error);
}

/// <summary>
/// 后台备份运行器：按配置 id 在后台跑 BackupOrchestrator，进度存内存供轮询。
/// 同一配置正在运行时不重复启动。压缩全局非并发由单例 StagingArea 保证。
/// </summary>
public sealed class BackupRunner(IServiceScopeFactory scopes, BackupBusyTracker busy)
{
    private readonly Dictionary<int, BackupRunState> _runs = [];
    private readonly Lock _lock = new();

    public BackupRunState Start(int configId)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new BackupRunState();
            _runs[configId] = state;
            _ = Task.Run(() => RunAsync(configId, state));
            return state;
        }
    }

    public BackupRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    private async Task RunAsync(int configId, BackupRunState state)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var configs = sp.GetRequiredService<IBackupConfigService>();
            var accounts = sp.GetRequiredService<IAccountService>();
            var settingsSvc = sp.GetRequiredService<IGlobalSettingsService>();
            var orchestrator = sp.GetRequiredService<BackupOrchestrator>();

            var config = await configs.GetAsync(configId)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            var account = await accounts.GetAsync(config.AccountId)
                ?? throw new InvalidOperationException($"Account {config.AccountId} not found.");
            var settings = await settingsSvc.GetAsync();

            // 标记该备份忙碌（供计划任务检测），已忙碌则拒绝并发操作。
            if (!busy.TryAcquire(account.Id, config.ContainerName, "BackingUp"))
            {
                state.Error = "This backup is busy with another operation.";
                state.Status = RunStatus.Failed;
                return;
            }
            try
            {
                var result = await orchestrator.RunAsync(
                    BackupRequestMapper.From(config, account, settings), new StateProgress(state), CancellationToken.None);
                state.Version = result.Version;
                state.Status = RunStatus.Completed;
            }
            finally
            {
                busy.Release(account.Id, config.ContainerName);
            }
            await WriteStatusAsync(configs, configId, error: null);
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
            // 原 scope 可能已随异常释放（`using var scope` 在 try 块退出时释放）：另开一个写状态。
            using var scope = scopes.CreateScope();
            await WriteStatusAsync(scope.ServiceProvider.GetRequiredService<IBackupConfigService>(), configId, ex.Message);
        }
    }

    /// <summary>
    /// 状态落库（决策 2）：成功 → Normal；失败 → Error + 消息。Best-effort —
    /// 写状态本身失败不应把一次已确定成功/失败的运行结果掩盖或误判。
    /// </summary>
    private static async Task WriteStatusAsync(IBackupConfigService configs, int configId, string? error)
    {
        try
        {
            if (error is null)
                await configs.SetNormalAsync(configId);
            else
                await configs.SetErrorAsync(configId, error);
        }
        catch
        {
            // best-effort；不影响已确定的运行结果。
        }
    }

    private sealed class StateProgress(BackupRunState state) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => state.Progress = value;
    }
}
