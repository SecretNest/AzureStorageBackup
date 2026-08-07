using Azure;
using Azure.Storage.Blobs;
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
/// 一次保留清理实际删掉了什么。清理从前是只做不说的：删了几个版本、腾出多少空间，做完就没人知道了。
/// <para>
/// pack 与 data blob 分开计数，因为它们是两种不同的存储形态（一箱小文件 vs 单个大文件），
/// 合成一个数字就看不出是哪一边在流转。两者都按**去重后的基名**计：一个分了卷的包在容器里是
/// <c>packs/{id}.7z.001…NNN</c> 好几个对象，按对象数报会把一个包说成几十个。
/// </para>
/// </summary>
public sealed record CleanupReport(int RetiredVersions, int DeletedPacks, int DeletedBlobs, long FreedBytes)
{
    public static readonly CleanupReport Empty = new(0, 0, 0, 0);

    public bool IsEmpty => RetiredVersions == 0 && DeletedPacks == 0 && DeletedBlobs == 0 && FreedBytes == 0;
}

/// <summary>
/// 版本保留清理（M4 §10）：退役超期版本，删其第二级索引及不再被任何有效版本引用的 data blob/pack；
/// 随后对仍存活但死重超阈值的 pack 做原地压实（§6，经 <see cref="DeadWeightCompactor"/>）。
/// 编排器备份完成时与调度器的 Cleanup 任务共用。
/// </summary>
public sealed class RetentionCleaner(
    IBlobClientFactory factory, IBackupInfoStore store, RetentionEvaluator retention,
    DeadWeightCompactor? compactor = null, ILocalIndexCache? indexCache = null, TrackedInfoStore? trackedInfo = null,
    BackupJournalStore? journals = null)
{
    /// <summary>独立清理：自行读取信息文件（优先本地权威副本）。</summary>
    public async Task<CleanupReport> CleanupAsync(
        Account account, string container, string? password, CleanupOptions options, CancellationToken ct = default,
        StagingArea.StagingLease? lease = null, bool sweepOrphans = false)
    {
        var info = trackedInfo is not null
            ? await trackedInfo.LoadAsync(account, container, password, ct)
            : await store.ReadInfoAsync(account, container, password, ct);
        // 一个版本都没提交过的容器（第一轮备份被取消：data/ 里已经有块、还没有任何版本索引）
        // 在这里直接返回，**即便调用方要求扫描**。判据的一半是"保留版本引用了什么"，而这半边
        // 此刻根本读不出来：info 为 null 时连信息文件都没有，Versions 为空时无从证明列出来的块
        // 是孤儿——把整个 data/ 当孤儿删掉，删的是用户真有的东西。
        // 这批块另有两条正当归宿，都不经过这里：journal 还在时它保着（判据认 journal）；
        // 而"配置删了又重建"那一支由**第一轮运行必扫**兜住（见 BackupRunControl.OpenJournalAsync），
        // 那条路走的是下面那个重载——没有这道闸门，且跑在本轮版本提交之后，判据的两半都齐了。
        return info is not null && info.Versions.Count > 0
            ? await CleanupAsync(account, container, password, options, info, ct, lease, sweepOrphans)
            : CleanupReport.Empty;
    }

    /// <summary>已持有信息文件时清理（编排器备份完成后调用）。</summary>
    /// <param name="lease">
    /// 调用方的暂存席位，透传给死重压实。备份收尾顺带清理时要传**备份自己的**席位——
    /// 另取一个会让均分的分母虚高，把并行的其它备份额度算小。
    /// </param>
    public async Task<CleanupReport> CleanupAsync(
        Account account, string container, string? password, CleanupOptions options,
        BackupInfoFile info, CancellationToken ct = default, StagingArea.StagingLease? lease = null,
        bool sweepOrphans = false)
    {
        var toDelete = retention.VersionsToDelete(
            info.Versions.Select(v => new VersionRef(v.Version, v.CreatedAt)).ToList(),
            options.Retention, DateTimeOffset.UtcNow);
        // 从前这里是「没有版本退役 → 直接返回」。取消和挂起打破了这个前提：容器里会留下
        // 「云上已有、索引里还没有」的完整块，而那种情形一个版本都不会退役。
        // 但也不能无条件全扫——孤儿扫描要把 data/ 与 packs/ 两个前缀整个列一遍，
        // 几十万对象的容器上这不是白干的，而绝大多数备份根本没有孤儿。
        if (toDelete.Count == 0 && !sweepOrphans)
            return CleanupReport.Empty;

        var container_ = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var deleted = new HashSet<int>(toDelete);

        var identity = info.Backup.CreatedAt.UtcTicks;
        long freedBytes = 0;

        // 删除退役版本的第二级索引（云端 + 本地缓存），并从信息文件移除。
        foreach (var v in info.Versions.Where(v => deleted.Contains(v.Version)))
        {
            var indexBlob = container_.GetBlobClient(v.IndexBlob);
            // 删之前先问一次尺寸。索引不是可以忽略不计的小东西——几十万条目的版本索引压出来能有
            // 几十 MB，漏掉它会让"释放了多少空间"明显偏小。每个退役版本一次 HEAD，而退役版本
            // 通常只有个位数，代价可以忽略。
            var indexBytes = await TrySizeOfAsync(indexBlob, ct);
            if ((await indexBlob.DeleteIfExistsAsync(cancellationToken: ct)).Value)
                freedBytes += indexBytes;
            if (indexCache is not null)
                await indexCache.RemoveAsync(account.Id, container, v.Version, ct);
        }
        info.Versions.RemoveAll(v => deleted.Contains(v.Version));

        // 收集剩余版本仍引用的 data blob、pack，以及每个 pack 仍有效的成员（供死重压实）。
        //
        // 这一段**每个保留版本各读一次索引**，孤儿扫描离不开它：不知道保留版本引用了什么，
        // 就没法判断列出来的对象是不是孤儿。但代价要说实话——读的是本地权威副本
        // （ILocalIndexCache，零云端流量），可 SQLite 里存的是序列化字节，命中也要把整份索引
        // 重建成对象：本仓库实测 50 万条目的索引一次约 0.9 s / 350 MB 分配，而它上面那层
        // 进程内 LRU（VersionIndexMemoryCache）默认只放 2 份。于是一份留着十几个版本的大备份，
        // 每晚一次「独立清理」就要付十几秒 CPU 加几 GB 的临时分配——这是每晚扫一遍孤儿的真实价钱。
        var referencedBlobs = new HashSet<string>(StringComparer.Ordinal);
        var referencedPacks = new HashSet<string>(StringComparer.Ordinal);
        var liveByPack = new Dictionary<string, Dictionary<string, LivePackMember>>(StringComparer.Ordinal);
        foreach (var v in info.Versions)
        {
            var vi = indexCache is not null
                ? await indexCache.ReadAsync(account, container, v.Version, identity, v.IndexBlob, password, ct)
                : await store.ReadIndexAsync(account, container, v.IndexBlob, password, ct);
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
                        // 按 entryName 归组（pack 内唯一）：同内容不同路径去重成同 fullHash 但仍是两个成员，不可用 hash 作 key。
                        var entryName = e.Storage.EntryName ?? e.Path;
                        members[entryName] = new LivePackMember(entryName, e.Length, e.FullHash);
                    }
                }
                else
                {
                    referencedBlobs.Add(e.Storage.Ref);
                }
            }
        }

        // 判据的另一半：活动 journal 引用着的内容。它们云上有、索引里还没有，只有 journal
        // 记着它们存在——删了就等于让下一轮恢复白跑，用户点 Resume 会发现要从头再传一遍。
        var active = journals is not null
            ? await journals.LoadActiveRefsAsync(account.Id, container, ct)
            : ActiveJournalRefs.Empty;

        // 删除不再被任何保留版本引用的 pack（含分卷 packs/{id}.7z.NNN，也清孤儿 pack）。枚举 packs/ 前缀按 packId 归组，
        // 避免仅删基名漏删分卷（§7）；判据用「未被保留版本引用」，与 data blob 侧对称。
        // 计数按基名去重（一个分了卷的包/blob 在容器里是好几个对象），释放字节则按对象逐个累加。
        var deletedPacks = new HashSet<string>(StringComparer.Ordinal);
        var deletedBlobs = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var blob in container_.GetBlobsAsync(BlobTraits.None, BlobStates.None, "packs/", ct))
        {
            var packId = PackIdOf(blob.Name);
            if (referencedPacks.Contains(packId) || active.Packs.Contains(packId))
                continue;
            if ((await container_.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: ct)).Value)
            {
                deletedPacks.Add(packId);
                freedBytes += blob.Properties.ContentLength ?? 0;
            }
        }
        var prunedFromInfo = info.Packs.Keys
            .Where(id => !referencedPacks.Contains(id) && !active.Packs.Contains(id)).ToList();
        foreach (var packId in prunedFromInfo)
            info.Packs.Remove(packId);

        // 删除不再被引用的 data blob（枚举 data/ 前缀）。分卷名 data/{hash}.NNN 归一化回基名后再比对，
        // 避免误删仍被引用的分卷（§7、否则数据丢失）。
        await foreach (var blob in container_.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", ct))
        {
            var baseRef = BaseRef(blob.Name);
            if (referencedBlobs.Contains(baseRef) || active.Blobs.Contains(baseRef))
                continue;
            if ((await container_.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: ct)).Value)
            {
                deletedBlobs.Add(baseRef);
                freedBytes += blob.Properties.ContentLength ?? 0;
            }
        }

        // 死重压实（§6）：对仍存活但死重超阈值的 pack 原地重压。**仅在真有版本退役时**才检查——
        // 死重是"某个成员不再被任何保留版本引用"堆出来的，只有退役会让它增加。
        //
        // 光是孤儿扫描（sweepOrphans 为真、却一个版本都没退役）绝不能把它带起来：压实失败或放弃时
        // DeadWeightCompactor 只是把同一个 DeadBytes 写回去（见其 catch 与"本地缺失成员"分支），
        // 下一轮的判断因此一模一样。挂在每晚一次的定时清理上，就是同一批包每晚下载、重压、重传一遍，
        // 永远如此——而在此之前，这件事只在一次退役之后发生一次。
        if (compactor is not null && toDelete.Count > 0)
            await compactor.CompactAsync(
                account, container_, password, info, liveByPack,
                options.DataTier, options.VolumeBytes, options.DeadWeightThreshold,
                options.LocalRoot, options.AllowRepackDownload, ct, lease);

        // 信息文件只在内容真的变了时才重写。变法只有两种：退役删掉了版本，或孤儿扫描从 info.Packs
        // 里剔掉了包（压实也会改 info.Packs，但它只在有退役时才跑，已被第一项覆盖）。
        // 判据不能写成"删了云端对象就重写"——删掉的孤儿本来就不在 info 里，那样写等于每次扫描
        // 都白付一次信息文件写入，而这条路上的写是带 If-Match 的条件写：白写一次就白换一次 ETag。
        if (toDelete.Count > 0 || prunedFromInfo.Count > 0)
        {
            if (trackedInfo is not null)
                await trackedInfo.WriteAsync(account, container, info, password, tier: null, ct);
            else
                await store.WriteInfoAsync(account, container, info, password, tier: null, ct);
        }

        // 死重压实是把 pack **重写**得更紧，不是删除，故不计入这里——把它算成"删掉了 N 个包"
        // 会让操作员以为有数据被退役了。
        return new CleanupReport(toDelete.Count, deletedPacks.Count, deletedBlobs.Count, freedBytes);
    }

    /// <summary>删除前问一次尺寸。blob 已经不在（并发清理、上一轮删到一半）时算 0，不让它中断清理。</summary>
    private static async Task<long> TrySizeOfAsync(BlobClient blob, CancellationToken ct)
    {
        try
        {
            return (await blob.GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength;
        }
        catch (RequestFailedException)
        {
            return 0;
        }
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
