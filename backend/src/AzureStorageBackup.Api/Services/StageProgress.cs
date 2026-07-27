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
    long BytesPerSecond)
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

    public void BeginItem(string item) => _active.TryAdd(item, 0);

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

        publish(new StageProgress(stage, _processed, _total, _bytes, _current, [.. _active.Keys], speed));
    }
}
