using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 单卷/多卷归档在 blob 上的读写（§7）。单卷用基名；多卷用 基名.001/.002...
/// 供数据 blob 与 pack 共用，还原/检查按同规则重组下载。
/// </summary>
public static class VolumeBlobIO
{
    /// <summary>上传压缩产出的卷文件。单卷→baseRef；多卷→baseRef.001、baseRef.002...</summary>
    public static async Task UploadAsync(
        IBlobUploader uploader, Account account, string container, string baseRef,
        IReadOnlyList<string> volumeFiles, AccessTier tier, CancellationToken ct)
    {
        if (volumeFiles.Count == 1)
        {
            await uploader.UploadIfMissingAsync(account, container, baseRef, volumeFiles[0], tier, ct: ct);
            return;
        }
        for (var i = 0; i < volumeFiles.Count; i++)
            await uploader.UploadIfMissingAsync(account, container, VolumeName(baseRef, i + 1), volumeFiles[i], tier, ct: ct);
    }

    /// <summary>归档是否存在（单卷或多卷首卷）。</summary>
    public static async Task<bool> ExistsAsync(BlobContainerClient cc, string baseRef, CancellationToken ct)
        => (await cc.GetBlobClient(baseRef).ExistsAsync(ct)).Value
           || (await cc.GetBlobClient(VolumeName(baseRef, 1)).ExistsAsync(ct)).Value;

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
