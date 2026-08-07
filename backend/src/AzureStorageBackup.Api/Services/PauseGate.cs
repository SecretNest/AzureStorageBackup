namespace AzureStorageBackup.Api.Services;

/// <summary>挂起中的现场，给前端看的。</summary>
/// <param name="Reason">触发挂起的那条错误消息。</param>
/// <param name="Since">这一轮挂起是什么时候开始的。</param>
/// <param name="NextRetryAt">自愈计时器下一次放行的时刻。</param>
/// <param name="Failures">连续第几次出事（成功一次即清零）。</param>
public sealed record PauseInfo(string Reason, DateTimeOffset Since, DateTimeOffset? NextRetryAt, int Failures);

/// <summary>
/// 瞬时错误的挂起闸门。撞上网络/云端抖动的工作者在这里原地等，而不是把整轮备份判死。
/// <para>
/// 第一个出事的工作者开闸门并起自愈计时器；后到的一起等同一个信号。计时器到点、
/// 或用户点 <c>Retry now</c>，所有等待者一起放行重试。
/// </para>
/// <para>
/// <see cref="ReportSuccess"/> 是关键的一味：只要还有工作者在正常干活，网络就是通的，
/// 失败计数与耐心计时一并清零。否则一个始终传不上去的倒霉文件会把整轮好端端的备份拖去降级。
/// </para>
/// <para>
/// 耐心用尽则降级：<see cref="WaitAsync"/> 返回 false，调用方据此走"挂起退出"——
/// 落盘 journal、放掉暂存席位与产出锁。不这么做，一个挂起的运行会一直占着全局暂存额度，
/// 把并行的其它备份**完全**卡死（StagingArea 的额度闸门是全局的，不分席位）。
/// </para>
/// </summary>
public sealed class PauseGate : IDisposable
{
    private static readonly TimeSpan[] DefaultSchedule =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private readonly IReadOnlyList<TimeSpan> _schedule;
    private readonly TimeSpan _steady;
    private readonly TimeSpan _patience;
    private readonly Lock _lock = new();

    /// <summary>整个闸门的寿命。挂着的 5 分钟 Task.Delay 绝不能比运行活得还久。</summary>
    private readonly CancellationTokenSource _life = new();

    private TaskCompletionSource<bool>? _release;   // 非 null = 此刻正挂着
    private CancellationTokenSource? _timer;
    private int _failures;
    private DateTimeOffset? _troubleSince;          // null = 眼下没在出事（成功清零）
    private PauseInfo? _current;
    private bool _downgraded;

    public PauseGate(
        IReadOnlyList<TimeSpan>? schedule = null, TimeSpan? steady = null, TimeSpan? patience = null)
    {
        _schedule = schedule is { Count: > 0 } ? schedule : DefaultSchedule;
        _steady = steady ?? TimeSpan.FromMinutes(5);
        _patience = patience ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>此刻的挂起现场；没挂着就是 null。</summary>
    public PauseInfo? Current { get { lock (_lock) return _current; } }

    public bool IsDowngraded { get { lock (_lock) return _downgraded; } }

    /// <summary>
    /// 在闸门前等。
    /// </summary>
    /// <returns>true = 放行，去重试；false = 已降级，调用方该走挂起退出了。</returns>
    /// <exception cref="OperationCanceledException">用户取消了运行。取消永远赢。</exception>
    public async Task<bool> WaitAsync(Exception cause, CancellationToken ct)
    {
        Task<bool> release;
        lock (_lock)
        {
            if (_downgraded)
                return false;
            release = _release?.Task ?? OpenLocked(cause);
        }
        return await release.WaitAsync(ct);
    }

    /// <summary>用户点了 <c>Retry now</c>：不等计时器，现在就放，并当作重新开始（退避与耐心一并归零）。</summary>
    public void ReleaseNow()
    {
        lock (_lock)
        {
            _failures = 0;
            _troubleSince = null;
            ReleaseLocked(true);
        }
    }

    /// <summary>有工作者干成了一件活。网络是通的，把失败计数与耐心计时清零。</summary>
    public void ReportSuccess()
    {
        lock (_lock)
        {
            _failures = 0;
            _troubleSince = null;
        }
    }

    /// <summary>降级：用户点了 Suspend，或耐心用尽。所有等待者收到 false。</summary>
    public void Downgrade()
    {
        lock (_lock)
            DowngradeLocked();
    }

    private Task<bool> OpenLocked(Exception cause)
    {
        var now = DateTimeOffset.UtcNow;
        _troubleSince ??= now;
        _failures++;

        if (now - _troubleSince.Value >= _patience)
        {
            DowngradeLocked();
            return Task.FromResult(false);
        }

        var delay = DelayFor(_failures);
        _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _current = new PauseInfo(cause.Message, now, now + delay, _failures);
        _timer = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);

        var token = _timer.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) { return; }   // 提前放行 / 降级 / 闸门没了
            lock (_lock)
            {
                // 到点了先问一句：这一轮麻烦持续得是不是已经超过耐心了？
                // 只在开闸时判是不够的——最后一次退避可能长达 5 分钟。
                if (_troubleSince is { } since && DateTimeOffset.UtcNow - since >= _patience)
                    DowngradeLocked();
                else
                    ReleaseLocked(true);
            }
        }, CancellationToken.None);

        return _release.Task;
    }

    /// <summary>退避表用完之后按固定间隔继续，别无限翻倍成几个小时。</summary>
    private TimeSpan DelayFor(int failures)
        => failures <= _schedule.Count ? _schedule[failures - 1] : _steady;

    private void ReleaseLocked(bool proceed)
    {
        _timer?.Cancel();
        _timer?.Dispose();
        _timer = null;
        _current = null;
        var tcs = _release;
        _release = null;
        tcs?.TrySetResult(proceed);
    }

    private void DowngradeLocked()
    {
        _downgraded = true;
        ReleaseLocked(false);
    }

    public void Dispose()
    {
        lock (_lock)
            DowngradeLocked();
        _life.Cancel();
        _life.Dispose();
    }
}
