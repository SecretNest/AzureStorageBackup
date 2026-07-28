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

        if (!overwrite && (await blob.ExistsAsync(ct)).Value)
            return false;

        var options = new BlobUploadOptions { AccessTier = tier, ProgressHandler = progress };
        if (metadata is not null)
            options.Metadata = metadata.ToDictionary(kv => kv.Key, kv => kv.Value);

        await RetryPolicy.ExecuteAsync(async token =>
        {
            await using var stream = File.OpenRead(filePath);
            await blob.UploadAsync(stream, options, token);
        }, retry, IsTransient, ct);

        return true;
    }

    /// <summary>可重试的瞬时错误：服务端 5xx、超时(408)、限流(429)、网络 IO。</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        RequestFailedException rfe => rfe.Status == 0 || rfe.Status >= 500 || rfe.Status is 408 or 429,
        IOException => true,
        _ => false,
    };
}
