namespace AzureStorageBackup.Api.Services;

public enum RunStatus
{
    Running,
    Completed,
    Failed,

    /// <summary>用户按了停止。既不算失败也不算成功：**不写 Error 状态**（否则这份备份此后一直
    /// 挂着一条红色 Error，还要手动 Reset 才消），也不记成一次成功的运行。</summary>
    Canceled,

    /// <summary>
    /// 现场保住了，活没干完。与 Failed 的区别很实在：journal 还在盘上，下一轮（用户点 Resume
    /// 或下次计划任务）会把已经传上去的内容原样认下来，不重传。
    /// <para>
    /// 注意这**只**用于运行真的退出了的时刻。瞬时错误等待重试期间状态仍是 Running（见
    /// <see cref="BackupRunState.Pause"/>）——那时 Task 还活着、席位还占着，报成终态会让调度器
    /// 以为这轮完了，再起一轮把它顶掉。
    /// </para>
    /// </summary>
    Suspended,
}

/// <summary>一次备份运行的内存状态（前端轮询用）。</summary>
public sealed class BackupRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;
    public BackupProgress? Progress { get; set; }
    public int? Version { get; set; }

    /// <summary>本轮读不开、因而沿用了旧索引条目的文件数。一次"成功"的备份可能什么都没存下来，
    /// 界面上不显示这个数字，操作员就只能靠通知——而通知会被别的消息淹没。</summary>
    public int? UnreadableFiles { get; set; }

    public string? Error { get; set; }

    /// <summary>本次备份的起止时刻，取自版本记录（见 <see cref="BackupRunResult.CompletedAt"/>）。
    /// 完成前为 null。</summary>
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>这一次运行的标识。journal 文件名就是它，恢复时按它对上号。</summary>
    public string RunId { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>挂起（Suspended）的缘由；没挂起就是 null。</summary>
    public SuspendReason? SuspendReason { get; set; }

    /// <summary>内部机制，不进 HTTP 契约：这次运行的把手，Suspend / Retry now 要靠它够到闸门。</summary>
    internal BackupRunControl? Control { get; set; }

    /// <summary>
    /// 眼下是不是卡在瞬时错误上等重试。**这不是一个状态值**：Status 仍是 Running，
    /// 因为 Task 还活着、席位还占着，报成终态会让调度器再起一轮把它顶掉。
    /// </summary>
    public PauseInfo? Pause => Control?.Gate.Current;

    /// <summary>
    /// 内部机制，不进 HTTP 契约：失败时的原始异常。RunCoreAsync 的 catch 里连 Error 一起设置，
    /// 供 TaskDispatcher 在向上抛出时挂作 InnerException——容器日志因此保留 Azure 异常自带的
    /// 状态码、请求 id 与真实堆栈，而不是只剩一句消息和从 throw 处开始的栈(Fix 4)。
    /// </summary>
    internal Exception? Failure { get; set; }

    /// <summary>
    /// 内部机制，不进 HTTP 契约：该次运行到达终态（Completed/Failed/Canceled）时触发一次。
    /// 供 RunTrackedAsync 的短路分支等待，不给前端轮询用。
    /// </summary>
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>内部机制，不进 HTTP 契约：本次运行的取消源，供 /cancel 端点用。
    /// 在此之前，一次跑了几小时的备份唯一的停法是重启容器——而用户跑在 NAS 上，
    /// 那会连带停掉别的服务；「正忙时不许删配置」又把删除这条退路也堵上了。</summary>
    internal CancellationTokenSource Cancellation { get; } = new();
}

public sealed record BackupRunResponse(
    string Status, BackupProgress? Progress, int? Version, int? UnreadableFiles, string? Error,
    DateTimeOffset? StartedAt = null, DateTimeOffset? CompletedAt = null,
    string RunId = "", PauseInfo? Pause = null, string? SuspendReason = null)
{
    public static BackupRunResponse From(BackupRunState s) =>
        new(s.Status.ToString(), s.Progress, s.Version, s.UnreadableFiles, s.Error, s.StartedAt, s.CompletedAt,
            s.RunId, s.Pause, s.SuspendReason?.ToString());
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
                await RunCoreAsync(configId, state, state.Cancellation.Token);
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
            // 在锁先于登记的顺序下这个分支目前不可达，纯属防御性保留；但如果它以后又
            // 变得可达，不带取消令牌的 await 会让调度器永远占着忙碌锁挂起，关机也无法
            // 打断它——带上 ct，让它至少能跟着关机一起收尾(Fix 5)。
            await state.Completion.Task.WaitAsync(ct);
            return state;
        }

        // 调度器的 ct（关机）与本次运行自己的取消源（用户按停止）二选一先到即算取消：
        // 定时备份同样能在界面上停掉——它跑的是同一条执行体，也一样可能跑上一整夜。
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, state.Cancellation.Token);
        await RunCoreAsync(configId, state, linked.Token);
        return state;
    }

    public BackupRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    /// <summary>停止正在跑的那次备份。返回 false = 当前没有在跑的运行。</summary>
    public bool Cancel(int configId)
    {
        BackupRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running })
            return false;
        // Cancel() 会在**当前线程**同步执行已注册的回调；放在 _lock 里的话，任一回调只要回头
        // 碰到这个 runner 就会自锁。锁只用来取那一条记录。
        state.Cancellation.Cancel();
        return true;
    }

    /// <summary>用户点了 <c>Retry now</c>：不等自愈计时器，立刻放行重试。</summary>
    public bool RetryNow(int configId)
    {
        BackupRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running } || state.Pause is null)
            return false;
        state.Control!.Gate.ReleaseNow();
        return true;
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

            await using var control = new BackupRunControl(
                sp.GetRequiredService<BackupJournalStore>(), configId, state.RunId);
            state.Control = control;
            var result = await sp.GetRequiredService<BackupOrchestrator>().RunAsync(
                BackupRequestMapper.From(config, account, password, settings, sp.GetService<PackLimits>()),
                new StateProgress(state), ct, control);
            state.Version = result.Version;
            state.UnreadableFiles = result.UnreadableFiles;
            state.StartedAt = result.StartedAt;
            state.CompletedAt = result.CompletedAt;
            state.Status = RunStatus.Completed;

            await configs.WriteStatusAsync(configId, error: null, sp.GetService<ILogger<BackupRunner>>());
            state.Completion.TrySetResult();
        }
        catch (BackupSuspendedException ex)
        {
            // 不是失败：journal 还在盘上，Error 也不写（否则这份备份此后一直挂着红字，
            // 还要手动 Reset 才消），下一轮会把已传的内容原样认下来。
            state.Status = RunStatus.Suspended;
            state.SuspendReason = ex.Reason;
            // 和其它三个终态分支一样要放行等待者，否则 RunTrackedAsync 会一直挂在 Completion 上。
            state.Completion.TrySetResult();
        }
        catch (OperationCanceledException)
        {
            // 用户按了停止（或进程正在关停）：不是失败。既不写 Error 状态，也不写 Normal——
            // 这一轮什么结论都没有，落库的持久状态保持原样。
            state.Status = RunStatus.Canceled;
            state.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Failure = ex;
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
