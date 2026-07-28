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
    /// <summary>正在过暂存区的件数（备份上传阶段＝正占着压缩锁在产出卷文件）。
    /// 这段时间可以长达几十秒（一箱 100 MB 过 7z -mx9），此前它在界面上完全不可见：
    /// 不在 <see cref="ActiveItems"/> 里、不产生字节，于是连测速窗口都是空的。
    /// <para>
    /// 因为 <c>StagingArea</c> 里那把压缩锁是全局的，这个数只会是 0 或 1。工作线程池比它大得多
    /// （<c>UploadConcurrency + 1</c>），多出来的线程是为了让压完的活各自去占一条上传流，
    /// 不是为了并行压缩——它们排在锁后面干等，那些件算 <see cref="Queued"/>。
    /// </para></summary>
    int Preparing = 0,
    /// <summary>还没开工的件数：既包括还在队列里没被领走的，也包括已被领走、正排在压缩锁后面
    /// 干等的。两者对用户是同一件事——排着队，什么都没在动。</summary>
    int Queued = 0,
    /// <summary>由 <see cref="StageTracker"/> 按「本阶段全程平均进度」算出的剩余秒数；
    /// 阶段没有申报工作量、或还没干完一件时为 null，此时退回下面那个基于当前速度的粗估。</summary>
    double? EtaSeconds = null)
{
    public int? Percent => Total > 0 ? (int)Math.Min(100, 100L * Processed / Total) : null;

    /// <summary>
    /// 估算的剩余时间。首选 <see cref="EtaSeconds"/>——它按「已用时间 × 剩余工作量 ÷ 已完成工作量」
    /// 外推，等价于用**全程平均**吞吐，而不是眼下这一瞬的速度。
    /// <para>
    /// 为什么不用 <see cref="BytesPerSecond"/> 算：那是 10 秒滚动窗口，量的是"此刻网线上有多快"。
    /// 备份的实际节奏是「压一箱几十秒 → 传几秒」，压缩期间窗口里一个字节都没有，速度掉到 0，
    /// 剩余时间就整段消失，压完又猛地冒出一个很小的数——用户看到的就是"很飘"。而压缩那几十秒
    /// 同样是剩余时间的一部分，全程平均天然把它算了进去。
    /// </para>
    /// <para>
    /// 回退公式（阶段没申报工作量时）仍是老样子：拿"平均每件字节 × 剩余件数 ÷ 当前速度"粗估。
    /// </para>
    /// </summary>
    public TimeSpan? EstimatedRemaining =>
        EtaSeconds is { } s
            ? TimeSpan.FromSeconds(s)
            : Total > 0 && Processed > 0 && Processed < Total && BytesPerSecond > 0 && Bytes > 0
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
/// <param name="speedWhileInFlight">测速的分母是否只算「至少有一条在途项开着」的时间。
/// 会登记在途项的阶段（上传/还原/校验）置 true：它们的节奏是「压一箱几十秒 → 传几秒」，
/// 拿墙钟当分母量出来的既不是传输速度也不是墙钟吞吐。从不调 <see cref="BeginItem"/> 的阶段
/// （扫描/差分/本地检查）必须保持 false——虚拟时钟对它们永远不走，速度会恒为 0。</param>
public sealed class StageTracker(
    string stage, int total, Action<StageProgress> publish, bool speedWhileInFlight = false) : IDisposable
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
    // 已经进入"上传"这一段的**件**数。不能拿 _active.Count 代替：那里装的是**卷**，
    // 一件活可以同时有好几卷在飞，相减会把还在压缩的件算没了（preparing 被压成 0）。
    private int _inUpload;
    // 进了暂存区这一段的件数，以及其中真正拿到压缩锁的件数（后者按锁的定义只会是 0 或 1）。
    // 必须分开记，不能拿"手上件数 - 在上传件数"反推：那样会把排在锁后面干等的线程算成"在准备"，
    // 默认配置下界面显示 5 preparing，看着像五件活在并行推进，实际是一件在压、四个在闲等。
    private int _inStaging;
    private int _inPacking;
    // 剩余时间用的"工作量"。与 _bytes 是两回事：后者是真正过了网线的字节（压缩后、去重命中为 0），
    // 拿它当完成度会让剩余时间随压缩率和去重命中率乱跳。没有阶段申报工作量时（0），
    // 剩余时间退回按件数外推。
    private long _totalWork;
    private long _doneWork;
    // 本阶段真正开工的时刻。上传阶段的 tracker 在 diff 刚起步时就建好了，此后可能空等一阵才
    // 有第一件活；从建对象那一刻起算平均速度，会把这段空转摊进去，ETA 一路偏长。
    // -1 = 还没开工（没人调 BeginWork 的阶段——如 diff——一律按"建对象即开工"处理，那是对的）。
    private long _workStartMs = -1;

    // 测速用的时间轴：只在 _active 非空时前进（speedWhileInFlight 为 true 时）。
    // 压缩期它冻着，于是停顿两侧的采样在窗口里是连着的——速度既不被空转稀释，
    // 也不会出现"老采样整批超龄 → 当场报 0 → 压完猛跳"。
    private long _activeMs;
    // 当前活跃段的起点；-1 = 当下一条流都没开。
    private long _activeSince = -1;

    /// <summary>测试注入的毫秒时间源。10 秒测速窗口不可能靠真等来验，注入之后整个跟踪器
    /// 在时间上完全确定。生产为 null，走内部的 <see cref="Stopwatch"/>。</summary>
    internal Func<long>? Clock { get; init; }

    private long NowMs() => Clock?.Invoke() ?? _clock.ElapsedMilliseconds;

    /// <summary>测速用的时刻。开了开关的阶段走"有流才走"的虚拟轴，其余照走墙钟。</summary>
    private long SpeedNow(long now) =>
        speedWhileInFlight ? _activeMs + (_activeSince >= 0 ? now - _activeSince : 0) : now;

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
    /// <param name="bytes">计入测速与 <c>Bytes</c> 的字节。</param>
    /// <param name="work">计入剩余时间估算的工作量，默认与 <paramref name="bytes"/> 相同。
    /// 上传阶段两者不同：字节是压缩后真正传上去的（去重命中时是 0），工作量则是这一件活对应的
    /// 原始字节——必须与 <see cref="Enqueue"/> 时申报的是同一个量，否则完工时剩余量归不了零。</param>
    public void Advance(long bytes, long? work = null)
    {
        lock (_gate)
        {
            _processed++;
            _bytes += bytes;
            _doneWork += work ?? bytes;
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
    /// <param name="work">这件活的工作量（原始字节），累加成本阶段的总工作量。
    /// 它在 diff 收工前一直在涨，所以 ETA 与百分比一样用 <c>_total &gt; 0</c> 把门——
    /// 拿一个还在涨的分母外推，剩余时间会先缩到很小再弹回去。</param>
    public void Enqueue(long work = 0)
    {
        Interlocked.Increment(ref _enqueued);
        if (work > 0)
            Interlocked.Add(ref _totalWork, work);
    }

    /// <summary>工作线程领走一件活（此后它算"在准备"，直到 <see cref="BeginItem"/> 开始推字节）。</summary>
    public void BeginWork()
    {
        Interlocked.Increment(ref _inWork);
        // 第一件活被领走 = 本阶段真正开工，平均速度从这里开始量。
        Interlocked.CompareExchange(ref _workStartMs, NowMs(), -1);
    }

    /// <summary>工作线程干完一件活（成功或失败都要调）。与 <see cref="Advance"/> 一样**不计数**——
    /// 槽位计数只归 Advance 管，在这里顺手加一次进度条就会冲过 100%。</summary>
    public void EndWork() => Interlocked.Decrement(ref _inWork);

    /// <summary>一件活压完了、开始往上传（成对调 <see cref="EndUpload"/>）。只用来把"在准备"
    /// 与"在上传"分开算，同样**不计数**。</summary>
    public void BeginUpload() => Interlocked.Increment(ref _inUpload);

    public void EndUpload() => Interlocked.Decrement(ref _inUpload);

    /// <summary>一件活进了暂存区这一段——此刻它多半还在排压缩锁，所以算"排队中"
    /// （成对调 <see cref="EndStaging"/>）。</summary>
    public void BeginStaging() => Interlocked.Increment(ref _inStaging);

    public void EndStaging() => Interlocked.Decrement(ref _inStaging);

    /// <summary>拿到压缩锁、真正开始产出卷文件（成对调 <see cref="EndPacking"/>）。
    /// 界面上的 "N preparing" 只数这个，因此按锁的定义永远是 0 或 1。</summary>
    public void BeginPacking() => Interlocked.Increment(ref _inPacking);

    public void EndPacking() => Interlocked.Decrement(ref _inPacking);

    /// <summary>登记一个在途的传输对象。上传阶段登记的是**卷**（<c>data/xxx.007</c>），
    /// 不是件——界面上那个 "N uploading" 要回答的是"网线上现在有几条流"。
    /// <para>
    /// 空→非空这一下同时开启测速时钟：在此之前的压缩与排队不算进速度的分母。
    /// 集合的增删挪进锁里，是为了让"是不是空的"与时钟开关在同一个临界区内定下来。
    /// </para></summary>
    public void BeginItem(string item)
    {
        lock (_gate)
        {
            if (!_active.TryAdd(item, 0))
                return;
            if (speedWhileInFlight && _activeSince < 0)
                _activeSince = NowMs();
        }
    }

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
    /// 在这里顺手加一次就会重复计数，进度条会冲过 100%。
    /// <para>最后一条流收工时把这一段活跃时长落账，测速时钟就此停下，直到下一条流开起来。</para></summary>
    public void EndItem(string item, long bytes)
    {
        lock (_gate)
        {
            if (_active.TryRemove(item, out _) && speedWhileInFlight && _active.IsEmpty && _activeSince >= 0)
            {
                _activeMs += NowMs() - _activeSince;
                _activeSince = -1;
            }
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
        var now = NowMs();
        if (!force && now - _lastPublishMs < ThrottleMs)
            return;
        _lastPublishMs = now;

        // 节流用墙钟（它管的是"多久刷一次界面"），测速用虚拟轴（它管的是"这些字节花了多少传输时间"）。
        var tick = SpeedNow(now);
        _samples.Enqueue((tick, _bytes));
        while (_samples.Count > 1 && tick - _samples.Peek().Ms > SpeedWindowMs)
            _samples.Dequeue();

        long speed = 0;
        if (_samples.Count > 1)
        {
            var oldest = _samples.Peek();
            var spanMs = tick - oldest.Ms;
            if (spanMs > 0)
                speed = (_bytes - oldest.Bytes) * 1000 / spanMs;
        }

        // 几个计数各自独立推进，读到的是错开半拍的快照——不夹到 0 以上，界面上就会闪出负数。
        var inWork = Volatile.Read(ref _inWork);
        var preparing = Math.Max(0, Volatile.Read(ref _inPacking));
        // 没开工的 = 还在队列里的 + 已领走但在排压缩锁的。
        // 刻意**不**用「入队 - 完成 - 在压 - 在传」那个减法：压完到开传之间还有一段实打实的活
        // （pack 逐成员重新 Stat、单文件查去重映射，去重命中的甚至根本不上传），减法会把它们
        // 全报成"排队中"——把正在干活的说成在排队，比原先那个虚高的 preparing 更误导。
        var waiting = Math.Max(0, Volatile.Read(ref _inStaging) - preparing);
        var queued = Math.Max(0, Volatile.Read(ref _enqueued) - _processed - inWork) + waiting;

        publish(new StageProgress(
            stage, _processed, _total, _bytes, _current, [.. _active.Keys], speed, preparing, queued,
            Eta(now)));
    }

    /// <summary>
    /// 剩余时间 = 已开工时长 × 剩余量 ÷ 已完成量。也就是拿**本阶段全程的平均进度**外推，
    /// 而不是拿最近 10 秒的网速——后者在"压一箱几十秒、传几秒"的节奏下会在 0 和峰值之间来回跳，
    /// 而压缩那几十秒同样是剩余时间的一部分，全程平均天然把它算进去了。
    /// <para>
    /// 「量」优先用申报的工作量（上传阶段＝原始字节）；没人申报就退回件数。
    /// 上传阶段非用字节不可：一件活可能是 100 GB 的单文件，也可能是一箱几百个 5 KB 的小文件，
    /// 按件数外推等于把它们当成一样重。反过来 diff 阶段件数才对——那里绝大多数条目只 stat 一下就过。
    /// </para>
    /// <para>
    /// 已知的粗糙之处：在途那一件的进度不算数（完工才一次性销账）。只剩一个 100 GB 文件在传时，
    /// 剩余时间会一路涨到它传完才掉下来。要修得把在途项的部分进度也折算进来，那需要每一项的
    /// 预期总量（压完才知道），代价比收益大——先让它在"多件活"的常态下准。
    /// </para>
    /// </summary>
    private double? Eta(long now)
    {
        if (_total <= 0)   // 总数还没定下来（diff 还在往队列里塞活）——分母都没有，别猜
            return null;

        var totalWork = Volatile.Read(ref _totalWork);
        var (total, done) = totalWork > 0 ? (totalWork, _doneWork) : (_total, _processed);
        if (done <= 0 || done >= total)
            return null;

        var startMs = Volatile.Read(ref _workStartMs);
        var elapsedMs = now - (startMs < 0 ? 0 : startMs);
        if (elapsedMs <= 0)
            return null;

        return (double)elapsedMs * (total - done) / done / 1000;
    }

    /// <summary>停掉心跳定时器（Task 2 起有实际内容）。</summary>
    public void Dispose() { }
}
