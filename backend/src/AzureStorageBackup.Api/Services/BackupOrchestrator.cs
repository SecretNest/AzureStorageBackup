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

        var storageByPath = new Dictionary<string, StorageRef>(StringComparer.Ordinal);

        // 5. Compress + Upload
        var total = plan.Blobs.Count + plan.Packs.Count;
        var uploaded = 0;
        void ReportItem()
        {
            uploaded++;
            progress?.Report(new BackupProgress(
                BackupStage.Uploading, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
        }
        progress?.Report(new BackupProgress(BackupStage.Uploading, diff.ChangedFiles, diff.ChangedBytes, 0, total));

        await UploadBlobsAsync(request, plan, storageByPath, ReportItem, ct);
        await UploadPacksAsync(request, plan, info, storageByPath, ReportItem, ct);

        // 6. 构建新版本第二级索引
        var entries = BuildEntries(diff, storageByPath);
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
        BackupRequest request, BackupPlan plan, Dictionary<string, StorageRef> storageByPath,
        Action onItem, CancellationToken ct)
    {
        foreach (var blob in plan.Blobs)
        {
            storageByPath[blob.Path] = new StorageRef { Kind = "blob", Ref = blob.Ref };

            // 内容寻址去重：已存在则跳过压缩+上传。
            if (!await BlobExistsAsync(request, blob.Ref, ct))
            {
                var storeOnly = request.Options.DontCompress?.IsIgnored(blob.Path) ?? false;

                async Task ProcessAsync(CancellationToken token)
                {
                    var staged = await staging.StageAsync((compressTemp, t) => CompressAsync(
                        request, compressTemp, SafeFileName(blob.FullHash), [blob.Path], storeOnly, t), token);
                    try
                    {
                        await uploader.UploadIfMissingAsync(
                            request.Account, request.Container, blob.Ref, staged.Files[0], request.DataTier, ct: token);
                    }
                    finally
                    {
                        staging.Release(staged);
                    }
                }

                // 处理后重校验（§9）：文件在处理期间反复变化达阈值 → 报 UnrecoverableError，以当前版本保存。
                if (verifier is not null)
                {
                    var localPath = Path.Combine(request.LocalRoot, blob.Path.Replace('/', Path.DirectorySeparatorChar));
                    var vr = await verifier.RunAsync(localPath, blob.FullHash, ProcessAsync, ct: ct);
                    if (vr.Outcome == ProcessingOutcome.Alarmed)
                        await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Container}",
                            $"File kept changing during backup: {blob.Path}",
                            $"Saved with current content after {vr.Attempts} attempts", ct);
                }
                else
                {
                    await ProcessAsync(ct);
                }
            }

            onItem();
        }
    }

    private async Task UploadPacksAsync(
        BackupRequest request, BackupPlan plan, BackupInfoFile info,
        Dictionary<string, StorageRef> storageByPath, Action onItem, CancellationToken ct)
    {
        foreach (var pack in plan.Packs)
        {
            var packBlob = $"packs/{pack.PackId}.7z";
            var entries = pack.Members.Select(m => m.EntryName).ToList();

            var staged = await staging.StageAsync((compressTemp, token) => CompressAsync(
                request, compressTemp, pack.PackId, entries, storeOnly: false, token), ct);
            try
            {
                await uploader.UploadIfMissingAsync(
                    request.Account, request.Container, packBlob, staged.Files[0], request.DataTier, ct: ct);
            }
            finally
            {
                staging.Release(staged);
            }

            onItem();

            foreach (var member in pack.Members)
                storageByPath[member.Path] = new StorageRef { Kind = "pack", Ref = pack.PackId, EntryName = member.EntryName };

            info.Packs[pack.PackId] = new PackInfo
            {
                Blob = packBlob,
                Members = pack.Members.Select(m => m.FullHash).ToList(),
                OriginalBytes = pack.OriginalBytes,
                DeadBytes = 0,
            };
        }
    }

    private async Task<IReadOnlyList<string>> CompressAsync(
        BackupRequest request, string compressTemp, string archiveName,
        IReadOnlyList<string> entries, bool storeOnly, CancellationToken ct)
    {
        var output = Path.Combine(compressTemp, archiveName + ".7z");
        var result = await compressor.CompressAsync(
            new CompressionRequest(request.LocalRoot, entries, output, request.Password, StoreOnly: storeOnly), ct);
        return result.VolumeFiles;
    }

    private static List<IndexEntry> BuildEntries(DiffResult diff, Dictionary<string, StorageRef> storageByPath)
    {
        var entries = new List<IndexEntry>();
        foreach (var c in diff.Changes)
        {
            if (c.Kind == ChangeKind.Deleted || c.Current is null)
                continue;

            entries.Add(new IndexEntry
            {
                Path = c.Path,
                Kind = c.Current.Kind == EntryKind.File ? "file" : "symlink",
                Length = c.Current.Length,
                Mtime = c.Current.ModifiedAt,
                Permissions = c.Current.Permissions,
                HeadHash = c.HeadHash,
                FullHash = c.FullHash,
                Target = c.Current.Target,
                Storage = storageByPath.GetValueOrDefault(c.Path) ?? c.CarriedStorage,
            });
        }
        return entries;
    }

    private async Task<bool> BlobExistsAsync(BackupRequest request, string blobName, CancellationToken ct)
    {
        var blob = factory.CreateServiceClient(request.Account)
            .GetBlobContainerClient(request.Container)
            .GetBlobClient(blobName);
        return (await blob.ExistsAsync(ct)).Value;
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
