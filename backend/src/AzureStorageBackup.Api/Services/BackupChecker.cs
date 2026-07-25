using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 备份完整性检查（M5、PRD 2.3），分级、双轴：
/// 云端轴 <see cref="CloudCheckLevel"/>（不查 / 元数据比对 / 存在+尺寸 / 下载重算 hash）；
/// 本地轴 <see cref="LocalCheckLevel"/>（不查 / 存在+尺寸+权限 / 内容 hash）。
/// 本地内容一致（可修复）＝修复的判据；结果按文件给出云端/本地状态，供修复与还原替代。
/// </summary>
public sealed class BackupChecker(
    IBlobClientFactory factory,
    IBackupInfoStore store,
    IFileCompressor? compressor = null,
    IFileHasher? hasher = null,
    string? tempRoot = null,
    INotifier? notifier = null,
    IOperationLog? opLog = null,
    TrackedInfoStore? trackedInfo = null)
{
    public async Task<CheckReport> CheckAsync(
        Account account, string container, string? password, int? version, CheckOptions options, string? localRoot = null,
        CancellationToken ct = default, int downloadConcurrency = 5)
    {
        var source = $"check:{account.Id}/{container}";
        await Record(NotificationEvents.CheckStart, source, $"Check started: {container}", "", ct);
        try
        {
            var report = await CheckCoreAsync(account, container, password, version, options, localRoot, downloadConcurrency, ct);
            var problems = report.Findings.Count(f => f.Cloud == CloudState.MissingOrBad);
            await Record(
                report.Ok ? NotificationEvents.CheckSuccess : NotificationEvents.CheckFailure, source,
                $"Check {(report.Ok ? "passed" : "failed")}: {container}",
                report.Ok
                    ? $"{report.Findings.Count} file(s) OK"
                    : $"{problems} problem(s), {report.RepairablePaths.Count} repairable from local", ct);
            return report;
        }
        catch (Exception ex)
        {
            await Record(NotificationEvents.CheckFailure, source, $"Check failed: {container}", ex.Message, ct);
            throw;
        }
    }

    private async Task Record(NotificationEvents evt, string source, string title, string body, CancellationToken ct)
    {
        if (opLog is not null)
            await opLog.AppendAsync(EventLog.LevelOf(evt), source, $"{title} — {body}", ct, durable: true);
        if (notifier is not null)
            await notifier.NotifyAsync(evt, title, body, ct);
    }

    private async Task<CheckReport> CheckCoreAsync(
        Account account, string container, string? password, int? version, CheckOptions options, string? localRoot,
        int downloadConcurrency, CancellationToken ct)
    {
        var info = await store.ReadInfoAsync(account, container, password, ct)
            ?? throw new InvalidOperationException("No backup found in container.");
        if (info.Versions.Count == 0)
            throw new InvalidOperationException("Backup has no versions.");

        var ver = version is { } v
            ? info.Versions.FirstOrDefault(x => x.Version == v)
              ?? throw new InvalidOperationException($"Version {v} not found.")
            : info.Versions[^1];

        var index = await store.ReadIndexAsync(account, container, ver.IndexBlob, password, ct);

        var metaIssue = options.Cloud == CloudCheckLevel.Metadata
            ? await CheckMetadataDriftAsync(account, container, password, info, ct)
            : null;

        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);

        // 云端状态（按文件）：只在 ExistenceSize/Content 级实际查数据 blob。
        var cloudBad = new HashSet<string>(StringComparer.Ordinal);
        if (options.Cloud >= CloudCheckLevel.ExistenceSize)
            cloudBad = await CloudCheckAsync(cc, info, index, options, password, downloadConcurrency, ct);

        var findings = new List<FileFinding>(index.Entries.Count);
        foreach (var e in index.Entries)
        {
            var refName = e.Storage is { } s ? BlobNameOf(s) : null;
            var cloud = options.Cloud < CloudCheckLevel.ExistenceSize || e.Storage is null
                ? CloudState.NotChecked
                : cloudBad.Contains(e.Path) ? CloudState.MissingOrBad : CloudState.Ok;
            var local = await LocalCheckAsync(e, localRoot, options.Local, ct);
            findings.Add(new FileFinding(e.Path, refName, cloud, local));
        }

        var orphans = options.ListOrphans
            ? await ListOrphansAsync(cc, account, container, password, info, ct)
            : [];

        return new CheckReport(ver.Version, findings, metaIssue) { OrphanBlobs = orphans };
    }

    /// <summary>
    /// 云端列表检查（§4.8）：枚举 container 全部 blob 减去引用集 = 孤儿。构不出**完整**引用集
    /// （缺版本索引且云端读失败）→ 放弃列举、记 Warning、返回空（绝不据不完整信息把被引用 blob 当孤儿）。
    /// </summary>
    private async Task<IReadOnlyList<string>> ListOrphansAsync(
        BlobContainerClient cc, Account account, string container, string? password, BackupInfoFile info, CancellationToken ct)
    {
        HashSet<string> referenced;
        try
        {
            referenced = await BuildReferencedSetAsync(account, container, password, info, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (opLog is not null)
                await opLog.AppendAsync(OperationLogLevel.Warning, $"check:{account.Id}/{container}",
                    $"Orphan detection skipped: could not build the full reference set ({ex.Message}).", ct, durable: true);
            return [];
        }

        var orphans = new List<string>();
        await foreach (var b in cc.GetBlobsAsync(cancellationToken: ct))
            if (!referenced.Contains(b.Name))
                orphans.Add(b.Name);
        return orphans;
    }

    /// <summary>
    /// 构造全部保留版本引用的 blob 名集合：读全部版本的第二级索引（本地权威 store），再调纯函数
    /// <see cref="ReferencedBlobNames"/>。任一版本索引读不到（本地缺且云端读失败）会抛出——调用方据此放弃删除。
    /// </summary>
    public async Task<HashSet<string>> BuildReferencedSetAsync(
        Account account, string container, string? password, BackupInfoFile info, CancellationToken ct = default)
    {
        var indexes = new Dictionary<int, VersionIndex>();
        foreach (var ver in info.Versions)
            indexes[ver.Version] = await store.ReadIndexAsync(account, container, ver.IndexBlob, password, ct);
        return ReferencedBlobNames(info, indexes);
    }

    /// <summary>
    /// **纯函数**：给定信息文件 + 全部保留版本索引，返回一切被引用的 blob 名（删除孤儿的承重安全依据）。涵盖：
    /// 信息文件（明文 + 加密两种命名都保护）；每个版本的 <c>IndexBlob</c>；每个 <see cref="StorageRef"/> 的**全部分卷**
    /// （单文件 blob 按 <see cref="StorageRef.Volumes"/>；pack 按 <see cref="PackInfo.Volumes"/>）——跨全部版本，
    /// 含仅被旧版本引用者。pack 被引用却在 <c>info.Packs</c> 缺元数据 → 无法确定分卷数 → 抛错（迫使放弃删除）。
    /// </summary>
    public static HashSet<string> ReferencedBlobNames(BackupInfoFile info, IReadOnlyDictionary<int, VersionIndex> indexes)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal)
        {
            // 信息文件：两种命名都纳入引用集，任何情况下都不当孤儿删除。
            BackupDiscovery.IndexBlobName,
            BackupDiscovery.EncryptedIndexBlobName,
        };

        // 每个版本的第二级索引 blob（即便某版本索引未在 indexes 中提供，其名也须保护）。
        foreach (var v in info.Versions)
            refs.Add(v.IndexBlob);

        // 每个版本索引的每个存储引用的全部分卷。
        foreach (var idx in indexes.Values)
            foreach (var e in idx.Entries)
            {
                if (e.Storage is not { } s)
                    continue;
                var baseName = BlobNameOf(s);
                var volumes = s.Kind == "pack"
                    ? info.Packs.TryGetValue(s.Ref, out var pi)
                        ? pi.Volumes
                        : throw new InvalidOperationException(
                            $"Pack '{s.Ref}' is referenced but missing from info.Packs; cannot determine its volumes.")
                    : s.Volumes;
                foreach (var name in VolumeBlobIO.VolumeNames(baseName, volumes))
                    refs.Add(name);
            }

        return refs;
    }

    /// <summary>
    /// 云端数据检查，返回**云端已坏的文件路径集**。ExistenceSize：每个 blob/分卷 HEAD 验存在+尺寸；
    /// Content：在此基础上对可读 blob 下载重算 hash（Archive 未活化则跳过，不误判为坏）。
    /// </summary>
    private async Task<HashSet<string>> CloudCheckAsync(
        BlobContainerClient cc, BackupInfoFile info, VersionIndex index, CheckOptions options, string? password,
        int downloadConcurrency, CancellationToken ct)
    {
        var bad = new HashSet<string>(StringComparer.Ordinal);

        // 按 blob 归组（blobName → 该 blob 的条目 + 期望分卷数/尺寸）。
        var groups = index.Entries
            .Where(e => e.Storage is not null)
            .GroupBy(e => BlobNameOf(e.Storage!))
            .ToList();

        var presentGroups = new List<IGrouping<string, IndexEntry>>();
        foreach (var g in groups)
        {
            var s = g.First().Storage!;
            var (vols, sizes) = ExpectedVolumes(info, s);
            var (present, sizeOk) = await VolumeBlobIO.VerifyVolumesAsync(cc, g.Key, vols, sizes, ct);
            if (!present || !sizeOk)
            {
                foreach (var e in g)
                    bad.Add(e.Path);
            }
            else
            {
                presentGroups.Add(g);
            }
        }

        if (options.Cloud >= CloudCheckLevel.Content)
        {
            var corrupted = await DeepVerifyAsync(cc, presentGroups, options, password, downloadConcurrency, ct);
            foreach (var p in corrupted)
                bad.Add(p);
        }

        return bad;
    }

    private static (int Volumes, IReadOnlyList<long> Sizes) ExpectedVolumes(BackupInfoFile info, StorageRef s) =>
        s.Kind == "pack"
            ? info.Packs.TryGetValue(s.Ref, out var pi) ? (pi.Volumes, pi.VolumeSizes) : (1, [])
            : (s.Volumes, s.VolumeSizes);

    /// <summary>深度校验：并发下载解压、重算 fullHash 与索引比对。仅内容不符计入损坏；
    /// Archive 未活化（下载报 archived）不计损坏（无法验证，跳过）。</summary>
    private async Task<IReadOnlyList<string>> DeepVerifyAsync(
        BlobContainerClient cc, List<IGrouping<string, IndexEntry>> presentGroups,
        CheckOptions options, string? password, int downloadConcurrency, CancellationToken ct)
    {
        if (compressor is null || hasher is null || string.IsNullOrEmpty(tempRoot))
            throw new InvalidOperationException("Content check requires compressor/hasher/tempRoot.");

        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var gate = new SemaphoreSlim(Math.Max(1, downloadConcurrency));
        try
        {
            var perGroup = await Task.WhenAll(presentGroups.Select(g =>
                VerifyGroupAsync(cc, work, g.Key, g.ToList(), options, password, gate, ct)));
            return perGroup.SelectMany(x => x).ToList();
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task<IReadOnlyList<string>> VerifyGroupAsync(
        BlobContainerClient cc, string work, string blobName, List<IndexEntry> members,
        CheckOptions options, string? password, SemaphoreSlim gate, CancellationToken ct)
    {
        var corrupted = new List<string>();
        var groupDir = Path.Combine(work, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(groupDir);
        await gate.WaitAsync(ct);
        try
        {
            var firstVolume = await VolumeBlobIO.DownloadAsync(cc, blobName, groupDir, ct);

            if (members[0].Storage!.Kind == "blob")
            {
                string content;
                if (members[0].Storage!.Raw)
                {
                    content = firstVolume;
                }
                else
                {
                    var extractDir = Path.Combine(groupDir, "x");
                    await compressor!.ExtractAsync(firstVolume, extractDir, password, ct);
                    content = Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories).First();
                }
                var actual = await hasher!.FullHashAsync(content, ct);
                foreach (var e in members)
                    if (e.FullHash is not null && actual != e.FullHash)
                        corrupted.Add(e.Path);
            }
            else
            {
                var extractDir = Path.Combine(groupDir, "x");
                await compressor!.ExtractAsync(firstVolume, extractDir, password, ct);

                foreach (var e in members)
                {
                    var path = Path.Combine(extractDir, e.Path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(path) || (e.FullHash is not null && await hasher!.FullHashAsync(path, ct) != e.FullHash))
                        corrupted.Add(e.Path);
                }
            }
        }
        catch (RequestFailedException ex) when (IsArchived(ex))
        {
            // Archive 未活化 → 无法下载验证；若指定活化 tier 则发起活化。不计为损坏。
            if (options.RehydrateTier is { } tier)
                await RehydrateAsync(cc, blobName, tier, ct);
        }
        catch
        {
            corrupted.AddRange(members.Select(m => m.Path)); // 其它下载/解压失败 → 整组损坏
        }
        finally
        {
            gate.Release();
            try { Directory.Delete(groupDir, recursive: true); } catch { /* best effort */ }
        }
        return corrupted;
    }

    private static bool IsArchived(RequestFailedException ex) =>
        ex.ErrorCode == "BlobArchived" || ex.Status == 409;

    private static Task RehydrateAsync(BlobContainerClient cc, string baseRef, AccessTier tier, CancellationToken ct) =>
        // 对归档全部分卷发起活化（异步，几小时后需用户重跑检查）；忽略失败（best effort）。
        BlobRehydration.BeginAsync(cc, baseRef, tier, ct);

    /// <summary>本地源文件状态。localRoot 缺失或本地轴关闭 → NotChecked。</summary>
    private async Task<LocalState> LocalCheckAsync(IndexEntry e, string? localRoot, LocalCheckLevel level, CancellationToken ct)
    {
        if (level == LocalCheckLevel.None || string.IsNullOrEmpty(localRoot))
            return LocalState.NotChecked;

        var local = Path.Combine(localRoot, e.Path.Replace('/', Path.DirectorySeparatorChar));

        // e.Path 来自云端索引，/import 之后即攻击者可控（设计 §5）：`..` 或绝对路径能让
        // Path.Combine 把探测点甩到 localRoot 之外，变成一个「文件是否存在 / 内容是否等于
        // 某个 hash」的确认预言机。判越界一律当 Missing——本地拿不出可用副本，既不读它、
        // 也不让它成为修复来源，与「本地文件不在」处置一致。
        if (!PathBoundary.IsWithin(localRoot, local))
            return LocalState.Missing;

        if (e.Kind == "symlink")
        {
            var target = TryLinkTarget(local);
            if (target is null)
                return LocalState.Missing;
            return target == e.Target ? LocalState.Ok : LocalState.Changed;
        }

        if (!File.Exists(local))
            return LocalState.Missing;

        if (level == LocalCheckLevel.Attributes)
        {
            var permOk = ReadPermissions(local) == e.Permissions;
            return new FileInfo(local).Length == e.Length && permOk ? LocalState.Ok : LocalState.Changed;
        }

        // Content：hash 一致＝可从本地修复。
        if (hasher is null)
            return LocalState.NotChecked;
        var full = await hasher.FullHashAsync(local, ct);
        return full == e.FullHash ? LocalState.Ok : LocalState.Changed;
    }

    private static string? TryLinkTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget;
        }
        catch { return null; }
    }

    private static string ReadPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return "0000";
        var mode = (int)File.GetUnixFileMode(path);
        return Convert.ToString(mode, 8).PadLeft(4, '0');
    }

    /// <summary>元数据漂移检查：云端 info 与本地权威缓存比对（版本数 / 各版本 IndexBlob / CreatedAt）。</summary>
    private async Task<string?> CheckMetadataDriftAsync(
        Account account, string container, string? password, BackupInfoFile cloud, CancellationToken ct)
    {
        if (trackedInfo is null)
            return null; // 无本地缓存可比对
        if (!await trackedInfo.HasLocalAsync(account, container, ct))
            return "No local cache to compare against (backup not synced on this device).";

        var local = await trackedInfo.LoadAsync(account, container, password, ct);
        if (local is null)
            return "Local cache missing while cloud has a backup.";
        if (local.Versions.Count != cloud.Versions.Count)
            return $"Version count differs: local {local.Versions.Count} vs cloud {cloud.Versions.Count}.";
        for (var i = 0; i < cloud.Versions.Count; i++)
        {
            if (local.Versions[i].IndexBlob != cloud.Versions[i].IndexBlob
                || local.Versions[i].CreatedAt != cloud.Versions[i].CreatedAt)
                return $"Version {cloud.Versions[i].Version} metadata differs between local cache and cloud.";
        }
        return null;
    }

    private static string BlobNameOf(StorageRef s) => s.Kind == "pack" ? $"packs/{s.Ref}.7z" : s.Ref;
}
