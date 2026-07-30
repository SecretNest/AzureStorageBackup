using System.Collections.Concurrent;
using System.Diagnostics;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 一条正在传的流。<paramref name="Label"/> 是给人看的名字——上传阶段是**源文件路径**，
/// 而不是 blob 名：blob 是内容寻址的（加密时还是 HMAC 后的乱码），
/// <c>data/9f2a3b7c…001</c> 对着屏幕的人毫无意义。一箱 pack 装着几百个文件，列不下，
/// 报的是包号与成员数。
/// </summary>
/// <param name="Sent">这一条已经过网线的字节。</param>
/// <param name="Total">这一条一共有多少字节；0 = 未知（下载路径在拿到响应头之前不知道）。</param>
public sealed record ActiveTransfer(string Label, long Sent, long Total)
{
    public int? Percent => Total > 0 ? (int)Math.Min(100, 100L * Sent / Total) : null;
}

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
    IReadOnlyList<ActiveTransfer> ActiveItems,
    long BytesPerSecond,
    /// <summary>正在做本地 CPU 工作、既不在排队也不产生传输字节的件数。三个阶段各用各的含义：
    /// 上传＝正占着压缩锁在产出卷文件；还原/校验＝下载已完成、正在解压/算 hash。
    /// 这段时间可以长达几十秒（一箱 100 MB 过 7z -mx9 是压，解一箱同样大小的包是解），此前它在
    /// 界面上完全不可见：不在 <see cref="ActiveItems"/> 里、不产生字节，于是连测速窗口都是空的。
    /// <para>
    /// 三个阶段的界限不一样：上传阶段用的是 <c>StagingArea</c> 里那把全局压缩锁，这个数只会是
    /// 0 或 1，工作线程池比它大得多（<c>UploadConcurrency + 1</c>），多出来的线程是为了让压完的
    /// 活各自去占一条上传流，不是为了并行压缩——它们排在锁后面干等，那些件算 <see cref="Queued"/>。
    /// 还原/校验阶段复用同一对方法记这一段，但那里没有全局锁，解压/算 hash 各组各干各的，
    /// 这个数可以到 <c>DownloadConcurrency</c>，不是 0/1。
    /// </para></summary>
    int Preparing = 0,
    /// <summary>还没开工的件数：既包括还在队列里没被领走的，也包括已被领走、正排在压缩锁后面
    /// 干等的。两者对用户是同一件事——排着队，什么都没在动。</summary>
    int Queued = 0,
    /// <summary>由 <see cref="StageTracker"/> 按「本阶段全程平均进度」算出的剩余秒数；
    /// 阶段没有申报工作量、或还没干完一件时为 null，此时退回下面那个基于当前速度的粗估。</summary>
    double? EtaSeconds = null,
    /// <summary>本阶段申报的**源端**字节总量（压缩前）。上传阶段是渐增的——diff 边判边入队，
    /// 判完之前这个数还会往上长。0 = 该阶段不申报工作量。</summary>
    long WorkTotal = 0,
    /// <summary>其中已经彻底完工的源端字节。**不含在途**：一件活整件做完才销账。</summary>
    long WorkDone = 0,
    /// <summary>已完工的项真正推上网线的字节（压缩后）。同样**不含在途**——
    /// 与 <see cref="Bytes"/> 的区别正在这里：那个是边传边加的，用于测速，含正在传的那部分。</summary>
    long TransferredBytes = 0,
    /// <summary>
    /// 已经**稳稳落在云上、但所属的那件活还没销账**的字节（压缩后）。一件大活切成许多卷，
    /// 前几卷传完时那些字节确实已经到了云上，可整件还没完成，于是既进不了
    /// <see cref="TransferredBytes"/>（那本账按件记，才对得上按件销账的
    /// <see cref="WorkDone"/>），也早已不在 <see cref="StagedBytes"/> 里（池子逐卷释放）。
    /// 没有这一项，这批字节在界面上就凭空消失了，大文件上传的几十分钟里看着像什么都没发生。
    /// <para>整件完成时它并入 TransferredBytes 并归零；0 = 没有这种半完成的活，界面上整段不显示。</para>
    /// </summary>
    long UnfinishedItemBytes = 0,
    /// <summary>
    /// 待传池子里**还没送出去**的字节（压缩后）：池子里所有文件的总尺寸，减去在途那几条已经
    /// 传出去的那部分。压缩跑在上传前面时它会涨，上传跟上来就落回去——这个数把「压缩快还是
    /// 网络快」直接摆在脸上。
    /// <para>
    /// 减的是在途的**已传**字节而不是整卷：那几卷确实还整个躺在池子里（逐卷释放，传完才删），
    /// 已经送走的只是其中一截。不减的话，同一批字节会在这里和 <see cref="ActiveItems"/> 里各数一遍。
    /// </para>
    /// </summary>
    long StagedBytes = 0,
    /// <summary>
    /// 这一阶段一共要过多少网线字节（压缩后）。0 = 未知。
    /// <para>
    /// 只有下载侧填得出：要拉哪些对象、各多大，索引里都记着。上传侧给不出——压完才知道有多大，
    /// 开始传之前这个数不存在。老索引缺卷尺寸时同样报 0：宁可不显示，也不能给一个偏小的分母，
    /// 那会让百分比一路虚高然后卡在 100% 上不动。
    /// </para>
    /// </summary>
    long TransferTotal = 0,
    /// <summary>
    /// 这一阶段正卡在下游上，不是在干自己的活。差分阶段判得比压缩上传快几个数量级（判一条未变
    /// 文件只要一次 stat），流水线的有界队列必然被填满，之后每前进一格都要等上传吃掉一件——
    /// 而 <see cref="CurrentItem"/> 那时亮着的是**刚判完**的那条，界面看上去就是"卡死在这个文件上"。
    /// 说清楚它其实是在等，比让人盯着一个不动的文件名强。
    /// </summary>
    bool WaitingOnDownstream = false)
{
    /// <summary>还没开始处理的源端字节（压缩前）。</summary>
    public long WorkRemaining => Math.Max(0, WorkTotal - WorkDone);

    /// <summary>
    /// 按**源端字节**（压缩前）算的完成度。上传阶段应当优先用它而不是 <see cref="Percent"/>：
    /// 一件活可能是一个 100 GB 的单文件，也可能是一箱几百个 5 KB 的小文件，按件数算等于把它们
    /// 当成一样重——界面上会先飞快冲到 90%，再在最后一件上卡住半小时。
    /// <para>
    /// 只在总量已经定下来（<see cref="Total"/> &gt; 0）时才给。上传的工作量是 diff 边判边入队的，
    /// 判完之前分母还在长，这时算出来的百分比会先冲高再掉下去。件数那边用同一个信号。
    /// </para>
    /// </summary>
    public int? WorkPercent => Total > 0 && WorkTotal > 0
        ? (int)Math.Min(100, 100L * WorkDone / WorkTotal)
        : null;

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
/// <param name="stagedBytes">
/// 待传池子当前占用（压缩后字节）的读数。上传阶段传本次运行的暂存席位；其余阶段没有池子，省略。
/// 每次发布时现读——它随压缩与上传此消彼长，缓存下来就永远慢一拍。
/// </param>
public sealed class StageTracker(
    string stage, int total, Action<StageProgress> publish, bool speedWhileInFlight = false,
    Func<long>? stagedBytes = null) : IDisposable
{
    private const int ThrottleMs = 200;
    private const int SpeedWindowMs = 10_000;
    private const int HeartbeatMs = 1_000;
    // 采样队列的硬上限，防的是虚拟时钟冻结期间"按时间淘汰"这一半条件永远不成立的那种增长：
    // 冻着的时候每个采样的 Ms 都相同，tick - _samples.Peek().Ms 恒为 0，谁都淘汰不掉。
    // 200ms 节流下最多 5 个采样/秒，10 秒窗口正常撑满时最多约 50 个——256 是它的 5 倍余量，
    // 这条子句在正常运行下摸不到，只会在真出现长时间冻结（活跃段内密集发布、但虚拟时钟
    // 本身不走的那种边角）时把陈年残留顺手清掉，不靠"多久发布一次"这种运气兜底。
    // 要动这个数（或调小 ThrottleMs）之前先知道它触发时的代价：冻结期所有采样的 Ms 相同，
    // 按个数淘汰是从队头丢，先丢掉的恰是**冻结前**那些带着真实 spanMs 的采样；一旦丢过了头，
    // oldest.Ms == tick、spanMs == 0，速度就从"保留最后一次在网线上的读数"塌成 0。
    private const int MaxSamples = 256;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    /// <summary>在途的流：key 是 blob/卷名（唯一），值带着给人看的标签、已传字节与总字节。</summary>
    private sealed class InFlight(string label, long total)
    {
        public string Label { get; } = label;
        public long Total { get; } = total;
        private long _sent;
        public long Sent => Interlocked.Read(ref _sent);
        public void Add(long delta) => Interlocked.Add(ref _sent, delta);
    }

    private readonly ConcurrentDictionary<string, InFlight> _active = new(StringComparer.Ordinal);
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
    // 已经稳稳落在云上/盘上的字节（压缩后）。与 _bytes 的区别：那个边传边加、含在途的部分，
    // 用来测速；这个只认走完的。上传侧由 SetTransferred 按**件**给出权威读数，下载侧按卷累加。
    private long _transferred;
    // 上述权威读数是否已接管。见 SetTransferred。
    private bool _transferredByItem;
    // 已落云、但所属那件活还没销账的字节。卷传完时加，件销账时按 _transferred 的增量减——
    // 那个增量恰好是刚归档那件的全部卷。多件并发也对：这是笔总量守恒的账，不认哪一卷属于哪一件。
    private long _unfinishedItemBytes;
    // 这一阶段一共要过网线多少字节（压缩后）。只有下载侧申报得出，上传侧压完才知道，恒为 0。
    private long _transferTotal;
    // 正卡在下游上的调用方数量（差分侧被有界队列挡住时 >0）。
    private int _blocked;
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
    // 只在活跃段内跑的定时器。压缩期停着，一个多余的快照都不发。
    private Timer? _heartbeat;
    // Complete() 之后收到的迟到回调（已经排上线程池、Dispose 也叫不停）必须原地作废——
    // 见 Tick() 里的用法。
    private bool _completed;

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

    /// <summary>
    /// 「已传字节」改由调用方按**件**给出权威读数（绝对值，不是增量），此后 <see cref="EndItem"/>
    /// 的按卷累加让位。调用一次即接管，之后每件活销账时刷新。
    /// <para>
    /// 上传侧非用它不可，因为这个数字要和<b>按件</b>销账的原始字节摆在一起读（界面上的
    /// "X uploaded (N% of original)"）。按卷累加的话，一件大活边压边传的那几十分钟里分子一路涨、
    /// 分母纹丝不动——它要等整件完成才跳——百分比于是结构性地冲过 100%（实测 112%，那件活
    /// 完成后落回 99%）。文件越大差得越远，和压缩率毫无关系。
    /// </para>
    /// <para>
    /// 顺带修掉两处按卷累加固有的偏差：重传的字节不再重复计（<see cref="DeltaProgress"/> 把回退
    /// 按"重新开始"处理，对测速是对的，但云上还是那一份），去重命中也不再被当成传过
    /// （if-missing 撞上已存在的 blob 时一个字节都没上网线）。件级读数天生没有这两个问题，
    /// 而且与完工日志里那个"本次上传量"同源，界面和日志从此对得上。
    /// </para>
    /// </summary>
    public void SetTransferred(long total)
    {
        lock (_gate)
        {
            _transferredByItem = true;
            // 刚归档那件的全部卷从"未销账"里划走：增量就是它的量，不必知道哪一卷属于哪一件。
            // 夹到 0 是防守——重传/失败重压等情形下卷侧可能加得比件侧少，宁可这一栏早一步归零，
            // 也不能让它显示成负数。
            _unfinishedItemBytes = Math.Max(0, _unfinishedItemBytes - (total - _transferred));
            _transferred = total;
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
    /// <param name="work">这一件活的**源端**字节（压缩前）——完成度与剩余时间按它外推。</param>
    /// <param name="transfer">这一件活要过网线的字节（压缩后）。只有下载侧给得出，见
    /// <see cref="StageProgress.TransferTotal"/>。</param>
    public void Enqueue(long work = 0, long transfer = 0)
    {
        Interlocked.Increment(ref _enqueued);
        if (work > 0)
            Interlocked.Add(ref _totalWork, work);
        if (transfer > 0)
            Interlocked.Add(ref _transferTotal, transfer);
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
    /// <summary>开始等下游收活（成对调 <see cref="EndWaitingOnDownstream"/>）。
    /// 进 <c>_gate</c> 主动发一次：被挡住的那段里本调用方不再产生任何进度，不推的话
    /// "开始等了"这件事要等到队列松动之后才会被界面看到——而那正是它要说明的那一段。</summary>
    public void BeginWaitingOnDownstream()
    {
        Interlocked.Increment(ref _blocked);
        lock (_gate)
            PublishIfDue(force: true);
    }

    public void EndWaitingOnDownstream()
    {
        Interlocked.Decrement(ref _blocked);
        lock (_gate)
            PublishIfDue(force: true);
    }

    public void BeginStaging() => Interlocked.Increment(ref _inStaging);

    public void EndStaging() => Interlocked.Decrement(ref _inStaging);

    /// <summary>拿到压缩锁、真正开始产出卷文件（成对调 <see cref="EndPacking"/>）。
    /// 界面上上传阶段的 "N preparing" 只数这个，因此按锁的定义永远是 0 或 1；还原/校验阶段
    /// 复用同一对方法标记"下载完、正在解压/算 hash"这一段本地 CPU 工作，那里没有全局锁，
    /// 同时几个组各自解压，这个数可以大于 1。
    /// <para>
    /// 进 <c>_gate</c> 发一次 <see cref="PublishIfDue"/>：这一段的前后手（<c>EndItem</c> 摘掉在途项、
    /// 随后进入这一段）本身不产生任何字节，若不在这里主动推一次，preparing 从 0 变到 1 这件事
    /// 只能等下一次别的调用顺带发布才会被界面看到——而下载刚结束、解压/算 hash 期间恰恰没有别的
    /// 调用在跑，这一拍就会一直卡在旧快照上，界面冻结到这一段结束，正是它要修的那个"冻住"。
    /// 200ms 节流仍然生效，不是每次调用都真发。</para></summary>
    public void BeginPacking()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _inPacking);
            PublishIfDue(force: false);
        }
    }

    public void EndPacking()
    {
        lock (_gate)
        {
            Interlocked.Decrement(ref _inPacking);
            PublishIfDue(force: false);
        }
    }

    /// <summary>登记一个在途的传输对象。上传阶段登记的是**卷**（<c>data/xxx.007</c>），
    /// 不是件——界面上那个 "N uploading" 要回答的是"网线上现在有几条流"。
    /// <para>
    /// 空→非空这一下同时开启测速时钟：在此之前的压缩与排队不算进速度的分母。
    /// 集合的增删挪进锁里，是为了让"是不是空的"与时钟开关在同一个临界区内定下来。
    /// </para></summary>
    /// <param name="label">给人看的名字（上传阶段传**源文件路径**，不是内容寻址的 blob 名）。
    /// 省略时退回用 key 本身，与从前的行为一致。</param>
    /// <param name="totalBytes">这一条一共多少字节；0 = 未知（下载在拿到响应头前不知道）。</param>
    public void BeginItem(string item, string? label = null, long totalBytes = 0)
    {
        lock (_gate)
        {
            if (!_active.TryAdd(item, new InFlight(label ?? item, totalBytes)))
                return;
            if (speedWhileInFlight && _activeSince < 0)
            {
                _activeSince = NowMs();
                Heartbeat(on: true);
            }
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
    /// <param name="item">对应 <see cref="BeginItem"/> 的 key：这一笔字节要记到那一条流的账上，
    /// 界面才显示得出「这一条传了多少 / 一共多大」。省略则只累加阶段总字节，不落到具体某条流上。</param>
    public IProgress<long> ItemProgress(string? item = null) =>
        new DeltaProgress(delta => AddBytes(item, delta));

    /// <summary>累加字节：既进阶段总量（测速用），也进这一条流自己的账（界面显示用）。</summary>
    private void AddBytes(string? item, long delta)
    {
        lock (_gate)
        {
            _bytes += delta;
            if (item is not null && _active.TryGetValue(item, out var flow))
                flow.Add(delta);
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
            if (_active.TryRemove(item, out var flow))
            {
                // 这一条走完了：把它的字节从"在途"挪进"已传"。界面上那个"已传"要能回答
                // "有多少已经**稳稳落在云上**"，所以在途的部分一律不算进去，走完才认。
                // 有件级权威读数时（SetTransferred）这里让位——**按卷**累加与按件销账的
                // 工作量不同步，两个数字摆在一起就读不成话，原委见 SetTransferred。
                // 让位不等于把这批字节丢掉：它们确实已经在云上了，先记进"所属活未销账"那一栏，
                // 等整件完成时并入。记标称大小（Total）才能和件级账的增量精确抵消——Sent 含重传，
                // 抵不平会留下正向漂移。Sent == 0 表示这一卷根本没上网线（if-missing 撞上已有的
                // blob），件级账也不会计它，所以这里同样不加。
                if (_transferredByItem)
                    _unfinishedItemBytes += flow.Sent > 0 ? (flow.Total > 0 ? flow.Total : flow.Sent) : 0;
                else
                    _transferred += flow.Sent + bytes;
                if (speedWhileInFlight && _active.IsEmpty && _activeSince >= 0)
                {
                    _activeMs += NowMs() - _activeSince;
                    _activeSince = -1;
                    Heartbeat(on: false);
                }
            }
            _bytes += bytes;
            PublishIfDue(force: false);
        }
    }

    /// <summary>心跳的一拍：重算一次测速窗口并上报。卡住的流不产生任何事件，
    /// 没有它，速度会一直冻在卡住前的数字上。</summary>
    internal void Tick()
    {
        lock (_gate)
        {
            // 阶段已经收尾：Complete() 发过的终态快照就是界面该看到的最后一条。
            // Dispose() 不能撤回一个已经在排队或正在跑的回调，它排到这把锁时 Complete()
            // 早就放行了——不挡住的话，终态之后还会再冒出一条，把"最后一条是真终态"的承诺破了。
            if (_completed)
                return;
            // 一条流都没开：这段时间本就不进分母，也没有新东西可报。
            // 这条与上面那条是两回事：即便阶段没收尾，虚拟时钟冻着的时候采样也不能进——
            // 冻着的采样全带同一个时间戳，永远挤不出 _samples 窗口。
            if (speedWhileInFlight && _activeSince < 0)
                return;
            PublishIfDue(force: false);
        }
    }

    /// <summary>随活跃段启停心跳。必须在 <c>_gate</c> 内调用。
    /// 注入了时钟＝单测在手工驱动 <see cref="Tick"/>，此时不叠一个真定时器上去，结果才确定。</summary>
    private void Heartbeat(bool on)
    {
        if (Clock is not null)
            return;
        if (on)
        {
            // 心跳跑在线程池定时器线程上，没有调用方能接住它抛出的异常——顶到运行时手里，
            // .NET 的默认行为是直接打掉整个进程。这条回调经 Task 3 之后会挂在 RestoreOrchestrator/
            // BackupChecker 传进来的 onProgress 上，那是调用方自己的代码，出故障的概率不为零。
            // 进度上报只是锦上添花的旁路，宁可丢这一拍也不能把正在跑的备份/还原/校验拖下水一起死，
            // 所以这里必须吞掉——其余路径（Advance/Touch/EndItem 等）都在调用方线程上跑，
            // 异常能传回能处理它的人，那些地方**不**该照抄这个 catch。
            _heartbeat ??= new Timer(_ =>
            {
                try
                {
                    Tick();
                }
                catch
                {
                    // 见上面的注释：故意吞掉，别把定时器线程的异常带到进程头上。但吞完不能就此
                    // 当没事发生——如果什么都不做，下一拍还会原样再跑一次 Tick()，publish 大概率
                    // 还是那个坏掉的 sink，于是异常每秒发生一次、每次都被悄悄吃掉，整条跟踪器
                    // 剩下的生命周期里全程隐身重试，没有任何痕迹能让人发现进度上报早就废了。
                    // 两个更极端的做法都想过：什么都不做＝原样重试，就是刚说的隐身循环；
                    // 往外抛＝顶到 .NET 默认行为直接打掉整个进程，让一条"锦上添花"的旁路
                    // 拖死正在跑的备份/还原/校验，比吞掉还糟。折中是停表：一个刚失败的 sink
                    // 大概率还是坏的，重试不会有产出，不如先撤下来——只停这一个 tracker 的
                    // 心跳，不影响其余状态变化路径（Advance/Touch/EndItem 等）照常在调用方
                    // 线程上跑，异常照常传给能处理它的人，那些地方不该照抄这个 catch。
                    // 注意这不是永久停用：StopHeartbeat 把 _heartbeat 置回 null，而 Heartbeat(on:true)
                    // 是 `??=`，所以**下一个活跃段**开始时会重新建表再试一次。要的正是这个粒度——
                    // 从"每秒一次"降到"每段一次"，既止住隐身循环，sink 恢复了也能自己续上。
                    // Tick() 抛出时锁已经在它自己的 try 块里被释放（lock 语句的 finally 语义），
                    // 这里重新拿一次 _gate 是安全的，不会自锁。
                    lock (_gate)
                    {
                        StopHeartbeat();
                    }
                }
            }, null, Timeout.Infinite, Timeout.Infinite);
            _heartbeat.Change(HeartbeatMs, HeartbeatMs);
        }
        else
            _heartbeat?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void StopHeartbeat()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
    }

    /// <summary>阶段收尾：无条件产出一次，把进度落到实处，并停掉心跳。</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _current = null;
            // PublishIfDue 若抛出（比如 publish 回调本身坏了），下面两句收尾动作不能跟着漏掉——
            // 漏了 _completed=true，Tick() 会把这个"本该已经收尾"的阶段当成还活着继续处理；
            // 漏了 StopHeartbeat()，定时器留着继续跑，成了没人管的泄漏。两句都只是内存里的
            // 状态清理，不会自己再抛第二次异常，包一层 finally 就能把它们从"跟着陪葬"里摘出来。
            try
            {
                PublishIfDue(force: true);
            }
            finally
            {
                _completed = true;
                StopHeartbeat();
            }
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
        // 按时间淘汰（正路）之外再加一条按数量硬淘汰：虚拟时钟冻结期间所有采样共享同一个
        // Ms，第一个条件永远不成立，队列只能靠这条兜住上限（见 MaxSamples 上的注释）。
        while (_samples.Count > 1 && (tick - _samples.Peek().Ms > SpeedWindowMs || _samples.Count > MaxSamples))
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

        // 在途快照。各条流的已传字节是并发更新的，这里取的是同一瞬的读数，
        // 下面那个减法也用同一批值——分两次读会让"待传"偶尔算出个负数再被夹回 0，界面上就是跳。
        var inFlight = _active.Values
            .Select(f => new ActiveTransfer(f.Label, f.Sent, f.Total))
            .ToList();
        // 待传池子里还没送出去的：池子占用 - 在途那几条已经传走的部分（它们还整个躺在池子里，
        // 逐卷释放要传完才删）。不减就会在这里和 ActiveItems 里把同一批字节数两遍。
        var staged = stagedBytes is null
            ? 0
            : Math.Max(0, stagedBytes() - inFlight.Sum(f => f.Sent));

        publish(new StageProgress(
            stage, _processed, _total, _bytes, _current, inFlight, speed, preparing, queued,
            Eta(now), Volatile.Read(ref _totalWork), _doneWork, _transferred, _unfinishedItemBytes, staged,
            Volatile.Read(ref _transferTotal), Volatile.Read(ref _blocked) > 0));
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

    /// <summary>停掉心跳。阶段收尾时 <see cref="Complete"/> 已经做过一次；异常路径漏掉也不要紧——
    /// 三处在途登记都在 <c>finally</c> 里成对调 <see cref="EndItem"/>，最后一条流一结束心跳就已停了。
    /// <para>
    /// 与 <see cref="Complete"/> 一样要先置 <see cref="_completed"/>：单单停表并不能挡住已经排上
    /// 线程池、Dispose 也叫不停的那一拍回调，留着这条缝就是白留——<see cref="Tick"/> 靠的正是
    /// 这个标记，不是靠表停没停。
    /// </para></summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _completed = true;
            StopHeartbeat();
        }
    }
}
