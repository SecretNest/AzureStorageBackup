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

    /// <summary>
    /// 界面用：解析配置 → 抢忙碌锁 → 登记 → 后台跑。同一配置已在运行则返回现有状态。
    /// 解析配置需要异步 I/O，故本方法整体是 async：必须先拿到锁再登记进 _runs（见下）。
    /// </summary>
    public async Task<BackupRunState> StartAsync(int configId)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;
        }

        int accountId;
        string container;
        try
        {
            using var scope = scopes.CreateScope();
            var config = await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().GetAsync(configId)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            accountId = config.AccountId;
            container = config.ContainerName;
        }
        catch (Exception ex)
        {
            var failed = new BackupRunState { Error = ex.Message, Status = RunStatus.Failed };
            failed.Completion.TrySetResult();
            return failed;
        }

        // 标记该备份忙碌（供计划任务检测），已忙碌则拒绝并发操作。
        if (!busy.TryAcquire(accountId, container, "BackingUp"))
        {
            var failed = new BackupRunState { Error = "This backup is busy with another operation.", Status = RunStatus.Failed };
            failed.Completion.TrySetResult();
            return failed;
        }

        // _runs 只在已经拿到忙碌锁之后才写入：旧实现是先登记、后抢锁，两者之间有一个
        // 窗口——_runs 里已经出现一条 Running 记录，但没有锁在保护它。调度器
        // （TaskDispatcher.DispatchAsync）恰好会在这个窗口里抢到本该被这里持有的锁，
        // 随后 RunTrackedAsync 看到这条“Running”记录就把这一轮调度当成“已有一次真正
        // 在跑的备份”而转去等它，自己什么也不执行；而这边随后的 TryAcquire 必然落空，
        // 把这个共享 state 标记成 Failed——于是整轮调度什么都没跑，却被记成了出错。
        // 现在锁必须先到手，_runs 才会写入，一条 Running 记录就永远意味着真的有一次
        // 运行持有着锁，这个窗口也就不存在了。
        var state = new BackupRunState();
        lock (_lock)
            _runs[configId] = state;

        _ = Task.Run(async () =>
        {
            try
            {
                await RunCoreAsync(configId, state, CancellationToken.None);
            }
            finally
            {
                busy.Release(accountId, container);
            }
        });

        return state;
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
