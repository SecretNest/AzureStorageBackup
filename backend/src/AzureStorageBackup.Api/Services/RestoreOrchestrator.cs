using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>一次还原请求。Version 为 null 时还原最新版本。</summary>
public sealed record RestoreRequest
{
    public required Account Account { get; init; }
    public required string Container { get; init; }
    public required string TargetRoot { get; init; }
    public string? Password { get; init; }
    public int? Version { get; init; }

    /// <summary>下载并发上限（PRD 3.4，默认 5）。</summary>
    public int DownloadConcurrency { get; init; } = 5;

    /// <summary>不可恢复文件的替代来源：路径 → 用哪个版本的该文件内容替代（用户逐个选，可批量）。</summary>
    public IReadOnlyDictionary<string, int> Substitutions { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>遇 Archive blob 的活化目标 tier（Archive 无法直接下载，需先活化，异步几小时）。</summary>
    public AccessTier RehydrateTier { get; init; } = AccessTier.Hot;

    /// <summary>活化轮询间隔秒（还原 job 不占锁，可长等）。</summary>
    public int RehydratePollSeconds { get; init; } = 60;

    /// <summary>还原完成后把活化过的 blob 重新归档回 Archive（默认 true，保持备份原 tier、避免长期热存费）。</summary>
    public bool ReArchiveAfterRestore { get; init; } = true;
}

/// <summary>还原结果。SkippedFiles = 本地已是相同内容而跳过（仅当变更时覆盖）。FailedFiles = 所在存储分组下载/解压失败而未能还原。</summary>
public sealed record RestoreResult(int Version, int RestoredFiles, int SkippedFiles, int RestoredDirs, int FailedFiles);

/// <summary>
/// 还原编排器（M5、PRD 1.5）：读信息文件+第二级索引，下载 data blob / pack 并 7z 解压，
/// 写回本地根，恢复权限/mtime 与空文件夹。"覆盖仅当变更时"——本地已是相同 hash 则跳过。
/// </summary>
public sealed class RestoreOrchestrator(
    IBlobClientFactory factory,
    IBackupInfoStore store,
    IFileCompressor compressor,
    IFileHasher hasher,
    string tempRoot,
    INotifier? notifier = null,
    IOperationLog? opLog = null)
{
    public async Task<RestoreResult> RunAsync(RestoreRequest request, CancellationToken ct = default, IProgress<string>? phase = null)
    {
        var source = $"restore:{request.Container}";
        await Record(NotificationEvents.RestoreStart, source, $"Restore started: {request.Container}", request.TargetRoot, ct);
        try
        {
            var result = await RunCoreAsync(request, phase, ct);
            await Record(NotificationEvents.RestoreSuccess, source, $"Restore succeeded: {request.Container}",
                $"Restored {result.RestoredFiles} file(s) to {request.TargetRoot} (version {result.Version})", ct);
            return result;
        }
        catch (Exception ex)
        {
            await Record(NotificationEvents.RestoreFailure, source, $"Restore failed: {request.Container}", ex.Message, ct);
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

    private async Task<RestoreResult> RunCoreAsync(RestoreRequest request, IProgress<string>? phase, CancellationToken ct)
    {
        var info = await store.ReadInfoAsync(request.Account, request.Container, request.Password, ct)
            ?? throw new InvalidOperationException("No backup found in container.");
        if (info.Versions.Count == 0)
            throw new InvalidOperationException("Backup has no versions.");

        var version = request.Version is { } v
            ? info.Versions.FirstOrDefault(x => x.Version == v)
              ?? throw new InvalidOperationException($"Version {v} not found.")
            : info.Versions[^1];

        var index = await store.ReadIndexAsync(request.Account, request.Container, version.IndexBlob, request.Password, ct);

        Directory.CreateDirectory(request.TargetRoot);
        var container = factory.CreateServiceClient(request.Account).GetBlobContainerClient(request.Container);

        var restored = 0;
        var skipped = 0;

        // 逐路径生效条目：默认取本版本；被替代的路径改用指定版本的同路径条目（内容+元数据取该版本）。
        var byPath = index.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal); // 真正解析成功的替代路径
        foreach (var grp in request.Substitutions.GroupBy(kv => kv.Value))
        {
            var sv = info.Versions.FirstOrDefault(x => x.Version == grp.Key);
            if (sv is null)
                continue; // 替代版本已被保留清理删除 → 该组全部回落跳过
            var srcIndex = await store.ReadIndexAsync(request.Account, request.Container, sv.IndexBlob, request.Password, ct);
            var srcByPath = srcIndex.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
            foreach (var kv in grp)
                if (srcByPath.TryGetValue(kv.Key, out var se))
                {
                    byPath[kv.Key] = se;
                    resolved.Add(kv.Key);
                }
        }

        // 不可恢复且未「解析成功」替代 → 跳过（声明了意图但替代不可得的也回落跳过，不报错）。
        var unresolved = index.UnrecoverablePaths.Where(p => !resolved.Contains(p)).ToHashSet(StringComparer.Ordinal);
        skipped += unresolved.Count;

        // 空文件夹（还原需重建）
        foreach (var dir in index.EmptyDirs)
            Directory.CreateDirectory(Path.Combine(request.TargetRoot, ToLocal(dir)));

        // symlink 与文件分开处理
        var fileEntries = new List<IndexEntry>();
        foreach (var e in byPath.Values)
        {
            if (unresolved.Contains(e.Path))
                continue;
            if (e.Kind == "symlink")
            {
                if (RestoreSymlink(request.TargetRoot, e)) restored++;
                else skipped++;
            }
            else
            {
                fileEntries.Add(e);
            }
        }

        // 按存储分组：同一 pack 只下载/解压一次。各组并发下载（PRD 3.4），每组独立临时子目录避免冲突。
        var work = NewTempDir();
        var rehydrated = new System.Collections.Concurrent.ConcurrentBag<string>(); // 活化过的 blob 基名，完成后重新归档
        using var gate = new SemaphoreSlim(Math.Max(1, request.DownloadConcurrency));
        var failed = 0;
        try
        {
            var groups = fileEntries.Where(e => e.Storage is not null).GroupBy(e => StorageKey(e.Storage!)).ToList();
            var tasks = groups.Select(async g =>
            {
                try { return await RestoreGroupAsync(container, request, work, g.ToList(), gate, rehydrated, phase, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    phase?.Report($"Group failed ({g.Key}): {ex.Message}");
                    return (Restored: 0, Skipped: 0, Failed: g.Count());
                }
            });
            var counts = await Task.WhenAll(tasks);
            restored += counts.Sum(c => c.Restored);
            skipped += counts.Sum(c => c.Skipped);
            failed += counts.Sum(c => c.Failed);
        }
        finally
        {
            TryDelete(work);
        }

        // 还原完成后把活化过的 blob 重新归档回 Archive（保持备份原 tier；best effort）。
        if (request.ReArchiveAfterRestore && !rehydrated.IsEmpty)
        {
            phase?.Report($"Re-archiving {rehydrated.Distinct().Count()} object(s)…");
            foreach (var baseRef in rehydrated.Distinct())
                await SetTierForVolumesAsync(container, baseRef, AccessTier.Archive, ct);
        }

        return new RestoreResult(version.Version, restored, skipped, index.EmptyDirs.Count, failed);
    }

    private async Task<(int Restored, int Skipped, int Failed)> RestoreGroupAsync(
        BlobContainerClient container, RestoreRequest request, string work,
        List<IndexEntry> group, SemaphoreSlim gate, System.Collections.Concurrent.ConcurrentBag<string> rehydrated,
        IProgress<string>? phase, CancellationToken ct)
    {
        var skipped = 0;
        var needed = new List<IndexEntry>();
        foreach (var e in group)
        {
            if (await NeedsRestoreAsync(request.TargetRoot, e, ct))
                needed.Add(e);
            else
                skipped++;
        }
        if (needed.Count == 0)
            return (0, skipped, 0);

        var storage = group[0].Storage!;
        var blobName = storage.Kind == "pack" ? $"packs/{storage.Ref}.7z" : storage.Ref;

        // 每组独立临时目录（并发安全）。
        var groupDir = Path.Combine(work, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(groupDir);
        var restored = 0;
        await gate.WaitAsync(ct);
        try
        {
            string firstVolume;
            try
            {
                firstVolume = await VolumeBlobIO.DownloadAsync(container, blobName, groupDir, ct);
            }
            catch (RequestFailedException ex) when (ex.ErrorCode == "BlobArchived" || ex.Status == 409)
            {
                // Archive 未活化：发起活化并轮询到就绪（可长等，还原 job 不占锁），再下载。
                await EnsureOnlineAsync(container, blobName, request.RehydrateTier, request.RehydratePollSeconds, phase, ct);
                rehydrated.Add(blobName);
                firstVolume = await VolumeBlobIO.DownloadAsync(container, blobName, groupDir, ct);
            }

            if (storage.Kind == "blob")
            {
                // 单文件 blob：内容就是一个文件（raw=原始字节；否则 7z 里唯一条目）。
                // 内容寻址去重时同一 blob 可被多个路径引用 → 复制给每个引用条目。
                string content;
                if (storage.Raw)
                {
                    content = firstVolume;
                }
                else
                {
                    var extractDir = Path.Combine(groupDir, "x");
                    await compressor.ExtractAsync(firstVolume, extractDir, request.Password, ct);
                    content = Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories).First();
                }
                foreach (var e in needed)
                {
                    var dest = Path.Combine(request.TargetRoot, ToLocal(e.Path));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(content, dest, overwrite: true);
                    ApplyMetadata(dest, e);
                    restored++;
                }
            }
            else
            {
                // pack：解压后按各成员的归档条目名复制。
                var extractDir = Path.Combine(groupDir, "x");
                await compressor.ExtractAsync(firstVolume, extractDir, request.Password, ct);

                foreach (var e in needed)
                {
                    var source = Path.Combine(extractDir, ToLocal(e.Path));
                    var dest = Path.Combine(request.TargetRoot, ToLocal(e.Path));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(source, dest, overwrite: true);
                    ApplyMetadata(dest, e);
                    restored++;
                }
            }
        }
        finally
        {
            gate.Release();
            try { Directory.Delete(groupDir, recursive: true); } catch { /* best effort */ }
        }
        return (restored, skipped, 0);
    }

    private async Task<bool> NeedsRestoreAsync(string targetRoot, IndexEntry entry, CancellationToken ct)
    {
        var dest = Path.Combine(targetRoot, ToLocal(entry.Path));
        if (!File.Exists(dest) || entry.FullHash is null)
            return true;

        // 覆盖仅当变更时：本地已是相同内容则跳过。
        return await hasher.FullHashAsync(dest, ct) != entry.FullHash;
    }

    private bool RestoreSymlink(string targetRoot, IndexEntry entry)
    {
        if (entry.Target is null)
            return false;

        var dest = Path.Combine(targetRoot, ToLocal(entry.Path));
        var info = new FileInfo(dest);
        if (info.Exists && info.LinkTarget == entry.Target)
            return false; // 未变

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (info.Exists) File.Delete(dest);
        File.CreateSymbolicLink(dest, entry.Target);
        return true;
    }

    private static void ApplyMetadata(string dest, IndexEntry entry)
    {
        File.SetLastWriteTimeUtc(dest, entry.Mtime.UtcDateTime);

        if (!OperatingSystem.IsWindows()
            && !string.IsNullOrEmpty(entry.Permissions) && entry.Permissions != "0000")
        {
            try
            {
                File.SetUnixFileMode(dest, (UnixFileMode)Convert.ToInt32(entry.Permissions, 8));
            }
            catch (FormatException) { /* 非八进制权限，忽略 */ }
        }
    }

    private static string StorageKey(StorageRef s) => s.Kind == "pack" ? "pack:" + s.Ref : "blob:" + s.Ref;

    /// <summary>确保某归档（含全部分卷）已从 Archive 活化为可下载：对未活化的发起活化，轮询到全部就绪。</summary>
    private static async Task EnsureOnlineAsync(
        BlobContainerClient container, string baseRef, AccessTier tier, int pollSeconds,
        IProgress<string>? phase, CancellationToken ct)
    {
        var vols = new List<string>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, ct))
            vols.Add(b.Name);

        // 未开始活化的分卷发起活化（标准优先级；全部分卷，非仅首卷）。
        // 注意：此处故意不复用 BlobRehydration.BeginAsync（它逐卷吞掉 SetAccessTierAsync 异常）——
        // 本方法持有下载并发 gate 并无限期轮询，活化请求失败必须快速传播为还原失败，
        // 否则会在吞掉异常后无限期挂起并占住 gate。
        foreach (var name in vols)
        {
            var props = (await container.GetBlobClient(name).GetPropertiesAsync(cancellationToken: ct)).Value;
            if (props.AccessTier == "Archive" && string.IsNullOrEmpty(props.ArchiveStatus))
                await container.GetBlobClient(name).SetAccessTierAsync(tier, cancellationToken: ct);
        }

        // 轮询到全部分卷不再是 Archive（活化完成，几小时级）。
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pending = 0;
            foreach (var name in vols)
            {
                var props = (await container.GetBlobClient(name).GetPropertiesAsync(cancellationToken: ct)).Value;
                if (props.AccessTier == "Archive")
                    pending++;
            }
            if (pending == 0)
                return;
            phase?.Report($"Waiting for rehydration of {baseRef} — {pending} volume(s) still archived…");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, pollSeconds)), ct);
        }
    }

    /// <summary>把某归档的全部分卷设为指定 tier（best effort，用于还原后重新归档）。</summary>
    private static async Task SetTierForVolumesAsync(BlobContainerClient container, string baseRef, AccessTier tier, CancellationToken ct)
    {
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, ct))
        {
            try { await container.GetBlobClient(b.Name).SetAccessTierAsync(tier, cancellationToken: ct); }
            catch { /* best effort */ }
        }
    }

    private static string ToLocal(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private string NewTempDir()
    {
        var dir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
