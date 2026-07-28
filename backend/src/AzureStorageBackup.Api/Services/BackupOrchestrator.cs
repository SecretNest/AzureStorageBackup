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

    /// <summary>命中者允许跨目录装箱（散列分片目录用；空 = 全部按目录打包）。</summary>
    public IgnoreRuleSet? CrossDirGroup { get; init; }
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

    /// <summary>压缩后重校验中，同一个成员反复变化时的重处理次数上限（PRD §5.1，默认 5）。</summary>
    public int ProcessingMaxAttempts { get; init; } = 5;

    /// <summary>
    /// 差分与「压缩+上传」是否重叠跑（默认开）。开着时判定一出来就开始传，网络不必等到哈希全部
    /// 跑完；代价是差分的读与压缩的读会同时压在同一块盘上。机械盘的 NAS 上两股读可能互相拖慢到
    /// 净收益为负——那种情况下关掉它，回到"先全部判完再传"的老行为。
    /// </summary>
    public bool OverlapDiffAndUpload { get; init; } = true;
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

    /// <summary>当前正在跑的各阶段各自在做什么（正在处理哪个文件、已处理多少、多快）。
    /// 流水线化之后 Diffing 与 Uploading 是**同时**在跑的，所以这里是一个列表而不是单值：
    /// 只报其中一条，界面上就会看不见另一条在动。</summary>
    public IReadOnlyList<StageProgress> Details { get; init; } = [];

    /// <summary>头条明细。串行阶段（扫描、写索引…）只有一条，就是它。
    /// 保留这个单值字段是为了让"只看一条"的调用方（既有前端与测试）不必先判断有没有第二条。</summary>
    public StageProgress? Detail
    {
        get => Details.Count > 0 ? Details[0] : null;
        init => Details = value is null ? [] : [value];
    }
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
    ILocalIndexCache? indexCache = null,
    TrackedInfoStore? trackedInfo = null,
    VerboseFileLog? verboseLog = null)
{
    /// <summary>流水线上的一件活：一个单文件 blob，或一箱已封好的 pack 成员。</summary>
    private readonly record struct WorkItem(PlannedFile? Single, IReadOnlyList<PlannedFile>? Pack);

    /// <summary>
    /// diff 最多可以领先压缩上传侧多少件活。这个数只是内存的护栏，不是节流阀：
    /// 每件活不过是几个已经在内存里的路径，所以给得足够宽，正常情况下 diff 从不会被它挡住。
    /// </summary>
    private const int WorkQueueCapacity = 4096;

    /// <summary>
    /// 流水线的进度汇总。Diffing 与 Uploading 是**同时**在跑的，任何一侧更新都要连另一侧的
    /// 最新快照一起发出去——只发自己那条，界面上两行会互相把对方擦掉。
    /// </summary>
    private sealed class PipelineReporter(IProgress<BackupProgress>? sink)
    {
        private readonly Lock _gate = new();
        private StageProgress? _diff;
        private StageProgress? _upload;
        private BackupStage _stage = BackupStage.Diffing;
        private int _changedFiles;
        private long _changedBytes;
        private int _uploaded;
        private int _total;

        public void ReportDiff(StageProgress d) { lock (_gate) { _diff = d; Publish(); } }
        public void ReportUpload(StageProgress u) { lock (_gate) { _upload = u; Publish(); } }

        public void SetChanged(int files, long bytes)
        {
            lock (_gate) { _changedFiles = files; _changedBytes = bytes; }
        }

        public void SetUploaded(int done) { lock (_gate) { _uploaded = done; Publish(); } }

        /// <summary>两条流都跑完了：收起 diff 那条明细，总数这时才是确定的。</summary>
        public void Settle(int total)
        {
            lock (_gate) { _diff = null; _stage = BackupStage.Uploading; _total = total; Publish(); }
        }

        private void Publish() => sink?.Report(
            new BackupProgress(_stage, _changedFiles, _changedBytes, _uploaded, _total)
            {
                Details = (_diff, _upload) switch
                {
                    (null, null) => [],
                    (null, { } u) => [u],
                    ({ } d, null) => [d],
                    var (d, u) => [d!, u!],
                },
            });
    }

    /// <summary>等两条流都停下来，吞掉它们的异常——调用方手上已经有要抛的那个了。</summary>
    private static async Task SettleAsync(IEnumerable<Task> consumers)
    {
        try { await Task.WhenAll(consumers); }
        catch { /* 先出的那个错才是根因，这里只负责"等干净" */ }
    }

    private static PlannedFile ToPlannedFile(PackEntry m) => new(m.Path, m.Length, m.FullHash);

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

        // 3./4./5. Diff 与「装箱 + 压缩 + 上传」流水线化。
        // 从前这三段严格串行：Diffing 全部跑完 → Plan → Uploading。首次备份的 diff 要把每个文件
        // 完整读一遍算 hash，那几小时里网络一个字节都没在传。而 Plan 其实不必当这道全局屏障——
        // 归类只看路径与长度（见 GroupingPlanner.Classify），扫描一结束就已经定局。
        var packOptions = opts.Plan with { DontGroup = opts.DontGroup, CrossDirGroup = opts.CrossDirGroup };
        var classification = planner.Classify(scan.Entries, packOptions);

        var storageByPath = new ConcurrentDictionary<string, StorageRef>(StringComparer.Ordinal);
        var tailByPath = new ConcurrentDictionary<string, string>(StringComparer.Ordinal); // 单文件 blob 的尾部 hash → 索引条目
        // 处理中内容变化的文件：以稳定后的新 hash/元数据覆盖 diff 时的索引条目（§9、PRD 特别说明 D）。
        var overrides = new ConcurrentDictionary<string, EntryOverride>(StringComparer.Ordinal);
        // diff 之后才读不开（压缩/上传阶段重新打开源文件时撞上）：与 diff 时就读不开的文件同等对待——
        // 不产生 blob、索引沿用旧条目、计入 UnreadableFiles，绝不能让整轮备份因此崩溃（M4 设计 §3 遗漏点）。
        var postDiffUnreadable = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        var reporter = new PipelineReporter(progress);
        // diff 不申报字节工作量，剩余时间按**件数**外推（见 StageTracker.Eta）：这个阶段的耗时
        // 主要摊在"每条至少 stat 一次"上，真要整个读一遍的只是少数变更文件——按字节推的话，
        // 一个没变的 100 GB 文件秒过，会让剩余时间当场塌掉一大截。
        var diffTracker = new StageTracker("Diffing", scan.Entries.Count, reporter.ReportDiff);
        // 上传的总数是**边跑边长出来的**（diff 还在往队列里塞活），先报 0＝未知：
        // 用一个还在涨的分母算百分比，会先冲到 100 再掉回去。工作量同理，随 Enqueue 一件件累加。
        var uploadTracker = new StageTracker("Uploading", total: 0, reporter.ReportUpload);

        var totalItems = 0;
        var uploadedItems = 0;
        // work = 这一件活对应的**原始**字节。不能用实传字节当完成度：去重命中一个字节都不传，
        // 压缩率又随文件类型大幅摆动，拿它算剩余时间会随命中率和压缩率乱跳。
        void ReportItem(long work)
        {
            // 槽位计数归这里（它有"恰好一次"的既有约束）；tracker 只负责在途项与字节/测速。
            reporter.SetUploaded(Interlocked.Increment(ref uploadedItems));
            uploadTracker.Advance(0, work);
        }

        // 并发额度按**卷**发放，不按件（见 VolumeUploadScope）：一件活可能是一个大文件切出来的
        // 上千卷，按件发的话它整段只占一条流，设置里那个数字在传大文件时根本不起作用。
        var streams = Math.Max(1, opts.UploadConcurrency);
        using var uploadGate = new SemaphoreSlim(streams, streams);
        var uploadScope = new VolumeUploadScope(uploadGate, uploadTracker, streams);
        // 跨目录并发共享的 pack 号（内容寻址 data blob 不受影响；pack 号只需唯一）。
        var packCounter = new[] { NextPackNumber(info.Packs) - 1 };

        // 有界队列把两条流解耦：staged 满时挡住的只是压缩侧，diff 该读盘照样读盘——
        // 反压一路顶回 diff，磁盘就跟着停了，这次改造也就白做了。
        // 关掉重叠时用无界队列：那条路上根本没人在消费，容量限制只会把 diff 卡死。
        var overlap = opts.OverlapDiffAndUpload;
        var work = overlap
            ? System.Threading.Channels.Channel.CreateBounded<WorkItem>(
                new System.Threading.Channels.BoundedChannelOptions(WorkQueueCapacity) { SingleWriter = true })
            : System.Threading.Channels.Channel.CreateUnbounded<WorkItem>(
                new System.Threading.Channels.UnboundedChannelOptions { SingleWriter = true });

        // 上传侧出错要让 diff 停下来（继续读盘没有意义），但**不**打断已经在跑的其它上传——
        // 与从前 Task.WhenAll 的收场方式一致：在途的做完，再把第一个真实异常抛出去。
        using var stopProducing = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task ConsumeAsync()
        {
            try
            {
                await foreach (var item in work.Reader.ReadAllAsync(ct))
                {
                    // 领走一件活。从这里到 BeginUpload（压完、开始抢流的额度）之间是压缩与暂存，
                    // 一箱 100 MB 过 7z 可以几十秒——界面上得看得见这段，否则就是"什么都没在发生"。
                    uploadTracker.BeginWork();
                    try
                    {
                        if (item.Single is { } single)
                            await HandleBlobAsync(request, single, addressing, localResolver, storageByPath, tailByPath,
                                overrides, postDiffUnreadable, uploadScope, ReportItem, uploadTracker, ct);
                        else
                            await ProcessPackAsync(request, item.Pack!, addressing, localResolver, info, storageByPath,
                                tailByPath, overrides, postDiffUnreadable, uploadScope, packCounter, ReportItem,
                                uploadTracker, ct);
                    }
                    finally
                    {
                        uploadTracker.EndWork();
                    }
                }
            }
            catch
            {
                await stopProducing.CancelAsync(); // 别再让 diff 白读盘
                throw;
            }
        }

        var workers = Math.Max(2, Math.Max(1, opts.UploadConcurrency) + 1);
        List<Task> consumers = [];
        void StartConsumers() => consumers = [.. Enumerable.Range(0, workers).Select(_ => Task.Run(ConsumeAsync, ct))];

        if (overlap)
            StartConsumers();

        // 装箱的在途状态。diff 单线程按扫描顺序推进，所以这些都不需要加锁。
        var cap = packOptions.GroupCapBytes;
        var dirPending = new Dictionary<string, List<PlannedFile>>(StringComparer.Ordinal);
        var dirRemaining = new Dictionary<string, int>(classification.DirectoryCandidates, StringComparer.Ordinal);
        var crossPending = new List<PlannedFile>();
        long crossBytes = 0;
        var changedFiles = 0;
        long changedBytes = 0;

        async Task EnqueueAsync(WorkItem item, CancellationToken token)
        {
            Interlocked.Increment(ref totalItems);
            // 申报这件活的原始字节，作为剩余时间估算的工作量。完工时 ReportItem 会照同一个量
            // 销账（单文件按 Length，一箱按成员长度和），两边必须对得上，否则剩余量归不了零。
            uploadTracker.Enqueue(item.Single?.Length ?? item.Pack!.Sum(f => f.Length));
            await work.Writer.WriteAsync(item, token);
        }

        async Task OnChangeAsync(FileChange c, CancellationToken token)
        {
            var changed = c.Kind is ChangeKind.Added or ChangeKind.Modified && c.Current is not null;
            if (changed)
            {
                changedFiles++;
                changedBytes += c.Current!.Length;
                reporter.SetChanged(changedFiles, changedBytes);
            }

            if (!classification.ByPath.TryGetValue(c.Path, out var klass))
                return;

            // FullHash 可能为空——单文件 blob 的全文 hash 延后到压缩那一遍算（见 DeferFullHash）。
            var file = changed ? new PlannedFile(c.Path, c.Current!.Length, c.FullHash) : null;

            switch (klass.Category)
            {
                case FileCategory.SingleFile:
                    // 单文件：判定一出来立刻走流式压缩上传，不等任何人。
                    if (file is not null)
                        await EnqueueAsync(new WorkItem(file, null), token);
                    return;

                case FileCategory.CrossDirectoryGroup:
                    // 扫描结果按 ordinal 路径序排好，与跨目录装箱用的是同一个序，因此"边 diff 边填、
                    // 填满即封"得到的包，与"等全部 diff 完再一次装箱"逐字节相同。
                    if (file is not null)
                    {
                        if (crossPending.Count > 0 && crossBytes + file.Length > cap)
                        {
                            await EnqueueAsync(new WorkItem(null, crossPending), token);
                            crossPending = [];
                            crossBytes = 0;
                        }
                        crossPending.Add(file);
                        crossBytes += file.Length;
                    }
                    return;

                default:
                    // 按目录：必须等**整个目录**都判完才能封箱——未变的、读不开的、以及 hash 算完
                    // 发现内容其实没变的（MetadataOnly），都不该进包，而这些要 diff 过才知道。
                    var dir = klass.GroupKey!;
                    if (file is not null)
                    {
                        if (!dirPending.TryGetValue(dir, out var pending))
                            dirPending[dir] = pending = [];
                        pending.Add(file);
                    }
                    if (--dirRemaining[dir] == 0 && dirPending.Remove(dir, out var members))
                    {
                        // 装箱仍由规划器那个纯函数负责，输入换成"这一组里确实变更的文件"。
                        foreach (var pack in planner.Plan(members, packOptions).Packs)
                            await EnqueueAsync(new WorkItem(null, [.. pack.Members.Select(ToPlannedFile)]), token);
                    }
                    return;
            }
        }

        // 走单文件 blob 的条目，diff 阶段不必把文件整个读一遍算全文 hash：那条路上 hash 是压缩
        // 那一遍读顺手算出来的（StreamAndStageAsync），算完还会覆盖 diff 记的值。归类只看
        // 路径与长度，扫描一结束就定了，所以这个判定在 diff 之前就能给出来。
        bool DeferFullHash(string path) =>
            classification.ByPath.TryGetValue(path, out var k) && k.Category == FileCategory.SingleFile;

        DiffResult diff;
        try
        {
            try
            {
                diff = await differ.DiffAsync(
                    request.LocalRoot, scan, previous, opts.Diff, stopProducing.Token, diffTracker, OnChangeAsync,
                    DeferFullHash);

                // 收尾：把还没填满的箱子封掉。跨目录的那一箱肯定有剩；按目录的理论上都已在
                // 计数归零时封过，这里只是不留活口。
                if (crossPending.Count > 0)
                    await EnqueueAsync(new WorkItem(null, crossPending), stopProducing.Token);
                foreach (var leftover in dirPending.Values.Where(m => m.Count > 0))
                    await EnqueueAsync(new WorkItem(null, leftover), stopProducing.Token);
            }
            finally
            {
                diffTracker.Complete();
                work.Writer.TryComplete(); // 无论如何都要让消费者知道"没有更多活了"，否则它们永远等下去
            }
        }
        catch (OperationCanceledException) when (stopProducing.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // diff 是被上传侧的失败叫停的：真正的原因在消费者那边，等它们把它抛出来。
            await Task.WhenAll(consumers);
            throw; // 消费者居然没抛：那就把这个取消交上去，绝不静默当成功
        }
        catch
        {
            // 忙碌锁要等 RunAsync 返回才释放，早一步返回就等于把一堆压缩/上传丢在锁外面继续跑。
            await SettleAsync(consumers);
            throw;
        }

        // 关掉重叠：活全部攒好了才开工，回到"先全部判完再传"的老行为。
        if (!overlap)
            StartConsumers();

        // diff 收工，队列里再不会多出活来 → 上传的分母到此才是确定的，界面上的百分比这时才有意义。
        uploadTracker.SetTotal(totalItems);
        reporter.Settle(totalItems);

        // 读不开的文件既不算变更也不算删除，索引阶段会静默沿用旧条目——但操作员必须被告知。
        // 排在等待上传之前：这条路径每一轮都要执行，压到最后就等于让一次上传失败顺手把
        // 这些告警也吞掉，而"有文件读不开"恰恰是最需要告诉操作员的时候。
        await RecordUnreadableWarningsAsync(request, scan, diff, ct);

        await Task.WhenAll(consumers);
        // 与扫描/差分同理：不强制产出终态，最后一批传完的字节就永远发布不出去——
        // 节流会把它们压在最后一个窗口里，而那之后不再有任何一次上报。
        uploadTracker.Complete();

        var total = totalItems;
        var uploaded = uploadedItems;

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

    /// <summary>一遍读得到的内容身份：三段 hash + 长度 + 读完时的 mtime，外加它是否以原始字节存储。</summary>
    private sealed record BlobContent(
        string FullHash, string HeadHash, string TailHash, long Length, DateTimeOffset Mtime, bool Raw);

    /// <summary>单文件 blob 的最终落位：存储引用 + 实际存下去的内容身份。</summary>
    private sealed record BlobPlacement(
        string Ref, bool Collision, int Volumes, IReadOnlyList<long> VolumeSizes, BlobContent Content);

    /// <summary>
    /// 处理单文件内容寻址 blob：**一遍读**同时算 hash 并压缩，然后上传 data/{hash}。
    /// <para>
    /// 顺序与从前相反。过去是「先算全文 hash → 查去重 → 命中就整个跳过 → 否则再读一遍去压」；
    /// 现在是「边读边压边算 → 压完才知道名字」。代价是已经存在的内容会被白压一遍，所以前面加了一道
    /// 只需读文件头的预筛（长度 + head hash）：本地索引里真有候选时才退回老路（读一遍算全文 hash，
    /// 命中就一个字节都不压）。首次备份没有任何候选，走的全是一遍读的快路径。
    /// </para>
    /// <para>
    /// 顺带消掉一整类竞态：hash 现在算的**就是压进归档的那些字节**，两者不可能对不上，
    /// 因此这条路径不再需要处理后重校验，也不再需要为写索引覆盖条目而第二次打开源文件
    /// （那正是"内容变了随后又被锁住"会崩在的地方）。pack 路径仍是先 hash 后压，重校验照旧保留。
    /// </para>
    /// </summary>
    private async Task HandleBlobAsync(
        BackupRequest request, PlannedFile file, BlobAddressScheme addressing, LocalDedupResolver? localResolver,
        ConcurrentDictionary<string, StorageRef> storageByPath, ConcurrentDictionary<string, string> tailByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, ConcurrentDictionary<string, string> postDiffUnreadable,
        VolumeUploadScope uploadScope, Action<long> onItem, StageTracker uploadTracker, CancellationToken ct)
    {
        var localPath = Local(request, file.Path);
        var storeOnly = request.Options.DontCompress?.MatchesFileOrAncestorDir(file.Path) ?? false;

        BlobPlacement placement;
        try
        {
            placement = await PlaceBlobAsync(
                request, file, localPath, storeOnly, addressing, localResolver, uploadScope, uploadTracker, ct);
        }
        // 这个 try 圈住的不只是源文件读取，还有压缩、暂存和上传——所以异常类型本身不足以判定
        // "文件读不开"：BlobUploader 把 IOException 归为可重试的网络错误（BlobUploader.IsTransient），
        // 重试预算耗尽后它会原样抛出来，落进这里。仅凭类型收下它，一次 NAS 断网就会被记成
        // 若干个"文件不可读、沿用旧条目"，整轮备份照常报告成功——操作员看到的是"Backup succeeded,
        // 0 changed files"，而实际上什么都没传上去。因此 filter 里再探一次源文件：真读不开才降级，
        // 读得开就让异常照常向上抛，让整轮备份响亮地失败。
        // ArchiveMembersMissingException 是例外，不需要探测：它只在 7z 没把这个文件完整放进归档时
        // 抛出，已经是"本轮没能把这个文件存下来"的确证，而且抛在上传之前，云端不会留下空归档。
        catch (Exception ex) when (ex is ArchiveMembersMissingException
            || ((ex is IOException or UnauthorizedAccessException) && SourceUnreadable(localPath)))
        {
            // diff 时可读、随后（压缩/直传重新打开源文件时）才读不开：与 diff 阶段读不开同等处置——
            // 不产生 blob、不写 storageByPath/overrides（索引阶段据此沿用旧条目或整条缺席），
            // 只记一条复用既有通道的告警，绝不能让这一个文件拖垮整轮备份。
            await MarkPostDiffUnreadableAsync(request, file.Path, ex.Message, postDiffUnreadable, ct);
            onItem(file.Length);
            return;
        }

        // 碰撞告警是内容已成功处理/上传之后的事后上报，不再触碰源文件——绝不能留在上面的 try 里：
        // 否则这条通知（或其内部日志写入）失败会被误判成"文件读不开"，导致已经成功上传的内容
        // 在索引里被沿用旧条目或整条丢弃，而云端其实已经有这份数据。
        if (placement.Collision)
            await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Account.Id}/{request.Container}",
                $"Hash collision avoided: {file.Path}",
                $"Different content shares hash {placement.Content.FullHash}; stored at {placement.Ref}", ct);

        // 实际存下去的内容与 diff 时看到的不是同一份：以前者覆盖索引条目，保证 fullHash/长度/头尾 hash
        // 与 data/{hash} 里的字节一致。这些值全都来自刚才那一遍读，**不再重开源文件**。
        // file.FullHash 为空（diff 把全文 hash 延后给了这一遍读）时必然不等，于是照常写覆盖——
        // 索引里的 hash 因此永远来自"真正压进归档的那些字节"，而不是 diff 时看到的那一份。
        var content = placement.Content;
        if (content.FullHash != file.FullHash)
            overrides[file.Path] = new EntryOverride(
                content.FullHash, content.HeadHash, content.Length, content.Mtime);

        storageByPath[file.Path] = new StorageRef
        {
            Kind = "blob", Ref = placement.Ref, Volumes = Math.Max(1, placement.Volumes), Raw = content.Raw,
            VolumeSizes = [.. placement.VolumeSizes],
        };
        tailByPath[file.Path] = content.TailHash;

        await LogFileAsync(request, file.Path, ct);
        onItem(file.Length);
    }

    /// <summary>决定这份内容最终落在哪个 blob 上：先预筛探一次（命中去重就完全不压），
    /// 否则一遍读完成 hash + 压缩，再按算出来的 hash 判去重/碰撞避让并上传。</summary>
    private async Task<BlobPlacement> PlaceBlobAsync(
        BackupRequest request, PlannedFile file, string localPath, bool storeOnly,
        BlobAddressScheme addressing, LocalDedupResolver? localResolver,
        VolumeUploadScope uploadScope, StageTracker uploadTracker, CancellationToken ct)
    {
        var headBytes = request.Options.Diff.HeadHashBytes;
        var cc = factory.CreateServiceClient(request.Account).GetBlobContainerClient(request.Container);

        // 1. 预筛 + 探测。命中既有 blob 就到此为止：一个字节都不用压、不用传。
        BlobContent? probed = null;
        (string Ref, bool Collision)? probedPlacement = null;
        if (await ProbeForDedupAsync(file, localPath, headBytes, localResolver, ct) is { } p)
        {
            probed = p;
            if (localResolver is not null)
            {
                if (localResolver.TryFindExisting(p.FullHash, p.Length, p.HeadHash, p.TailHash) is { } prior)
                    return new BlobPlacement(prior.Ref, false, prior.Volumes, prior.VolumeSizes, p with { Raw = prior.Raw });
            }
            else
            {
                // 回退：导入未同步的备份没有本地权威索引，只能发云端 HEAD 比对元数据。
                var (refName, exists, collision, existingRaw) = await ResolveDataRefAsync(
                    cc, addressing, p.FullHash, p.Length, p.HeadHash, p.TailHash, ct);
                if (exists)
                    return new BlobPlacement(
                        refName, collision, await VolumeBlobIO.CountVolumesAsync(cc, refName, ct), [],
                        p with { Raw = existingRaw });
                probedPlacement = (refName, collision); // 空位已经问出来了，内容没变就别再问一遍
            }
        }

        // 2. 一遍读：边读边算三段 hash，边把字节喂进 7z（或直接拷成 raw 临时文件）。
        var (content, staged) = await StreamAndStageAsync(
            request, localPath, file.Path, storeOnly, headBytes, uploadTracker, ct);
        try
        {
            // 3. 压完才知道名字，此时才判去重与碰撞避让。
            if (localResolver is not null)
            {
                // 纯本地判定：跨版本查映射、同批经预约协调（同内容共享 ref/raw/卷数，不同内容避让）。不读云端。
                var res = await localResolver.ResolveAsync(
                    content.FullHash, content.Length, content.HeadHash, content.TailHash);
                if (res.Exists)
                {
                    var prior = res.Existing!;
                    return new BlobPlacement(res.Ref, res.Collision, prior.Volumes, prior.VolumeSizes,
                        content with { Raw = prior.Raw }); // 以既有 blob 的实际 raw 为准
                }
                try
                {
                    var (volumes, sizes) = await UploadStagedBlobAsync(
                        request, res.Ref, staged, content, addressing, uploadScope, uploadTracker, ct);
                    res.Complete(content.Raw, volumes, sizes); // 唤醒同批同内容的后到者，给它们相同存储信息
                    return new BlobPlacement(res.Ref, res.Collision, volumes, sizes, content);
                }
                catch (Exception ex)
                {
                    res.Fail(ex);   // 令等待者一并失败，绝不去重到未成功上传的 blob
                    throw;
                }
            }

            // 云端回退路径：探测时已经问出过一个空位。读到的内容与探测时一致（绝大多数情况）
            // 就直接用它——重复的 HEAD 除了在压缩与上传之间插进一段等待之外没有任何作用。
            string blobRef;
            bool cloudCollision;
            if (probedPlacement is { } known && probed is { } q && SameContent(q, content))
            {
                (blobRef, cloudCollision) = known;
            }
            else
            {
                var resolved = await ResolveDataRefAsync(
                    cc, addressing, content.FullHash, content.Length, content.HeadHash, content.TailHash, ct);
                if (resolved.Exists)
                    return new BlobPlacement(resolved.Ref, resolved.Collision,
                        await VolumeBlobIO.CountVolumesAsync(cc, resolved.Ref, ct), [],
                        content with { Raw = resolved.ExistingRaw });
                (blobRef, cloudCollision) = (resolved.Ref, resolved.Collision);
            }

            var (uploadedVolumes, uploadedSizes) = await UploadStagedBlobAsync(
                request, blobRef, staged, content, addressing, uploadScope, uploadTracker, ct);
            return new BlobPlacement(blobRef, cloudCollision, uploadedVolumes, uploadedSizes, content);
        }
        finally
        {
            // 去重命中时这份归档白压了，一样要立刻还给暂存区——它占着背压额度。
            staging.Release(staged);
        }
    }

    /// <summary>两次读到的是不是同一份内容（四项内容身份全等，与去重判定用的是同一个标准）。</summary>
    private static bool SameContent(BlobContent a, BlobContent b) =>
        a.FullHash == b.FullHash && a.Length == b.Length
        && a.HeadHash == b.HeadHash && a.TailHash == b.TailHash;

    /// <summary>
    /// 去重预筛：先只读文件头算 head hash，本地索引里连（长度 + head）都对不上就返回 null，
    /// 让调用方直接走一遍读的流式快路径；有候选才把整个文件读一遍算出完整内容身份。
    /// </summary>
    private async Task<BlobContent?> ProbeForDedupAsync(
        PlannedFile file, string localPath, int headBytes, LocalDedupResolver? localResolver, CancellationToken ct)
    {
        // 云端回退（导入未同步的备份）：没有本地索引可预筛，判去重只能发 HEAD，而 HEAD 要一个
        // 完整的内容身份。全文 hash 直接沿用 diff 已经算好的那个——为了预筛把文件再整个读一遍，
        // 会把这条路径上本来两遍的读变成三遍。内容若真在此期间变了，压完之后的比对会发现并重判。
        if (localResolver is null)
        {
            // diff 把全文 hash 延后了（单文件 blob 的常态）→ 手上没有内容身份，预筛无从谈起。
            // 返回 null 让调用方直接走一遍读的流式快路径，压完拿到真 hash 再判去重——那正是
            // 延后想要的效果：为了提前问一次 HEAD 而把 100 GB 再读一遍，比重压一次还亏。
            if (file.FullHash is null)
                return null;

            var stat = new FileInfo(localPath);
            return new BlobContent(
                file.FullHash,
                await hasher.HeadHashAsync(localPath, headBytes, ct),
                await hasher.TailHashAsync(localPath, headBytes, ct),
                stat.Length, new DateTimeOffset(stat.LastWriteTimeUtc), Raw: false);
        }

        var length = new FileInfo(localPath).Length;
        var head = await hasher.HeadHashAsync(localPath, headBytes, ct);
        var may = localResolver.MayDeduplicate(length, head);
        localResolver.NoteInFlight(length, head);
        return may ? await ReadContentIdentityAsync(localPath, headBytes, ct) : null;
    }

    /// <summary>把文件完整读一遍，一次算出 head/full/tail 三段 hash 与长度
    /// （分三次各读一遍是白付两趟 IO）。</summary>
    private static async Task<BlobContent> ReadContentIdentityAsync(
        string localPath, int segmentBytes, CancellationToken ct)
    {
        var mtime = new FileInfo(localPath).LastWriteTimeUtc;
        var streaming = new StreamingHasher(segmentBytes, segmentBytes);
        await using (var source = FileHasher.OpenRead(localPath))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
                streaming.Append(buffer.AsSpan(0, read));
        }
        return Identity(streaming, mtime, raw: false);
    }

    /// <summary>一遍读：源文件的字节同时流进 hasher 与归档（或 raw 临时文件）。
    /// 于是"算 hash 的字节"与"存下去的字节"按构造就是同一批。</summary>
    private async Task<(BlobContent Content, StagedItem Staged)> StreamAndStageAsync(
        BackupRequest request, string localPath, string entryName, bool storeOnly, int segmentBytes,
        StageTracker uploadTracker, CancellationToken ct)
    {
        // 开读之前先取一次元数据。长度用于判 raw；mtime 要**取读之前的那个**：文件若在读的过程中
        // 又被改写，记下更早的 mtime 会让下一轮 diff 认为它变过而重新检查（安全方向），
        // 记下更晚的则会让那份更新的内容此后再也不被备份（危险方向）。
        var before = new FileInfo(localPath);
        var mtime = before.LastWriteTimeUtc;

        // 原始直传（PRD 3.3.2）：不压缩(store-only) + 未加密 + 无需分卷 → 直接拷原文件，省一次 7z 封装。
        var raw = storeOnly && string.IsNullOrEmpty(request.Password)
            && (request.Options.VolumeBytes is not { } vb || before.Length <= vb);

        var streaming = new StreamingHasher(segmentBytes, segmentBytes);
        var name = StagedName(entryName);
        var staged = await staging.StageAsync(async (compressTemp, token) => raw
            ? [await CopyRawStreamingAsync(localPath, compressTemp, name, streaming, token)]
            : await CompressStreamingAsync(
                request, compressTemp, name, entryName, localPath, storeOnly, before.Length, streaming, token),
            ct, uploadTracker);

        return (Identity(streaming, mtime, raw), staged);
    }

    private static BlobContent Identity(StreamingHasher streaming, DateTime mtimeUtc, bool raw) => new(
        streaming.FullHash, streaming.HeadHash, streaming.TailHash, streaming.Length,
        new DateTimeOffset(mtimeUtc), raw);

    /// <summary>压缩临时区里的文件名。流式之前用的是内容 hash，而现在压完才知道 hash——
    /// 这个名字只在临时区里活几秒，唯一要求是同一时刻不重名（压缩全局串行，产出移出后才放锁）。</summary>
    private static string StagedName(string entryPath) =>
        "b" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(entryPath))).ToLowerInvariant()[..16];

    private static async Task<string> CopyRawStreamingAsync(
        string localPath, string compressTemp, string name, StreamingHasher streaming, CancellationToken ct)
    {
        var dest = Path.Combine(compressTemp, name);
        await using var source = FileHasher.OpenRead(localPath);
        await using var file = File.Create(dest);
        await using var sink = new HashingStream(streaming, file);
        await source.CopyToAsync(sink, ct);
        return dest;
    }

    private async Task<IReadOnlyList<string>> CompressStreamingAsync(
        BackupRequest request, string compressTemp, string archiveName, string entryName, string localPath,
        bool storeOnly, long expectedBytes, StreamingHasher streaming, CancellationToken ct)
    {
        var output = Path.Combine(compressTemp, archiveName + ".7z");
        var result = await compressor.CompressStreamAsync(
            new StreamCompressionRequest(entryName, output, request.Password,
                VolumeBytes: request.Options.VolumeBytes, StoreOnly: storeOnly, ExpectedBytes: expectedBytes),
            async (stdin, token) =>
            {
                await using var source = FileHasher.OpenRead(localPath);
                await using var sink = new HashingStream(streaming, stdin);
                await source.CopyToAsync(sink, token);
                return streaming.Length;
            }, ct);
        return result.VolumeFiles;
    }

    /// <returns>该 blob 的分卷数与各卷字节尺寸。</returns>
    private async Task<(int Volumes, IReadOnlyList<long> Sizes)> UploadStagedBlobAsync(
        BackupRequest request, string blobRef, StagedItem staged, BlobContent content,
        BlobAddressScheme addressing, VolumeUploadScope uploadScope, StageTracker uploadTracker, CancellationToken ct)
    {
        var sizes = staged.Files.Select(f => new FileInfo(f).Length).ToList();
        // 闸门与在途登记都下沉到了每一卷（VolumeUploadScope）；这里只标记"这件活进入上传段了"，
        // 好把它与还在压缩的那些区分开。
        uploadTracker.BeginUpload();
        try
        {
            var meta = new Dictionary<string, string>(
                addressing.Metadata(content.FullHash, content.Length, content.HeadHash, content.TailHash));
            if (content.Raw)
                meta["raw"] = "1";
            await VolumeBlobIO.UploadAsync(
                uploader, request.Account, request.Container, blobRef, staged.Files,
                request.DataTier, request.Options.Upload, ct, meta, uploadScope,
                onVolumeUploaded: staging.ReleaseFile);   // 传完一卷就把它从临时盘上撤掉
            return (staged.Files.Count, sizes);
        }
        finally
        {
            uploadTracker.EndUpload();
        }
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
    /// 处理**一箱**已封好的可分组小文件（§6/§9）：压缩 + 压缩后校验，压缩中变化的成员以稳定后的
    /// 新 hash 重新入队（自然进入下一箱），而非移出为单文件；仅当变大到超阈值、或反复变化达阈值
    /// 时才降级为单文件（后者报警）。
    /// <para>
    /// 封箱时机移到了 diff 那一侧（见 <see cref="GroupingPlanner.Classify"/> 与流水线）：从前这里
    /// 拿到的是"一个目录的全部可分组文件"，自己边取边装箱；现在拿到的就是装好的一箱，
    /// 因此箱与箱之间可以并发，而不必等同目录的上一箱传完。
    /// </para>
    /// </summary>
    private async Task ProcessPackAsync(
        BackupRequest request, IReadOnlyList<PlannedFile> pool, BlobAddressScheme addressing, LocalDedupResolver? localResolver,
        BackupInfoFile info, ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, string> tailByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, ConcurrentDictionary<string, string> postDiffUnreadable,
        VolumeUploadScope uploadScope, int[] packCounter, Action<long> onItem, StageTracker uploadTracker, CancellationToken ct)
    {
        var cap = request.Options.Plan.GroupCapBytes;
        var threshold = request.Options.Plan.SingleFileThresholdBytes;
        var headBytes = request.Options.Diff.HeadHashBytes;
        var maxAttempts = Math.Max(1, request.Options.ProcessingMaxAttempts);
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
            // 这些 PlannedFile 全部由 ToPlannedFile(PackEntry) 而来，FullHash 按构造非空——
            // 延后计算只发生在单文件 blob 上，那条路不产生 pack。
            var members = group.Select(f => new PackEntry(f.Path, f.Path, f.FullHash!, f.Length)).ToList();

            // 这份快照离 diff 可能已隔了几小时：封箱之后这个包还要在有界队列里排队，前面挤着多少
            // 活、消费者有几个，都不归它管。期间一个成员完全可能被删掉（构建产物）或被收回权限，
            // 而 Stat 会就此抛出，让整轮备份倒在与本分支所修完全相同的形状上。不另起机制：读不到
            // 就把快照记成 null，交给下面既有的"排除成员"路径处理（与"内容在压缩期间变了"同一条
            // 路：排除出归档 → 重取新内容 → 仍读不开则降级）。
            var before = members.ToDictionary(m => m.Path, m => TryStat(Local(request, m.Path)));
            var (staged, missing) = await CompressPackTolerantAsync(request, packId, members, uploadTracker, ct);

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
                var vols = await UploadStagedPackAsync(request, packId, staged!, uploadScope, uploadTracker, ct);
                RecordPack(request, packId, members, vols, info, storageByPath);
                foreach (var m in members) await LogFileAsync(request, m.Path, ct);
                onItem(bytes); // 销账用整组的原始字节：入队时申报的是整个池，池被拆成的每一组各销一份
                continue;
            }

            // 丢弃本次归档；稳定成员照常成 pack；变化成员以新 hash 处理。
            // staged 为 null 只可能是整组成员都被 7z 丢掉（连空归档都没留下），此时无物可释放。
            if (staged is not null)
                staging.Release(staged);
            var stable = members.Where(m => !changed.Contains(m)).ToList();
            if (stable.Count > 0)
            {
                var staged2 = await CompressPackAsync(request, packId, stable, uploadTracker, ct);
                var vols2 = await UploadStagedPackAsync(request, packId, staged2, uploadScope, uploadTracker, ct);
                RecordPack(request, packId, stable, vols2, info, storageByPath);
                foreach (var m in stable) await LogFileAsync(request, m.Path, ct);
            }
            // 无论这一组里有多少成员被排除出稳定 pack（内容变化、还是读不开)，这次分组迭代都对应
            // total 里预留的一个槽位，必须**恰好上报一次**——即便 stable.Count == 0（整组成员一起
            // 读不开，Finding 2 命中的最坏情形），否则 uploaded 永远追不上 total，完工也显示不了 100%。
            // 反过来，onItem() 放在这里而不是 foreach(changed) 内部的每个成员上，也避免了同一组里
            // 多个成员一起失败时被重复计数（该组只占一个槽位，不是每个成员各占一个）。
            // 剩余时间的销账同理：整组的原始字节一次记清，哪怕组里没剩下一个稳定成员——
            // 这一组的活确实做完了，工作量不销就永远悬在那里，剩余时间收不到 0。
            onItem(bytes);

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
                        storageByPath, tailByPath, overrides, postDiffUnreadable, uploadScope, static _ => { }, uploadTracker, ct);
                }
                else
                {
                    queue.Add(new PlannedFile(m.Path, newLen, newHash)); // 自然进入下一组
                }
            }
        }
    }

    /// <summary>
    /// 压缩一组成员，容忍 7z 静默丢弃读不了的成员：把被丢掉的剔除后重压，直到归档与成员集一致
    /// 或成员耗尽。返回归档（整组都读不了时为 null）与被丢掉的条目名。
    /// 不让 <see cref="ArchiveMembersMissingException"/> 直接冒出去——在本模块的既定语义里，
    /// 一个读不了的成员是"排除该成员"，不是"整轮备份失败"。
    /// </summary>
    private async Task<(StagedItem? Staged, IReadOnlySet<string> Missing)> CompressPackTolerantAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members,
        StageTracker uploadTracker, CancellationToken ct)
    {
        var remaining = members.ToList();
        var missing = new HashSet<string>(StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            try
            {
                return (await CompressPackAsync(request, packId, remaining, uploadTracker, ct), missing);
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
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members,
        StageTracker uploadTracker, CancellationToken ct)
    {
        var entries = members.Select(m => m.EntryName).ToList();
        return staging.StageAsync((compressTemp, token) => CompressAsync(
            request, compressTemp, packId, entries, storeOnly: false, token), ct, uploadTracker);
    }

    /// <returns>该 pack 各分卷的字节尺寸（按 .001..N 顺序；供记录，核验分卷完整性/尺寸用）。</returns>
    private async Task<IReadOnlyList<long>> UploadStagedPackAsync(
        BackupRequest request, string packId, StagedItem staged, VolumeUploadScope uploadScope,
        StageTracker uploadTracker, CancellationToken ct)
    {
        var sizes = staged.Files.Select(f => new FileInfo(f).Length).ToList(); // Release 前先取尺寸
        var blobName = $"packs/{packId}.7z";
        uploadTracker.BeginUpload();   // 闸门与在途登记见 VolumeUploadScope，都在每卷那一层
        try
        {
            await VolumeBlobIO.UploadAsync(
                uploader, request.Account, request.Container, blobName, staged.Files,
                request.DataTier, request.Options.Upload, ct, scope: uploadScope,
                onVolumeUploaded: staging.ReleaseFile);   // 传完一卷就把它从临时盘上撤掉
        }
        finally
        {
            uploadTracker.EndUpload();
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

}
