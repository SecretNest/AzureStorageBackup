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

    /// <summary>下达停止意愿。返回被叫停的运行，没有在跑则返回 null。</summary>
    private BackupRunState? RequestStop(
        int configId, StopKind kind, SuspendReason reason = SuspendReason.UserRequested)
    {
        BackupRunState? state;
        // Cancel()/RequestStop() 会在**当前线程**同步执行已注册的回调；放在 _lock 里的话，
        // 任一回调只要回头碰到这个 runner 就会自锁。锁只用来取那一条记录。
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running })
            return null;
        if (state.Control is { } control)
        {
            // 状态改成终态之前 control 就已经释放了（`await using` 先于 catch 块生效）。
            // 那一瞬间进来的停止请求什么也做不了——这一轮反正已经在收尾了，当作"没在跑"。
            try { control.RequestStop(kind, reason); }
            catch (ObjectDisposedException) { return null; }
        }
        else
            state.Cancellation.Cancel();   // 还没跑到建 control 那一步（解析配置阶段）
        return state;
    }

    /// <summary>立刻停（不等落盘）。保留给共用的 /cancel 端点与其它运行器同形。</summary>
    public bool Cancel(int configId) => RequestStop(configId, StopKind.StopNow) is not null;

    /// <summary>主动暂停：做完手上这件活，落盘，退出成 Suspended。等落盘完成才返回。</summary>
    /// <param name="reason">写进盘上标记的挂起理由。界面上按的暂停用默认值，关机路径传
    /// <see cref="SuspendReason.ShuttingDown"/>。</param>
    public async Task<bool> SuspendAsync(
        int configId, SuspendReason reason = SuspendReason.UserRequested, CancellationToken ct = default)
    {
        if (RequestStop(configId, StopKind.Suspend, reason) is not { } state)
            return false;
        await state.Completion.Task.WaitAsync(ct);
        return true;
    }

    /// <summary>
    /// 关机时等所有运行落盘的上限。三个数字是一串的，动其中一个就得回头看另外两个：
    /// <code>
    /// docker-compose stop_grace_period 45s  &gt;  HostOptions.ShutdownTimeout 30s  &gt;  这里的 20s
    /// </code>
    /// 45 &gt; 30：docker 的宽限期一到就是 SIGKILL，必须让 .NET 自己的超时先到，才还有机会把日志写出来。
    /// 30 &gt; 20：宿主等 <c>StopAsync</c> 也是有超时的，超了它不等、直接往下拆服务——那时连"谁没停下来"
    /// 都没人记得下。留出的这 10 秒是给下面那条警告日志和其余宿主服务收尾用的。
    /// <para>
    /// <c>internal</c> 而非 <c>private</c>：同 <see cref="Endpoints.BackupConfigEndpoints.StopWaitCap"/>
    /// 的先例——测试项目靠 <c>InternalsVisibleTo</c>（见 AssemblyInfo.cs）把 20 秒调成毫秒级，才测得起
    /// "到点还没落盘"那条分支，不用真的等 20 秒。跟那个先例一样，它是**进程内共享的可变静态字段**，
    /// 安全性靠两条没有代码强制的约定撑着：(1) 只有 GracefulSuspendTests.cs 这一个测试文件碰
    /// <c>SuspendAllAsync</c>，改这个字段不会绊到别的文件；(2) xUnit 同一个类里的 <c>[Fact]</c>
    /// 顺序执行，改字段的用例把它放进 try/finally 复原，不会和同类里的别的用例打架。
    /// 生产环境永远是 20 秒。
    /// </para>
    /// </summary>
    internal static TimeSpan SuspendWaitCap = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 让**所有**在跑的运行挂起，并等它们把 journal 落盘。返回真的停成
    /// <see cref="RunStatus.Suspended"/> 的运行数。
    /// <para>
    /// 关机路径专用，而且要如实说清它做不到什么：<see cref="StopKind.Suspend"/> 有意不碰 AbortToken，
    /// 消费循环也是在**下一件**活开始前才退出的，所以手上这件——可能是一个几 GB 的上传——会被放着跑完。
    /// 因此这里的等待是**有上限**的（<see cref="SuspendWaitCap"/>）：到点还没落盘的运行就丢在半路，
    /// 下次启动时它是一次**没有标记**的中断运行，得操作员自己按 Resume，不会被自动接着跑。
    /// </para>
    /// </summary>
    public async Task<int> SuspendAllAsync(SuspendReason reason, CancellationToken ct)
    {
        // 先在锁里把 id 抄一份出来。_runs 是把普通 Dictionary，不加锁地枚举它，一边有人登记新运行
        // 就会当场 InvalidOperationException——而这一下正落在关机路径上，没有第二次机会。
        // 抄完就放锁：RequestStop 会在**当前线程**同步跑取消回调，攥着锁进去必然自锁
        //（同 RequestStop 处的说明）。
        List<int> running;
        lock (_lock)
            running = [.. _runs.Where(kv => kv.Value.Status == RunStatus.Running).Select(kv => kv.Key)];

        // 分两趟：**先**把停止意愿发给每一个运行，**再**统一等它们落盘。
        // 合成一趟（发一个、等一个）在并发备份下是致命的：排头那个若正压着一个几 GB 的上传，
        // 它一个人就吃掉整个关机预算，后面的运行连 RequestStop 都收不到——没落盘、没标记、直接挨砍。
        // 发信号本身只是几次赋值加同步回调，一瞬间就能全发完。
        var pending = new List<(int ConfigId, BackupRunState State)>();
        foreach (var configId in running)
        {
            try
            {
                if (RequestStop(configId, StopKind.Suspend, reason) is { } state)
                    pending.Add((configId, state));
            }
            catch (Exception ex)
            {
                // 一个运行下达失败不能挡住别的运行——关机路径上没有第二次机会。
                // 日志按本类既有做法临时开一个 scope 取（这个类没有注入的 logger），
                // 而且只在出事那一次才开：正常关机一个 scope 都不必建。
                using var scope = scopes.CreateScope();
                scope.ServiceProvider.GetService<ILogger<BackupRunner>>()?
                    .LogWarning(ex, "Failed to suspend backup {ConfigId} during shutdown", configId);
            }
        }
        if (pending.Count == 0)
            return 0;

        using var capped = CancellationTokenSource.CreateLinkedTokenSource(ct);
        capped.CancelAfter(SuspendWaitCap);
        try
        {
            await Task.WhenAll(pending.Select(p => p.State.Completion.Task)).WaitAsync(capped.Token);
        }
        catch (OperationCanceledException)
        {
            // 超时（或调用方的 ct 先断）不往上抛：抛出去就没人写下面这条日志了，而事后想弄明白
            // "这一卷为什么没有标记"，就只剩这条日志能说清。但这条日志本身不能认错超时是谁的——
            // 同一个 catch 既接得住这里自己的 SuspendWaitCap（20s），也接得住调用方 ct 先断
            // （宿主的 ShutdownTimeout，30s）。两者的证据价值一样大，名字却不能张冠李戴：
            // 真到了排查"为什么没有标记"的时候，一条指错了截止时间的日志比没有日志更误导人。
            var stuck = pending.Where(p => !p.State.Completion.Task.IsCompleted).Select(p => p.ConfigId);
            using var scope = scopes.CreateScope();
            var logger = scope.ServiceProvider.GetService<ILogger<BackupRunner>>();
            if (capped.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // 是我们自己的 SuspendWaitCap 到点了：可以点名具体秒数。
                logger?.LogWarning(
                    "Gave up after {Seconds}s waiting for backup(s) {ConfigIds} to suspend; they are left "
                    + "mid-flight and will come back as interrupted runs to be resumed by hand",
                    SuspendWaitCap.TotalSeconds, string.Join(", ", stuck));
            }
            else
            {
                // 调用方的令牌先断了（宿主自己的 ShutdownTimeout），不是我们的 20 秒——措辞中立，
                // 不点名一个可能根本没到期的数字。
                logger?.LogWarning(
                    "Gave up waiting for backup(s) {ConfigIds} to suspend because shutdown was cancelled; "
                    + "they are left mid-flight and will come back as interrupted runs to be resumed by hand",
                    string.Join(", ", stuck));
            }
        }

        // 只数真的停成 Suspended 的。等超时的、以及被同时到达的 Stop now 抢先按成 Canceled 的，
        // 盘上都没有标记——把它们算进来，关机日志就在说大话。
        return pending.Count(p => p.State.Status == RunStatus.Suspended);
    }

    /// <summary>取消。<paramref name="finishCurrentFiles"/> 为 true 时等在途文件（含其全部分卷）传完。
    /// 用户要求"Cancel 要等落盘成功再返回"，所以这里一定要等到终态。</summary>
    public async Task<bool> CancelAsync(int configId, bool finishCurrentFiles, CancellationToken ct = default)
    {
        var kind = finishCurrentFiles ? StopKind.FinishCurrentFiles : StopKind.StopNow;
        if (RequestStop(configId, kind) is not { } state)
            return false;
        await state.Completion.Task.WaitAsync(ct);
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
