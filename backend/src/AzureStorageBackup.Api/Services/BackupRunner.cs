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

    /// <summary>
    /// 内部机制，不进 HTTP 契约：该次运行到达终态（Completed/Failed）时触发一次。
    /// 供 RunTrackedAsync 的短路分支等待，不给前端轮询用。
    /// </summary>
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>界面用：抢忙碌锁并在后台跑。同一配置已在运行则返回现有状态。</summary>
    public BackupRunState Start(int configId)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new BackupRunState();
            _runs[configId] = state;
            _ = Task.Run(() => RunOwningLockAsync(configId, state));
            return state;
        }
    }

    /// <summary>
    /// 调度器用：调用方**已持有**该 (account, container) 的忙碌锁
    /// （TaskDispatcher.DispatchAsync 在进入执行前就抢了）。本方法不抢也不释放，
    /// 只负责执行并把状态登记进 _runs 供 GET 端点轮询。
    ///
    /// 锁的归属由「调用哪个方法」表达，而不是由一个布尔参数表达：布尔值传错一次，
    /// 不是每次定时备份都拒跑，就是锁根本没人持有，而两种都不会在编译期暴露。
    /// </summary>
    public async Task<BackupRunState> RunTrackedAsync(int configId, CancellationToken ct)
    {
        BackupRunState state;
        bool alreadyRunning;
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
            {
                state = existing;
                alreadyRunning = true;
            }
            else
            {
                state = new BackupRunState();
                _runs[configId] = state;
                alreadyRunning = false;
            }
        }

        if (alreadyRunning)
        {
            // 调用方约定本方法只返回终态：若原样返回这个仍是 Running 的 state，
            // 调度器「Status == Failed 才算失败」的判断会把这次根本没跑的备份
            // 当成静默成功。等它跑到终态（Completed/Failed）再返回。
            await state.Completion.Task;
            return state;
        }

        await RunCoreAsync(configId, state, ct);
        return state;
    }

    public BackupRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    /// <summary>Start 的执行体：抢锁 → 跑 → 释放。</summary>
    private async Task RunOwningLockAsync(int configId, BackupRunState state)
    {
        int accountId;
        string container;
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var config = await sp.GetRequiredService<IBackupConfigService>().GetAsync(configId)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            accountId = config.AccountId;
            container = config.ContainerName;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
            state.Completion.TrySetResult();
            return;
        }

        // 标记该备份忙碌（供计划任务检测），已忙碌则拒绝并发操作。
        if (!busy.TryAcquire(accountId, container, "BackingUp"))
        {
            state.Error = "This backup is busy with another operation.";
            state.Status = RunStatus.Failed;
            state.Completion.TrySetResult();
            return;
        }

        try
        {
            await RunCoreAsync(configId, state, CancellationToken.None);
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }

    /// <summary>两个入口共用的执行体。**不碰忙碌锁**——锁由调用方负责。</summary>
    private async Task RunCoreAsync(int configId, BackupRunState state, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var configs = sp.GetRequiredService<IBackupConfigService>();

            var config = await configs.GetAsync(configId, ct)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            var account = await sp.GetRequiredService<IAccountService>().GetAsync(config.AccountId, ct)
                ?? throw new InvalidOperationException($"Account {config.AccountId} not found.");
            var settings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
            var password = sp.GetRequiredService<ISecretReader>().RevealBackupPassword(config);

            var result = await sp.GetRequiredService<BackupOrchestrator>().RunAsync(
                BackupRequestMapper.From(config, account, password, settings), new StateProgress(state), ct);
            state.Version = result.Version;
            state.Status = RunStatus.Completed;

            await configs.WriteStatusAsync(configId, error: null, sp.GetService<ILogger<BackupRunner>>());
            state.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
            // 原 scope 可能已随异常释放（`using var scope` 在 try 块退出时释放）：另开一个写状态。
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .WriteStatusAsync(configId, ex.Message, scope.ServiceProvider.GetService<ILogger<BackupRunner>>());
            state.Completion.TrySetResult();
        }
    }

    private sealed class StateProgress(BackupRunState state) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => state.Progress = value;
    }
}
