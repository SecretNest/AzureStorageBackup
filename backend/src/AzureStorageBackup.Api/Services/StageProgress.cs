using System.Collections.Concurrent;
using System.Diagnostics;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 某个阶段正在做什么。备份/还原/检查共用一套形状，阶段名各用各的。
/// <para>
/// 存在的理由：在此之前，界面上一个阶段只在**进入**时上报一次。首次备份的 Diffing 要把每个文件
/// 完整读一遍算 hash，1 TB 数据在 100 MB/s 的盘上就是三小时——全程一个不动的 0%，
/// 分不清是在干活还是挂死了（而 FIFO 那个 bug 恰好会真的挂死）。
/// </para>
/// </summary>
public sealed record StageProgress(
    string Stage,
    int Processed,
    /// <summary>0 = 总数未知（例如扫描还没走完，根本不知道有多少文件）。</summary>
    int Total,
    long Bytes,
    /// <summary>当前正在处理的那一个（串行阶段）。</summary>
    string? CurrentItem,
    /// <summary>正在并发处理的多个（上传/下载阶段）。</summary>
    IReadOnlyList<string> ActiveItems,
    long BytesPerSecond,
    /// <summary>已被工作线程领走、但还没开始推字节的件数（备份上传阶段＝正在压缩/暂存）。
    /// 这段时间可以长达几十秒（一箱 100 MB 过 7z -mx9），此前它在界面上完全不可见：
    /// 不在 <see cref="ActiveItems"/> 里、不产生字节，于是连测速窗口都是空的。</summary>
    int Preparing = 0,
    /// <summary>已排进队列、还没被工作线程领走的件数。</summary>
    int Queued = 0)
{
    public int? Percent => Total > 0 ? (int)Math.Min(100, 100L * Processed / Total) : null;

    /// <summary>按当前速度估算的剩余时间。速度为 0 或总数未知时为 null——
    /// 与其给一个瞎猜的数字，不如不显示。</summary>
    public TimeSpan? EstimatedRemaining =>
        Total > 0 && Processed > 0 && Processed < Total && BytesPerSecond > 0 && Bytes > 0
            ? TimeSpan.FromSeconds((double)Bytes / Processed * (Total - Processed) / BytesPerSecond)
            : null;
}

