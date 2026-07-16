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
        Account account, string container, string? password, int? version, bool deep = false, CancellationToken ct = default)
    {
        var source = $"check:{container}";
        await Record(NotificationEvents.CheckStart, source, $"Check started: {container}", "", ct);
        try
        {
            var result = await CheckCoreAsync(account, container, password, version, deep, ct);
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
            await opLog.AppendAsync(EventLog.LevelOf(evt), source, $"{title} — {body}", ct);
        if (notifier is not null)
            await notifier.NotifyAsync(evt, title, body, ct);
    }

    private async Task<CheckResult> CheckCoreAsync(
        Account account, string container, string? password, int? version, bool deep, CancellationToken ct)
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

        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in index.Entries)
        {
            if (e.Storage is { } s)
                refs.Add(BlobNameOf(s));
        }

        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var missing = new List<string>();
        foreach (var name in refs)
        {
            if (!(await cc.GetBlobClient(name).ExistsAsync(ct)).Value)
                missing.Add(name);
        }

        var corrupted = deep
            ? await DeepVerifyAsync(account, container, password, index, missing, ct)
            : [];

        return new CheckResult(ver.Version, refs.Count, missing, corrupted);
    }

    /// <summary>深度校验：按存储分组下载解压，重算 fullHash 与索引比对；解不开或不符即损坏。</summary>
    private async Task<IReadOnlyList<string>> DeepVerifyAsync(
        Account account, string container, string? password, VersionIndex index, List<string> missing, CancellationToken ct)
    {
        if (compressor is null || hasher is null || string.IsNullOrEmpty(tempRoot))
            throw new InvalidOperationException("Deep check requires compressor/hasher/tempRoot.");

        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var corrupted = new List<string>();
        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var groups = index.Entries
                .Where(e => e.Kind == "file" && e.Storage is not null)
                .GroupBy(e => BlobNameOf(e.Storage!));

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                if (missing.Contains(group.Key))
                    continue; // 缺失已单独报告

                var members = group.ToList();
                var extractDir = Path.Combine(work, "x");
                try
                {
                    if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
                    var archive = Path.Combine(work, "arc.7z");
                    if (File.Exists(archive)) File.Delete(archive);

                    await cc.GetBlobClient(group.Key).DownloadToAsync(archive, ct);
                    await compressor.ExtractAsync(archive, extractDir, password, ct);

                    foreach (var e in members)
                    {
                        var path = Path.Combine(extractDir, e.Path.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(path) || (e.FullHash is not null && await hasher.FullHashAsync(path, ct) != e.FullHash))
                            corrupted.Add(e.Path);
                    }
                }
                catch
                {
                    // 下载/解压失败 → 整组视为损坏
                    corrupted.AddRange(members.Select(m => m.Path));
                }
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }

        return corrupted;
    }

    private static string BlobNameOf(StorageRef s) => s.Kind == "pack" ? $"packs/{s.Ref}.7z" : s.Ref;
}
