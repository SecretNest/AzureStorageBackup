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
}

/// <summary>还原结果。SkippedFiles = 本地已是相同内容而跳过（仅当变更时覆盖）。</summary>
public sealed record RestoreResult(int Version, int RestoredFiles, int SkippedFiles, int RestoredDirs);

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
    public async Task<RestoreResult> RunAsync(RestoreRequest request, CancellationToken ct = default)
    {
        var source = $"restore:{request.Container}";
        await Record(NotificationEvents.RestoreStart, source, $"Restore started: {request.Container}", request.TargetRoot, ct);
        try
        {
            var result = await RunCoreAsync(request, ct);
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
            await opLog.AppendAsync(EventLog.LevelOf(evt), source, $"{title} — {body}", ct);
        if (notifier is not null)
            await notifier.NotifyAsync(evt, title, body, ct);
    }

    private async Task<RestoreResult> RunCoreAsync(RestoreRequest request, CancellationToken ct)
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

        // 空文件夹（还原需重建）
        foreach (var dir in index.EmptyDirs)
            Directory.CreateDirectory(Path.Combine(request.TargetRoot, ToLocal(dir)));

        // symlink 与文件分开处理
        var fileEntries = new List<IndexEntry>();
        foreach (var e in index.Entries)
        {
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

        var work = NewTempDir();
        try
        {
            // 按存储分组：同一 pack 只下载/解压一次。
            foreach (var group in fileEntries.Where(e => e.Storage is not null).GroupBy(e => StorageKey(e.Storage!)))
            {
                ct.ThrowIfCancellationRequested();

                var needed = new List<IndexEntry>();
                foreach (var e in group)
                {
                    if (await NeedsRestoreAsync(request.TargetRoot, e, ct))
                        needed.Add(e);
                    else
                        skipped++;
                }
                if (needed.Count == 0)
                    continue;

                var storage = group.First().Storage!;
                var blobName = storage.Kind == "pack" ? $"packs/{storage.Ref}.7z" : storage.Ref;

                var archive = Path.Combine(work, "arc.7z");
                var extractDir = Path.Combine(work, "x");
                if (File.Exists(archive)) File.Delete(archive);
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);

                await container.GetBlobClient(blobName).DownloadToAsync(archive, ct);
                await compressor.ExtractAsync(archive, extractDir, request.Password, ct);

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
            TryDelete(work);
        }

        return new RestoreResult(version.Version, restored, skipped, index.EmptyDirs.Count);
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
