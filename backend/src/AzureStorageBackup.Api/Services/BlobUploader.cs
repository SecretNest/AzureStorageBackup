using Azure;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>data/pack/索引 blob 的上传（M4 §5）：设置 Tier、重试退避、内容寻址幂等跳过、并发。</summary>
public interface IBlobUploader
{
    /// <summary>上传文件到 blob（带 Tier + 可选元数据）。blob 已存在则跳过并返回 false（内容寻址幂等）。</summary>
    Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null);

    /// <summary>
    /// 同上，外加**上传过程中**的字节回报（<paramref name="progress"/> 收到的是本次调用内的累计值）。
    /// 没有它，界面上的速度只能按「一个 blob 传完」这个粒度跳变：传一个 100 MB 的包要几十秒，
    /// 那几十秒里测速窗口是空的，读数归零。
    /// <para>
    /// 默认实现直接丢掉进度、转发到无进度版本——测试替身不必为此改一行。进度参数放在最后
    /// **且不给默认值**：8 个实参唯一匹配上面那个重载，9 个唯一匹配这个，不存在歧义。
    /// </para>
    /// </summary>
    Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        => UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);

    /// <summary>覆盖上传文件到 blob（带 Tier + 可选元数据），**不做**存在性短路——目标存在也直接覆盖。
    /// 原子替换用：先覆盖上传新卷、再删残留旧卷，使崩溃窗口从「整 blob 丢失」降为「新旧卷混合」（可修复）。</summary>
    Task UploadOverwriteAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null);
}

public sealed class BlobUploader(IBlobClientFactory factory) : IBlobUploader
{
    public Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null)
        => UploadCoreAsync(account, container, blobName, filePath, tier, overwrite: false, retry, ct, metadata);

    public Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        => UploadCoreAsync(account, container, blobName, filePath, tier, overwrite: false, retry, ct, metadata, progress);

    public async Task UploadOverwriteAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null)
        => await UploadCoreAsync(account, container, blobName, filePath, tier, overwrite: true, retry, ct, metadata);

    /// <summary>上传核心：overwrite=false 时若 blob 已存在则短路返回 false（if-missing 语义）；
    /// overwrite=true 时直接覆盖上传。返回是否实际上传。</summary>
    private async Task<bool> UploadCoreAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, bool overwrite, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress = null)
    {
        var blob = factory.CreateServiceClient(account)
            .GetBlobContainerClient(container)
            .GetBlobClient(blobName);

        var options = new BlobUploadOptions { AccessTier = tier, ProgressHandler = progress };
        if (metadata is not null)
            options.Metadata = metadata.ToDictionary(kv => kv.Key, kv => kv.Value);

        // if-missing 语义交给**服务端**做，不再靠"先 Exists 再上传"。
        //
        // 那样做有个不原子的缺口，而上传是会重试的：网络抖一下（NAS 上很常见），服务端其实已经
        // 把 blob 写进去了、客户端只收到超时或 5xx，重试于是去覆盖一个已经存在的 blob。
        // 数据层是 Archive 时这一下直接失败——归档 blob 不允许被覆盖（Put Block 更是连 tier 都
        // 带不了），返回 409 BlobArchived，而它不在可重试之列，整轮备份就此倒掉。
        // 同一个缺口也会被并发撞上：两个任务对同一个 blob 名先后走过存在性检查，都看到"不存在"。
        //
        // 条件请求没有这个窗口：服务端在写之前判条件，不满足就直接拒绝（412），一个字节都不写，
        // 所以重试与并发都天然幂等，也就绝不会去动一个已归档的 blob。顺带还省掉那一次 HEAD。
        if (!overwrite)
            options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };

        try
        {
            await RetryPolicy.ExecuteAsync(async token =>
            {
                await using var stream = File.OpenRead(filePath);
                await blob.UploadAsync(stream, options, token);
            }, retry, IsTransient, ct);
        }
        catch (RequestFailedException ex) when (!overwrite && IsAlreadyThere(ex))
        {
            // 已经有了就是 if-missing 想要的结果，不是错误。
            return false;
        }

        return true;
    }

    /// <summary>
    /// 条件上传被"已经存在"挡下了。412 是 If-None-Match 不满足的正路；409 BlobAlreadyExists
    /// 也一并收下——重试碰上自己刚写成功的那一份时，服务端两种都可能给。
    /// <para>
    /// **BlobArchived 也算**。条件请求救不了归档 blob：对已归档对象的写操作，服务端在判条件
    /// **之前**就拒绝，于是拿不到 412，拿到的是 409 BlobArchived。而在 if-missing 语义下这条
    /// 错误的含义是确定的——目标已经在那儿了，正是"不必再传"。Archive 数据层上跑备份时，
    /// 每一个已经存过的对象都会走到这里。
    /// </para>
    /// <para>
    /// 只在 if-missing 那一侧收。<c>overwrite: true</c>（修复、死重压实）撞上 BlobArchived 是
    /// 真的要覆盖归档数据，那种情况必须响亮地失败，不能静默当成功。
    /// </para>
    /// </summary>
    private static bool IsAlreadyThere(RequestFailedException ex) =>
        ex.Status == 412 || ex.ErrorCode is "BlobAlreadyExists" or "BlobArchived";

    /// <summary>可重试的瞬时错误：服务端 5xx、超时(408)、限流(429)、网络 IO。</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        RequestFailedException rfe => rfe.Status == 0 || rfe.Status >= 500 || rfe.Status is 408 or 429,
        IOException => true,
        _ => false,
    };
}
