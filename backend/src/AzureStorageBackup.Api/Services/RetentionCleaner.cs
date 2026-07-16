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

        // 删除不再被引用的 pack。
        foreach (var packId in info.Packs.Keys.Where(id => !referencedPacks.Contains(id)).ToList())
        {
            await container_.GetBlobClient(info.Packs[packId].Blob).DeleteIfExistsAsync(cancellationToken: ct);
            info.Packs.Remove(packId);
        }

        // 删除不再被引用的 data blob（枚举 data/ 前缀）。
        await foreach (var blob in container_.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", ct))
        {
            if (!referencedBlobs.Contains(blob.Name))
                await container_.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: ct);
        }

        await store.WriteInfoAsync(account, container, info, password, tier: null, ct);
    }
}
