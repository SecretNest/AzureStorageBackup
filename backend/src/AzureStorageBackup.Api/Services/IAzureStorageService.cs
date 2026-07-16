namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 与 Azure Blob Storage 交互的抽象。骨架阶段仅定义最小操作，随需求扩展
/// （分块上传、覆盖策略、SAS、进度回调等）。
/// </summary>
public interface IAzureStorageService
{
    /// <summary>确保容器存在，不存在则创建。</summary>
    Task EnsureContainerAsync(string containerName, CancellationToken ct = default);

    /// <summary>上传单个本地文件到指定容器。返回 Blob 的 URI。</summary>
    Task<Uri> UploadFileAsync(
        string containerName,
        string blobName,
        string localFilePath,
        CancellationToken ct = default);

    /// <summary>健康检查：能否连通 Storage 账户。</summary>
    Task<bool> CanConnectAsync(CancellationToken ct = default);
}
