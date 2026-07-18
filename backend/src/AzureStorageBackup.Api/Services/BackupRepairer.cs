using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>修复结果：已修复的路径、判为不可恢复的路径、被回收删除的孤儿 blob 名（§4.8）。</summary>
public sealed record RepairReport(
    IReadOnlyList<string> Repaired, IReadOnlyList<string> Unrecoverable, IReadOnlyList<string> DeletedOrphans);

/// <summary>
/// 从**本地文件**修复云端损坏/缺失/分卷不全的 blob（显式动作，PRD 检查）：
/// 本地文件仍在且内容 hash 一致 → 重压并**完整替换**该 blob（先删旧全部分卷）；归档内 mtime 无所谓
/// （展示用索引元数据，还原后重设时间/权限）。本地已删或 hash 变了且云端已坏 → 标记该文件在相关版本**不可恢复**。
/// 因 blob/pack 跨版本共享：修复后同步更新所有引用版本的分卷数/尺寸；pack 按所有版本的存活成员整体重压。
/// </summary>
public sealed class BackupRepairer(
    IBlobClientFactory factory,
    IBackupInfoStore store,
    IFileCompressor compressor,
    IFileHasher hasher,
    IBlobUploader uploader,
    string tempRoot,
    INotifier? notifier = null,
    IOperationLog? opLog = null,
    BackupChecker? checker = null,
    TrackedInfoStore? trackedInfo = null,
    ILocalIndexCache? indexCache = null)
{
    public async Task<RepairReport> RepairAsync(
        Account account, string container, string? password, string localRoot, int? version,
        CheckOptions checkOptions, AccessTier dataTier, long? volumeBytes, CancellationToken ct = default)
    {
        var info = await store.ReadInfoAsync(account, container, password, ct)
            ?? throw new InvalidOperationException("No backup found in container.");
        if (info.Versions.Count == 0)
            throw new InvalidOperationException("Backup has no versions.");
        var target = version is { } v
            ? info.Versions.FirstOrDefault(x => x.Version == v) ?? throw new InvalidOperationException($"Version {v} not found.")
            : info.Versions[^1];

        // 找出云端坏掉的 blob：用检查器（按所选深度）扫目标版本。孤儿列举留到删除步骤自行重算（TOCTOU 安全）。
        var report = await (checker ?? throw new InvalidOperationException("Repair requires a checker."))
            .CheckAsync(account, container, password, target.Version,
                checkOptions with { Local = LocalCheckLevel.None, ListOrphans = false }, localRoot, ct);
        var badBlobs = report.Findings
            .Where(f => f.Cloud == CloudState.MissingOrBad && f.Ref is not null)
            .Select(f => f.Ref!).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);

        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var repaired = new List<string>();
        var unrecoverable = new List<string>();
        var deletedOrphans = new List<string>();

        if (badBlobs.Count > 0)
        {
            // 载入全部版本索引（pack 成员跨版本聚合 + 修复后同步尺寸/标记不可恢复）。
            var indexes = new Dictionary<int, VersionIndex>();
            foreach (var ver in info.Versions)
                indexes[ver.Version] = await store.ReadIndexAsync(account, container, ver.IndexBlob, password, ct);

            var changedVersions = new HashSet<int>();
            foreach (var badRef in badBlobs)
            {
                if (badRef.StartsWith("packs/", StringComparison.Ordinal))
                    await RepairPackAsync(account, cc, badRef, info, indexes, localRoot, password, dataTier, volumeBytes,
                        repaired, unrecoverable, changedVersions, ct);
                else
                    await RepairBlobAsync(account, cc, badRef, indexes, localRoot, dataTier, volumeBytes,
                        repaired, unrecoverable, changedVersions, ct);
            }

            // 持久化被改动的版本索引 + 信息文件（经本地权威状态机，保持 ETag/缓存一致，避免下次备份 412）。
            var identity = info.Backup.CreatedAt.UtcTicks;
            foreach (var vnum in changedVersions)
            {
                await store.WriteIndexAsync(account, container, vnum, indexes[vnum], password, ct: ct);
                if (indexCache is not null)
                    await indexCache.PutAsync(account.Id, container, vnum, identity, indexes[vnum], ct);
            }
            if (trackedInfo is not null)
                await trackedInfo.WriteAsync(account, container, info, password, tier: null, ct: ct);
            else
                await store.WriteInfoAsync(account, container, info, password, ct: ct);
        }

        // 孤儿回收（§4.8）：修复写入已落地后进行——删除前**重新**构引用集（TOCTOU 安全）。
        if (checkOptions.ListOrphans)
            await DeleteOrphansAsync(account, container, cc, password, deletedOrphans, ct);

        await Record(NotificationEvents.CheckSuccess, $"repair:{container}",
            $"Repair finished: {container}",
            $"{repaired.Distinct().Count()} repaired, {unrecoverable.Distinct().Count()} unrecoverable, {deletedOrphans.Count} orphan(s) deleted", ct);
        if (unrecoverable.Count > 0)
            await Record(NotificationEvents.UnrecoverableError, $"repair:{container}",
                $"Unrecoverable files after repair: {container}", string.Join(", ", unrecoverable.Distinct().Take(20)), ct);

        return new RepairReport(repaired.Distinct().ToList(), unrecoverable.Distinct().ToList(), deletedOrphans);
    }

    /// <summary>
    /// 删除未被任何保留版本引用的孤儿 blob（§4.8）。**TOCTOU 安全**：删除前立刻**重新读**信息文件 + 全部版本索引
    /// 构造引用集（反映本次修复刚落地的改动）。构不出完整引用集（信息文件消失或某版本索引读失败）→ **放弃删除**、
    /// 记 Warning、一个都不删。绝不删除信息文件 / 索引 / 任何被引用卷（它们都在引用集内）。
    /// </summary>
    private async Task DeleteOrphansAsync(
        Account account, string container, BlobContainerClient cc, string? password, List<string> deletedOrphans, CancellationToken ct)
    {
        HashSet<string> referenced;
        try
        {
            var freshInfo = await store.ReadInfoAsync(account, container, password, ct)
                ?? throw new InvalidOperationException("Info file not found.");
            referenced = await (checker ?? throw new InvalidOperationException("Repair requires a checker."))
                .BuildReferencedSetAsync(account, container, password, freshInfo, ct);
        }
        catch (Exception ex)
        {
            if (opLog is not null)
                await opLog.AppendAsync(OperationLogLevel.Warning, $"repair:{container}",
                    $"Orphan cleanup abandoned: could not build the full reference set ({ex.Message}). No blobs were deleted.", ct, durable: true);
            return;
        }

        await foreach (var b in cc.GetBlobsAsync(cancellationToken: ct))
        {
            if (referenced.Contains(b.Name))
                continue;
            await cc.GetBlobClient(b.Name).DeleteIfExistsAsync(cancellationToken: ct);
            deletedOrphans.Add(b.Name);
        }
    }

    /// <summary>修复单文件 data blob：从任一引用路径的本地文件（hash 校验）重造并替换；更新全部引用版本的尺寸。</summary>
    private async Task RepairBlobAsync(
        Account account, BlobContainerClient cc, string blobRef, Dictionary<int, VersionIndex> indexes, string localRoot,
        AccessTier dataTier, long? volumeBytes, List<string> repaired, List<string> unrecoverable,
        HashSet<int> changedVersions, CancellationToken ct)
    {
        // 全部版本中引用此 blob 的条目（同内容不同路径可有多个）。
        var refs = indexes.SelectMany(kv => kv.Value.Entries
                .Where(e => e.Storage is { Kind: "blob" } s && s.Ref == blobRef)
                .Select(e => (Version: kv.Key, Entry: e)))
            .ToList();
        if (refs.Count == 0)
            return;
        var fullHash = refs[0].Entry.FullHash;
        var raw = refs[0].Entry.Storage!.Raw;

        // 从任一引用路径找到内容一致的本地文件。
        string? localSource = null;
        foreach (var (_, e) in refs)
        {
            var local = Path.Combine(localRoot, e.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(local) && fullHash is not null && await hasher.FullHashAsync(local, ct) == fullHash)
            {
                localSource = local;
                break;
            }
        }

        if (localSource is null)
        {
            // 本地无法提供 → 该 blob 的全部引用条目在各自版本不可恢复。
            foreach (var (vnum, e) in refs)
                MarkUnrecoverable(indexes[vnum], e.Path, unrecoverable, changedVersions, vnum);
            return;
        }

        var newSizes = await ReplaceBlobAsync(account, cc, blobRef, localSource, raw, dataTier, volumeBytes, ct);

        // 更新全部引用版本的分卷数/尺寸（内容不变故 ref 不变）。
        foreach (var (vnum, e) in refs)
        {
            var idx = indexes[vnum];
            var i = idx.Entries.IndexOf(e);
            idx.Entries[i] = e with { Storage = e.Storage! with { Volumes = newSizes.Count, VolumeSizes = [.. newSizes] } };
            changedVersions.Add(vnum);
        }
        repaired.AddRange(refs.Select(r => r.Entry.Path));
    }

    /// <summary>修复 pack：聚合所有版本的存活成员，从本地（hash 校验）重造能取到的成员并整体重压替换；
    /// 取不到的成员在其引用版本标记不可恢复。</summary>
    private async Task RepairPackAsync(
        Account account, BlobContainerClient cc, string packBlobRef, BackupInfoFile info, Dictionary<int, VersionIndex> indexes,
        string localRoot, string? password, AccessTier dataTier, long? volumeBytes,
        List<string> repaired, List<string> unrecoverable, HashSet<int> changedVersions, CancellationToken ct)
    {
        var packId = packBlobRef["packs/".Length..^".7z".Length];

        // 聚合所有版本引用此 pack 的成员：entryName → (fullHash, 引用它的版本+路径)。
        var members = new Dictionary<string, (string? Hash, List<(int Version, string Path)> Refs)>(StringComparer.Ordinal);
        foreach (var (vnum, idx) in indexes)
            foreach (var e in idx.Entries)
                if (e.Storage is { Kind: "pack" } s && s.Ref == packId && s.EntryName is { } en)
                {
                    if (!members.TryGetValue(en, out var m))
                        m = members[en] = (e.FullHash, []);
                    m.Refs.Add((vnum, e.Path));
                }

        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        var composeDir = Path.Combine(work, "compose");
        Directory.CreateDirectory(composeDir);
        try
        {
            var available = new List<string>();
            foreach (var (entryName, m) in members)
            {
                var local = Path.Combine(localRoot, entryName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(local) && m.Hash is not null && await hasher.FullHashAsync(local, ct) == m.Hash)
                {
                    var dest = Path.Combine(composeDir, entryName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(local, dest, overwrite: true);
                    available.Add(entryName);
                }
                else
                {
                    foreach (var (vnum, path) in m.Refs)
                        MarkUnrecoverable(indexes[vnum], path, unrecoverable, changedVersions, vnum);
                }
            }

            if (available.Count == 0)
            {
                info.Packs.Remove(packId); // 整包无法从本地重建，成员已全部标记不可恢复
                return;
            }

            // 用可得成员重压，替换同 packId：先覆盖上传新卷、后删残留旧卷（不再先删空）。
            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            var output = Path.Combine(outDir, packId + ".7z");
            var result = await compressor.CompressAsync(
                new CompressionRequest(composeDir, available, output, password, VolumeBytes: volumeBytes, StoreOnly: false), ct);
            await VolumeBlobIO.ReplaceAsync(uploader, account, cc, packBlobRef, result.VolumeFiles, dataTier, retry: null, ct);
            var newSizes = result.VolumeFiles.Select(f => new FileInfo(f).Length).ToList();

            if (info.Packs.TryGetValue(packId, out var pi))
                info.Packs[packId] = pi with
                {
                    Members = available.Select(en => members[en].Hash!).ToList(),
                    Volumes = newSizes.Count,
                    VolumeSizes = newSizes,
                };
            repaired.AddRange(available.SelectMany(en => members[en].Refs.Select(r => r.Path)));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>上传新内容替换单文件 blob：先覆盖上传新卷、后删残留旧卷（不再先删空）。返回新各分卷尺寸。</summary>
    private async Task<IReadOnlyList<long>> ReplaceBlobAsync(
        Account account, BlobContainerClient cc, string blobRef, string localSource, bool raw, AccessTier dataTier, long? volumeBytes, CancellationToken ct)
    {
        if (raw)
        {
            await VolumeBlobIO.ReplaceAsync(uploader, account, cc, blobRef, [localSource], dataTier, retry: null, ct,
                new Dictionary<string, string> { ["raw"] = "1" });
            return [new FileInfo(localSource).Length];
        }

        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(outDir);
        try
        {
            var srcDir = Path.GetDirectoryName(localSource)!;
            var entry = Path.GetFileName(localSource);
            var result = await compressor.CompressAsync(
                new CompressionRequest(srcDir, [entry], Path.Combine(outDir, "b.7z"), null, VolumeBytes: volumeBytes, StoreOnly: false), ct);
            await VolumeBlobIO.ReplaceAsync(uploader, account, cc, blobRef, result.VolumeFiles, dataTier, retry: null, ct);
            return result.VolumeFiles.Select(f => new FileInfo(f).Length).ToList();
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void MarkUnrecoverable(
        VersionIndex index, string path, List<string> unrecoverable, HashSet<int> changedVersions, int vnum)
    {
        if (!index.UnrecoverablePaths.Contains(path))
        {
            index.UnrecoverablePaths.Add(path);
            changedVersions.Add(vnum);
        }
        unrecoverable.Add(path);
    }

    private async Task Record(NotificationEvents evt, string source, string title, string body, CancellationToken ct)
    {
        if (opLog is not null)
            await opLog.AppendAsync(EventLog.LevelOf(evt), source, $"{title} — {body}", ct, durable: true);
        if (notifier is not null)
            await notifier.NotifyAsync(evt, title, body, ct);
    }
}
