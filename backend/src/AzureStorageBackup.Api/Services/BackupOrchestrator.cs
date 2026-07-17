using System.Collections.Concurrent;
using Azure.Storage.Blobs;
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

    /// <summary>死重压实阈值（默认 30%，M4 §6）。</summary>
    public double DeadWeightThreshold { get; init; } = 0.30;

    /// <summary>死重重 pack 时本地缺失成员是否允许下载云端 pack 补齐（按数据 tier 的开关，Archive 默认 false）。</summary>
    public bool AllowRepackDownload { get; init; } = true;
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
        : (int)Math.Min(100L, 100L * UploadedItems / TotalItems);
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
    ProcessingVerifier? verifier = null,
    ILocalIndexCache? indexCache = null)
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

        // 2. 载入上一版本。信息文件从云端读（权威）；大的版本索引优先走本地缓存（§3.3），避免每次下载解压。
        var info = await store.ReadInfoAsync(request.Account, request.Container, password, ct)
            ?? NewInfo(request);
        var identity = info.Backup.CreatedAt.UtcTicks;
        VersionIndex? previous = null;
        if (info.Versions.Count > 0)
        {
            var last = info.Versions[^1];
            previous = indexCache is not null
                ? await indexCache.ReadAsync(request.Account, request.Container, last.Version, identity, last.IndexBlob, password, ct)
                : await store.ReadIndexAsync(request.Account, request.Container, last.IndexBlob, password, ct);
        }

        // data blob 寻址方案：加密备份用密钥化地址防指纹识别（密钥从密码 + 信息文件里的盐派生）。
        var addressing = new BlobAddressScheme(password, info.Backup.KdfSalt);

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

        await UploadBlobsAsync(request, plan, addressing, storageByPath, overrides, uploadGate, ReportItem, ct);
        await UploadGroupablesAsync(request, plan, addressing, info, storageByPath, overrides, uploadGate, ReportItem, ct);

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
        if (indexCache is not null)
            await indexCache.PutAsync(request.Account.Id, request.Container, version, identity, index, ct);

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
        await cleaner.CleanupAsync(request.Account, request.Container, password, new CleanupOptions
        {
            Retention = request.Options.Retention,
            DataTier = request.DataTier,
            VolumeBytes = request.Options.VolumeBytes,
            DeadWeightThreshold = request.Options.DeadWeightThreshold,
            LocalRoot = request.LocalRoot,
            AllowRepackDownload = request.Options.AllowRepackDownload,
        }, info, ct);

        progress?.Report(new BackupProgress(BackupStage.Completed, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
        return new BackupRunResult(version, diff.ChangedFiles, diff.ChangedBytes);
    }

    private async Task UploadBlobsAsync(
        BackupRequest request, BackupPlan plan, BlobAddressScheme addressing,
        ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, SemaphoreSlim uploadGate,
        Action onItem, CancellationToken ct)
        => await Task.WhenAll(plan.Blobs.Select(blob =>
            HandleBlobAsync(request, new PlannedFile(blob.Path, 0, blob.FullHash), addressing,
                storageByPath, overrides, uploadGate, onItem, ct)));

    /// <summary>
    /// 处理单文件内容寻址 blob：压缩+上传 data/{hash}，经重校验循环（§9）。
    /// 内容在处理中变化时，用稳定后的新 hash 决定 blob 名并回写索引覆盖，避免存储名与内容不符（PRD 特别说明 D）。
    /// </summary>
    private async Task HandleBlobAsync(
        BackupRequest request, PlannedFile file, BlobAddressScheme addressing,
        ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, SemaphoreSlim uploadGate,
        Action onItem, CancellationToken ct)
    {
        var localPath = Path.Combine(request.LocalRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
        var storeOnly = request.Options.DontCompress?.IsIgnored(file.Path) ?? false;
        var headBytes = request.Options.Diff.HeadHashBytes;
        var cc = factory.CreateServiceClient(request.Account).GetBlobContainerClient(request.Container);

        // 内容寻址去重 + hash 碰撞避让：按寻址方案定位 blob（加密备份为密钥化地址），用元数据确认确实同内容才去重；
        // 不同内容碰撞到同 hash 时改用备用名 …~N 并报警，避免覆盖/丢数据。verifier 内容变化时用新 hash 重调。
        var uploadedVolumes = 0;
        var chosenRef = addressing.DataAddress(file.FullHash);
        var collided = false;
        async Task ProcessAsync(string hash, CancellationToken token)
        {
            var length = new FileInfo(localPath).Length;
            var head = await hasher.HeadHashAsync(localPath, headBytes, token);
            var (blobRef, exists, collision) = await ResolveDataRefAsync(cc, addressing, hash, length, head, token);
            chosenRef = blobRef;
            collided = collision;
            if (exists)
                return; // 确认同内容 → 去重跳过
            var staged = await staging.StageAsync((compressTemp, t) => CompressAsync(
                request, compressTemp, SafeFileName(hash), [file.Path], storeOnly, t), token);
            await uploadGate.WaitAsync(token);
            try
            {
                await VolumeBlobIO.UploadAsync(
                    uploader, request.Account, request.Container, blobRef, staged.Files,
                    request.DataTier, request.Options.Upload, token, addressing.Metadata(hash, length, head));
                uploadedVolumes = staged.Files.Count;
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

        if (collided)
            await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Container}",
                $"Hash collision avoided: {file.Path}",
                $"Different content shares hash {finalHash}; stored at {chosenRef}", ct);

        // 记录分卷数（供检查核验全部分卷）：本次上传的卷数；若去重跳过则统计云端现存卷数。
        var volumes = uploadedVolumes > 0
            ? uploadedVolumes
            : await VolumeBlobIO.CountVolumesAsync(cc, chosenRef, ct);
        storageByPath[file.Path] = new StorageRef
        {
            Kind = "blob", Ref = chosenRef, Volumes = Math.Max(1, volumes),
        };
        // 内容在处理中变化：以稳定后的新 hash/元数据覆盖索引条目，保证 data/{hash} 内容与名一致。
        if (finalHash != file.FullHash)
            overrides[file.Path] = await BuildOverrideAsync(localPath, finalHash, request.Options.Diff.HeadHashBytes, ct);

        onItem();
    }

    /// <summary>
    /// 定位 data blob 的实际存储名并判断是否可去重。基名由寻址方案给出（加密备份为密钥化地址）；
    /// 元数据确认同内容才去重，内容不同却 hash 相同（碰撞）时顺延到备用名 …~1、~2…。
    /// </summary>
    private static async Task<(string Ref, bool Exists, bool Collision)> ResolveDataRefAsync(
        BlobContainerClient cc, BlobAddressScheme addressing, string hash, long length, string headHash, CancellationToken ct)
    {
        var baseAddr = addressing.DataAddress(hash);
        for (var n = 0; ; n++)
        {
            var refName = n == 0 ? baseAddr : $"{baseAddr}~{n}";
            var meta = await ReadBlobMetaAsync(cc, refName, ct);
            if (meta is null)
                return (refName, false, n > 0);                                   // 空位 → 在此上传（n>0=已避让碰撞）
            if (addressing.MetadataMatches(meta, hash, length, headHash))
                return (refName, true, n > 0);                                    // 确认同内容 → 去重
            // 元数据不符 → 碰撞，试下一个备用名。
        }
    }

    private static async Task<IDictionary<string, string>?> ReadBlobMetaAsync(
        BlobContainerClient cc, string baseRef, CancellationToken ct)
    {
        var blob = cc.GetBlobClient(baseRef);
        if (!(await blob.ExistsAsync(ct)).Value)
        {
            blob = cc.GetBlobClient(baseRef + ".001"); // 多卷首卷
            if (!(await blob.ExistsAsync(ct)).Value)
                return null;
        }
        return (await blob.GetPropertiesAsync(cancellationToken: ct)).Value.Metadata;
    }

    private async Task<EntryOverride> BuildOverrideAsync(
        string localPath, string fullHash, int headBytes, CancellationToken ct)
    {
        var info = new FileInfo(localPath);
        var head = await hasher.HeadHashAsync(localPath, headBytes, ct);
        return new EntryOverride(fullHash, head, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
    }

    /// <summary>
    /// 处理可分组小文件（§6/§9）：按目录**增量成组**——每次从目录池取总长≤上限的一组压缩+校验，
    /// 压缩中变化的成员以稳定后的新 hash **重新入队**（自然进入下一组），而非移出为单文件；
    /// 仅当变大到超阈值、或反复变化达阈值时才降级为单文件（后者报警）。各目录并发，目录内顺序。
    /// </summary>
    private async Task UploadGroupablesAsync(
        BackupRequest request, BackupPlan plan, BlobAddressScheme addressing, BackupInfoFile info,
        ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, SemaphoreSlim uploadGate,
        Action onItem, CancellationToken ct)
    {
        // 从计划重建每个目录的可分组文件池（顺序不变）；成组/压缩/校验在处理时增量进行。
        var poolByDir = plan.Packs
            .GroupBy(p => DirectoryOf(p.Members[0].Path), StringComparer.Ordinal)
            .Select(g => g.SelectMany(p => p.Members)
                .Select(m => new PlannedFile(m.Path, m.Length, m.FullHash)).ToList())
            .ToList();

        // 跨目录并发共享的 pack 号（内容寻址 data blob 不受影响；pack 号只需唯一）。
        var packCounter = new[] { NextPackNumber(info.Packs) - 1 };
        await Task.WhenAll(poolByDir.Select(pool =>
            ProcessDirectoryAsync(request, pool, addressing, info, storageByPath, overrides, uploadGate, packCounter, onItem, ct)));
    }

    private async Task ProcessDirectoryAsync(
        BackupRequest request, List<PlannedFile> pool, BlobAddressScheme addressing, BackupInfoFile info,
        ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, SemaphoreSlim uploadGate,
        int[] packCounter, Action onItem, CancellationToken ct)
    {
        var cap = request.Options.Plan.GroupCapBytes;
        var threshold = request.Options.Plan.SingleFileThresholdBytes;
        var headBytes = request.Options.Diff.HeadHashBytes;
        const int maxAttempts = 5;
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new List<PlannedFile>(pool);

        while (queue.Count > 0)
        {
            // 取出目录中未处理、总长≤上限的一组（至少一个）。
            var group = new List<PlannedFile>();
            long bytes = 0;
            var take = 0;
            while (take < queue.Count)
            {
                var f = queue[take];
                if (group.Count > 0 && bytes + f.Length > cap) break;
                group.Add(f); bytes += f.Length; take++;
            }
            queue.RemoveRange(0, group.Count);

            var packId = "p" + Interlocked.Increment(ref packCounter[0]).ToString("D4");
            var members = group.Select(f => new PackEntry(f.Path, f.Path, f.FullHash, f.Length)).ToList();

            // 无 verifier：直接压缩上传，不做成员重校验。
            if (verifier is null)
            {
                var staged0 = await CompressPackAsync(request, packId, members, ct);
                var vols0 = await UploadStagedPackAsync(request, packId, staged0, uploadGate, ct);
                RecordPack(request, packId, members, vols0, info, storageByPath);
                onItem();
                continue;
            }

            var before = members.ToDictionary(m => m.Path, m => Stat(Local(request, m.Path)));
            var staged = await CompressPackAsync(request, packId, members, ct);

            // 压缩后重校验：元数据变且内容 hash 变 → 该成员在压缩期间变化。
            var changed = new List<PackEntry>();
            foreach (var m in members)
            {
                var local = Local(request, m.Path);
                if (Stat(local) != before[m.Path] && await hasher.FullHashAsync(local, ct) != m.FullHash)
                    changed.Add(m);
            }

            if (changed.Count == 0)
            {
                var vols = await UploadStagedPackAsync(request, packId, staged, uploadGate, ct);
                RecordPack(request, packId, members, vols, info, storageByPath);
                onItem();
                continue;
            }

            // 丢弃本次归档；稳定成员照常成 pack；变化成员以新 hash 处理。
            staging.Release(staged);
            var stable = members.Where(m => !changed.Contains(m)).ToList();
            if (stable.Count > 0)
            {
                var staged2 = await CompressPackAsync(request, packId, stable, ct);
                var vols2 = await UploadStagedPackAsync(request, packId, staged2, uploadGate, ct);
                RecordPack(request, packId, stable, vols2, info, storageByPath);
                onItem();
            }

            foreach (var m in changed)
            {
                var local = Local(request, m.Path);
                var newHash = await hasher.FullHashAsync(local, ct);
                var newLen = new FileInfo(local).Length;
                // 内容已变（≠ diff 时 fullHash）：写索引覆盖，使 fullHash/名字/元数据与新内容一致。
                overrides[m.Path] = await BuildOverrideAsync(local, newHash, headBytes, ct);

                var n = attempts[m.Path] = attempts.GetValueOrDefault(m.Path) + 1;
                if (newLen >= threshold || n >= maxAttempts)
                {
                    // 变大到超阈值、或反复变化达阈值 → 单文件（后者报警）。
                    if (n >= maxAttempts)
                        await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Container}",
                            $"File kept changing during grouping: {m.Path}",
                            $"Stored as single file after {n} attempts", ct);
                    await HandleBlobAsync(request, new PlannedFile(m.Path, newLen, newHash), addressing,
                        storageByPath, overrides, uploadGate, static () => { }, ct);
                }
                else
                {
                    queue.Add(new PlannedFile(m.Path, newLen, newHash)); // 自然进入下一组
                }
            }
        }
    }

    private Task<StagedItem> CompressPackAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, CancellationToken ct)
    {
        var entries = members.Select(m => m.EntryName).ToList();
        return staging.StageAsync((compressTemp, token) => CompressAsync(
            request, compressTemp, packId, entries, storeOnly: false, token), ct);
    }

    /// <returns>该 pack 归档的分卷数（供记录，核验分卷完整性用）。</returns>
    private async Task<int> UploadStagedPackAsync(
        BackupRequest request, string packId, StagedItem staged, SemaphoreSlim uploadGate, CancellationToken ct)
    {
        var volumes = staged.Files.Count;
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
        return volumes;
    }

    private static void RecordPack(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, int volumes,
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
            Volumes = Math.Max(1, volumes),
        };
        lock (info.Packs)
            info.Packs[packId] = packInfo;
    }

    private static string Local(BackupRequest request, string relPath) =>
        Path.Combine(request.LocalRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>直接父目录（不含文件名）；根目录为空串。用于按目录分组。</summary>
    private static string DirectoryOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

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


    private static BackupInfoFile NewInfo(BackupRequest request)
    {
        var encrypted = !string.IsNullOrEmpty(request.Password);
        return new BackupInfoFile
        {
            Backup = new BackupMeta
            {
                Name = request.Name,
                Description = request.Description,
                SourceRootHint = request.LocalRoot,
                Encrypted = encrypted,
                CreatedAt = DateTimeOffset.UtcNow,
                // 加密备份：随机盐用于 data blob 密钥化寻址（防指纹识别）。
                KdfSalt = encrypted ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(16) : null,
            },
        };
    }

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
