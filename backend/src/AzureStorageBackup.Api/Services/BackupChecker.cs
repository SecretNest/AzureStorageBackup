using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>完整性检查结果。MissingRefs=引用但缺失的 blob；CorruptedPaths=深度校验内容不符/无法解开的条目。</summary>
public sealed record CheckResult(
    int Version, int CheckedRefs, IReadOnlyList<string> MissingRefs, IReadOnlyList<string> CorruptedPaths)
{
    public bool Ok => MissingRefs.Count == 0 && CorruptedPaths.Count == 0;
}

/// <summary>
/// 备份完整性检查（M5、PRD 2.3）：读某版本索引，校验引用的 data blob/pack。
/// 浅检查=存在性（快、不下载）；深度检查(deep)=下载解压并重算 hash 与索引比对。
/// 深度检查需要 compressor/hasher/tempRoot（DI 提供）。
/// </summary>
public sealed class BackupChecker(
    IBlobClientFactory factory,
    IBackupInfoStore store,
    IFileCompressor? compressor = null,
    IFileHasher? hasher = null,
    string? tempRoot = null,
    INotifier? notifier = null,
    IOperationLog? opLog = null)
{
    public async Task<CheckResult> CheckAsync(
        Account account, string container, string? password, int? version, bool deep = false,
        CancellationToken ct = default, int downloadConcurrency = 5)
    {
        var source = $"check:{container}";
        await Record(NotificationEvents.CheckStart, source, $"Check started: {container}", "", ct);
        try
        {
            var result = await CheckCoreAsync(account, container, password, version, deep, downloadConcurrency, ct);
            await Record(
                result.Ok ? NotificationEvents.CheckSuccess : NotificationEvents.CheckFailure, source,
                $"Check {(result.Ok ? "passed" : "failed")}: {container}",
                result.Ok
                    ? $"{result.CheckedRefs} object(s) OK"
                    : $"{result.MissingRefs.Count} missing, {result.CorruptedPaths.Count} corrupted", ct);
            return result;
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

    private async Task<CheckResult> CheckCoreAsync(
        Account account, string container, string? password, int? version, bool deep, int downloadConcurrency, CancellationToken ct)
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

        // blob 名 → 期望分卷数：单文件 blob 取索引条目 StorageRef.Volumes；pack 取 PackInfo.Volumes（压实会改，记在信息文件）。
        var refs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in index.Entries)
        {
            if (e.Storage is not { } s)
                continue;
            var name = BlobNameOf(s);
            var vols = s.Kind == "pack"
                ? (info.Packs.TryGetValue(s.Ref, out var pi) ? pi.Volumes : 1)
                : s.Volumes;
            refs[name] = Math.Max(refs.GetValueOrDefault(name, 1), Math.Max(1, vols));
        }

        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var missing = new List<string>();
        foreach (var (name, vols) in refs)
        {
            if (!await VolumeBlobIO.AllVolumesExistAsync(cc, name, vols, ct))
                missing.Add(name);
        }

        var corrupted = deep
            ? await DeepVerifyAsync(account, container, password, index, missing, downloadConcurrency, ct)
            : [];

        return new CheckResult(ver.Version, refs.Count, missing, corrupted);
    }

    /// <summary>深度校验：按存储分组**并发**下载解压（PRD 3.4），重算 fullHash 与索引比对；解不开或不符即损坏。</summary>
    private async Task<IReadOnlyList<string>> DeepVerifyAsync(
        Account account, string container, string? password, VersionIndex index, List<string> missing,
        int downloadConcurrency, CancellationToken ct)
    {
        if (compressor is null || hasher is null || string.IsNullOrEmpty(tempRoot))
            throw new InvalidOperationException("Deep check requires compressor/hasher/tempRoot.");

        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var gate = new SemaphoreSlim(Math.Max(1, downloadConcurrency));
        try
        {
            var groups = index.Entries
                .Where(e => e.Kind == "file" && e.Storage is not null && !missing.Contains(BlobNameOf(e.Storage!)))
                .GroupBy(e => BlobNameOf(e.Storage!))
                .ToList();

            var perGroup = await Task.WhenAll(groups.Select(g => VerifyGroupAsync(cc, work, g.Key, g.ToList(), password, gate, ct)));
            return perGroup.SelectMany(x => x).ToList();
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task<IReadOnlyList<string>> VerifyGroupAsync(
        Azure.Storage.Blobs.BlobContainerClient cc, string work, string blobName, List<IndexEntry> members,
        string? password, SemaphoreSlim gate, CancellationToken ct)
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
                // 单文件 blob：内容唯一（raw=原始字节；否则 7z 里唯一条目）；去重时可被多个条目引用，逐条比对。
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
        catch
        {
            corrupted.AddRange(members.Select(m => m.Path)); // 下载/解压失败 → 整组损坏
        }
        finally
        {
            gate.Release();
            try { Directory.Delete(groupDir, recursive: true); } catch { /* best effort */ }
        }
        return corrupted;
    }

    private static string BlobNameOf(StorageRef s) => s.Kind == "pack" ? $"packs/{s.Ref}.7z" : s.Ref;
}
