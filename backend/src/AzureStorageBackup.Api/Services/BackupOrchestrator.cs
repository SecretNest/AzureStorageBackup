using System.Collections.Concurrent;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>备份引擎的可调选项（忽略/不压缩/不分组规则 + 各阶段选项）。</summary>
public sealed record BackupEngineOptions
{
    public IgnoreRuleSet Ignore { get; init; } = new([]);
    public IgnoreRuleSet? DontCompress { get; init; }
    public IgnoreRuleSet? DontGroup { get; init; }
    public ScanOptions Scan { get; init; } = new();
    public DiffOptions Diff { get; init; } = new();
    public PlanOptions Plan { get; init; } = new();
    public RetentionPolicy Retention { get; init; } = new();

    /// <summary>分卷大小（字节）；null=不分卷（单归档）。大文件/大 pack 拆成多卷 blob（§7）。</summary>
    public long? VolumeBytes { get; init; }

    /// <summary>并发上传上限（PRD 3.4，默认 5）。压缩仍全局串行；仅上传并行。</summary>
    public int UploadConcurrency { get; init; } = 5;

    /// <summary>上传的网络重试退避策略（PRD 4.1）。</summary>
    public RetryOptions Upload { get; init; } = new();
}

/// <summary>一次备份执行请求。</summary>
public sealed record BackupRequest
{
    public required Account Account { get; init; }
    public required string Container { get; init; }
    public required string LocalRoot { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Password { get; init; }
    public AccessTier IndexTier { get; init; } = AccessTier.Hot;
    public AccessTier DataTier { get; init; } = AccessTier.Hot;
    public BackupEngineOptions Options { get; init; } = new();
}

/// <summary>一次备份执行结果。</summary>
public sealed record BackupRunResult(int Version, int ChangedFiles, long ChangedBytes);

/// <summary>备份管线阶段。</summary>
public enum BackupStage
{
    Scanning,
    Diffing,
    Uploading,
    WritingIndex,
    Finalizing,
    CleaningUp,
    Completed,
}

/// <summary>进度快照（PRD 备份设计 §2：百分比 + 变更文件数/尺寸）。前端轮询用。</summary>
public sealed record BackupProgress(
    BackupStage Stage, int ChangedFiles, long ChangedBytes, int UploadedItems, int TotalItems)
{
    public int Percent => TotalItems == 0 ? (Stage == BackupStage.Completed ? 100 : 0)
        : (int)(100L * UploadedItems / TotalItems);
}

/// <summary>
/// 备份编排器（M4 设计 §4）：串 Scan→Diff→Plan→Compress→Upload→WriteIndex→Finalize，产出一个新版本。
/// data blob 与 pack 一律 7z 归档；data blob 按 fullHash 内容寻址去重。
/// 压缩经共享 StagingArea（全局非并发 + 背压）。保留清理与进度上报为后续增量。
/// </summary>
public sealed class BackupOrchestrator(
    LocalFileScanner scanner,
    BackupDiffer differ,
    GroupingPlanner planner,
    IFileCompressor compressor,
    IBlobUploader uploader,
    IBlobClientFactory factory,
    IBackupInfoStore store,
    StagingArea staging,
    RetentionCleaner cleaner,
    IFileHasher hasher,
    INotifier? notifier = null,
    IOperationLog? opLog = null,
    ProcessingVerifier? verifier = null)
{
    public async Task<BackupRunResult> RunAsync(
        BackupRequest request, IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var source = $"backup:{request.Container}";
        await Record(NotificationEvents.BackupStart, source, $"Backup started: {request.Name}", request.Container, ct);
        try
        {
            var result = await RunCoreAsync(request, progress, ct);
            await Record(NotificationEvents.BackupSuccess, source, $"Backup succeeded: {request.Name}",
                $"Version {result.Version}, {result.ChangedFiles} changed file(s)", ct);
            return result;
        }
        catch (Exception ex)
        {
            await Record(NotificationEvents.BackupFailure, source, $"Backup failed: {request.Name}", ex.Message, ct);
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

    private async Task<BackupRunResult> RunCoreAsync(
        BackupRequest request, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var opts = request.Options;
        var password = request.Password;

        // 0. 确保 container 存在（HTTP 触发的备份自足）
        await factory.CreateServiceClient(request.Account)
            .GetBlobContainerClient(request.Container)
            .CreateIfNotExistsAsync(cancellationToken: ct);

        // 1. Scan
        progress?.Report(new BackupProgress(BackupStage.Scanning, 0, 0, 0, 0));
        var scan = await scanner.ScanAsync(request.LocalRoot, opts.Ignore, opts.Scan, ct);

        // 2. 载入上一版本
        var info = await store.ReadInfoAsync(request.Account, request.Container, password, ct)
            ?? NewInfo(request);
        VersionIndex? previous = info.Versions.Count > 0
            ? await store.ReadIndexAsync(request.Account, request.Container, info.Versions[^1].IndexBlob, password, ct)
            : null;

        // 3. Diff
        progress?.Report(new BackupProgress(BackupStage.Diffing, 0, 0, 0, 0));
        var diff = await differ.DiffAsync(request.LocalRoot, scan, previous, opts.Diff, ct);

        // 4. Plan（对 Added/Modified 决定 blob/pack）
        var changed = diff.Changes
            .Where(c => c.Kind is ChangeKind.Added or ChangeKind.Modified && c.Current is not null)
            .Select(c => new PlannedFile(c.Path, c.Current!.Length, c.FullHash!))
            .ToList();
        var plan = planner.Plan(changed, opts.Plan with
        {
            DontGroup = opts.DontGroup,
            FirstPackNumber = NextPackNumber(info.Packs),
        });

        var storageByPath = new ConcurrentDictionary<string, StorageRef>(StringComparer.Ordinal);
        // 处理中内容变化的文件：以稳定后的新 hash/元数据覆盖 diff 时的索引条目（§9、PRD 特别说明 D）。
        var overrides = new ConcurrentDictionary<string, EntryOverride>(StringComparer.Ordinal);

        // 5. Compress + Upload。压缩仍经单例 StagingArea 全局串行；仅上传按 UploadConcurrency 并行（PRD 3.4）。
        var total = plan.Blobs.Count + plan.Packs.Count;
        var uploaded = 0;
        void ReportItem()
        {
            var done = Interlocked.Increment(ref uploaded);
            progress?.Report(new BackupProgress(
                BackupStage.Uploading, diff.ChangedFiles, diff.ChangedBytes, done, total));
        }
        progress?.Report(new BackupProgress(BackupStage.Uploading, diff.ChangedFiles, diff.ChangedBytes, 0, total));

        using var uploadGate = new SemaphoreSlim(
            Math.Max(1, opts.UploadConcurrency), Math.Max(1, opts.UploadConcurrency));

        await UploadBlobsAsync(request, plan, storageByPath, overrides, uploadGate, ReportItem, ct);
        await UploadPacksAsync(request, plan, info, storageByPath, overrides, uploadGate, ReportItem, ct);

        // 6. 构建新版本第二级索引
        var entries = BuildEntries(diff, storageByPath, overrides);
        var version = (info.Versions.LastOrDefault()?.Version ?? 0) + 1;
        var index = new VersionIndex
        {
            Version = version,
            Entries = entries,
            EmptyDirs = scan.EmptyDirs.ToList(),
        };

        // 7. WriteIndex（先上传第二级索引）
        progress?.Report(new BackupProgress(BackupStage.WritingIndex, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
        var indexBlob = await store.WriteIndexAsync(request.Account, request.Container, version, index, password, request.IndexTier, ct);

        // 8/9. Finalize（原子更新信息文件）
        progress?.Report(new BackupProgress(BackupStage.Finalizing, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
        info.Versions.Add(new BackupVersion
        {
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            IndexBlob = indexBlob,
            Stats = new VersionStats(entries.Count, entries.Sum(e => e.Length), diff.ChangedFiles, diff.ChangedBytes),
        });
        await store.WriteInfoAsync(request.Account, request.Container, info, password, request.IndexTier, ct);

        // 10. Cleanup（按保留策略清理超期版本及其独占数据，§10）
        progress?.Report(new BackupProgress(BackupStage.CleaningUp, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
        await cleaner.CleanupAsync(request.Account, request.Container, password, request.Options.Retention, info, ct);

        progress?.Report(new BackupProgress(BackupStage.Completed, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
        return new BackupRunResult(version, diff.ChangedFiles, diff.ChangedBytes);
    }

    private async Task UploadBlobsAsync(
        BackupRequest request, BackupPlan plan, ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, SemaphoreSlim uploadGate,
        Action onItem, CancellationToken ct)
        => await Task.WhenAll(plan.Blobs.Select(blob =>
            HandleBlobAsync(request, new PlannedFile(blob.Path, 0, blob.FullHash),
                storageByPath, overrides, uploadGate, onItem, ct)));

    /// <summary>
    /// 处理单文件内容寻址 blob：压缩+上传 data/{hash}，经重校验循环（§9）。
    /// 内容在处理中变化时，用稳定后的新 hash 决定 blob 名并回写索引覆盖，避免存储名与内容不符（PRD 特别说明 D）。
    /// </summary>
    private async Task HandleBlobAsync(
        BackupRequest request, PlannedFile file, ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, SemaphoreSlim uploadGate,
        Action onItem, CancellationToken ct)
    {
        var localPath = Path.Combine(request.LocalRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
        var storeOnly = request.Options.DontCompress?.IsIgnored(file.Path) ?? false;
        var cc = factory.CreateServiceClient(request.Account).GetBlobContainerClient(request.Container);

        // 用当前 hash 决定 blob 名；内容寻址去重（已存在则跳过）。verifier 会在内容变化时用新 hash 重调。
        async Task ProcessAsync(string hash, CancellationToken token)
        {
            var blobRef = "data/" + hash;
            if (await VolumeBlobIO.ExistsAsync(cc, blobRef, token))
                return;
            var staged = await staging.StageAsync((compressTemp, t) => CompressAsync(
                request, compressTemp, SafeFileName(hash), [file.Path], storeOnly, t), token);
            await uploadGate.WaitAsync(token);
            try
            {
                await VolumeBlobIO.UploadAsync(
                    uploader, request.Account, request.Container, blobRef, staged.Files,
                    request.DataTier, request.Options.Upload, token);
            }
            finally
            {
                uploadGate.Release();
                staging.Release(staged);
            }
        }

        var finalHash = file.FullHash;
        if (verifier is not null)
        {
            var vr = await verifier.RunAsync(localPath, file.FullHash, ProcessAsync, ct: ct);
            finalHash = vr.FullHash;
            if (vr.Outcome == ProcessingOutcome.Alarmed)
                await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Container}",
                    $"File kept changing during backup: {file.Path}",
                    $"Saved with current content after {vr.Attempts} attempts", ct);
        }
        else
        {
            await ProcessAsync(file.FullHash, ct);
        }

        storageByPath[file.Path] = new StorageRef { Kind = "blob", Ref = "data/" + finalHash };
        // 内容在处理中变化：以稳定后的新 hash/元数据覆盖索引条目，保证 data/{hash} 内容与名一致。
        if (finalHash != file.FullHash)
            overrides[file.Path] = await BuildOverrideAsync(localPath, finalHash, request.Options.Diff.HeadHashBytes, ct);

        onItem();
    }

    private async Task<EntryOverride> BuildOverrideAsync(
        string localPath, string fullHash, int headBytes, CancellationToken ct)
    {
        var info = new FileInfo(localPath);
        var head = await hasher.HeadHashAsync(localPath, headBytes, ct);
        return new EntryOverride(fullHash, head, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
    }

    private async Task UploadPacksAsync(
        BackupRequest request, BackupPlan plan, BackupInfoFile info,
        ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, SemaphoreSlim uploadGate,
        Action onItem, CancellationToken ct)
        => await Task.WhenAll(plan.Packs.Select(pack =>
            HandlePackAsync(request, pack, info, storageByPath, overrides, uploadGate, onItem, ct)));

    /// <summary>
    /// 压缩+上传一个 pack；启用重校验时（§9）对组内成员在压缩后重校验：内容变化的成员移出分组，
    /// 改走单文件内容寻址 blob（在那里做重命名/重校验），其余成员重新压缩。反复变化达阈值即报警、以当前归档保存。
    /// </summary>
    private async Task HandlePackAsync(
        BackupRequest request, PlannedPack pack, BackupInfoFile info,
        ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, SemaphoreSlim uploadGate,
        Action onItem, CancellationToken ct)
    {
        var members = pack.Members.ToList();

        if (verifier is null)
        {
            var staged = await CompressPackAsync(request, pack.PackId, members, ct);
            await UploadStagedPackAsync(request, pack.PackId, staged, uploadGate, ct);
            RecordPack(request, pack.PackId, members, info, storageByPath);
            onItem();
            return;
        }

        const int maxAttempts = 5; // 与 VerificationOptions 默认一致
        for (var attempt = 1; ; attempt++)
        {
            if (members.Count == 0)
                break; // 全部成员移出分组，无 pack 可传

            var before = members.ToDictionary(m => m.Path, m => Stat(Local(request, m.Path)));
            var staged = await CompressPackAsync(request, pack.PackId, members, ct);

            // 压缩后重校验组内成员：元数据变且内容 hash 变 → 该成员在压缩期间变化。
            var changed = new List<PackEntry>();
            foreach (var m in members)
            {
                var local = Local(request, m.Path);
                if (Stat(local) != before[m.Path] && await hasher.FullHashAsync(local, ct) != m.FullHash)
                    changed.Add(m);
            }

            if (changed.Count == 0)
            {
                await UploadStagedPackAsync(request, pack.PackId, staged, uploadGate, ct);
                RecordPack(request, pack.PackId, members, info, storageByPath);
                break;
            }

            // 有成员在压缩期间变化 → 丢弃本次归档，移出这些成员改走单文件（用稳定后的新 hash），其余重压。
            staging.Release(staged);
            members = members.Where(m => !changed.Contains(m)).ToList();
            await Task.WhenAll(changed.Select(async m =>
            {
                var local = Local(request, m.Path);
                var newHash = await hasher.FullHashAsync(local, ct);
                // 该成员内容已变（≠ diff 时的 fullHash）：写索引覆盖，使 fullHash/名字/元数据与新内容一致。
                overrides[m.Path] = await BuildOverrideAsync(local, newHash, request.Options.Diff.HeadHashBytes, ct);
                await HandleBlobAsync(request, new PlannedFile(m.Path, new FileInfo(local).Length, newHash),
                    storageByPath, overrides, uploadGate, static () => { }, ct);
            }));

            if (attempt >= maxAttempts)
            {
                // 反复变化达阈值：把剩余成员做最后一次压缩上传并报警（不再重校验）。
                if (members.Count > 0)
                {
                    var staged2 = await CompressPackAsync(request, pack.PackId, members, ct);
                    await UploadStagedPackAsync(request, pack.PackId, staged2, uploadGate, ct);
                    RecordPack(request, pack.PackId, members, info, storageByPath);
                }
                await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Container}",
                    $"Pack members kept changing during backup: {pack.PackId}",
                    $"Stabilized after moving changing members out over {attempt} attempts", ct);
                break;
            }
        }

        onItem();
    }

    private Task<StagedItem> CompressPackAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, CancellationToken ct)
    {
        var entries = members.Select(m => m.EntryName).ToList();
        return staging.StageAsync((compressTemp, token) => CompressAsync(
            request, compressTemp, packId, entries, storeOnly: false, token), ct);
    }

    private async Task UploadStagedPackAsync(
        BackupRequest request, string packId, StagedItem staged, SemaphoreSlim uploadGate, CancellationToken ct)
    {
        await uploadGate.WaitAsync(ct);
        try
        {
            await VolumeBlobIO.UploadAsync(
                uploader, request.Account, request.Container, $"packs/{packId}.7z", staged.Files,
                request.DataTier, request.Options.Upload, ct);
        }
        finally
        {
            uploadGate.Release();
            staging.Release(staged);
        }
    }

    private static void RecordPack(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members,
        BackupInfoFile info, ConcurrentDictionary<string, StorageRef> storageByPath)
    {
        foreach (var m in members)
            storageByPath[m.Path] = new StorageRef { Kind = "pack", Ref = packId, EntryName = m.EntryName };

        var packInfo = new PackInfo
        {
            Blob = $"packs/{packId}.7z",
            Members = members.Select(m => m.FullHash).ToList(),
            OriginalBytes = members.Sum(m => m.Length),
            DeadBytes = 0,
        };
        lock (info.Packs)
            info.Packs[packId] = packInfo;
    }

    private static string Local(BackupRequest request, string relPath) =>
        Path.Combine(request.LocalRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

    private static (long Mtime, long Length, int Mode) Stat(string path)
    {
        var info = new FileInfo(path);
        var mode = OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(path);
        return (info.LastWriteTimeUtc.Ticks, info.Length, mode);
    }

    private async Task<IReadOnlyList<string>> CompressAsync(
        BackupRequest request, string compressTemp, string archiveName,
        IReadOnlyList<string> entries, bool storeOnly, CancellationToken ct)
    {
        var output = Path.Combine(compressTemp, archiveName + ".7z");
        var result = await compressor.CompressAsync(
            new CompressionRequest(request.LocalRoot, entries, output, request.Password,
                VolumeBytes: request.Options.VolumeBytes, StoreOnly: storeOnly), ct);
        return result.VolumeFiles;
    }

    /// <summary>处理中内容变化的文件：稳定后的 hash/元数据覆盖 diff 时的索引条目（§9）。</summary>
    private sealed record EntryOverride(string FullHash, string? HeadHash, long Length, DateTimeOffset Mtime);

    private static List<IndexEntry> BuildEntries(
        DiffResult diff, IReadOnlyDictionary<string, StorageRef> storageByPath,
        IReadOnlyDictionary<string, EntryOverride> overrides)
    {
        var entries = new List<IndexEntry>();
        foreach (var c in diff.Changes)
        {
            if (c.Kind == ChangeKind.Deleted || c.Current is null)
                continue;

            var ov = overrides.GetValueOrDefault(c.Path);
            entries.Add(new IndexEntry
            {
                Path = c.Path,
                Kind = c.Current.Kind == EntryKind.File ? "file" : "symlink",
                Length = ov?.Length ?? c.Current.Length,
                Mtime = ov?.Mtime ?? c.Current.ModifiedAt,
                Permissions = c.Current.Permissions,
                HeadHash = ov?.HeadHash ?? c.HeadHash,
                FullHash = ov?.FullHash ?? c.FullHash,
                Target = c.Current.Target,
                Storage = storageByPath.GetValueOrDefault(c.Path) ?? c.CarriedStorage,
            });
        }
        return entries;
    }


    private static BackupInfoFile NewInfo(BackupRequest request) => new()
    {
        Backup = new BackupMeta
        {
            Name = request.Name,
            Description = request.Description,
            SourceRootHint = request.LocalRoot,
            Encrypted = !string.IsNullOrEmpty(request.Password),
            CreatedAt = DateTimeOffset.UtcNow,
        },
    };

    private static int NextPackNumber(IReadOnlyDictionary<string, PackInfo> packs)
    {
        var max = 0;
        foreach (var id in packs.Keys)
        {
            if (id.StartsWith('p') && int.TryParse(id.AsSpan(1), out var n) && n > max)
                max = n;
        }
        return max + 1;
    }

    private static string SafeFileName(string fullHash) => fullHash.Replace(':', '_');
}
