using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 全局上传额度闸门。容量就是设置里那个「上传并发数」，区别只在**谁先拿到**：不是先到先得，
/// 而是**按件龄**——最早开始上传的那一族卷优先，它用不完的余量才轮到后来的件。
/// <para>
/// 先到先得会把额度摊薄到所有在传的件上。压缩是全局串行的，所以稳态是「1 件在压 + N 件在传」
/// （N = 并发数），这 N 件各分到大约一条流，于是**N 件同时半完成**，而且每一件都推进得很慢。
/// 代价不只是难看：整族卷传完、云端确认返回之后才记 journal、才销在途账，所以「同时半完成的件数」
/// 就是一次中断会白扔掉多少活——<c>Stop now</c> 要把在途件的残卷全删，挂起/崩溃则让它们整件重来。
/// 按件龄仲裁把这个数从 N 降到通常 1~2 件。
/// </para>
/// <para>
/// 吞吐不受影响：额度始终满载。老件的滑动窗口用不满时（比如它只剩一卷没传），空出来的额度当场
/// 落到下一件手上，不会闲着。
/// </para>
/// </summary>
public sealed class VolumeUploadGate
{
    /// <summary>排序键 <c>(票号, 卷号)</c>：先按件龄，同一件内按卷号升序。
    /// 后者不是可有可无的整齐——界面上那张在途列表照着这个顺序读，一件一件往下推进才看得懂。</summary>
    private readonly PriorityQueue<TaskCompletionSource, (long Ticket, int Volume)> _waiters = new();
    private readonly Lock _lock = new();
    private long _nextTicket;
    private int _free;

    public VolumeUploadGate(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        _free = Capacity;
    }

    public int Capacity { get; }

    /// <summary>此刻还空着几份额度。给测试与诊断用——判「额度有没有被漏掉」只能看这个数。</summary>
    public int Free { get { lock (_lock) return _free; } }

    /// <summary>领一张票。**一族卷领一张**，也就是「这个归档开始上传的时刻」。</summary>
    public long NextTicket() => Interlocked.Increment(ref _nextTicket);

    /// <summary>
    /// 要一份额度。返回的 Task **已完成**就表示闸门当时空着、一次队都没排——调用方据此决定要不要
    /// 报「在等额度」（见 <see cref="VolumeUploadScope.RunAsync"/>）。
    /// </summary>
    public Task AcquireAsync(long ticket, int volume, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);

        // 续体必须异步跑：Pump 是在锁里置结果的，同步续体会直接在锁内跑到调用方的代码里去。
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            // **一律入队**，不留「闸门空着就随手拿走」的快速通道。那条通道正是要修掉的行为：
            // 它让一个刚到的新件绕过队列，插到已经等在那里的老件前面去。
            _waiters.Enqueue(tcs, (ticket, volume));
            Pump();
        }
        return tcs.Task.IsCompletedSuccessfully ? tcs.Task : WaitAsync(tcs, ct);
    }

    private static async Task WaitAsync(TaskCompletionSource tcs, CancellationToken ct)
    {
        // 取消与 Pump 抢同一个 TCS：谁先置上谁说了算，输的那一方什么都拿不到。
        // 取消赢了 → 这个等待者变成队里的一具尸体，下次 Pump 弹到它时 TrySetResult 失败、跳过，
        // 额度不会记到它头上。Pump 赢了 → 额度已经是它的了，await 正常返回，随后调用方自己的
        // 上传会因为令牌已断而抛，finally 照常把额度还回来。两条路都不漏额度。
        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        await tcs.Task;
    }

    public void Release()
    {
        lock (_lock)
        {
            // 被换掉的 SemaphoreSlim 在还多了的时候会抛 SemaphoreFullException，这一句是把那道
            // 保险接回来。重复归还不是小事：额度凭空变多，在途流数就静静地超过用户设的并发数，
            // 而它坏起来不响——只会看见备份莫名其妙比设定的更吃带宽。
            if (_free >= Capacity)
                throw new InvalidOperationException(
                    $"Upload slot released more times than acquired (capacity {Capacity}).");
            _free++;
            Pump();
        }
    }

    /// <summary>把空着的额度发给优先级最高的**活**等待者。必须在锁内调用。</summary>
    private void Pump()
    {
        // 循环而不是只弹一个：弹出来的可能是已取消的尸体，那时额度还没送出去，得继续往下找。
        // 也正因为每次都从 _free > 0 起循环，不存在「队里全是尸体 + 有空额度 = 谁都拿不到」
        // 那种死锁——尸体会被后续的 Pump 一路清掉。
        while (_free > 0 && _waiters.TryDequeue(out var waiter, out _))
            if (waiter.TrySetResult())
                _free--;
    }
}

