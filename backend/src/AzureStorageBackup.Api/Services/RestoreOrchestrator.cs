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

    /// <summary>选择性还原（需求 B）：为 null 时还原整版本（现状）；非 null 时只还原恰好这些路径。
    /// 过滤在分组前生效——pack 因此只下载一次、只写选中成员，不会 over-restore 未选成员。</summary>
    public IReadOnlyList<string>? SelectedPaths { get; init; }

    /// <summary>冲突处理模式（决策 3）。默认 OverwriteIfChanged = 现状。</summary>
    public RestoreConflictMode Conflict { get; init; } = RestoreConflictMode.OverwriteIfChanged;

    /// <summary>Archive blob 活化优先级（透传 Azure RehydratePriority）。默认 Standard。</summary>
    public RestoreRehydratePriority RehydratePriority { get; init; } = RestoreRehydratePriority.Standard;

    /// <summary>遇 Archive blob 的活化目标 tier（Archive 无法直接下载，需先活化，异步几小时）。</summary>
    public AccessTier RehydrateTier { get; init; } = AccessTier.Hot;

    /// <summary>活化轮询间隔秒（还原 job 不占锁，可长等）。</summary>
    public int RehydratePollSeconds { get; init; } = 60;

    /// <summary>还原完成后把活化过的 blob 重新归档回 Archive（默认 true，保持备份原 tier、避免长期热存费）。</summary>
    public bool ReArchiveAfterRestore { get; init; } = true;
}