/// <summary>
/// 阶段进度的累加与**节流**。
/// <para>
/// 节流是必需的而不是优化：百万文件逐个上报会产生百万次对象分配，而人眼一秒也看不了几次。
/// 但阶段收尾时必须强制产出一次终态，否则进度会永远停在 99%——这类"差最后一下"的 bug
/// 在这个项目里已经出现过（见 onItem 计数那一轮）。
/// </para>
/// </summary>
public sealed class StageTracker(string stage, int total, Action<StageProgress> publish)
{
    private const int ThrottleMs = 200;
    private const int SpeedWindowMs = 10_000;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);
    // (毫秒, 累计字节) 采样，用于算最近一段时间的速度。文件大小差异很大时，
    // 全程平均值会长期偏离当下的实际速度，滚动窗口才对得上用户看到的现象。
    private readonly Queue<(long Ms, long Bytes)> _samples = new();
    private readonly Lock _gate = new();

    private int _processed;
    private int _total = total;
    private long _bytes;
    private string? _current;
    private long _lastPublishMs = -ThrottleMs;
    private int _enqueued;
    private int _inWork;

    /// <summary>把总数定下来。流水线化之后上传阶段的总数是**边跑边长出来的**（diff 还在往队列里
    /// 塞活），在它定下来之前只能报 0＝未知——报一个还在涨的分母，百分比会先冲到 100 再掉回去。</summary>
    public void SetTotal(int value)
    {
        lock (_gate)
        {
            _total = value;
            PublishIfDue(force: true);
        }
    }

    /// <summary>处理完一项：计数 +1 并累加已读字节。**不动**当前项——当前项由 <see cref="Touch"/>
    /// 维护，让它一直停留在最后进入的那个路径上，卡住时才看得到究竟卡在哪。</summary>
    public void Advance(long bytes)
    {
        lock (_gate)
        {
            _processed++;
            _bytes += bytes;
            PublishIfDue(force: false);
        }
    }

    /// <summary>进入下一项（在**处理之前**调用）。只改"正在处理什么"，不计数。</summary>
    public void Touch(string? current)
    {
        lock (_gate)
        {
            _current = current;
            PublishIfDue(force: false);
        }
    }

    /// <summary>一件活排进了队列。生产侧（diff）单线程调用，但它与消费侧并发，故用 Interlocked。
    /// **不**用它去动 <c>_total</c>：那个分母在 diff 收工前一直在涨，拿它算百分比会先冲到 100 再掉回来。</summary>
    public void Enqueue() => Interlocked.Increment(ref _enqueued);

    /// <summary>工作线程领走一件活（此后它算"在准备"，直到 <see cref="BeginItem"/> 开始推字节）。</summary>
    public void BeginWork() => Interlocked.Increment(ref _inWork);

    /// <summary>工作线程干完一件活（成功或失败都要调）。与 <see cref="Advance"/> 一样**不计数**——
    /// 槽位计数只归 Advance 管，在这里顺手加一次进度条就会冲过 100%。</summary>
    public void EndWork() => Interlocked.Decrement(ref _inWork);

    public void BeginItem(string item) => _active.TryAdd(item, 0);

    /// <summary>
    /// 造一个交给上传器的进度回调：把「本次调用内的累计字节」转成增量，边传边累加进本阶段的字节数。
    /// **每个上传项各要一个**——累计基线是 per-call 的，共用一个实例会把别人的进度当成回退。
    /// <para>
    /// 用它的项在结束时应当调 <c>EndItem(item, 0)</c>：字节已经在传输过程中逐笔计过了，
    /// 收尾再加一次总量就是双计。
    /// </para>
    /// </summary>
    public IProgress<long> ItemProgress() => new DeltaProgress(AddBytes);

    /// <summary>只累加字节，不计数、不动在途集合。</summary>
    private void AddBytes(long delta)
    {
        lock (_gate)
        {
            _bytes += delta;
            PublishIfDue(force: false);
        }
    }

    /// <summary>
    /// 累计值 → 增量。SDK 报的是本次上传调用内的累计，而我们的 <see cref="RetryPolicy"/> 重试
    /// 会让它从 0 重来（多卷上传同理，每卷各自从 0 开始）。回退一律按「重新开始」处理：
    /// 重传的字节会再算一次——对「当下网速」而言这是对的，那些字节确实又过了一遍网线。
    /// <para>分块并行上传时 <see cref="Report"/> 会被并发调用，所以要上锁。</para>
    /// </summary>
    private sealed class DeltaProgress(Action<long> onDelta) : IProgress<long>
    {
        private readonly Lock _gate = new();
        private long _last;

        public void Report(long cumulative)
        {
            long delta;
            lock (_gate)
            {
                delta = cumulative >= _last ? cumulative - _last : cumulative;
                _last = cumulative;
            }
            if (delta > 0)
                onDelta(delta);
        }
    }

    /// <summary>一个在途项结束：移出在途集合并累加字节，**不计数**。
    /// 计数归 <see cref="Advance"/> 专管——上传的槽位计数有"恰好一次"的精确约束
    /// （一个 pack 可能因成员变化被重压多次，却始终只占 total 里的一个槽位），
    /// 在这里顺手加一次就会重复计数，进度条会冲过 100%。</summary>
    public void EndItem(string item, long bytes)
    {
        _active.TryRemove(item, out _);
        lock (_gate)
        {
            _bytes += bytes;
            PublishIfDue(force: false);
        }
    }

    /// <summary>阶段收尾：无条件产出一次，把进度落到实处。</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _current = null;
            PublishIfDue(force: true);
        }
    }

    private void PublishIfDue(bool force)
    {
        var now = _clock.ElapsedMilliseconds;
        if (!force && now - _lastPublishMs < ThrottleMs)
            return;
        _lastPublishMs = now;

        _samples.Enqueue((now, _bytes));
        while (_samples.Count > 1 && now - _samples.Peek().Ms > SpeedWindowMs)
            _samples.Dequeue();

        long speed = 0;
        if (_samples.Count > 1)
        {
            var oldest = _samples.Peek();
            var spanMs = now - oldest.Ms;
            if (spanMs > 0)
                speed = (_bytes - oldest.Bytes) * 1000 / spanMs;
        }

        // 三个计数各自独立推进，读到的是错开半拍的快照——不夹到 0 以上，界面上就会闪出负数。
        var active = _active.Count;
        var inWork = Volatile.Read(ref _inWork);
        var preparing = Math.Max(0, inWork - active);
        var queued = Math.Max(0, Volatile.Read(ref _enqueued) - _processed - inWork);

        publish(new StageProgress(
            stage, _processed, _total, _bytes, _current, [.. _active.Keys], speed, preparing, queued));
    }
}
