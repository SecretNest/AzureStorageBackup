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

    /// <summary>是否写 debug 级日志（含操作文件名，短存）。</summary>
    public bool VerboseLogging { get; init; }

    /// <summary>死重重 pack 时本地缺失成员是否允许下载云端 pack 补齐（按数据 tier 的开关，Archive 默认 false）。</summary>
    public bool AllowRepackDownload { get; init; } = true;

    /// <summary>处理后重校验（<see cref="ProcessingVerifier"/>）反复重处理上限（PRD §5.1，默认 5）。</summary>
    public int ProcessingMaxAttempts { get; init; } = 5;
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
public sealed record BackupRunResult(int Version, int ChangedFiles, long ChangedBytes, int UnreadableFiles);

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

    /// <summary>当前阶段在做什么（正在处理哪个文件、已处理多少、多快）。
    /// 上传之外的阶段此前完全没有进度可言——扫描和 diff 各自只在**进入**时上报一次，
    /// 而首次备份的 diff 要把每个文件完整读一遍算 hash，可以跑几小时。</summary>
    public StageProgress? Detail { get; init; }
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
    ILocalIndexCache? indexCache = null,
    TrackedInfoStore? trackedInfo = null,
    VerboseFileLog? verboseLog = null)
{
    public async Task<BackupRunResult> RunAsync(
        BackupRequest request, IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        var source = $"backup:{request.Account.Id}/{request.Container}";
        await Record(NotificationEvents.BackupStart, source, $"Backup started: {request.Name}", request.Container, ct);
        try
        {
            var result = await RunCoreAsync(request, progress, ct);
            // 有文件被跳过时必须写进成功通知的摘要里：每个读不开的文件都单独推过一条告警，但那些
            // 告警可能淹没在别的消息里，而"备份成功"这一条是操作员一定会看的。只字不提跳过，
            // 等于让一次"成功"的备份掩盖掉本轮根本没存下来的文件。为零时不提，避免噪音。
            var summary = $"Version {result.Version}, {result.ChangedFiles} changed file(s)"
                + (result.UnreadableFiles > 0 ? $", {result.UnreadableFiles} unreadable file(s) skipped" : "");
            await Record(NotificationEvents.BackupSuccess, source, $"Backup succeeded: {request.Name}", summary, ct);
            return result;
        }
        catch (Exception ex)
        {
            await Record(NotificationEvents.BackupFailure, source, $"Backup failed: {request.Name}", ex.Message, ct);
            throw;
        }
    }

    // 日志/通知经 scoped 服务共享同一 EF DbContext（非线程安全）。碰撞/告警上报会在并发上传任务里发生，
    // 故串行化整个上报，避免并发访问 DbContext 击穿备份。
    private readonly SemaphoreSlim _recordGate = new(1, 1);

    private async Task Record(NotificationEvents evt, string source, string title, string body, CancellationToken ct)
    {
        await _recordGate.WaitAsync(ct);
        try
        {
            if (opLog is not null)
                await opLog.AppendAsync(EventLog.LevelOf(evt), source, $"{title} — {body}", ct, durable: true);
            if (notifier is not null)
                await notifier.NotifyAsync(evt, title, body, ct);
        }
        finally
        {
            _recordGate.Release();
        }
    }

    // verbose 时按文件写 debug 日志（含文件名）：落到**按备份+按日期的文本文件**（VerboseFileLog），
    // 而非 SQLite——避免每文件一次 DB 写成为超大备份的瓶颈，并把高频诊断与可查询审计日志分开。
    private async Task LogFileAsync(BackupRequest request, string path, CancellationToken ct)
    {
        if (!request.Options.VerboseLogging || verboseLog is null)
            return;
        await verboseLog.AppendAsync(request.Container, $"Backed up {path}", ct);
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
        var scanTracker = new StageTracker("Scanning", total: 0, d =>   // 扫完才知道总数，故 total=0
            progress?.Report(new BackupProgress(BackupStage.Scanning, 0, 0, 0, 0) { Detail = d }));
        var scan = await scanner.ScanAsync(request.LocalRoot, opts.Ignore, opts.Scan, ct, scanTracker);
        scanTracker.Complete();

        // 2. 载入上一版本。信息文件优先走本地权威副本（§3.3，避免读云端 Cold 信息文件）；大的版本索引优先走本地缓存。
        var info = (trackedInfo is not null
            ? await trackedInfo.LoadAsync(request.Account, request.Container, password, ct)
            : await store.ReadInfoAsync(request.Account, request.Container, password, ct))
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

        // 纯本地去重解析器（自建备份）：从本地缓存的保留版本索引建「内容身份→既有 blob」映射，
        // 备份时用它判断去重/碰撞/分卷/raw，**不发任何云端 HEAD**。仅当本地权威（有本地状态，或全新无版本）时启用；
        // 导入未同步的备份回退到云端存在性检查（见 ResolveDataRefAsync）。
        LocalDedupResolver? localResolver = null;
        if (indexCache is not null && trackedInfo is not null
            && (info.Versions.Count == 0 || await trackedInfo.HasLocalAsync(request.Account, request.Container, ct)))
        {
            var lastVer = info.Versions.LastOrDefault()?.Version;
            var indexes = new List<VersionIndex>(info.Versions.Count);
            foreach (var v in info.Versions)
                indexes.Add(previous is not null && v.Version == lastVer
                    ? previous
                    : await indexCache.ReadAsync(request.Account, request.Container, v.Version, identity, v.IndexBlob, password, ct));
            localResolver = LocalDedupResolver.Build(addressing, indexes);
        }

        // 3. Diff —— 首次备份时这一步最久（每个文件都要完整读一遍算 hash），
        // 也正是此前用户盯着一个不动的 0% 完全不知道在干什么的那个阶段。
        progress?.Report(new BackupProgress(BackupStage.Diffing, 0, 0, 0, 0));
        var diffTracker = new StageTracker("Diffing", scan.Entries.Count, d =>
            progress?.Report(new BackupProgress(BackupStage.Diffing, 0, 0, 0, 0) { Detail = d }));
        var diff = await differ.DiffAsync(request.LocalRoot, scan, previous, opts.Diff, ct, diffTracker);
        diffTracker.Complete();

        // 读不开的文件既不算变更也不算删除，索引阶段会静默沿用旧条目——但操作员必须被告知。
        // 放在 Plan 之前、diff 之后：这条路径每一轮都会执行，不依赖"是否有变更文件"这个后续分支，
        // 否则一次全程读不开的备份会一条告警都不产生（信号最弱的最坏情形）。
        await RecordUnreadableWarningsAsync(request, scan, diff, ct);

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
        var tailByPath = new ConcurrentDictionary<string, string>(StringComparer.Ordinal); // 单文件 blob 的尾部 hash → 索引条目
        // 处理中内容变化的文件：以稳定后的新 hash/元数据覆盖 diff 时的索引条目（§9、PRD 特别说明 D）。
        var overrides = new ConcurrentDictionary<string, EntryOverride>(StringComparer.Ordinal);
        // diff 之后才读不开（压缩/上传阶段重新打开源文件时撞上）：与 diff 时就读不开的文件同等对待——
        // 不产生 blob、索引沿用旧条目、计入 UnreadableFiles，绝不能让整轮备份因此崩溃（M4 设计 §3 遗漏点）。
        var postDiffUnreadable = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

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

        await UploadBlobsAsync(request, plan, addressing, localResolver, storageByPath, tailByPath, overrides, postDiffUnreadable, uploadGate, ReportItem, ct);
        await UploadGroupablesAsync(request, plan, addressing, localResolver, info, storageByPath, tailByPath, overrides, postDiffUnreadable, uploadGate, ReportItem, ct);

        // 6. 构建新版本第二级索引
        var entries = BuildEntries(diff, storageByPath, tailByPath, overrides, postDiffUnreadable);
        var version = (info.Versions.LastOrDefault()?.Version ?? 0) + 1;
        var index = new VersionIndex
        {
            Version = version,
            Entries = entries,
            EmptyDirs = CarryEmptyDirs(scan, previous),
        };

        // 7. WriteIndex（先上传第二级索引）
        progress?.Report(new BackupProgress(BackupStage.WritingIndex, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
        var indexBlob = await store.WriteIndexAsync(request.Account, request.Container, version, index, password, request.IndexTier, ct);
        // 本地索引缓存 Put 推迟到信息文件提交成功后（见下），避免信息文件写冲突时留下未提交版本的幽灵缓存。

        // 8/9. Finalize（原子更新信息文件）
        progress?.Report(new BackupProgress(BackupStage.Finalizing, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
        info.Versions.Add(new BackupVersion
        {
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            IndexBlob = indexBlob,
            Stats = new VersionStats(entries.Count, entries.Sum(e => e.Length), diff.ChangedFiles, diff.ChangedBytes),
        });
        if (trackedInfo is not null)
            await trackedInfo.WriteAsync(request.Account, request.Container, info, password, request.IndexTier, ct);
        else
            await store.WriteInfoAsync(request.Account, request.Container, info, password, request.IndexTier, ct);

        // 信息文件已提交 → 现在把版本索引写入本地缓存（冲突已在上一步抛出，不会到这里）。
        if (indexCache is not null)
            await indexCache.PutAsync(request.Account.Id, request.Container, version, identity, index, ct);

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
        return new BackupRunResult(version, diff.ChangedFiles, diff.ChangedBytes,
            diff.Changes.Count(c => c.Kind == ChangeKind.Unreadable) + postDiffUnreadable.Count);
    }

    /// <summary>每个读不开的文件各走一次 Record：源头一致沿用备份来源，消息保留系统给出的原因原文
    /// （被占用/权限不足/设备读错误需要不同处理，压成一句「无法读取」等于让操作员无从下手）。
    /// 复用 UnrecoverableError 事件——与"处理中反复变化"共用同一条推送通道（唯一 push 通道是通知 webhook，
    /// 操作日志是 pull-only，单用户无人值守场景下不推送等于没人知道），落地日志级别随之变为 Error（决策：可接受）。
    /// 同一文件连续多轮仍读不开时，每轮都要再报一次——静默会让操作员误以为问题自己好了（决策 8）。
    /// <para>
    /// 读不开的**目录**只推一条汇总：其下每个条目都推一条的话，一个五千文件的目录就是五千条 webhook，
    /// 既是通知风暴，也会让备份卡在推送上（每条都要过 _recordGate 并等一次 HTTP）。
    /// 操作员需要知道的是"这个目录整个读不到，影响了 N 个文件"，而不是五千条一模一样的原因。
    /// </para></summary>
    private async Task RecordUnreadableWarningsAsync(
        BackupRequest request, ScanResult scan, DiffResult diff, CancellationToken ct)
    {
        var source = $"backup:{request.Account.Id}/{request.Container}";
        var unreadableDirs = scan.Unreadable.Where(u => u.IsDirectory).ToList();

        foreach (var dir in unreadableDirs)
        {
            var affected = diff.Changes.Count(c => c.Kind == ChangeKind.Unreadable && IsUnder(dir.Path, c.Path));
            await Record(NotificationEvents.UnrecoverableError, source,
                $"Directory unreadable, skipped: {dir.Path}",
                $"{affected} entr{(affected == 1 ? "y" : "ies")} carried forward from the previous version. {dir.Reason}", ct);
        }

        foreach (var c in diff.Changes.Where(c => c.Kind == ChangeKind.Unreadable))
        {
            if (unreadableDirs.Any(d => IsUnder(d.Path, c.Path)))
                continue; // 已被上面的目录汇总覆盖
            await Record(NotificationEvents.UnrecoverableError, source,
                $"File unreadable, skipped: {c.Path}", c.UnreadableReason ?? "", ct);
        }
    }

    /// <summary>path 是否位于 dir 之下。dir 为根（"" 或 "."）时覆盖全部。</summary>
    private static bool IsUnder(string dir, string path) =>
        dir is "" or "." || path.StartsWith(dir + "/", StringComparison.Ordinal);

    /// <summary>新版本的空目录列表。读不开的目录本轮列不出内容，它自己和它下面的空目录都不会出现在
    /// 本次扫描里——直接用扫描结果会让这些目录在还原后凭空消失，所以要把上一版本里位于
    /// 读不开目录之下的项原样带过来。</summary>
    private static List<string> CarryEmptyDirs(ScanResult scan, VersionIndex? previous)
    {
        var dirs = new List<string>(scan.EmptyDirs);
        var unreadableDirs = scan.Unreadable.Where(u => u.IsDirectory).ToList();
        if (unreadableDirs.Count == 0 || previous is null)
            return dirs;

        var known = new HashSet<string>(dirs, StringComparer.Ordinal);
        foreach (var d in previous.EmptyDirs)
        {
            if (unreadableDirs.Any(u => IsUnder(u.Path, d)) && known.Add(d))
                dirs.Add(d);
        }
        dirs.Sort(StringComparer.Ordinal);
        return dirs;
    }

    /// <summary>diff 之后（压缩/上传阶段重新打开源文件时）才发现读不开：与 diff 时读不开复用完全相同的
    /// 通知通道、相同的 UnrecoverableError 事件、相同的消息格式——操作员不需要区分这个文件是在哪个
    /// 阶段读不开的，只需要知道"这个文件本轮没能读到"。</summary>
    private async Task RecordPostDiffUnreadableAsync(BackupRequest request, string path, string reason, CancellationToken ct) =>
        await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Account.Id}/{request.Container}",
            $"File unreadable, skipped: {path}", reason, ct);

    private async Task UploadBlobsAsync(
        BackupRequest request, BackupPlan plan, BlobAddressScheme addressing, LocalDedupResolver? localResolver,
        ConcurrentDictionary<string, StorageRef> storageByPath, ConcurrentDictionary<string, string> tailByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, ConcurrentDictionary<string, string> postDiffUnreadable,
        SemaphoreSlim uploadGate, Action onItem, CancellationToken ct)
        => await Task.WhenAll(plan.Blobs.Select(blob =>
            HandleBlobAsync(request, new PlannedFile(blob.Path, 0, blob.FullHash), addressing, localResolver,
                storageByPath, tailByPath, overrides, postDiffUnreadable, uploadGate, onItem, ct)));

    /// <summary>
    /// 处理单文件内容寻址 blob：压缩+上传 data/{hash}，经重校验循环（§9）。
    /// 内容在处理中变化时，用稳定后的新 hash 决定 blob 名并回写索引覆盖，避免存储名与内容不符（PRD 特别说明 D）。
    /// </summary>
    private async Task HandleBlobAsync(
        BackupRequest request, PlannedFile file, BlobAddressScheme addressing, LocalDedupResolver? localResolver,
        ConcurrentDictionary<string, StorageRef> storageByPath, ConcurrentDictionary<string, string> tailByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, ConcurrentDictionary<string, string> postDiffUnreadable,
        SemaphoreSlim uploadGate, Action onItem, CancellationToken ct)
    {
        var localPath = Path.Combine(request.LocalRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
        var storeOnly = request.Options.DontCompress?.MatchesFileOrAncestorDir(file.Path) ?? false;
        var headBytes = request.Options.Diff.HeadHashBytes;
        var cc = factory.CreateServiceClient(request.Account).GetBlobContainerClient(request.Container);

        // 内容寻址去重 + hash 碰撞避让：本地权威时纯本地判定（不发云端 HEAD，见 localResolver）；否则回退云端存在性检查。
        // 不同内容碰撞到同 hash 时改用备用名 …~N 并报警。verifier 内容变化时用新 hash 重调。
        var uploadedVolumes = 0;
        IReadOnlyList<long> uploadedSizes = [];   // 本次上传的各分卷尺寸
        int? dedupVolumes = null;                 // 去重命中时的既有分卷数（免云端 CountVolumes）
        IReadOnlyList<long>? dedupSizes = null;   // 去重命中时的既有分卷尺寸
        var chosenRef = addressing.DataAddress(file.FullHash);
        var collided = false;
        var wasRaw = false;
        string? finalTail = null;      // 最终内容的尾部 hash，回填索引条目

        // data/{hash}（或避让后的 …~N）不存在时：压缩/直传并上传，记录卷数/尺寸。
        async Task UploadNewAsync(string blobRef, string hash, long length, string head, string tail, bool raw, CancellationToken token)
        {
            var staged = await staging.StageAsync((compressTemp, t) => raw
                ? CopyRawAsync(localPath, compressTemp, SafeFileName(hash), t)
                : CompressAsync(request, compressTemp, SafeFileName(hash), [file.Path], storeOnly, t), token);
            var sizes = staged.Files.Select(f => new FileInfo(f).Length).ToList(); // Release 前先取尺寸
            await uploadGate.WaitAsync(token);
            try
            {
                var meta = new Dictionary<string, string>(addressing.Metadata(hash, length, head, tail));
                if (raw)
                    meta["raw"] = "1";
                await VolumeBlobIO.UploadAsync(
                    uploader, request.Account, request.Container, blobRef, staged.Files,
                    request.DataTier, request.Options.Upload, token, meta);
                uploadedVolumes = staged.Files.Count;
                uploadedSizes = sizes;
            }
            finally
            {
                uploadGate.Release();
                staging.Release(staged);
            }
        }

        async Task ProcessAsync(string hash, CancellationToken token)
        {
            var length = new FileInfo(localPath).Length;
            var head = await hasher.HeadHashAsync(localPath, headBytes, token);
            var tail = await hasher.TailHashAsync(localPath, headBytes, token);
            finalTail = tail;
            // 原始直传（PRD 3.3.2）：不压缩(store-only) + 未加密 + 无需分卷(≤分卷大小) → 直接拷原文件，省一次 7z 封装。
            var raw = storeOnly && string.IsNullOrEmpty(request.Password)
                && (request.Options.VolumeBytes is not { } vb || length <= vb);

            if (localResolver is not null)
            {
                // 纯本地判定：跨版本查映射、同批经预约协调（同内容共享 ref/raw/卷数，不同内容避让）。不读云端。
                var res = await localResolver.ResolveAsync(hash, length, head, tail);
                chosenRef = res.Ref;
                collided = res.Collision;
                if (res.Exists)
                {
                    wasRaw = res.Existing!.Raw;   // 以既有 blob 的实际 raw 为准（同批同内容也一致）
                    dedupVolumes = res.Existing.Volumes;
                    dedupSizes = res.Existing.VolumeSizes;
                    return;
                }
                try
                {
                    await UploadNewAsync(res.Ref, hash, length, head, tail, raw, token);
                    wasRaw = raw;
                    res.Complete(raw, uploadedVolumes, uploadedSizes); // 唤醒同批同内容的后到者，给它们相同存储信息
                }
                catch (Exception ex)
                {
                    res.Fail(ex);                        // 令等待者一并失败，绝不去重到未成功上传的 blob
                    throw;
                }
                return;
            }

            // 回退：导入未同步的备份走云端存在性检查 + 元数据比对。
            var (blobRef, exists, collision, existingRaw) = await ResolveDataRefAsync(cc, addressing, hash, length, head, tail, token);
            chosenRef = blobRef;
            collided = collision;
            if (exists)
            {
                wasRaw = existingRaw;
                return;
            }
            await UploadNewAsync(blobRef, hash, length, head, tail, raw, token);
            wasRaw = raw;
        }

        var finalHash = file.FullHash;
        ProcessingOutcome? verifyOutcome = null;
        var verifyAttempts = 0;
        try
        {
            if (verifier is not null)
            {
                var verificationOptions = new VerificationOptions { MaxAttempts = request.Options.ProcessingMaxAttempts };
                var vr = await verifier.RunAsync(localPath, file.FullHash, ProcessAsync, verificationOptions, ct);
                finalHash = vr.FullHash;
                verifyOutcome = vr.Outcome;
                verifyAttempts = vr.Attempts;
            }
            else
            {
                await ProcessAsync(file.FullHash, ct);
            }
        }
        // 这个 try 圈住的不只是源文件读取，还有压缩、暂存和上传——所以异常类型本身不足以判定
        // "文件读不开"：BlobUploader 把 IOException 归为可重试的网络错误（BlobUploader.IsTransient），
        // 重试预算耗尽后它会原样抛出来，落进这里。仅凭类型收下它，一次 NAS 断网就会被记成
        // 若干个"文件不可读、沿用旧条目"，整轮备份照常报告成功——操作员看到的是"Backup succeeded,
        // 0 changed files"，而实际上什么都没传上去。因此 filter 里再探一次源文件：真读不开才降级，
        // 读得开就让异常照常向上抛，让整轮备份响亮地失败。
        // ArchiveMembersMissingException 是例外，不需要探测：它只在 7z 把这个文件丢出归档时抛出，
        // 已经是"本轮没能把这个文件存下来"的确证，而且抛在上传之前，云端不会留下空归档。
        catch (Exception ex) when (ex is ArchiveMembersMissingException
            || ((ex is IOException or UnauthorizedAccessException) && SourceUnreadable(localPath)))
        {
            // diff 时可读、随后（压缩打包/原样直传重新打开源文件时）才读不开：与 diff 阶段读不开同等
            // 处置——不产生 blob、不写 storageByPath/overrides（索引阶段据此沿用旧条目或整条缺席），
            // 只记一条复用既有通道的告警，绝不能让这一个文件拖垮整轮备份。
            await MarkPostDiffUnreadableAsync(request, file.Path, ex.Message, postDiffUnreadable, ct);
            onItem();
            return;
        }

        // "反复变化"告警是内容已成功处理/上传之后的事后上报，不再触碰源文件——绝不能留在上面的
        // try 里：否则这条通知（或其内部日志写入）失败会被误判成"文件读不开"，导致已经成功上传
        // 的内容在索引里被沿用旧条目或整条丢弃，而云端其实已经有这份数据（本次修复 Finding 1）。
        if (verifyOutcome == ProcessingOutcome.Alarmed)
            await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Account.Id}/{request.Container}",
                $"File kept changing during backup: {file.Path}",
                $"Saved with current content after {verifyAttempts} attempts", ct);

        if (collided)
            await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Account.Id}/{request.Container}",
                $"Hash collision avoided: {file.Path}",
                $"Different content shares hash {finalHash}; stored at {chosenRef}", ct);

        // 内容在处理中变化：以稳定后的新 hash/元数据覆盖索引条目，保证 data/{hash} 内容与名一致。
        // 这一步要**再读一次源文件**（长度/mtime/头部 hash），因此和分组路径上的同一动作一样需要保护；
        // 之所以提到写 storageByPath 之前：读不开时该文件按"本轮没能存下来"降级，此时不该留下任何
        // 半条存储引用——一条 fullHash 指向新内容、长度/头部 hash 却还是旧内容的索引记录，
        // 比整条缺席更糟，检查与还原都会据此得出错误结论。
        if (finalHash != file.FullHash)
        {
            try
            {
                overrides[file.Path] = await BuildOverrideAsync(localPath, finalHash, request.Options.Diff.HeadHashBytes, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await MarkPostDiffUnreadableAsync(request, file.Path, ex.Message, postDiffUnreadable, ct);
                onItem();
                return;
            }
        }

        // 记录分卷数（供检查核验全部分卷）：本次上传的卷数；去重时用本地已知卷数（dedupVolumes），
        // 仅云端回退路径且无从得知时才 CountVolumes（本地权威路径不读云端）。
        var volumes = uploadedVolumes > 0
            ? uploadedVolumes
            : dedupVolumes ?? await VolumeBlobIO.CountVolumesAsync(cc, chosenRef, ct);
        // 分卷尺寸：本次上传取实测；去重取既有；云端回退路径无从得知则留空（尺寸检查降级为仅验存在）。
        var volumeSizes = uploadedVolumes > 0 ? uploadedSizes : dedupSizes ?? [];
        storageByPath[file.Path] = new StorageRef
        {
            Kind = "blob", Ref = chosenRef, Volumes = Math.Max(1, volumes), Raw = wasRaw,
            VolumeSizes = [.. volumeSizes],
        };
        if (finalTail is not null)
            tailByPath[file.Path] = finalTail;

        await LogFileAsync(request, file.Path, ct);
        onItem();
    }

    /// <summary>
    /// 定位 data blob 的实际存储名并判断是否可去重。基名由寻址方案给出（加密备份为密钥化地址）；
    /// 元数据确认同内容才去重，内容不同却 hash 相同（碰撞）时顺延到备用名 …~1、~2…。
    /// </summary>
    private static async Task<(string Ref, bool Exists, bool Collision, bool ExistingRaw)> ResolveDataRefAsync(
        BlobContainerClient cc, BlobAddressScheme addressing, string hash, long length, string headHash, string tailHash, CancellationToken ct)
    {
        var baseAddr = addressing.DataAddress(hash);
        for (var n = 0; ; n++)
        {
            var refName = n == 0 ? baseAddr : $"{baseAddr}~{n}";
            var meta = await ReadBlobMetaAsync(cc, refName, ct);
            if (meta is null)
                return (refName, false, n > 0, false);                            // 空位 → 在此上传（n>0=已避让碰撞）
            if (addressing.MetadataMatches(meta, hash, length, headHash, tailHash))
                return (refName, true, n > 0, meta.TryGetValue("raw", out var r) && r == "1"); // 同内容 → 去重，带既有 raw 属性
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
        BackupRequest request, BackupPlan plan, BlobAddressScheme addressing, LocalDedupResolver? localResolver,
        BackupInfoFile info, ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, string> tailByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, ConcurrentDictionary<string, string> postDiffUnreadable,
        SemaphoreSlim uploadGate, Action onItem, CancellationToken ct)
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
            ProcessDirectoryAsync(request, pool, addressing, localResolver, info, storageByPath, tailByPath, overrides, postDiffUnreadable, uploadGate, packCounter, onItem, ct)));
    }

    private async Task ProcessDirectoryAsync(
        BackupRequest request, List<PlannedFile> pool, BlobAddressScheme addressing, LocalDedupResolver? localResolver,
        BackupInfoFile info, ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, string> tailByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, ConcurrentDictionary<string, string> postDiffUnreadable,
        SemaphoreSlim uploadGate, int[] packCounter, Action onItem, CancellationToken ct)
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

            // 无 verifier：直接压缩上传，不做成员重校验。7z 丢成员的验收仍然要做——
            // 没有重校验兜底时，这条路径是唯一能发现"包里少了人"的地方。
            if (verifier is null)
            {
                var (staged0, missing0) = await CompressPackTolerantAsync(request, packId, members, ct);
                var kept0 = members.Where(m => !missing0.Contains(m.EntryName)).ToList();
                if (staged0 is not null && kept0.Count > 0)
                {
                    var vols0 = await UploadStagedPackAsync(request, packId, staged0, uploadGate, ct);
                    RecordPack(request, packId, kept0, vols0, info, storageByPath);
                    foreach (var m in kept0) await LogFileAsync(request, m.Path, ct);
                }
                else if (staged0 is not null)
                {
                    staging.Release(staged0);
                }
                foreach (var m in members.Where(m => missing0.Contains(m.EntryName)))
                    await MarkPostDiffUnreadableAsync(request, m.Path, ArchiverDroppedReason, postDiffUnreadable, ct);
                onItem();
                continue;
            }

            // 这份快照离 diff 可能已隔了几小时——所有单文件 blob 传完才轮到分组上传——期间一个成员
            // 完全可能被删掉（构建产物）或被收回权限，而 Stat 会就此抛出，让整轮备份倒在与本分支
            // 所修完全相同的形状上。不另起机制：读不到就把快照记成 null，交给下面既有的"排除成员"
            // 路径处理（与"内容在压缩期间变了"同一条路：排除出归档 → 重取新内容 → 仍读不开则降级）。
            var before = members.ToDictionary(m => m.Path, m => TryStat(Local(request, m.Path)));
            var (staged, missing) = await CompressPackTolerantAsync(request, packId, members, ct);

            // 被 7z 丢出归档的成员必须**直接**判为排除，不能指望下面的比对发现：那段比对看的是
            // 元数据与内容 hash，而权限被收回并不改 mtime/length——比对会说"这个成员没变"，
            // 于是一个缺成员的 pack 就被原样上传，索引却声称它在里面。
            var changed = members.Where(m => missing.Contains(m.EntryName)).ToList();

            // 压缩后重校验：元数据变且内容 hash 变 → 该成员在压缩期间变化。
            foreach (var m in members)
            {
                if (missing.Contains(m.EntryName))
                    continue;

                var local = Local(request, m.Path);
                bool exclude;
                try
                {
                    // 读不开与内容变了，对这个包而言后果相同：都不能把它留在归档里上传。
                    // 快照阶段就已经读不到（before 为 null）同样归入这一类，不必再读第二次去确认。
                    exclude = before[m.Path] is not { } snapshot
                        || (Stat(local) != snapshot && await hasher.FullHashAsync(local, ct) != m.FullHash);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    exclude = true;
                }
                if (exclude)
                    changed.Add(m);
            }

            if (changed.Count == 0)
            {
                var vols = await UploadStagedPackAsync(request, packId, staged!, uploadGate, ct);
                RecordPack(request, packId, members, vols, info, storageByPath);
                foreach (var m in members) await LogFileAsync(request, m.Path, ct);
                onItem();
                continue;
            }

            // 丢弃本次归档；稳定成员照常成 pack；变化成员以新 hash 处理。
            // staged 为 null 只可能是整组成员都被 7z 丢掉（连空归档都没留下），此时无物可释放。
            if (staged is not null)
                staging.Release(staged);
            var stable = members.Where(m => !changed.Contains(m)).ToList();
            if (stable.Count > 0)
            {
                var staged2 = await CompressPackAsync(request, packId, stable, ct);
                var vols2 = await UploadStagedPackAsync(request, packId, staged2, uploadGate, ct);
                RecordPack(request, packId, stable, vols2, info, storageByPath);
                foreach (var m in stable) await LogFileAsync(request, m.Path, ct);
            }
            // 无论这一组里有多少成员被排除出稳定 pack（内容变化、还是读不开)，这次分组迭代都对应
            // total 里预留的一个槽位，必须**恰好上报一次**——即便 stable.Count == 0（整组成员一起
            // 读不开，Finding 2 命中的最坏情形），否则 uploaded 永远追不上 total，完工也显示不了 100%。
            // 反过来，onItem() 放在这里而不是 foreach(changed) 内部的每个成员上，也避免了同一组里
            // 多个成员一起失败时被重复计数（该组只占一个槽位，不是每个成员各占一个）。
            onItem();

            foreach (var m in changed)
            {
                var local = Local(request, m.Path);
                string newHash;
                long newLen;
                try
                {
                    newHash = await hasher.FullHashAsync(local, ct);
                    newLen = new FileInfo(local).Length;
                    // 内容已变（≠ diff 时 fullHash）：写索引覆盖，使 fullHash/名字/元数据与新内容一致。
                    overrides[m.Path] = await BuildOverrideAsync(local, newHash, headBytes, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 上面已排除该成员出本次归档（内容变了 or 读不开，处置相同）；这里第二次尝试确认新内容时
                    // 若仍然读不开（并非瞬时抖动，而是真被锁住/权限收回），就不再假装能重新入队处理——
                    // 就地按"读不开"降级：不产生任何 blob、不进入任何 pack，索引沿用旧条目或整条缺席。
                    // 全目录一起读不开时，这一步保证第一个撞上的成员不会让同目录其余成员失去被处理的机会。
                    // 不在此处调用 onItem()：这一组的槽位已经在上面统一上报过一次，这里再报会双计。
                    await MarkPostDiffUnreadableAsync(request, m.Path, ex.Message, postDiffUnreadable, ct);
                    continue;
                }

                var n = attempts[m.Path] = attempts.GetValueOrDefault(m.Path) + 1;
                if (newLen >= threshold || n >= maxAttempts)
                {
                    // 变大到超阈值、或反复变化达阈值 → 单文件（后者报警）。
                    if (n >= maxAttempts)
                        await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Account.Id}/{request.Container}",
                            $"File kept changing during grouping: {m.Path}",
                            $"Stored as single file after {n} attempts", ct);
                    // 不重新用 try 包住整个调用：HandleBlobAsync 自己对源读取/处理/上传有正确范围的
                    // catch（成功上传后的收尾不在其内），这里的职责只是"别再包一层"，不是重新兜底
                    // 它已经处理过的失败（Finding 1：调用方的 catch 不应该圈住被调用方的全部工作）。
                    await HandleBlobAsync(request, new PlannedFile(m.Path, newLen, newHash), addressing, localResolver,
                        storageByPath, tailByPath, overrides, postDiffUnreadable, uploadGate, static () => { }, ct);
                }
                else
                {
                    queue.Add(new PlannedFile(m.Path, newLen, newHash)); // 自然进入下一组
                }
            }
        }
    }

    /// <summary>操作员看到的原因：7z 对读不了的成员只报警告并把它丢掉，没有给出任何系统级消息，
    /// 所以这里只能给出我们自己的判定。</summary>
    private const string ArchiverDroppedReason =
        "The archiver could not read this file, so it was left out of the archive.";

    /// <summary>
    /// 压缩一组成员，容忍 7z 静默丢弃读不了的成员：把被丢掉的剔除后重压，直到归档与成员集一致
    /// 或成员耗尽。返回归档（整组都读不了时为 null）与被丢掉的条目名。
    /// 不让 <see cref="ArchiveMembersMissingException"/> 直接冒出去——在本模块的既定语义里，
    /// 一个读不了的成员是"排除该成员"，不是"整轮备份失败"。
    /// </summary>
    private async Task<(StagedItem? Staged, IReadOnlySet<string> Missing)> CompressPackTolerantAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, CancellationToken ct)
    {
        var remaining = members.ToList();
        var missing = new HashSet<string>(StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            try
            {
                return (await CompressPackAsync(request, packId, remaining, ct), missing);
            }
            catch (ArchiveMembersMissingException ex)
            {
                var dropped = new HashSet<string>(ex.MissingEntries, StringComparer.Ordinal);
                // 一个成员都剔不掉，说明报回来的名字与成员名对不上（不该发生）。继续循环就是死循环，
                // 与其无声打转，不如让它响亮地失败。
                if (remaining.RemoveAll(m => dropped.Contains(m.EntryName)) == 0)
                    throw;
                missing.UnionWith(dropped);
            }
        }
        return (null, missing);
    }

    /// <summary>按"本轮没能把这个文件存下来"降级：索引据此沿用旧条目或整条缺席，并推一条告警。</summary>
    private async Task MarkPostDiffUnreadableAsync(
        BackupRequest request, string path, string reason,
        ConcurrentDictionary<string, string> postDiffUnreadable, CancellationToken ct)
    {
        postDiffUnreadable[path] = reason;
        await RecordPostDiffUnreadableAsync(request, path, reason, ct);
    }

    private Task<StagedItem> CompressPackAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, CancellationToken ct)
    {
        var entries = members.Select(m => m.EntryName).ToList();
        return staging.StageAsync((compressTemp, token) => CompressAsync(
            request, compressTemp, packId, entries, storeOnly: false, token), ct);
    }

    /// <returns>该 pack 各分卷的字节尺寸（按 .001..N 顺序；供记录，核验分卷完整性/尺寸用）。</returns>
    private async Task<IReadOnlyList<long>> UploadStagedPackAsync(
        BackupRequest request, string packId, StagedItem staged, SemaphoreSlim uploadGate, CancellationToken ct)
    {
        var sizes = staged.Files.Select(f => new FileInfo(f).Length).ToList(); // Release 前先取尺寸
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
        return sizes;
    }

    private static void RecordPack(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, IReadOnlyList<long> volumeSizes,
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
            Volumes = Math.Max(1, volumeSizes.Count),
            VolumeSizes = [.. volumeSizes],
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

    /// <summary>取不到元数据（文件已消失、权限被收回）时返回 null，由调用方按"该成员必须排除"处理。</summary>
    private static (long Mtime, long Length, int Mode)? TryStat(string path)
    {
        try
        {
            return Stat(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>源文件此刻是否真的读不开。用于把"文件读不开"与压缩/暂存/上传栈里同样以 IOException
    /// 现身的故障区分开——尤其是网络：BlobUploader 把 IOException 当可重试的网络错误，重试预算耗尽后
    /// 原样抛出，形状与"文件读不开"一模一样。打开成功还要真读一个字节：权限/介质错误可能到第一次
    /// 实际读才暴露。FileShare 放到最宽，只判"我们能不能读"，不去替别的写者判断它该不该写。</summary>
    private static bool SourceUnreadable(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.ReadByte();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>原始直传：直接把原文件拷到压缩临时区（单卷），不走 7z 封装（PRD 3.3.2）。</summary>
    private static async Task<IReadOnlyList<string>> CopyRawAsync(
        string localPath, string compressTemp, string name, CancellationToken ct)
    {
        var dest = Path.Combine(compressTemp, name);
        await using var src = File.OpenRead(localPath);
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst, ct);
        return [dest];
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
        IReadOnlyDictionary<string, string> tailByPath,
        IReadOnlyDictionary<string, EntryOverride> overrides,
        IReadOnlyDictionary<string, string> postDiffUnreadable)
    {
        var entries = new List<IndexEntry>();
        foreach (var c in diff.Changes)
        {
            // 读不开：沿用上一版本条目（含 Storage，因此不重传任何内容、不影响去重），
            // 仅追加 UnreadableAt。上一版本没有该文件时整条跳过——没有内容可指向。
            // diff 时读得开、但压缩/上传阶段重新打开时才读不开（postDiffUnreadable）走完全相同的处置：
            // 对索引而言，"这一轮没能把内容存下来"是同一件事，不该另起一套判断。
            // 这一段必须排在 Current is null 之前：目录读不开时派生出来的条目**没有** Current
            // （整棵子树压根没被扫到），排在后面就会被当作"无当前状态"直接跳过，
            // 于是那些条目从新索引里消失——正是本轮要修的静默数据丢失。
            if (c.Kind == ChangeKind.Unreadable || postDiffUnreadable.ContainsKey(c.Path))
            {
                if (c.Previous is not null)
                    // 已经带着 UnreadableAt 的条目保留原值：这个字段要回答的是"这份内容从什么时候起
                    // 就没能再更新"，每轮刷成 UtcNow 等于每轮把答案抹掉，只剩一句"刚才也没读到"。
                    // 一旦某轮重新读到，条目会正常重建，字段自然回到 null。
                    entries.Add(c.Previous with { UnreadableAt = c.Previous.UnreadableAt ?? DateTimeOffset.UtcNow });
                continue;
            }

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
                // 尾部 hash：本次上传的单文件 blob 用其算得值；未变/打包文件继承上一版本条目（打包成员为 null，不参与 blob 去重）。
                TailHash = tailByPath.GetValueOrDefault(c.Path) ?? c.Previous?.TailHash,
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
