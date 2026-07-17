using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>某 pack 内仍被有效版本引用的成员：fullHash → (归档条目名, 原始尺寸)。</summary>
public sealed record LivePackMember(string EntryName, long Length);

/// <summary>
/// 死重压实（M4 设计 §6）：pack 内成员被删/变更且所有有效版本都不再引用后成为死重。
/// 死重比例（原始尺寸）超阈值时**原地重压**该 pack——下载→解压→仅保留仍有效成员重压→覆盖同 packId blob（删旧分卷）。
/// 因 pack 按 packId+entryName 引用、有效成员 entryName 不变，无需改写任何版本索引。
/// 仅在版本退役时触发（死重只在此时增加）。数据 tier 为 Archive 等不可读时下载会失败——捕获跳过、仅记录死重。
/// </summary>
public sealed class DeadWeightCompactor(
    IBlobUploader uploader, IFileCompressor compressor, string tempRoot,
    ILogger<DeadWeightCompactor>? logger = null)
{
    /// <param name="liveByPack">packId → (fullHash → 仍有效成员)，由清理器扫描保留版本索引得出。</param>
    public async Task CompactAsync(
        Account account, BlobContainerClient container, string? password, BackupInfoFile info,
        IReadOnlyDictionary<string, Dictionary<string, LivePackMember>> liveByPack,
        AccessTier dataTier, long? volumeBytes, double threshold, CancellationToken ct)
    {
        foreach (var packId in info.Packs.Keys.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var packInfo = info.Packs[packId];
            var live = liveByPack.GetValueOrDefault(packId);
            var liveBytes = live?.Values.Sum(m => m.Length) ?? 0;
            var deadBytes = Math.Max(0, packInfo.OriginalBytes - liveBytes);
            var ratio = packInfo.OriginalBytes == 0 ? 0 : (double)deadBytes / packInfo.OriginalBytes;

            if (ratio <= threshold)
            {
                if (packInfo.DeadBytes != deadBytes)
                    info.Packs[packId] = packInfo with { DeadBytes = deadBytes };
                continue;
            }

            // 全部成员死重 → 整个 pack 已不被引用，交由保留清理删除，这里不处理。
            if (live is null || live.Count == 0)
                continue;

            try
            {
                await RecompactAsync(account, container, password, packId, live, dataTier, volumeBytes, ct);
                info.Packs[packId] = packInfo with
                {
                    Members = [.. live.Keys],
                    OriginalBytes = liveBytes,
                    DeadBytes = 0,
                };
                logger?.LogInformation(
                    "Compacted pack {Pack}: dropped {Dead} bytes of dead weight ({Ratio:P0})", packId, deadBytes, ratio);
            }
            catch (Exception ex)
            {
                // 例如 data tier=Archive 的 pack 无法在线下载（需 rehydrate）：记录死重、跳过，不影响备份。
                info.Packs[packId] = packInfo with { DeadBytes = deadBytes };
                logger?.LogWarning(ex,
                    "Dead-weight compaction skipped for pack {Pack} (unreadable, e.g. archived tier?)", packId);
            }
        }
    }

    private async Task RecompactAsync(
        Account account, BlobContainerClient container, string? password, string packId,
        Dictionary<string, LivePackMember> live, AccessTier dataTier, long? volumeBytes, CancellationToken ct)
    {
        var baseRef = $"packs/{packId}.7z";
        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // 下载并解压旧 pack（含全部成员）。
            var extractDir = Path.Combine(work, "x");
            var firstVolume = await VolumeBlobIO.DownloadAsync(container, baseRef, work, ct);
            await compressor.ExtractAsync(firstVolume, extractDir, password, ct);

            // 仅用仍有效成员的条目重压成新归档。
            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            var output = Path.Combine(outDir, packId + ".7z");
            var result = await compressor.CompressAsync(
                new CompressionRequest(extractDir, [.. live.Values.Select(m => m.EntryName)], output, password,
                    VolumeBytes: volumeBytes, StoreOnly: false), ct);

            // 删旧 pack 全部分卷（基名 + .NNN），再上传新归档到同 packId。
            await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, ct))
                await container.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: ct);

            await VolumeBlobIO.UploadAsync(
                uploader, account, container.Name, baseRef, result.VolumeFiles, dataTier, retry: null, ct);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }
}
