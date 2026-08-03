using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

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
public sealed class VolumeUploadScope(SemaphoreSlim gate, StageTracker tracker, int maxParallelPerItem)
{
    /// <summary>单件活最多同时压几卷上去。不放开成「整族一起排队」是为了公平：
    /// <see cref="SemaphoreSlim"/> 先到先得，上千个等待者会把后来的小活整段挡在队尾，
    /// 它们的暂存文件也就一直堆在临时盘上不走。</summary>
    public int MaxParallelPerItem { get; } = Math.Max(1, maxParallelPerItem);

    /// <param name="label">界面上显示的名字——**源文件路径**或包的描述，不是 blob 名。
    /// blob 是内容寻址的（加密时还是 HMAC），<c>data/9f2a3b7c…001</c> 对着屏幕的人毫无意义。</param>
    /// <param name="volumeBytes">这一卷多大，供界面显示"传了多少 / 一共多大"。</param>
    public async Task RunAsync(
        string blobName, Func<IProgress<long>, Task> upload, CancellationToken ct,
        string? label = null, long volumeBytes = 0)
    {
        // 先试一次非阻塞获取：闸门空着时随手就拿到，那种情况下标记「在等额度」等于给每一卷平白
        // 加一次强制发布——一件大活上千卷就是上千次。只有真的要排队才报，而真排上队的时候，
        // 屏幕上一个字节都没在动，那一栏正是唯一说得出「在等什么」的东西。
        // Wait(0) 不看取消令牌，而它替下来的 WaitAsync(ct) 是看的：不补这一句，已经取消的运行
        // 在闸门空着时会照常传完这一卷才发现自己该停了。
        ct.ThrowIfCancellationRequested();
        if (!gate.Wait(0))
        {
            tracker.BeginWait(UploadWait.Slot);
            try
            {
                await gate.WaitAsync(ct);
            }
            finally
            {
                tracker.EndWait(UploadWait.Slot);
            }
        }
        tracker.BeginItem(blobName, label, volumeBytes);
        try
        {
            // 每卷各要一个 ItemProgress：DeltaProgress 的基线是 per-call 的，多卷并行共用一个实例，
            // 彼此的累计值会被当成对方的回退。带上 key，这一笔字节才落得到对应那条流的账上。
            await upload(tracker.ItemProgress(blobName));
        }
        finally
        {
            // 字节在传输过程中已逐笔计过，这里再加一次总量就是双计。
            tracker.EndItem(blobName, 0);
            gate.Release();
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

        async Task One(string name, string file, int index)
        {
            if (scope is null)
                await uploader.UploadIfMissingAsync(account, container, name, file, tier, retry, ct, metadata);
            else
                await scope.RunAsync(
                    name,
                    p => uploader.UploadIfMissingAsync(account, container, name, file, tier, retry, ct, metadata, p),
                    ct, LabelFor(index), SizeOf(file));
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
        // 窗口宽度仍卡在 MaxParallelPerItem 上：不能把上千卷一次性全塞进全局闸门的等待队列，
        // SemaphoreSlim 先到先得，那样后来的小活会被整段挡在队尾（见 VolumeUploadScope）。
        var window = scope?.MaxParallelPerItem ?? 1;
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

    /// <summary>统计归档实际存在的分卷数（单卷=1；多卷=连续 .001..N 的 N；都不在=0）。dedup 记录分卷数用。</summary>
    public static async Task<int> CountVolumesAsync(BlobContainerClient cc, string baseRef, CancellationToken ct)
    {
        if ((await cc.GetBlobClient(baseRef).ExistsAsync(ct)).Value)
            return 1;
        var n = 0;
        while ((await cc.GetBlobClient(VolumeName(baseRef, n + 1)).ExistsAsync(ct)).Value)
            n++;
        return n;
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
