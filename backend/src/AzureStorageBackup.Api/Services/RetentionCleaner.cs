using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 版本保留清理（M4 §10）：退役超期版本，删其第二级索引及不再被任何有效版本引用的 data blob/pack。
/// 编排器备份完成时与调度器的 Cleanup 任务共用。
/// </summary>
public sealed class RetentionCleaner(IBlobClientFactory factory, IBackupInfoStore store, RetentionEvaluator retention)
{
    /// <summary>独立清理：自行读取信息文件。</summary>
    public async Task CleanupAsync(
        Account account, string container, string? password, RetentionPolicy policy, CancellationToken ct = default)
    {
        var info = await store.ReadInfoAsync(account, container, password, ct);
        if (info is not null && info.Versions.Count > 0)
            await CleanupAsync(account, container, password, policy, info, ct);
    }

    /// <summary>已持有信息文件时清理（编排器备份完成后调用）。</summary>
    public async Task CleanupAsync(
        Account account, string container, string? password, RetentionPolicy policy, BackupInfoFile info, CancellationToken ct = default)
    {
        var toDelete = retention.VersionsToDelete(
            info.Versions.Select(v => new VersionRef(v.Version, v.CreatedAt)).ToList(),
            policy, DateTimeOffset.UtcNow);
        if (toDelete.Count == 0)
            return;

        var container_ = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var deleted = new HashSet<int>(toDelete);

        // 删除退役版本的第二级索引，并从信息文件移除。
        foreach (var v in info.Versions.Where(v => deleted.Contains(v.Version)))
            await container_.GetBlobClient(v.IndexBlob).DeleteIfExistsAsync(cancellationToken: ct);
        info.Versions.RemoveAll(v => deleted.Contains(v.Version));

        // 收集剩余版本仍引用的 data blob 与 pack。
        var referencedBlobs = new HashSet<string>(StringComparer.Ordinal);
        var referencedPacks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in info.Versions)
        {
            var vi = await store.ReadIndexAsync(account, container, v.IndexBlob, password, ct);
            foreach (var e in vi.Entries)
            {
                if (e.Storage is null)
                    continue;
                if (e.Storage.Kind == "pack")
                    referencedPacks.Add(e.Storage.Ref);
                else
                    referencedBlobs.Add(e.Storage.Ref);
            }
        }

        // 删除不再被引用的 pack（含其分卷 packs/{id}.7z.NNN）。枚举 packs/ 前缀，按 packId 归组，
        // 避免仅删基名而漏删分卷（§7 分卷 pack）。
        var deletedPackIds = info.Packs.Keys.Where(id => !referencedPacks.Contains(id)).ToHashSet(StringComparer.Ordinal);
        await foreach (var blob in container_.GetBlobsAsync(BlobTraits.None, BlobStates.None, "packs/", ct))
        {
            if (deletedPackIds.Contains(PackIdOf(blob.Name)))
                await container_.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: ct);
        }
        foreach (var packId in deletedPackIds)
            info.Packs.Remove(packId);

        // 删除不再被引用的 data blob（枚举 data/ 前缀）。分卷名 data/{hash}.NNN 归一化回基名后再比对，
        // 避免误删仍被引用的分卷（§7、否则数据丢失）。
        await foreach (var blob in container_.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", ct))
        {
            if (!referencedBlobs.Contains(BaseRef(blob.Name)))
                await container_.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: ct);
        }

        await store.WriteInfoAsync(account, container, info, password, tier: null, ct);
    }

    /// <summary>把分卷名 baseRef.NNN（3 位数字后缀）归一化回基名；非分卷名原样返回（§7）。</summary>
    private static string BaseRef(string blobName)
    {
        var dot = blobName.LastIndexOf('.');
        if (dot >= 0 && blobName.Length - dot - 1 == 3)
        {
            var suffix = blobName.AsSpan(dot + 1);
            if (char.IsAsciiDigit(suffix[0]) && char.IsAsciiDigit(suffix[1]) && char.IsAsciiDigit(suffix[2]))
                return blobName[..dot];
        }
        return blobName;
    }

    /// <summary>从 pack blob 名（packs/{id}.7z 或 packs/{id}.7z.NNN）提取 packId。</summary>
    private static string PackIdOf(string blobName)
    {
        var rest = blobName["packs/".Length..];
        var cut = rest.IndexOf(".7z", StringComparison.Ordinal);
        return cut >= 0 ? rest[..cut] : rest;
    }
}
