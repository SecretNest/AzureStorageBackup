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
    /// <summary>
    /// 上传压缩产出的卷文件。单卷→baseRef；多卷→baseRef.001、baseRef.002...
    /// 多卷时**倒序上传**（先 .00N、最后 .001），使首卷 .001 成为「整族齐全」的提交标记——
    /// 上传中断时 .001 尚未写入，避免部分上传被存在性检查误判为已存在（§7）。
    /// </summary>
    public static async Task UploadAsync(
        IBlobUploader uploader, Account account, string container, string baseRef,
        IReadOnlyList<string> volumeFiles, AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default)
    {
        if (volumeFiles.Count == 1)
        {
            await uploader.UploadIfMissingAsync(account, container, baseRef, volumeFiles[0], tier, retry, ct);
            return;
        }
        for (var i = volumeFiles.Count - 1; i >= 0; i--)
            await uploader.UploadIfMissingAsync(account, container, VolumeName(baseRef, i + 1), volumeFiles[i], tier, retry, ct);
    }

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
