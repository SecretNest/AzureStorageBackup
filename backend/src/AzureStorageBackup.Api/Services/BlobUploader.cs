using Azure;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>一个待上传项：blob 名 + 源文件 + 目标 Tier。</summary>
public sealed record UploadItem(string BlobName, string FilePath, AccessTier Tier);

/// <summary>data/pack/索引 blob 的上传（M4 §5）：设置 Tier、重试退避、内容寻址幂等跳过、并发。</summary>
public interface IBlobUploader
{
    /// <summary>上传文件到 blob（带 Tier + 可选元数据）。blob 已存在则跳过并返回 false（内容寻址幂等）。</summary>
    Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null);

    /// <summary>并发上传一批项，上限 maxConcurrency。</summary>
    Task UploadBatchAsync(
        Account account, string container, IReadOnlyList<UploadItem> items,
        int maxConcurrency, RetryOptions? retry = null, CancellationToken ct = default);
}

public sealed class BlobUploader(IBlobClientFactory factory) : IBlobUploader
{
    public async Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var blob = factory.CreateServiceClient(account)
            .GetBlobContainerClient(container)
            .GetBlobClient(blobName);

        if ((await blob.ExistsAsync(ct)).Value)
            return false;

        var options = new BlobUploadOptions { AccessTier = tier };
        if (metadata is not null)
            options.Metadata = metadata.ToDictionary(kv => kv.Key, kv => kv.Value);

        await RetryPolicy.ExecuteAsync(async token =>
        {
            await using var stream = File.OpenRead(filePath);
            await blob.UploadAsync(stream, options, token);
        }, retry, IsTransient, ct);

        return true;
    }

    public async Task UploadBatchAsync(
        Account account, string container, IReadOnlyList<UploadItem> items,
        int maxConcurrency, RetryOptions? retry = null, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await UploadIfMissingAsync(account, container, item.BlobName, item.FilePath, item.Tier, retry, ct);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    /// <summary>可重试的瞬时错误：服务端 5xx、超时(408)、限流(429)、网络 IO。</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        RequestFailedException rfe => rfe.Status == 0 || rfe.Status >= 500 || rfe.Status is 408 or 429,
        IOException => true,
        _ => false,
    };
}