/// <summary>
/// 每一卷上传时套的外围：向全局闸门要一份**流**的额度、把这一卷登记为在途、给它一个独立的进度回报。
/// <para>
/// 额度按**卷**而不是按**件**计。按件计时，一个 100 GB 文件切出来的上千卷整段只占一条流——
/// 界面上设的「并发 5」在传大文件时形同虚设，实测 4–6 MB/s，正是单条 TCP 到 Azure 的天花板。
/// 按卷计之后，在途流数恒等于设定值，与队列里躺的是一个大文件还是一万个小文件无关。
/// </para>
/// <para>
/// 有意**不去动** SDK 的 <c>TransferOptions.MaximumConcurrency</c>（blob 内部的块级并发）：
/// 那一层会与这里的额度相乘，设定的 5 就不再等于任何能解释的数字。而默认卷大小 100 MB 低于
/// SDK 的 256 MB 单发阈值，一卷就是一个 PUT、一条连接，所以「一卷 = 一条流」是精确的而非近似。
/// </para>
/// </summary>
public sealed class VolumeUploadScope(VolumeUploadGate gate, StageTracker tracker, int maxParallelPerItem)
{
    /// <summary>单件活最多同时压几卷上去。留着这道窗口**不是**为了让后来的小活插得进来——
    /// 额度已经按件龄仲裁（见 <see cref="VolumeUploadGate"/>），挡住后来者正是有意为之。
    /// 它守的是另一件事：别把一个大文件的上千卷一次性全塞进等待队列，那是白占内存，
    /// 而且这些卷的暂存文件也只能等各自传完才撤得下去。</summary>
    public int MaxParallelPerItem { get; } = Math.Max(1, maxParallelPerItem);

    /// <summary>
    /// 滑动窗口的宽度：<see cref="MaxParallelPerItem"/> **再加一卷**。多出来的那一卷是接力棒。
    /// <para>
    /// 少了它，按件龄仲裁会在每次换卷的缝里漏一份额度出去：一卷传完是在 <c>RunAsync</c> 的
    /// finally 里 <c>Release</c> 的，而这一族的下一卷要等 <c>WhenAny</c> 的续体跑起来才排得上队。
    /// <c>Release</c> 里的放行是同步的，那一瞬间队里只有别的件——额度当场就送出去了。
    /// 每传完一卷漏一份，老件的优先权也就名存实亡。
    /// </para>
    /// <para>
    /// 多排一卷之后，换卷那一瞬这一族在闸门上通常还留着一个等待者，它凭更小的票号把额度接住。
    /// 代价只是每件多占一个等待者的内存。
    /// </para>
    /// <para>
    /// **它盖住的是常见时序，不是全部。** 那一族被放行之后要等续体才补上下一个等待者，这中间仍有
    /// 一道缝；线程池被饿着时续体迟到，缝里正好有别的卷传完，额度就漏给新件了。生产上这道缝以
    /// 微秒计而一卷上传以秒计，所以漏的至多是偶尔一卷——够不上要为它去改造成「一族卷各自攥着
    /// 额度不放」的写法（那才能做到绝对，代价是把这一层整个翻掉）。
    /// </para>
    /// </summary>
    public int WindowPerItem => MaxParallelPerItem + 1;

    /// <summary>领一张票，一族卷一张。见 <see cref="VolumeUploadGate.NextTicket"/>。</summary>
    public long NextTicket() => gate.NextTicket();