/// <summary>还原结果。SkippedFiles = 本地已是相同内容而跳过（仅当变更时覆盖）。
/// FailedFiles = 未能还原的条目数：所在存储分组下载/解压失败、条目会写到目标根之外（含 symlink 条目），
/// 或条目本身畸形导致写入抛错。RestoredDirs = **实际创建成功**的空目录数（越界/失败的不计）。</summary>
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
        var source = $"restore:{request.Account.Id}/{request.Container}";
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
        var failed = 0;

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

        // 选择性还原（需求 B）：把生效集限制到用户选中的路径。过滤在分组前生效，
        // 于是每个 pack 仍只下载一次，但只写选中成员——未选成员根本不进入 fileEntries，不会 over-restore。
        HashSet<string>? selected = request.SelectedPaths is null
            ? null
            : new HashSet<string>(request.SelectedPaths, StringComparer.Ordinal);
        if (selected is not null)
            foreach (var key in byPath.Keys.Where(k => !selected.Contains(k)).ToList())
                byPath.Remove(key);

        // 不可恢复且未「解析成功」替代 → 跳过（声明了意图但替代不可得的也回落跳过，不报错）。
        // 选择性还原时只计入选中的不可恢复路径。
        var unresolved = index.UnrecoverablePaths
            .Where(p => !resolved.Contains(p) && (selected is null || selected.Contains(p)))
            .ToHashSet(StringComparer.Ordinal);
        skipped += unresolved.Count;

        // 空文件夹（还原需重建）——选择性还原只针对选中文件，不重建整棵空目录树。
        // 同样来自云端索引：目录名含 .. 时会创到目标根之外，越界的目录条目跳过、不创建。
        // 判定作用在**解析后的真实路径**上：CreateDirectory 会跟随路径中间段的软链，
        // 前一次还原（或用户自己）在根内留下的一条指向根外的链接足以让「看起来在根内」
        // 的目录落到根外。
        var restoredDirs = 0;
        if (selected is null)
            foreach (var dir in index.EmptyDirs)
            {
                var dest = Path.Combine(request.TargetRoot, ToLocal(dir));
                if (!WriteStaysInsideRoot(request.TargetRoot, dest))
                {
                    phase?.Report($"Skipped unsafe directory entry (escapes the target root): {dir}");
                    continue;
                }

                // 畸形目录条目（中间段是文件等）只失败它自己，不中断整次还原。
                try
                {
                    Directory.CreateDirectory(dest);
                    restoredDirs++;
                }
                catch (Exception ex)
                {
                    phase?.Report($"Failed to create directory '{dir}': {ex.Message}");
                }
            }

        // symlink 与文件分开处理
        var fileEntries = new List<IndexEntry>();
        foreach (var e in byPath.Values)
        {
            if (unresolved.Contains(e.Path))
                continue;
            if (e.Kind == "symlink")
            {
                // 畸形条目（如 Path 为 "" / "."）会让 CreateSymbolicLink 抛错；
                // 这里逐条兜住，否则一条脏条目会中断整次还原。
                SymlinkOutcome outcome;
                try
                {
                    outcome = RestoreSymlink(request.TargetRoot, e);
                }
                catch (Exception ex)
                {
                    phase?.Report($"Failed to restore symlink '{e.Path}': {ex.Message}");
                    failed++;
                    continue;
                }

                switch (outcome)
                {
                    case SymlinkOutcome.Created:
                        restored++;
                        break;
                    case SymlinkOutcome.Unchanged:
                        skipped++;
                        break;
                    default:
                        // 安全检查触发必须可见：与「未变」同样静默会让用户完全看不到被拦下的条目。
                        phase?.Report(new UnsafeRestorePathException(e.Path).Message);
                        failed++;
                        break;
                }
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

        return new RestoreResult(version.Version, restored, skipped, restoredDirs, failed);
    }

    private async Task<(int Restored, int Skipped, int Failed)> RestoreGroupAsync(
        BlobContainerClient container, RestoreRequest request, string work,
        List<IndexEntry> group, SemaphoreSlim gate, System.Collections.Concurrent.ConcurrentBag<string> rehydrated,
        IProgress<string>? phase, CancellationToken ct)
    {
        var skipped = 0;
        var failedEntries = 0;
        var needed = new List<IndexEntry>();
        foreach (var e in group)
        {
            // 边界判定必须在 NeedsRestoreAsync **之前**：后者会对目标做 File.Exists 与全量
            // hash，越界条目等于让调用方拿一条索引记录去探测目标根之外任意路径的存在性与
            // 内容（结果通过 RestoredFiles/SkippedFiles 计数可见）。更糟的是根外若已有同内容
            // 文件，它会返回 false 而被计成「跳过」，于是根本走不到写入处的检查，
            // 既不计入失败也不上报——一次被拦下的越界变成了完全不可见的无事发生。
            var dest = Path.Combine(request.TargetRoot, ToLocal(e.Path));
            if (!WriteStaysInsideRoot(request.TargetRoot, dest))
            {
                phase?.Report(new UnsafeRestorePathException(e.Path).Message);
                failedEntries++;
                continue;
            }

            if (await NeedsRestoreAsync(dest, e, request.Conflict, ct))
                needed.Add(e);
            else
                skipped++;
        }
        if (needed.Count == 0)
            return (0, skipped, failedEntries);

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
                await EnsureOnlineAsync(container, blobName, request.RehydrateTier, MapPriority(request.RehydratePriority), request.RehydratePollSeconds, phase, ct);
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
                    if (TryWriteRestoredFile(request, e, content, phase))
                        restored++;
                    else
                        failedEntries++;
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
                    if (TryWriteRestoredFile(request, e, source, phase))
                        restored++;
                    else
                        failedEntries++;
                }
            }
        }
        finally
        {
            gate.Release();
            try { Directory.Delete(groupDir, recursive: true); } catch { /* best effort */ }
        }
        return (restored, skipped, failedEntries);
    }

    /// <summary><paramref name="dest"/> 必须是**已通过边界检查**的目标路径（见 RestoreGroupAsync）：
    /// 本方法会对它做 File.Exists 和全量 hash，绝不能作用在目标根之外的路径上。</summary>
    private async Task<bool> NeedsRestoreAsync(string dest, IndexEntry entry, RestoreConflictMode conflict, CancellationToken ct)
    {
        if (!File.Exists(dest))
            return true;

        // Skip：目标存在即跳过（无论内容异同）。
        if (conflict == RestoreConflictMode.Skip)
            return false;

        // OverwriteIfChanged / RenameKeep：本地已是相同内容则跳过；FullHash 缺失无从比较则视为需还原。
        if (entry.FullHash is null)
            return true;
        return await hasher.FullHashAsync(dest, ct) != entry.FullHash;
    }

    /// <summary>
    /// 写一个条目，把失败圈在这一条上：越界、以及畸形条目（如 Path 为 ""/"." 使目标就是一个目录，
    /// File.Copy 会抛 UnauthorizedAccess/IOException）都只让本条目失败并上报，
    /// 绝不冒泡到分组处理器——那会把整组合法条目一起判失败。返回是否写入成功。
    /// </summary>
    private static bool TryWriteRestoredFile(RestoreRequest request, IndexEntry entry, string sourceFile, IProgress<string>? phase)
    {
        try
        {
            WriteRestoredFile(request, entry, sourceFile);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnsafeRestorePathException ex)
        {
            phase?.Report(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            phase?.Report($"Failed to restore '{entry.Path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>把还原内容写到目标路径。RenameKeep 且目标已存在（能进到这一步即内容不同或无法比较）→
    /// 先把现有本地文件改名为 {name}.bak-{ts} 保留旧内容，再写还原内容到原名（旧内容永不丢失）。</summary>
    private static void WriteRestoredFile(RestoreRequest request, IndexEntry entry, string sourceFile)
    {
        var dest = Path.Combine(request.TargetRoot, ToLocal(entry.Path));

        // 索引来自云端（可能是 /import 导入的任意容器）：条目路径含 .. 时会写到目标根之外。
        // 判定作用在**解析后的真实路径**上——纯词法判定挡不住「先建链接再穿过它写」：
        // 索引里一条 symlink 条目（先于文件条目还原）指向根外，之后 <root>/link/x 词法上
        // 完全在根内，File.Copy 却会跟随链接落到根外。
        // 跳过该条目而不是中断整次还原——与既有的逐组容错语义一致。
        if (!WriteStaysInsideRoot(request.TargetRoot, dest))
            throw new UnsafeRestorePathException(entry.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (request.Conflict == RestoreConflictMode.RenameKeep && File.Exists(dest))
            RestoreConflict.RenameExisting(dest, DateTimeOffset.UtcNow);
        File.Copy(sourceFile, dest, overwrite: true);
        ApplyMetadata(dest, entry);
    }

    /// <summary>symlink 条目的还原结果。「未变」与「越界」必须区分开：
    /// 前者是无事发生，后者是安全检查被触发，用户必须看得见。</summary>
    private enum SymlinkOutcome
    {
        Created,
        Unchanged,
        Unsafe,
    }

    private SymlinkOutcome RestoreSymlink(string targetRoot, IndexEntry entry)
    {
        if (entry.Target is null)
            return SymlinkOutcome.Unchanged;

        var dest = Path.Combine(targetRoot, ToLocal(entry.Path));

        // 同 WriteRestoredFile：索引条目路径含 .. 或穿过一条指向根外的链接时，
        // 链接会被建到目标根之外，拦下。
        // 注意这里用的是「只解析父目录」的版本：entry.Target 指向根外是**合法**的
        // （备份如实记录了原本的绝对软链，还原它是对的），被禁止的只是「穿过链接写」。
        if (!LinkStaysInsideRoot(targetRoot, dest))
            return SymlinkOutcome.Unsafe;

        // 用 LinkTarget（底层 lstat）判「未变」，不用 FileInfo.Exists：后者对**指向目录的**
        // 软链恒为 false，于是这类链接永远判不出「未变」，第二次还原必然走到
        // CreateSymbolicLink 并因已存在抛错（改动前这会中止整次还原）。
        // LinkTarget 在「不是链接」和「不存在」时都为 null，正好是需要重建的两种情形。
        var existingLink = new FileInfo(dest).LinkTarget;
        if (existingLink == entry.Target)
            return SymlinkOutcome.Unchanged; // 未变

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        // 既有链接用 File.Delete 直接 unlink（不跟随），既有普通文件同样先删。
        // Path.Exists 跟随链接，悬空链接靠 existingLink 兜住。
        if (existingLink is not null || Path.Exists(dest)) File.Delete(dest);
        File.CreateSymbolicLink(dest, entry.Target);
        return SymlinkOutcome.Created;
    }

    /// <summary>
    /// 写入（文件/目录）前的越界判定，作用在**解析后的真实路径**上。
    /// <para>
    /// 纯词法的 <see cref="PathBoundary.IsWithin"/> 不足以守住这里：还原**先**建 symlink 条目、
    /// **后**写文件条目，于是索引里一条 <c>evil -&gt; /etc/cron.d</c> 加一条 <c>evil/x</c> 就能让
    /// <c>&lt;root&gt;/evil/x</c> 在词法上完全合规地通过检查，而 <c>File.Copy</c> / <c>CreateDirectory</c>
    /// 会跟随该链接把内容落到 <c>/etc/cron.d/x</c>。判定必须和内核一样在软链展开之后结算。
    /// </para>
    /// <para>
    /// 目标根**自身**也解析：还原到一个本身经软链到达的目录（<c>/data -&gt; /mnt/disk1/data</c>）
    /// 必须继续可用，若只解析候选路径就会把每一条合法条目都误判成越界。
    /// </para>
    /// <para>解析失败（成环 / 含 \0 / 空串）一律判越界——失败关闭。</para>
    /// </summary>
    private static bool WriteStaysInsideRoot(string targetRoot, string dest)
    {
        var realRoot = PathBoundary.ResolveReal(targetRoot);
        var realDest = PathBoundary.ResolveReal(dest);
        return realRoot is not null && realDest is not null && PathBoundary.IsWithin(realRoot, realDest);
    }

    /// <summary>
    /// 建 symlink 前的越界判定：解析**父目录**，末段按名字拼接、**不解析**。
    /// <para>
    /// 末段不能解析，因为创建/删除链接本身不跟随末段（<c>symlinkat</c>/<c>unlinkat</c> 语义），
    /// 而且合法备份里那条指向根外的绝对软链在第二次还原时，末段就是它自己——
    /// 解析末段会把「重复还原一条合法链接」误判成越界。父目录仍然必须解析：
    /// 链接建在哪个目录里，取决于路径中间段跟随后的真实位置。
    /// </para>
    /// </summary>
    private static bool LinkStaysInsideRoot(string targetRoot, string dest)
    {
        var parent = Path.GetDirectoryName(dest);
        if (string.IsNullOrEmpty(parent))
            return false;

        var realRoot = PathBoundary.ResolveReal(targetRoot);
        var realParent = PathBoundary.ResolveReal(parent);
        if (realRoot is null || realParent is null)
            return false;

        // 末段可能是 ".."/"."（畸形条目）：交给 IsWithin 的词法规范化收口。
        return PathBoundary.IsWithin(realRoot, Path.Combine(realParent, Path.GetFileName(dest)));
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
    private static RehydratePriority MapPriority(RestoreRehydratePriority p) =>
        p == RestoreRehydratePriority.High ? RehydratePriority.High : RehydratePriority.Standard;

    private static async Task EnsureOnlineAsync(
        BlobContainerClient container, string baseRef, AccessTier tier, RehydratePriority priority, int pollSeconds,
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
                await container.GetBlobClient(name).SetAccessTierAsync(tier, rehydratePriority: priority, cancellationToken: ct);
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

/// <summary>还原条目的目标路径逃出了 TargetRoot（索引被篡改或来自不可信容器）。</summary>
public sealed class UnsafeRestorePathException(string entryPath)
    : Exception($"Restore entry path escapes the target root: {entryPath}");
