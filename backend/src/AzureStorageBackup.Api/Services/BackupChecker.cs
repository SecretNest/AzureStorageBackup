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
        var source = $"check:{container}";
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

        return new CheckReport(ver.Version, findings, metaIssue);
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

    private static async Task RehydrateAsync(BlobContainerClient cc, string baseRef, AccessTier tier, CancellationToken ct)
    {
        // 对归档首卷发起活化（异步，几小时后需用户重跑检查）；忽略失败（best effort）。
        try { await cc.GetBlobClient(baseRef).SetAccessTierAsync(tier, cancellationToken: ct); }
        catch { /* best effort */ }
    }

    /// <summary>本地源文件状态。localRoot 缺失或本地轴关闭 → NotChecked。</summary>
    private async Task<LocalState> LocalCheckAsync(IndexEntry e, string? localRoot, LocalCheckLevel level, CancellationToken ct)
    {
        if (level == LocalCheckLevel.None || string.IsNullOrEmpty(localRoot))
            return LocalState.NotChecked;

        var local = Path.Combine(localRoot, e.Path.Replace('/', Path.DirectorySeparatorChar));

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