    /// <param name="ticket">这一族卷的票号，决定它在闸门上的优先级。</param>
    /// <param name="volumeIndex">族内第几卷（0 起）。同票号之间按它升序放行。</param>
    /// <param name="label">界面上显示的名字——**源文件路径**或包的描述，不是 blob 名。
    /// blob 是内容寻址的（加密时还是 HMAC），<c>data/9f2a3b7c…001</c> 对着屏幕的人毫无意义。</param>
    /// <param name="volumeBytes">这一卷多大，供界面显示"传了多少 / 一共多大"。</param>
    public async Task RunAsync(
        string blobName, Func<IProgress<long>, Task> upload, CancellationToken ct,
        long ticket = 0, int volumeIndex = 0, string? label = null, long volumeBytes = 0)
    {
        // 闸门空着时 AcquireAsync 返回的是一个已完成的 Task，此时**不报**「在等额度」：
        // 那种情况下标记等于给每一卷平白加一次强制发布——一件大活上千卷就是上千次。
        // 只有真的排上队才报，而真排上队的时候，屏幕上一个字节都没在动，那一栏正是唯一
        // 说得出「在等什么」的东西。
        // 这一句先问一次取消：不补它，已经取消的运行在闸门空着时会照常传完这一卷才发现该停了。
        ct.ThrowIfCancellationRequested();
        var acquire = gate.AcquireAsync(ticket, volumeIndex, ct);
        if (!acquire.IsCompletedSuccessfully)
        {
            tracker.BeginWait(UploadWait.Slot);
            var acquired = false;
            try
            {
                await acquire;
                acquired = true;
            }
            finally
            {
                // EndWait 会直接调到调用方给的 publish（写库、推 SSE 之类的外部代码），它可以抛，
                // 而且这条路上的异常是**故意**往外传的（见 StageProgress）。抛出的那一刻额度已经
                // 到手了，就这么让它走，那一份额度再也回不来——泄漏的形状见下面 Release 处的说明。
                try
                {
                    tracker.EndWait(UploadWait.Slot);
                }
                catch
                {
                    if (acquired)
                        gate.Release();
                    throw;
                }
            }
        }
        try
        {
            tracker.BeginItem(blobName, label, volumeBytes);
            // 每卷各要一个 ItemProgress：DeltaProgress 的基线是 per-call 的，多卷并行共用一个实例，
            // 彼此的累计值会被当成对方的回退。带上 key，这一笔字节才落得到对应那条流的账上。
            await upload(tracker.ItemProgress(blobName));
        }
        finally
        {
            // Release 必须自己有一层 finally，不能跟 EndItem 排在同一句之后：EndItem 同样会调
            // publish，它一抛就把后面那句整个跳过去。而这种泄漏还不响——异常往上撞到「文件读不开」
            // 那条兜底就被吞了（MarkPostDiffUnreadableAsync 收 IOException），备份照跑，只是少一条流；
            // 攒够设定的并发数，全部上传就永远停在闸门上，界面上是「什么都没在传、暂存池却压着一堆」，
            // 而且不会自愈。BeginItem 一并挪进 try：它抛出时 EndItem 找不到这条流会直接短路，无害。
            try
            {
                // 字节在传输过程中已逐笔计过，这里再加一次总量就是双计。
                tracker.EndItem(blobName, 0);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

/// <summary>
/// 单卷/多卷归档在 blob 上的读写（§7）。单卷用基名；多卷用 基名.001/.002...
/// 供数据 blob 与 pack 共用，还原/检查按同规则重组下载。
/// </summary>
public static class VolumeBlobIO
{
    /// <summary>
    /// 上传压缩产出的卷文件。单卷→baseRef；多卷→baseRef.001、baseRef.002...
    /// <para>
    /// 每一卷都进滑动窗口，**先后不论**——按文件顺序进队，谁先落地不作要求。
    /// </para>
    /// <para>
    /// 从前 .001 是最后单独传的，用作「整族齐全」的提交标记，好让部分上传不被去重的存在性检查
    /// 误判成已存在。那条检查（云端 HEAD 比对）已经删了——去重一律走本地权威索引，不问云端。
    /// 而这个标记的代价从来不像注释里算的那样便宜：按上千卷算确实可以忽略，可默认卷 100 MB、
    /// 并发 5，一个 100–500 MB 的文件正好切成 2–5 卷，收尾那一趟单卷串行就把整件的上传时间
    /// 翻了一倍——而那个尺寸段在真实备份里是大头。中断残留由别处兜着：逐卷 if-missing 会把缺的
    /// 补齐，加密多卷则在上传前先清（见 BackupOrchestrator.ClearLeftoverVolumesAsync）。
    /// </para>
    /// </summary>
    /// <param name="scope">每卷的并发额度与进度登记（见 <see cref="VolumeUploadScope"/>）。
    /// 为 null 时退化成老样子：串行、不限流、不报进度——修复/替换那些不在备份主路径上的调用用。</param>
    /// <param name="onVolumeUploaded">某一卷传完后立刻调用，参数是它的**本地**文件路径。
    /// 备份路径把暂存区的逐卷释放挂在这里：整族传完才删的话，临时盘峰值等于整个归档
    /// （一个 100 GB 的文件就要 100 GB 临时空间），水位还会整段贴在上限上把压缩堵死。</param>
    public static async Task UploadAsync(
        IBlobUploader uploader, Account account, string container, string baseRef,
        IReadOnlyList<string> volumeFiles, AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null, VolumeUploadScope? scope = null,
        Action<string>? onVolumeUploaded = null, string? label = null)
    {
        // 多卷时在标签上标出这是第几卷：一个大文件切成上千卷，光显示路径的话界面上会是同一行
        // 重复上千次，看不出在推进。
        string LabelFor(int index) => label is null
            ? baseRef
            : volumeFiles.Count > 1 ? $"{label} ({index + 1}/{volumeFiles.Count})" : label;

        // 一族卷领一张票，也就是「这个归档开始上传的时刻」——闸门据此把额度优先给老的那一族，
        // 而不是摊薄到所有在传的件上（见 VolumeUploadGate）。一箱的每一组各自调一次本方法，
        // 因此各领各的票；组与组之间本来就是串行的，这是对的。
        var ticket = scope?.NextTicket() ?? 0;

        async Task One(string name, string file, int index)
        {
            if (scope is null)
                await uploader.UploadIfMissingAsync(account, container, name, file, tier, retry, ct, metadata);
            else
                await scope.RunAsync(
                    name,
                    p => uploader.UploadIfMissingAsync(account, container, name, file, tier, retry, ct, metadata, p),
                    ct, ticket, index, LabelFor(index), SizeOf(file));
            onVolumeUploaded?.Invoke(file);
        }

        static long SizeOf(string file)
        {
            try { return new FileInfo(file).Length; } catch { return 0; }
        }

        if (volumeFiles.Count == 1)
        {
            await One(baseRef, volumeFiles[0], 0);
            return;
        }

        // 滑动窗口：完成一卷就补一卷。分批 Task.WhenAll 的话，一批里最慢的那一卷会让其余几条流
        // 全程空转等它——卷与卷的耗时本来就不齐（重试、分块并行度、服务端限流各不相同），
        // 界面上的表现是"5 条流一条条减到 0，然后又冒出 5 条"，而不是稳稳保持 5 条。
        // 窗口宽度仍是有界的：不能把上千卷一次性全塞进全局闸门的等待队列，那是白占内存，
        // 而且排在队里的卷各自的暂存文件也只能等它传完才撤得下去（见 VolumeUploadScope）。
        // 宽度取 MaxParallelPerItem + 1（见 WindowPerItem）：等于闸门容量的那部分让这一族能一个人
        // 吃满全部额度，多出来的一卷是换卷时接住额度的接力棒。
        var window = scope?.WindowPerItem ?? 1;
        var started = new List<Task>(volumeFiles.Count);
        var running = new List<Task>(window);
        for (var i = 0; i < volumeFiles.Count; i++)
        {
            if (running.Count >= window)
            {
                var done = await Task.WhenAny(running);
                running.Remove(done);
                // 有卷倒了就不再起新的。已经起飞的仍在下面等完——半路撒手会留下没人观察的
                // 孤儿任务，它们还占着闸门额度和临时盘。异常本身留给 WhenAll 抛，与原先分批时
                // 的语义一致：全部落定之后再抛，抛的是第一个。
                if (done.IsFaulted || done.IsCanceled)
                    break;
            }
            var one = One(VolumeName(baseRef, i + 1), volumeFiles[i], i);
            started.Add(one);
            running.Add(one);
        }
        await Task.WhenAll(started);
    }

    /// <summary>
    /// 替换某归档全部分卷：以**覆盖**方式上传新卷（单卷→baseRef；多卷→baseRef.001..M），
    /// 全部成功后再删除残留旧卷（尾部 .M+1..N，或旧单卷/新多卷时的旧基名等不属于新卷集者）。
    /// **先传后删**——崩溃窗口从「整 blob 丢失」降为「新旧卷混合」（可经检查/修复恢复）。
    /// </summary>
    public static async Task ReplaceAsync(
        IBlobUploader uploader, Account account, BlobContainerClient container, string baseRef,
        IReadOnlyList<string> volumeFiles, AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var newNames = VolumeNames(baseRef, volumeFiles.Count);

        // 1) 覆盖上传新卷。单卷时循环仅一次写 baseRef。
        for (var i = 0; i < volumeFiles.Count; i++)
            await uploader.UploadOverwriteAsync(account, container.Name, newNames[i], volumeFiles[i], tier, retry, ct, metadata);

        // 2) 删除不属于新卷集的残留旧卷（如旧卷数 > 新卷数的尾部，或单卷↔多卷切换后的旧命名）。
        //    只删本归档自身的卷（baseRef 精确 或 baseRef.<数字> 卷后缀）——前缀扫描会连带匹配到
        //    碰撞避让兄弟 data/{hash}~N（内容不同、独立引用），必须排除，否则会误删他人数据。
        var keep = new HashSet<string>(newNames, StringComparer.Ordinal);
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, ct))
            if (IsVolumeOf(baseRef, b.Name) && !keep.Contains(b.Name))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync(cancellationToken: ct);
    }

    /// <summary>
    /// <paramref name="name"/> 是否为归档 <paramref name="baseRef"/> 自身的卷：等于基名，或形如 基名.NNN（后缀为 <c>.</c>+纯数字）。
    /// 用于按前缀枚举后精确过滤，排除同前缀但内容不同的碰撞避让兄弟（如 data/{hash}~1、data/{hash}~1.001）。
    /// </summary>
    public static bool IsVolumeOf(string baseRef, string name)
    {
        if (name == baseRef)
            return true;
        if (!name.StartsWith(baseRef + ".", StringComparison.Ordinal))
            return false;
        var suffix = name[(baseRef.Length + 1)..];
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// 归档的全部分卷 blob 名：单卷（count≤1）→[baseRef]；多卷→[baseRef.001..count]。
    /// 单一命名真相源——上传/替换/引用集构造共用，避免各处各自拼名而漂移。
    /// </summary>
    public static IReadOnlyList<string> VolumeNames(string baseRef, int count)
        => count <= 1
            ? [baseRef]
            : Enumerable.Range(1, count).Select(i => VolumeName(baseRef, i)).ToList();

    /// <summary>
    /// 这个归档**沾到边**没有：单卷的基名在，或多卷的首卷在。
    /// <para>
    /// 说不了「整族齐全」——各卷并发上传，谁先落地不作要求，首卷在只代表有人往这个地址写过。
    /// 要核验齐全用 <see cref="VerifyVolumesAsync"/>，它按索引记的卷数逐卷查。
    /// </para>
    /// </summary>
    public static async Task<bool> ExistsAsync(BlobContainerClient cc, string baseRef, CancellationToken ct)
        => (await cc.GetBlobClient(baseRef).ExistsAsync(ct)).Value
           || (await cc.GetBlobClient(VolumeName(baseRef, 1)).ExistsAsync(ct)).Value;

    /// <summary>
    /// 「存在 + 尺寸」检查：核验全部分卷存在，且当 <paramref name="expectedSizes"/> 非空时每卷尺寸匹配。
    /// 只发 HEAD（GetProperties），不下载；Archive 亦可读属性无需活化。尺寸未知（为空）则只验存在。
    /// </summary>
    public static async Task<(bool Present, bool SizeOk)> VerifyVolumesAsync(
        BlobContainerClient cc, string baseRef, int expectedVolumes, IReadOnlyList<long> expectedSizes, CancellationToken ct)
    {
        if (expectedVolumes <= 1)
        {
            var len = await LengthAsync(cc.GetBlobClient(baseRef), ct)
                      ?? await LengthAsync(cc.GetBlobClient(VolumeName(baseRef, 1)), ct);
            if (len is null)
                return (false, false);
            return (true, expectedSizes.Count < 1 || len == expectedSizes[0]);
        }

        var sizeOk = true;
        for (var i = 1; i <= expectedVolumes; i++)
        {
            var len = await LengthAsync(cc.GetBlobClient(VolumeName(baseRef, i)), ct);
            if (len is null)
                return (false, false);
            if (expectedSizes.Count >= i && len != expectedSizes[i - 1])
                sizeOk = false;
        }
        return (true, sizeOk);
    }

    private static async Task<long?> LengthAsync(BlobClient blob, CancellationToken ct)
    {
        try { return (await blob.GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength; }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    /// <summary>把归档（单卷或多卷）下载到 workDir，返回供 7z 解压的首卷本地路径。</summary>
    /// <param name="progress">每卷的进度回调**工厂**。为什么必须是工厂而不是单个 <see cref="IProgress{T}"/>：
    /// SDK 的 <c>ProgressHandler</c> 报的是本次 <c>DownloadToAsync</c> 调用内的累计字节，
    /// <see cref="StageTracker.ItemProgress"/> 返回的 <c>DeltaProgress</c> 把累计转增量时是按
    /// **这一个实例自己的基线** <c>_last</c> 算的，且每次 <c>Report</c> 之后 <c>_last</c> 都**无条件**
    /// 更新一次（见 <see cref="StageTracker"/> 里 <c>DeltaProgress</c> 的注释）。
    /// <para>
    /// 多卷下载若共用一个实例：设上一卷收尾时的基线为 L，卷 k 的首次上报为 c₁。若 c₁ ≥ L，
    /// 这一下只会被记成 c₁ − L，之后卷 k 自己的增量照常累加，结果是**整卷少计 L 个字节**——
    /// 是漏记，不是虚高，且漏记的上限就是上一卷的大小。触发条件：一个较小的卷后面紧跟一个
    /// 较大的卷，大卷的第一个上报块超过了小卷收尾时的基线。（反过来，若 c₁ &lt; L，"当作重新
    /// 开始"的复位对这一卷而言算对了，不会漏。）真正的虚高只有一种来路：同一串 <c>Report</c>
    /// 调用里累计值忽然下跌（SDK 重试），那种情况换不换实例表现一致，是设计上刻意允许的
    /// （见 <c>DeltaProgress</c> 上的注释）。
    /// </para>
    /// 每卷调一次工厂拿一个全新实例，就是不让上一卷的基线泄漏进下一卷，
    /// 与 <see cref="VolumeUploadScope.RunAsync"/> 里"每卷各要一个 ItemProgress()"是同一个道理。
    /// 为 null 时不挂进度回调——修复/压实等不在途登记的调用路径用。</param>
    public static async Task<string> DownloadAsync(
        BlobContainerClient cc, string baseRef, string workDir, CancellationToken ct,
        Func<IProgress<long>>? progress = null)
    {
        async Task DownloadOne(BlobClient blob, string path)
        {
            if (progress is null)
                await blob.DownloadToAsync(path, ct);
            else
                await blob.DownloadToAsync(path, new BlobDownloadToOptions { ProgressHandler = progress() }, ct);
        }

        var single = cc.GetBlobClient(baseRef);
        if ((await single.ExistsAsync(ct)).Value)
        {
            var path = Path.Combine(workDir, "arc.7z");
            await DownloadOne(single, path);
            return path;
        }

        string? first = null;
        for (var i = 1; ; i++)
        {
            var blob = cc.GetBlobClient(VolumeName(baseRef, i));
            if (!(await blob.ExistsAsync(ct)).Value)
                break;
            var local = Path.Combine(workDir, $"arc.7z.{i:D3}");
            await DownloadOne(blob, local);
            first ??= local;
        }

        return first ?? throw new InvalidOperationException($"Archive '{baseRef}' not found in container.");
    }

    private static string VolumeName(string baseRef, int index) => $"{baseRef}.{index:D3}";
}
