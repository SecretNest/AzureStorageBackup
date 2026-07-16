using Azure.Storage.Blobs;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 基于 Azure.Storage.Blobs 的实现。骨架阶段提供可运行的最小实现，
/// 具体上传策略（分块、并发、重试、进度）等需求明确后再补。
/// </summary>
public class AzureStorageService(BlobServiceClient blobServiceClient, ILogger<AzureStorageService> logger)
    : IAzureStorageService
{
    public async Task EnsureContainerAsync(string containerName, CancellationToken ct = default)
    {
        var container = blobServiceClient.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
    }

    public async Task<Uri> UploadFileAsync(
        string containerName,
        string blobName,
        string localFilePath,
        CancellationToken ct = default)
    {
        var container = blobServiceClient.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(cancellationToken: ct);

        var blob = container.GetBlobClient(blobName);
        logger.LogInformation("上传 {Local} -> {Container}/{Blob}", localFilePath, containerName, blobName);

        await using var stream = File.OpenRead(localFilePath);
        await blob.UploadAsync(stream, overwrite: true, cancellationToken: ct);
        return blob.Uri;
    }

    public async Task<bool> CanConnectAsync(CancellationToken ct = default)
    {
        try
        {
            await blobServiceClient.GetPropertiesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "无法连接 Azure Storage 账户");
            return false;
        }
    }
}
