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

    public async Task RunAsync(string blobName, Func<IProgress<long>, Task> upload, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        tracker.BeginItem(blobName);
        try
        {
            // 每卷各要一个 ItemProgress：DeltaProgress 的基线是 per-call 的，多卷并行共用一个实例，
            // 彼此的累计值会被当成对方的回退。
            await upload(tracker.ItemProgress());
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
    /// 多卷时 .002…N 并行、**首卷 .001 最后单独传**，使 .001 成为「整族齐全」的提交标记——
    /// 上传中断时 .001 尚未写入，部分上传不会被存在性检查误判为已存在（§7）。它只是一条便宜的
    /// 快路径提示而非保证：blob 可以被人从 Azure 侧直接删掉，所以 check 一律按索引记的卷数
    /// 逐卷核验（<see cref="VerifyVolumesAsync"/>），并不信这个标记。上千卷里多收尾一个往返，
    /// 代价可以忽略，就顺手留着。
    /// </para>
    /// </summary>
    /// <param name="scope">每卷的并发额度与进度登记（见 <see cref="VolumeUploadScope"/>）。
    /// 为 null 时退化成老样子：串行、不限流、不报进度——修复/替换那些不在备份主路径上的调用用。</param>
    public static async Task UploadAsync(
        IBlobUploader uploader, Account account, string container, string baseRef,
        IReadOnlyList<string> volumeFiles, AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null, VolumeUploadScope? scope = null)
    {
        Task One(string name, string file) =>
            scope is null
                ? uploader.UploadIfMissingAsync(account, container, name, file, tier, retry, ct, metadata)
                : scope.RunAsync(
                    name,
                    p => uploader.UploadIfMissingAsync(account, container, name, file, tier, retry, ct, metadata, p),
                    ct);

        if (volumeFiles.Count == 1)
        {
            await One(baseRef, volumeFiles[0]);
            return;
        }

        var batch = scope?.MaxParallelPerItem ?? 1;
        for (var start = 1; start < volumeFiles.Count; start += batch)
        {
            var end = Math.Min(volumeFiles.Count, start + batch);
            await Task.WhenAll(Enumerable.Range(start, end - start)
                .Select(i => One(VolumeName(baseRef, i + 1), volumeFiles[i])));
        }
        await One(VolumeName(baseRef, 1), volumeFiles[0]);
    }

    /// <summary>
    /// 替换某归档全部分卷：以**覆盖**方式上传新卷（单卷→baseRef；多卷→baseRef.001..M），
    /// 全部成功后再删除残留旧卷（尾部 .M+1..N，或旧单卷/新多卷时的旧基名等不属于新卷集者）。
    /// **先传后删**——崩溃窗口从「整 blob 丢失」降为「新旧卷混合」（可经检查/修复恢复）。
    /// 与 <see cref="UploadAsync"/> 同命名，并沿用**倒序上传**（.001 最后写）——首卷 .001 仍是「整族齐全」提交标记，
    /// 使部分上传不被存在性检查误判为已完成（§7）。
    /// </summary>
    public static async Task ReplaceAsync(
        IBlobUploader uploader, Account account, BlobContainerClient container, string baseRef,
        IReadOnlyList<string> volumeFiles, AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var newNames = VolumeNames(baseRef, volumeFiles.Count);

        // 1) 覆盖上传新卷。倒序（.00M 先、.001 最后）保持提交标记语义。单卷时循环仅一次写 baseRef。
        for (var i = volumeFiles.Count - 1; i >= 0; i--)
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

    /// <summary>归档是否存在（单卷或多卷首卷）。多卷上传倒序，故 .001 在即代表整族齐全。</summary>
    public static async Task<bool> ExistsAsync(BlobContainerClient cc, string baseRef, CancellationToken ct)
        => (await cc.GetBlobClient(baseRef).ExistsAsync(ct)).Value
           || (await cc.GetBlobClient(VolumeName(baseRef, 1)).ExistsAsync(ct)).Value;

    /// <summary>核验归档的全部分卷都存在（按版本索引记录的分卷数，§7）。expectedVolumes≤1 时退化为存在性检查。</summary>
    public static async Task<bool> AllVolumesExistAsync(
        BlobContainerClient cc, string baseRef, int expectedVolumes, CancellationToken ct)
    {
        if (expectedVolumes <= 1)
            return await ExistsAsync(cc, baseRef, ct);
        for (var i = 1; i <= expectedVolumes; i++)
        {
            if (!(await cc.GetBlobClient(VolumeName(baseRef, i)).ExistsAsync(ct)).Value)
                return false;
        }
        return true;
    }

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
    public static async Task<string> DownloadAsync(
        BlobContainerClient cc, string baseRef, string workDir, CancellationToken ct)
    {
        var single = cc.GetBlobClient(baseRef);
        if ((await single.ExistsAsync(ct)).Value)
        {
            var path = Path.Combine(workDir, "arc.7z");
            await single.DownloadToAsync(path, ct);
            return path;
        }

        string? first = null;
        for (var i = 1; ; i++)
        {
            var blob = cc.GetBlobClient(VolumeName(baseRef, i));
            if (!(await blob.ExistsAsync(ct)).Value)
                break;
            var local = Path.Combine(workDir, $"arc.7z.{i:D3}");
            await blob.DownloadToAsync(local, ct);
            first ??= local;
        }

        return first ?? throw new InvalidOperationException($"Archive '{baseRef}' not found in container.");
    }

    private static string VolumeName(string baseRef, int index) => $"{baseRef}.{index:D3}";
}
