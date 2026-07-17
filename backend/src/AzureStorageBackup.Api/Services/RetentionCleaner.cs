using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>清理选项：保留策略 + 死重压实所需的数据 tier / 分卷 / 阈值。</summary>
public sealed record CleanupOptions
{
    public required RetentionPolicy Retention { get; init; }
    public AccessTier DataTier { get; init; } = AccessTier.Hot;
    public long? VolumeBytes { get; init; }

    /// <summary>死重压实阈值（默认 30%，M4 §6）。</summary>
    public double DeadWeightThreshold { get; init; } = 0.30;

    /// <summary>本地源根：死重重 pack 时优先用本地文件补齐成员。</summary>
    public string? LocalRoot { get; init; }

    /// <summary>本地缺失成员时是否允许下载云端 pack 补齐（按数据 tier 的开关，Archive 默认 false）。</summary>
    public bool AllowRepackDownload { get; init; } = true;
}

/// <summary>
/// 版本保留清理（M4 §10）：退役超期版本，删其第二级索引及不再被任何有效版本引用的 data blob/pack；
/// 随后对仍存活但死重超阈值的 pack 做原地压实（§6，经 <see cref="DeadWeightCompactor"/>）。
/// 编排器备份完成时与调度器的 Cleanup 任务共用。
/// </summary>
public sealed class RetentionCleaner(
    IBlobClientFactory factory, IBackupInfoStore store, RetentionEvaluator retention,
    DeadWeightCompactor? compactor = null)
{
    /// <summary>独立清理：自行读取信息文件。</summary>
    public async Task CleanupAsync(
        Account account, string container, string? password, CleanupOptions options, CancellationToken ct = default)
    {
        var info = await store.ReadInfoAsync(account, container, password, ct);
        if (info is not null && info.Versions.Count > 0)
            await CleanupAsync(account, container, password, options, info, ct);
    }

    /// <summary>已持有信息文件时清理（编排器备份完成后调用）。</summary>
    public async Task CleanupAsync(
        Account account, string container, string? password, CleanupOptions options,
        BackupInfoFile info, CancellationToken ct = default)
    {
        var toDelete = retention.VersionsToDelete(
            info.Versions.Select(v => new VersionRef(v.Version, v.CreatedAt)).ToList(),
            options.Retention, DateTimeOffset.UtcNow);
        if (toDelete.Count == 0)
            return;

        var container_ = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var deleted = new HashSet<int>(toDelete);

        // 删除退役版本的第二级索引，并从信息文件移除。
        foreach (var v in info.Versions.Where(v => deleted.Contains(v.Version)))
            await container_.GetBlobClient(v.IndexBlob).DeleteIfExistsAsync(cancellationToken: ct);
        info.Versions.RemoveAll(v => deleted.Contains(v.Version));

        // 收集剩余版本仍引用的 data blob、pack，以及每个 pack 仍有效的成员（供死重压实）。
        var referencedBlobs = new HashSet<string>(StringComparer.Ordinal);
        var referencedPacks = new HashSet<string>(StringComparer.Ordinal);
        var liveByPack = new Dictionary<string, Dictionary<string, LivePackMember>>(StringComparer.Ordinal);
        foreach (var v in info.Versions)
        {
            var vi = await store.ReadIndexAsync(account, container, v.IndexBlob, password, ct);
            foreach (var e in vi.Entries)
            {
                if (e.Storage is null)
                    continue;
                if (e.Storage.Kind == "pack")
                {
                    referencedPacks.Add(e.Storage.Ref);
                    if (e.FullHash is not null)
                    {
                        var members = liveByPack.TryGetValue(e.Storage.Ref, out var m)
                            ? m
                            : liveByPack[e.Storage.Ref] = new Dictionary<string, LivePackMember>(StringComparer.Ordinal);
                        members[e.FullHash] = new LivePackMember(e.Storage.EntryName ?? e.Path, e.Length);
                    }
                }
                else
                {
                    referencedBlobs.Add(e.Storage.Ref);
                }
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

        // 死重压实（§6）：对仍存活但死重超阈值的 pack 原地重压。仅在版本退役后（死重可能增加）才检查。
        if (compactor is not null)
            await compactor.CompactAsync(
                account, container_, password, info, liveByPack,
                options.DataTier, options.VolumeBytes, options.DeadWeightThreshold,
                options.LocalRoot, options.AllowRepackDownload, ct);

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
