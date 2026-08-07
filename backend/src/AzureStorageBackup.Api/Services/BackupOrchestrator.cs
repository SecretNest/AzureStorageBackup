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
/// <param name="ChangedFiles">Added + Modified 的件数（与 <see cref="NewFiles"/> + <see cref="ModifiedFiles"/> 恒等）。</param>
/// <param name="ChangedBytes">上述文件的**源端原始**字节（未压缩、未去重）。</param>
public sealed record BackupRunResult(int Version, int ChangedFiles, long ChangedBytes, int UnreadableFiles)
{
    /// <summary>上一版本没有的文件数。</summary>
    public int NewFiles { get; init; }

    /// <summary>内容变了的文件数。</summary>
    public int ModifiedFiles { get; init; }

    /// <summary>上一版本有、本次没有的文件数。</summary>
    public int DeletedFiles { get; init; }

    /// <summary>
    /// 本轮真正推上云的字节（压缩/加密**之后**的归档尺寸）。去重命中的内容一个字节都不计——
    /// 它压根没经过上传那一步。与 <see cref="ChangedBytes"/> 一起看才知道压缩和去重各省下了多少。
    /// </summary>
    public long UploadedBytes { get; init; }

    /// <summary>备份收尾时那次保留清理删掉了什么（未触发清理时为 <see cref="CleanupReport.Empty"/>）。</summary>
    public CleanupReport Cleanup { get; init; } = CleanupReport.Empty;

    /// <summary>本次备份开始跑的时刻，与写进版本记录的 <see cref="BackupVersion.StartedAt"/> 同一个值。</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>版本提交时刻，与写进版本记录的 <see cref="BackupVersion.CreatedAt"/> 同一个值。
    /// **不是**本次运行结束的时刻：提交之后还有保留清理要跑。界面上完成提示与还原下拉都读这个值，
    /// 各取各的时钟就会对同一次备份写出两个不同时间。</summary>
    public DateTimeOffset CompletedAt { get; init; }
}

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
    ILocalIndexCache indexCache,
    TrackedInfoStore trackedInfo,
    INotifier? notifier = null,
    IOperationLog? opLog = null,
    VerboseFileLog? verboseLog = null,
    DiffWorkQueueFactory? spillFactory = null)
{
    /// <summary>
    /// 一次运行的可变状态：边跑边攒的计数，以及本轮的 pack 号发号器。按参数一路传下去
    /// 而不是做成实例字段：编排器在 DI 里是 scoped，单次请求内不会有第二轮备份共用它，但
    /// "每轮的账记在这一轮的对象上"这件事应当由签名保证，而不是靠注册方式碰巧成立。
    /// 多个上传消费者并发访问，故计数与发号都走 Interlocked。
    /// </summary>
    private sealed class RunState(StagingArea.StagingLease staging)
    {
        /// <summary>本次运行在暂存区的席位：暂存盘额度按**当前在跑的运行数**均分，席位随运行来去。</summary>
        public StagingArea.StagingLease Staging => staging;

        private long _uploadedBytes;
        private readonly string _packTag = Guid.NewGuid().ToString("N")[..8];
        private int _packSeq;

        /// <summary>
        /// 发一个新的 pack 号。必须**跨运行**唯一：pack 不像 data blob 那样内容寻址——名字里没有
        /// 内容的影子，光靠"接着信息文件里的最大号往下发"在上一次运行失败时就会重号。那一次
        /// 已经把 packs/p0001.7z 传上去了、却没能写成信息文件，于是下一次又从 p0001 发起，
        /// 而这个同号包装的是**另一批成员**。上传走 if-missing，撞上同名就跳过，索引却声称它含
        /// 这一次的成员——还原时从那个包里根本取不到，静默地少一批文件。Archive 数据层上更藏不住：
        /// 跳过的理由从"已存在"变成 BlobArchived，同样是跳过。
        /// <para>
        /// 每轮一个随机前缀就够了。pack 号的唯一要求本来就只是"不重"，没有任何地方依赖它连续或
        /// 有序：PackIdOf 只做前缀切分，死重压实按同名重写，索引里记的是全名。
        /// </para>
        /// </summary>
        public string NextPackId() => $"p{_packTag}{Interlocked.Increment(ref _packSeq):D4}";

        /// <summary>本轮真正推上云的字节（压缩后）。去重命中走的是 early-return，根本到不了这里。</summary>
        public long UploadedBytes => Interlocked.Read(ref _uploadedBytes);

        public void AddUploaded(long bytes) => Interlocked.Add(ref _uploadedBytes, bytes);

    }

    /// <summary>
    /// 没有配溢出目录时（单元测试、没给临时盘）的退路：纯内存、不设界。
    /// 配了溢出目录就用 <see cref="DiffWorkQueueFactory"/> 里那套额度，这个不参与。
    /// </summary>
    private static readonly DiffQueueLimits InMemoryOnlyLimits =
        new(MaxCachedItems: int.MaxValue, MaxCachedBytes: long.MaxValue);

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

    /// <summary>
    /// 这一条是不是"零长度的普通文件"——那种既不必压缩、也不必上传、更不该占内容寻址地址的条目。
    /// <para>
    /// 从前空文件照常走存储：压成一个**比原文件还大**的 7z 归档（0 → 131 字节），或在 store-only
    /// 且无密码时 raw 直传成一个 0 字节 blob。两种形态在云端是**完全不同的字节**，偏偏所有空文件
    /// 的 fullHash 一模一样，于是它们全挤在同一个 data/{hash} 上：谁先传完，谁就决定了后到者
    /// 索引里那个 raw 标志，而对不上的那一次还原会把 7z 归档本身当成文件内容写出来。
    /// 不进存储，这一整类问题就不存在了。
    /// </para>
    /// <para>
    /// 只认 <see cref="EntryKind.File"/>：symlink 的内容是索引里的 Target 字段，长度不代表它，
    /// 还原侧也另有分支（<c>Kind == "symlink"</c>），不该被这条规则顺手改掉行为。
    /// </para>
    /// </summary>
    private static bool IsEmptyFile(ScannedEntry entry) => entry.Kind == EntryKind.File && entry.Length == 0;

    public async Task<BackupRunResult> RunAsync(
        BackupRequest request, IProgress<BackupProgress>? progress = null, CancellationToken ct = default,
        BackupRunControl? control = null)
    {
        // 开始时刻在任何 I/O 之前取：这是操作员心里"这次备份几点开跑"的那一刻。
        var startedAt = DateTimeOffset.UtcNow;
        var source = $"backup:{request.Account.Id}/{request.Container}";
        await Record(NotificationEvents.BackupStart, source, $"Backup started: {request.Name}", request.Container, ct);
        try
        {
            var result = await RunCoreAsync(request, startedAt, progress, ct, control);
            // 排版与"零值省略"规则见 BackupSummary：那条消息同时进操作日志和 webhook 通知，
            // 是操作员一定会看的一条，所以本轮动了什么、云上多了多少、清掉了多少，都得在里面。
            await Record(NotificationEvents.BackupSuccess, source, $"Backup succeeded: {request.Name}",
                BackupSummary.Format(result), ct);
            return result;
        }
        // 必须排在下面这个通用 catch (Exception ex) 之前：BackupSuspendedException 也是 Exception，
        // 匹配顺序反了的话它会先被那条兜底逻辑接住，用户看到的就是一条"Backup failed"——
        // 而现场其实好端端保着，下次跑就能接上。
        catch (BackupSuspendedException ex)
        {
            // 走 BackupFailure 这个订阅频道，但级别降为 Warning：
            // 频道选它，是因为订阅"备份没跑完"的人要的正是这条消息，而为此新增一个通知事件位
            // 意味着所有已有用户默认都收不到——一个只在出事那天才发现的静默默认值。
            // 级别降下来，是因为这不是错误：Error 会让它长存进审计日志、在界面上顶着红字，
            // 而它其实是一个可以接着跑的中点。措辞里把"接下来该做什么"直说。
            await Record(NotificationEvents.BackupFailure, source, $"Backup suspended: {request.Name}",
                $"{ex.Message} Progress is saved; run this backup again to pick up where it stopped.",
                ct, OperationLogLevel.Warning);
            throw;
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

    private async Task Record(
        NotificationEvents evt, string source, string title, string body, CancellationToken ct,
        OperationLogLevel? level = null)
    {
        await _recordGate.WaitAsync(ct);
        try
        {
            if (opLog is not null)
                await opLog.AppendAsync(level ?? EventLog.LevelOf(evt), source, $"{title} — {body}", ct, durable: true);
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
        BackupRequest request, DateTimeOffset startedAt, IProgress<BackupProgress>? progress, CancellationToken ct,
        BackupRunControl? control = null)
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

        // 范围把所有文件都剔光了：diff 会把上一版本的一切判成删除，写出一个空版本。
        // 旧版本还在，不是数据丢失，但这一定是误操作（比如勾错了一层目录），不能安静地发生。
        // 没配范围时的空根是正常情况，不在此列。
        // 有读不出来的路径时也不在此列：一个掉线的 SMB/NFS 挂载点会让整棵子树进 Unreadable
        // 而不是 Entries/EmptyDirs（"读不开 ≠ 删除"），此时 Entries/EmptyDirs 皆空只说明
        // 挂载点没答应，与范围选得对不对无关——真把这种情况当成范围误配置报出来，
        // 会误导用户去改一个本来就对的范围，而真正的问题（挂载点没起来）反而被掩盖了。
        if (scan.Entries.Count == 0 && scan.EmptyDirs.Count == 0 && scan.Unreadable.Count == 0
            && !opts.Scan.Scope.IsAll)
            throw new InvalidOperationException(
                "The configured scope selects no files under the local root. "
                + "Nothing would be backed up, so this run was stopped. "
                + "Check the scope selection on this backup.");

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
            previous = await indexCache.ReadAsync(
                request.Account, request.Container, last.Version, identity, last.IndexBlob, password, ct);
        }

        // data blob 寻址方案：加密备份用密钥化地址防指纹识别（密钥从密码 + 信息文件里的盐派生）。
        var addressing = new BlobAddressScheme(password, info.Backup.KdfSalt);

        // 纯本地去重解析器：从本地缓存的保留版本索引建「内容身份→既有 blob」映射，备份时用它
        // 判断去重/碰撞/分卷/raw，**不发任何云端 HEAD**。这是唯一一条路——不论备份是本工具新建的
        // 还是从既有容器导入的：导入时就把每个版本的索引全部拉进了本地缓存（见 /import 端点），
        // 信息文件也一并落地（TrackedInfoStore.SeedFromCloudAsync）。
        //
        // 曾经还有一条"没有本地索引就发云端 HEAD 比对元数据"的回退路径，已删。没有本地权威时
        // 信任云端上躺着的东西本身就是危险的：不知道那些 blob 是谁写的、用的什么密码、内容还对不对，
        // 而一次误判的"已存在"就是静默地把一份从没传上去的文件记成备份完成。
        var indexes = new List<VersionIndex>(info.Versions.Count);
        var lastVer = info.Versions.LastOrDefault()?.Version;
        foreach (var v in info.Versions)
            indexes.Add(previous is not null && v.Version == lastVer
                ? previous
                : await indexCache.ReadAsync(request.Account, request.Container, v.Version, identity, v.IndexBlob, password, ct));
        var localResolver = LocalDedupResolver.Build(addressing, indexes);

        // journal 开卷：基线版本与寻址身份到这里才齐。恢复时靠这两样判断"这卷还作不作数"。
        if (control is not null)
            await control.OpenJournalAsync(
                request.Account.Id, request.Container, lastVer ?? 0, request.LocalRoot, addressing.Identity,
                startedAt, ct);

        // 3./4./5. Diff 与「装箱 + 压缩 + 上传」流水线化。
        // 从前这三段严格串行：Diffing 全部跑完 → Plan → Uploading。首次备份的 diff 要把每个文件
        // 完整读一遍算 hash，那几小时里网络一个字节都没在传。而 Plan 其实不必当这道全局屏障——
        // 归类只看路径与长度（见 GroupingPlanner.Classify），扫描一结束就已经定局。
        var packOptions = opts.Plan with
        {
            DontGroup = opts.DontGroup,
            CrossDirGroup = opts.CrossDirGroup,
            // 装箱要用它把每个目录切成压缩箱/不压箱两组——不接这一句，规则就只对单文件 blob 生效，
            // 被打包的小文件照旧整箱压（这正是本功能之前的缺陷）。
            DontCompress = opts.DontCompress,
        };
        var classification = planner.Classify(scan.Entries, packOptions);

        var storageByPath = new ConcurrentDictionary<string, StorageRef>(StringComparer.Ordinal);
        var tailByPath = new ConcurrentDictionary<string, string>(StringComparer.Ordinal); // 单文件 blob 的尾部 hash → 索引条目
        // 处理中内容变化的文件：以稳定后的新 hash/元数据覆盖 diff 时的索引条目（§9、PRD 特别说明 D）。
        var overrides = new ConcurrentDictionary<string, EntryOverride>(StringComparer.Ordinal);
        // diff 之后才读不开（压缩/上传阶段重新打开源文件时撞上）：与 diff 时就读不开的文件同等对待——
        // 不产生 blob、索引沿用旧条目、计入 UnreadableFiles，绝不能让整轮备份因此崩溃（M4 设计 §3 遗漏点）。
        var postDiffUnreadable = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        // 席位在整轮运行期间持有：暂存盘额度按当前持有席位的运行数均分，跑完即还。
        using var stagingLease = staging.AcquireLease();
        var state = new RunState(stagingLease);
        var reporter = new PipelineReporter(progress);
        // diff 不申报字节工作量，剩余时间按**件数**外推（见 StageTracker.Eta）：这个阶段的耗时
        // 主要摊在"每条至少 stat 一次"上，真要整个读一遍的只是少数变更文件——按字节推的话，
        // 一个没变的 100 GB 文件秒过，会让剩余时间当场塌掉一大截。
        var diffTracker = new StageTracker("Diffing", scan.Entries.Count, reporter.ReportDiff);
        // 上传的总数是**边跑边长出来的**（diff 还在往队列里塞活），先报 0＝未知：
        // 用一个还在涨的分母算百分比，会先冲到 100 再掉回去。工作量同理，随 Enqueue 一件件累加。
        // 速度只算"网线上有流"的那段时间：这个阶段大部分时间花在 7z 上，把压缩算进分母
        // 量出来的既不是传输速度也不是墙钟吞吐（见 StageTracker.SpeedNow）。
        // 待传池子的读数接进来：界面上那个"已压好、还没送出去"就是它减掉在途已传的部分。
        var uploadTracker = new StageTracker(
            "Uploading", total: 0, reporter.ReportUpload, speedWhileInFlight: true,
            stagedBytes: () => stagingLease.Bytes);
        // 「已传字节」走件级权威读数（RunState.UploadedBytes），不用按卷累加——它要和按件销账的
        // 原始字节摆在一起读，口径必须一致。这一句在第一件活完成前就宣告接管，原委见 SetTransferred。
        uploadTracker.SetTransferred(0);

        var totalItems = 0;
        var uploadedItems = 0;
        // work = 这一件活对应的**原始**字节。不能用实传字节当完成度：去重命中一个字节都不传，
        // 压缩率又随文件类型大幅摆动，拿它算剩余时间会随命中率和压缩率乱跳。
        void ReportItem(long work)
        {
            // 槽位计数归这里（它有"恰好一次"的既有约束）；tracker 只负责在途项与字节/测速。
            reporter.SetUploaded(Interlocked.Increment(ref uploadedItems));
            uploadTracker.Advance(0, work);
            // 已传字节与工作量在**同一时刻、同一件活**上落账，界面上那个百分比才读得成话。
            // 两条路径都在调到这里之前就 AddUploaded 过了（单文件在 return 前，一箱在
            // UploadStagedPackAsync 里），所以这个快照已经含上刚完成的这一件。
            uploadTracker.SetTransferred(state.UploadedBytes);
        }

        // 并发额度按**卷**发放，不按件（见 VolumeUploadScope）：一件活可能是一个大文件切出来的
        // 上千卷，按件发的话它整段只占一条流，设置里那个数字在传大文件时根本不起作用。
        var streams = Math.Max(1, opts.UploadConcurrency);
        using var uploadGate = new SemaphoreSlim(streams, streams);
        var uploadScope = new VolumeUploadScope(uploadGate, uploadTracker, streams);
        // 跨目录并发共享的 pack 号（内容寻址 data blob 不受影响；pack 号只需唯一）。
        // pack 号的分配收在 RunState 里（见 NextPackId）：它必须跨运行唯一。

        // 队列把两条流解耦：staged 满时挡住的只是压缩侧，diff 该读盘照样读盘——
        // 反压一路顶回 diff，磁盘就跟着停了，这次改造也就白做了。
        //
        // 写侧**永不阻塞**：内存装不下就落盘（见 DiffWorkQueue）。这一条是剩余时间能不能显示的
        // 前提——上传阶段的 ETA 要等 SetTotal 才算得出来，而那个总数只有 diff 跑完才确定。
        // 写侧一旦被队列挡住，diff 就只能跟着上传的节奏挪，"diff 收工"＝"只剩一个队列深度没做"，
        // 剩余时间要到整轮备份的尾巴上才肯出现。
        //
        // 关掉重叠那条路更需要落盘：那时根本没人在消费，所有活会一路攒到 diff 结束。
        var overlap = opts.OverlapDiffAndUpload;
        using var work = spillFactory?.Create() ?? new DiffWorkQueue(null, InMemoryOnlyLimits);

        // 上传侧出错要让 diff 停下来（继续读盘没有意义），但**不**打断已经在跑的其它上传——
        // 与从前 Task.WhenAll 的收场方式一致：在途的做完，再把第一个真实异常抛出去。
        using var stopProducing = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 一件活撞上瞬时错误就在闸门前等，放行了重试——但重试的**单位**两条路不同。
        //
        // 单文件：整件重试是安全的，每次从头读/压/暂存（PlaceBlobAsync 的 finally 会释放上一次的
        // 暂存物），分卷 if-missing 之前还会先清掉上一次尝试的残留卷。
        //
        // pack：一件活是**一整个池**，ProcessPackAsync 会把它切成若干组、每组各领一个包号。整件
        // 重试就等于把第 9 组的一次抖动退回到第 1 组，而且退回去之后领的是**新的**包号——前 8 组
        // 已经传上去的归档从此没有任何索引引用它，只在容器里占着地方；info.Packs 里也各留一条
        // 指向孤儿的记录。所以 pack 的重试下沉到组里（见 ProcessPackAsync），这里直接调。
        async Task RunItemAsync(WorkItem item, CancellationToken token)
        {
            if (item.Single is { } single)
                await WithPauseAsync(control, () => HandleBlobAsync(
                    request, single, addressing, localResolver, storageByPath, tailByPath,
                    overrides, postDiffUnreadable, uploadScope, ReportItem, uploadTracker, state, control, token), token);
            else
                await ProcessPackAsync(request, item.Pack!, item.StoreOnly, addressing, localResolver,
                    info, storageByPath, tailByPath, overrides, postDiffUnreadable, uploadScope, ReportItem,
                    uploadTracker, state, control, token);
        }

        async Task ConsumeAsync()
        {
            try
            {
                while (await work.DequeueAsync(ct) is { } item)
                {
                    // 领走一件活。从这里到 BeginUpload（压完、开始抢流的额度）之间是压缩与暂存，
                    // 一箱 100 MB 过 7z 可以几十秒——界面上得看得见这段，否则就是"什么都没在发生"。
                    uploadTracker.BeginWork();
                    try
                    {
                        await RunItemAsync(item, ct);
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
        // 本轮内跨箱的打包成员去重：同内容的后到者不入箱，只挂在首个之下，收尾统一回填。
        var aliasTable = new PackAliasTable();
        var dirPending = new Dictionary<string, List<PlannedFile>>(StringComparer.Ordinal);
        var dirRemaining = new Dictionary<string, int>(classification.DirectoryCandidates, StringComparer.Ordinal);
        // 跨目录那一路按可压缩性拆成两条独立的流水线（下标 0＝压缩箱，1＝不压箱）：一箱只能有
        // 一种压法，所以这一刀必须在装箱之前落。两条各自计数、各自封箱，互不影响对方的三条界。
        var crossPending = new List<PlannedFile>[] { [], [] };
        var crossBytes = new long[2];
        var crossPathBytes = new long[2];
        var changedFiles = 0;
        long changedBytes = 0;

        var reportedSpill = 0L;
        void Enqueue(WorkItem item)
        {
            Interlocked.Increment(ref totalItems);
            // 申报这件活的原始字节，作为剩余时间估算的工作量。完工时 ReportItem 会照同一个量
            // 销账（单文件按 Length，一箱按成员长度和），两边必须对得上，否则剩余量归不了零。
            uploadTracker.Enqueue(item.Single?.Length ?? item.Pack!.Sum(f => f.Length));

            // 永不阻塞：内存装不下就落盘。diff 因此能一路跑到底，SetTotal 才有机会早早落定。
            work.Enqueue(item);

            // 落了多少盘要说出来——它就是"diff 领先上传多少"的直接读数，而这一段
            // 从前是靠 CurrentItem 卡住不动来间接体现的。
            // 只在数变了才报：SetSpilled 要进发布锁，而正常规模下一件都不落盘，
            // 那样等于给 diff 的热路径上每件活都加一把没有用处的锁。
            var spilled = work.SpilledItems;
            if (spilled != reportedSpill)
            {
                reportedSpill = spilled;
                diffTracker.SetSpilled(spilled);
            }
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
            // 0 字节文件在这里就被挡下：它没有内容可存，索引条目里 Length==0 本身就是完备信息，
            // 还原据此直接建一个空文件（见 IsEmptyFile）。file 为 null 走的是与"这一条没有变更"
            // 完全相同的既有路径——目录计数照常递减、封箱时机不受影响。
            var file = changed && !IsEmptyFile(c.Current!)
                ? new PlannedFile(c.Path, c.Current!.Length, c.FullHash)
                : null;

            // 打包成员的文件级去重：这份内容已经躺在某个既有 pack 里 → 直接指过去，不装箱、不压、不传。
            // 只对要成组的条目做（单文件 blob 那条路自己有内容寻址去重）。
            //
            // 同一箱内的重复本来就被 7z 的 solid 归档消掉了（字典跨成员匹配），这里省下的是**跨箱、
            // 跨版本**那部分：不同箱之间压缩不共享字典，同一份内容会实打实地存两遍。
            //
            // 对已有备份是**只读**的：老索引一个字节都不改，只是多了一种命中可能。命中之后写下的
            // 引用形状（Kind=pack + Ref + EntryName）与从前逐字节相同，所以保留清理按 Ref 收集引用、
            // 死重压实按 EntryName 归组存活成员、还原按 EntryName 从归档里取成员——三处都不必改
            // （RetentionCleaner 那句"同内容不同路径去重成同 fullHash 但仍是两个成员"的注释早已
            // 预见了这一天）。
            if (file is not null && klass.Category != FileCategory.SingleFile
                && localResolver is not null
                && file.FullHash is { } packHash && c.HeadHash is { } packHead
                && localResolver.TryFindPackMember(packHash, file.Length, packHead, c.TailHash) is { } priorMember)
            {
                storageByPath[c.Path] = new StorageRef
                {
                    Kind = "pack", Ref = priorMember.PackId, EntryName = priorMember.EntryName,
                };
                // 之后走的是与"这一条没有变更内容"完全相同的既有路径：目录计数照常递减、封箱时机
                // 不受影响、不占上传槽位也不必销账。
                file = null;
            }

            // 本轮内、跨箱的成员去重。上面那一档查的是**既有版本**的包（_packMembers 只从历史索引
            // 构建），本轮新封的箱不在其中——于是首次备份、或一次新增大量重复小文件时，同内容一旦
            // 被分进不同的箱就实打实地各存一份（不同箱之间压缩不共享字典，省不下来）。
            //
            // 后到者不入箱，只挂在首个之下；它最终指向哪个包要等消费者收工才知道（leader 可能在
            // 压缩窗口里被改写、可能读不开、可能变大到改走单文件 blob），所以回填放在收尾统一做。
            // 判断只看最终态，这里因此一个并发原语都不需要。
            //
            // 顺序上不会与上面那一档打架：leader 若命中既有包，后来的同内容文件用同一张表、同一套
            // 四项判据也会命中，根本走不到这里。所以进这张表的 leader 一定是"本轮新装箱的"。
            //
            // 这是**路径说明，不是安全约束**——两档对调也产不出错数据（那时先到者成了本轮 leader，
            // 但它随后仍会命中跨版本档拿到既有包的 StorageRef，收尾回填原样复制给别名，两种顺序
            // 得到的索引逐字节相同）。写在这里是免得有人以为对调会出错而不敢动，也免得有人以为
            // 这个顺序是安全所必需的。当前这个顺序的理由只是少一层间接。
            //
            // 沉默的例外：别名不入箱，意味着它的源文件本轮不会被第二次打开——"每个打包成员在
            // 压缩后都会被重校验"这条既有不变量，对别名不成立。它在压缩窗口里被改写或删除，
            // 本轮都察觉不到。索引依然自洽：存的是 leader 在 diff 时刻的内容 = 别名在 diff 时刻
            // 的内容，条目写的也是 diff 时刻的 hash/mtime——不是丢数据，下一轮 mtime 一变自然
            // 重备，但这个沉默的例外必须写出来。
            if (file is not null && klass.Category != FileCategory.SingleFile
                && file.FullHash is { } aliasHash && c.HeadHash is { } aliasHead && c.TailHash is { } aliasTail
                && aliasTable.TryClaim(aliasHash, file.Length, aliasHead, aliasTail, c.Path))
            {
                // 与上面那一档收场完全相同：走"这一条没有变更"的既有路径。
                // storageByPath 留到收尾回填——现在还不知道 leader 会落在哪个包上。
                file = null;
            }

            switch (klass.Category)
            {
                case FileCategory.SingleFile:
                    // 单文件：判定一出来立刻走流式压缩上传，不等任何人。
                    if (file is not null)
                        Enqueue(new WorkItem(file, null));
                    return;

                case FileCategory.CrossDirectoryGroup:
                    // 扫描结果按 ordinal 路径序排好，与跨目录装箱用的是同一个序，因此"边 diff 边填、
                    // 填满即封"得到的包，与"等全部 diff 完再一次装箱"逐字节相同。
                    if (file is not null)
                    {
                        // 先分流再装箱。分流之后每一条内部**仍是 ordinal 路径序**（扫描序过滤不改相对
                        // 顺序），恰好等于规划器 SplitByCompressibility「先分两组、组内按路径排序」的
                        // 结果——这正是两边能对上的理由，动其中任何一侧的排序都会破掉它。
                        var storeOnly = packOptions.DontCompress?.MatchesFileOrAncestorDir(file.Path) ?? false;
                        var side = storeOnly ? 1 : 0;

                        // 三条界共用 GroupingPlanner.GroupIsFull：这一处与规划器那个纯函数、
                        // 以及压缩前的重新切分必须口径完全一致，否则"实际产出与规划器一致"那条
                        // 不变量就破了（PipelinedBackupTests 正是拿纯函数当基准在守它）。
                        if (crossPending[side].Count > 0
                            && GroupingPlanner.GroupIsFull(
                                crossPending[side].Count, crossBytes[side], crossPathBytes[side], file, packOptions))
                        {
                            Enqueue(new WorkItem(null, crossPending[side], storeOnly));
                            crossPending[side] = [];
                            crossBytes[side] = 0;
                            crossPathBytes[side] = 0;
                        }
                        crossPending[side].Add(file);
                        crossBytes[side] += file.Length;
                        crossPathBytes[side] += GroupingPlanner.EntryArgBytes(file.Path);

                        // 装满即封，不等下一个文件来推。上面那个判断问的是"再收下它会不会越界"，
                        // 非有下一个文件不可；而成员数与路径字节两条与下一个是谁无关（见
                        // GroupTakesNoMore），箱子在这一刻就已经定局。等下去的代价按扫描顺序算：
                        // 后面若长期没有跨目录候选（比如接下来全是走单文件的大文件），这一箱要
                        // 一路挂到 diff 收尾才被兜底封上，白等整个差分。分箱结果不受影响——
                        // 这条成立时，下一个文件必然也会让 GroupIsFull 成立。
                        if (GroupingPlanner.GroupTakesNoMore(crossPending[side].Count, crossPathBytes[side], packOptions))
                        {
                            Enqueue(new WorkItem(null, crossPending[side], storeOnly));
                            crossPending[side] = [];
                            crossBytes[side] = 0;
                            crossPathBytes[side] = 0;
                        }
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
                        // 按可压缩性分箱也在它里面完成，所以这个目录可能一次封出两箱（各一种压法）。
                        foreach (var pack in planner.Plan(members, packOptions).Packs)
                            Enqueue(new WorkItem(null, [.. pack.Members.Select(ToPlannedFile)], pack.StoreOnly));
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

                // 收尾：把还没填满的箱子封掉。跨目录那两条各自可能有剩；按目录的理论上都已在
                // 计数归零时封过，这里只是不留活口。
                for (var side = 0; side < crossPending.Length; side++)
                    if (crossPending[side].Count > 0)
                        Enqueue(new WorkItem(null, crossPending[side], StoreOnly: side == 1));
                // 兜底这一支也过规划器：它攒的是**未经装箱**的原始列表，直接封成一箱既会混进两种
                // 压法，也不受三条界约束（成员数/路径字节撑爆 argv 就是 E2BIG）。走 Plan 才与
                // 正常路径同一个口径。
                foreach (var leftover in dirPending.Values.Where(m => m.Count > 0))
                    foreach (var pack in planner.Plan(leftover, packOptions).Packs)
                        Enqueue(new WorkItem(null, [.. pack.Members.Select(ToPlannedFile)], pack.StoreOnly));
            }
            finally
            {
                diffTracker.Complete();
                work.CompleteAdding(); // 无论如何都要让消费者知道"没有更多活了"，否则它们永远等下去
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

        // 本轮内跨箱去重的收尾：把挂在各 leader 身上的别名回填成与 leader 相同的 StorageRef。
        // 放在这里而不是命中当时，是因为判断只看**最终态**——leader 会不会在压缩窗口里被改写、
        // 会不会读不开、会不会变大到改走单文件 blob，只有消费者全部收工才知道。于是装箱侧
        // 一个并发原语都不需要，也不存在"diff 刚挂上一个别名、消费者已经把 leader 判死"的竞态。
        var orphanAliases = new List<PlannedFile>();
        foreach (var (leaderPath, aliases) in aliasTable.AliasesByLeader)
        {
            // 两条真实路径 + 一条冗余保险，对应 leader 走岔的所有情形：
            //   overrides 有它            → 内容在压缩窗口里变过，写下的是新 hash；
            //   storage 不是 pack 或缺失  → 变大到超阈值改走了单文件 blob，或整组一起读不开。
            //   postDiffUnreadable 有它   → 今天**不可达**：leader 被 MarkPostDiffUnreadableAsync
            //     标记时必然已经被排除出稳定 pack，RecordPack 从没为它写过 storageByPath，
            //     上面那条"storage 缺失"已经完全覆盖这个情形。留着是零成本的冗余保险——
            //     防将来这两件事被解耦（比如 postDiffUnreadable 独立出一条不经过 storageByPath
            //     的路径）之后，这里悄悄失守。
            // 任一命中，别名的内容就已经**不等于** leader 最终存下去的那份了——绝不能指过去，
            // 那会让索引指向别人的内容，还原出来是错数据。
            //
            // 这三条判据的前提是 overrides / postDiffUnreadable / storageByPath 三张表在本轮内
            // **全程只增不删**（现状确实如此——代码里没有任何一处对它们 Remove/TryRemove）。
            // 一旦将来有人加一次删除（比如"重试成功后把失败标记清掉"），一个走岔的 leader 在
            // 收尾时会看起来完好，别名照样指过去，还原出来是别人的内容——而且不会有任何测试
            // 变红，因为现有测试钉的是"三张表当前的状态"，不钉"它们会不会被删过东西"。改这
            // 三张表的写入方式之前，先想清楚这里的假设还成不成立。
            var leaderStorage = storageByPath.GetValueOrDefault(leaderPath);
            if (leaderStorage is { Kind: "pack" }
                && !overrides.ContainsKey(leaderPath)
                && !postDiffUnreadable.ContainsKey(leaderPath))
            {
                // 整个 StorageRef 原样复制：Ref 与 EntryName 都是 leader 的，形状与 RecordPack
                // 从前写的逐字节相同，保留清理/死重压实/还原/检查因此都不必改。
                foreach (var a in aliases)
                    storageByPath[a.Path] = leaderStorage;
            }
            else
            {
                orphanAliases.AddRange(aliases.Select(a => new PlannedFile(a.Path, a.Length, a.FullHash)));
            }
        }

        // 悬空别名：leader 走岔了，但它们自己好好的，不该被连累。重新跑一遍，第一个自然成为新
        // leader。它们之间不再互相去重——这条路要求 leader 恰好在压缩窗口内被改写或读不开。
        //
        // "本来就罕见"这个前提要打个折扣：NAS 上一个共享中途掉线，那棵子树里的 leader 会**成批**
        // 变成 postDiffUnreadable，挂在它们身上、分布在别处、活得好好的别名会**成批**悬空，
        // 然后在下面这个循环里被**串行**重跑——不一定是一小段，可能是一整棵子树。
        //
        // 进度取舍比"界面停在 100%"更狠：uploadTracker.BeginWork()/EndWork() 没有包住这段重跑
        // （那两个只在上面 ConsumeAsync 里配对出现），onItem 传的是下面这个空操作，ReportItem
        // 不会跑，SetTransferred 也就不会被调用——重跑上传的字节，在读数上直到下面
        // uploadTracker.Complete() 之前**完全不可见**，在途件数也一直是 0。界面停在 100% 静默
        // 跑很久，对这个用户群（多在 NAS 上、拿不到命令行）是最容易被误判成"卡死"的形状。
        //
        // 这段故意不包 try：包了并 catch 掉，BuildEntries 会给这些别名产出 Length > 0 且
        // Storage == null 的条目（Added 的 CarriedStorage 是 null，storageByPath 又没有它）——
        // 那才是真正的静默丢数据形状。让它抛是对的：一轮备份失败、不写索引，孤儿 pack 交给
        // 保留清理回收，下轮重来。不写下来，将来一定有人"顺手加个 try"。
        //
        // onItem 传 static _ => { }：Enqueue 是"一个 WorkItem 一次"，而 ProcessPackAsync 内部按
        // GroupIsFull 拆出几组是它自己决定的，外部无法预先申报对应的次数，手动补分母只会算错。
        // 零进零出，配对天然平衡。先例见上面 changed 成员改走单文件 blob 那一处。
        //
        // storeOnly 按**别名自己的路径**算，与装箱时同一个写法：规则按路径匹配，别名和 leader
        // 分属不同目录时压法完全可能不同，而一箱只能有一种压法。
        foreach (var side in orphanAliases.ToLookup(
                     f => packOptions.DontCompress?.MatchesFileOrAncestorDir(f.Path) ?? false))
        {
            // 按 ordinal 路径序排一下，与文件那条路的排序纪律保持一致（见 crossPending 附近的
            // 注释）：悬空重跑是独立的一次 ProcessPackAsync 调用，不受"组内仍是 ordinal 路径序"
            // 那条不变量约束（AliasesByLeader 的枚举序是"leader 首次出现序"，别名在其内按插入序，
            // 整体是交错的），这里排纯粹是为了与文件那条路的纪律一致——影响的是 solid 压缩率和
            // 分组切点，不是正确性。
            var pool = side.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            await ProcessPackAsync(request, pool, side.Key, addressing, localResolver, info,
                storageByPath, tailByPath, overrides, postDiffUnreadable, uploadScope, static _ => { },
                uploadTracker, state, control, ct);
        }

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
        var completedAt = DateTimeOffset.UtcNow;
        info.Versions.Add(new BackupVersion
        {
            Version = version,
            CreatedAt = completedAt,
            StartedAt = startedAt,
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

        // 索引已提交，journal 使命完成。必须删在清理之前：留着它，清理会以为这些内容还"在途"而不敢动；
        // 删得比信息文件提交还早，则会出现两边都不认的空档，刚传上去的内容会被当成孤儿删掉。
        if (control is not null)
            await control.CompleteAsync();

        // 10. Cleanup（按保留策略清理超期版本及其独占数据，§10）
        progress?.Report(new BackupProgress(BackupStage.CleaningUp, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
        var cleanup = await cleaner.CleanupAsync(request.Account, request.Container, password, new CleanupOptions
        {
            Retention = request.Options.Retention,
            DataTier = request.DataTier,
            VolumeBytes = request.Options.VolumeBytes,
            DeadWeightThreshold = request.Options.DeadWeightThreshold,
            LocalRoot = request.LocalRoot,
            AllowRepackDownload = request.Options.AllowRepackDownload,
            // 收尾顺带压实用**本轮自己的**席位：另取一个会让均分的分母虚高，把并行的其它备份额度算小。
        }, info, ct, stagingLease);

        progress?.Report(new BackupProgress(BackupStage.Completed, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));

        // 各分类各数一遍，折进同一次遍历：50 万条目的索引上，每多走一趟 Count(…) 就是多 50 万次
        // 委托调用，而这些数字全都在同一个列表里。
        var newFiles = 0;
        var modifiedFiles = 0;
        var deletedFiles = 0;
        var unreadableFiles = 0;
        foreach (var c in diff.Changes)
        {
            switch (c.Kind)
            {
                case ChangeKind.Added: newFiles++; break;
                case ChangeKind.Modified: modifiedFiles++; break;
                case ChangeKind.Deleted: deletedFiles++; break;
                case ChangeKind.Unreadable: unreadableFiles++; break;
                default: break;   // MetadataOnly / Unchanged：这一轮什么都没动，不进摘要
            }
        }

        return new BackupRunResult(version, diff.ChangedFiles, diff.ChangedBytes,
            unreadableFiles + postDiffUnreadable.Count)
        {
            // 刻意**不**从新增/变更里扣除 post-diff 才发现读不开的文件：扣了就会出现
            // "340 changed" 而 "128 + 209 ≠ 340" 的账，得对着源码才看得懂。读不开的单独成项，
            // 谁都能自己把账算平（见 BackupSummaryTests）。
            NewFiles = newFiles,
            ModifiedFiles = modifiedFiles,
            DeletedFiles = deletedFiles,
            UploadedBytes = state.UploadedBytes,
            Cleanup = cleanup,
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };
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
        BackupRequest request, PlannedFile file, BlobAddressScheme addressing, LocalDedupResolver localResolver,
        ConcurrentDictionary<string, StorageRef> storageByPath, ConcurrentDictionary<string, string> tailByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, ConcurrentDictionary<string, string> postDiffUnreadable,
        VolumeUploadScope uploadScope, Action<long> onItem, StageTracker uploadTracker, RunState state,
        BackupRunControl? control, CancellationToken ct)
    {
        var localPath = Local(request, file.Path);
        var storeOnly = request.Options.DontCompress?.MatchesFileOrAncestorDir(file.Path) ?? false;

        BlobPlacement placement;
        try
        {
            placement = await PlaceBlobAsync(
                request, file, localPath, storeOnly, addressing, localResolver, uploadScope, uploadTracker, state, ct);
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

        // 实际存下去的内容与 diff 时看到的不是同一份：以前者覆盖索引条目，保证 fullHash/长度/头尾 hash
        // 与 data/{hash} 里的字节一致。这些值全都来自刚才那一遍读，**不再重开源文件**。
        // file.FullHash 为空（diff 把全文 hash 延后给了这一遍读）时必然不等，于是照常写覆盖——
        // 索引里的 hash 因此永远来自"真正压进归档的那些字节"，而不是 diff 时看到的那一份。
        var content = placement.Content;

        // journal：上传（或 if-missing 命中）已经确认返回，这块内容此刻确实在云上了，现在才敢记。
        // 顺序不能动——先记后传就会记下一块并不存在的内容，下次恢复直接跳过它，那是数据丢失。
        // 放在下面的碰撞告警**之前**：告警要打数据库和 webhook，是一次与这条记录无关的 I/O，
        // 失败了不该连累 journal——journal 追加只是几十字节的本地写，成本比告警低得多，
        // 而且是下一次运行真正要靠它判断"这块内容要不要重传"的东西，不能因为无关的失败而丢失。
        // 传的是 CancellationToken.None，不是这次运行的 ct：Task 9 会取消同一个 ct 来挂起/取消
        // 运行，而这一刻上传早已确认，云上已经有这块内容了——取消这个写入不会撤销任何东西，
        // 只会让下次恢复以为这块没传过、白白重传一次。半截写的风险也一样：write 被取消可能截断
        // 这一行，下次拼接进新 journal 时把它连同下一条一起解析坏掉。
        if (control is not null)
            await control.RecordBlobAsync(
                file.Path, placement.Ref, content.FullHash, content.HeadHash, content.TailHash, content.Length,
                Math.Max(1, placement.Volumes), content.Raw, [.. placement.VolumeSizes], CancellationToken.None);

        // 碰撞告警是内容已成功处理/上传之后的事后上报，不再触碰源文件——绝不能留在上面的 try 里：
        // 否则这条通知（或其内部日志写入）失败会被误判成"文件读不开"，导致已经成功上传的内容
        // 在索引里被沿用旧条目或整条丢弃，而云端其实已经有这份数据。
        if (placement.Collision)
            await Record(NotificationEvents.UnrecoverableError, $"backup:{request.Account.Id}/{request.Container}",
                $"Hash collision avoided: {file.Path}",
                $"Different content shares hash {content.FullHash}; stored at {placement.Ref}", ct);

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
        BlobAddressScheme addressing, LocalDedupResolver localResolver,
        VolumeUploadScope uploadScope, StageTracker uploadTracker, RunState state, CancellationToken ct)
    {
        var headBytes = request.Options.Diff.HeadHashBytes;

        // 1. 预筛 + 探测。命中既有 blob 就到此为止：一个字节都不用压、不用传。
        if (await ProbeForDedupAsync(file, localPath, headBytes, localResolver, uploadTracker, ct) is { } p
            && localResolver.TryFindExisting(p.FullHash, p.Length, p.HeadHash, p.TailHash) is { } prior)
        {
            return new BlobPlacement(prior.Ref, false, prior.Volumes, prior.VolumeSizes, p with { Raw = prior.Raw });
        }

        // 2. 一遍读：边读边算三段 hash，边把字节喂进 7z（或直接拷成 raw 临时文件）。
        var (content, staged) = await StreamAndStageAsync(
            request, localPath, file.Path, storeOnly, headBytes, uploadTracker, state, ct);
        try
        {
            // 3. 压完才知道名字，此时才判去重与碰撞避让。
            // 纯本地判定：跨版本查映射、同批经预约协调（同内容共享 ref/raw/卷数，不同内容避让）。不读云端。
            var res = await localResolver.ResolveAsync(
                content.FullHash, content.Length, content.HeadHash, content.TailHash, uploadTracker);
            if (res.Exists)
            {
                var existing = res.Existing!;
                return new BlobPlacement(res.Ref, res.Collision, existing.Volumes, existing.VolumeSizes,
                    content with { Raw = existing.Raw }); // 以既有 blob 的实际 raw 为准
            }
            try
            {
                var (volumes, sizes) = await UploadStagedBlobAsync(
                    request, res.Ref, staged, content, addressing, uploadScope, uploadTracker, state,
                    file.Path, ct);
                res.Complete(content.Raw, volumes, sizes); // 唤醒同批同内容的后到者，给它们相同存储信息
                return new BlobPlacement(res.Ref, res.Collision, volumes, sizes, content);
            }
            catch (Exception ex)
            {
                res.Fail(ex);   // 令等待者一并失败，绝不去重到未成功上传的 blob
                throw;
            }
        }
        finally
        {
            // 去重命中时这份归档白压了，一样要立刻还给暂存区——它占着背压额度。
            staging.Release(staged);
        }
    }

    /// <summary>
    /// 去重预筛：先只读文件头算 head hash，本地索引里连（长度 + head）都对不上就返回 null，
    /// 让调用方直接走一遍读的流式快路径；有候选才把整个文件读一遍算出完整内容身份。
    /// </summary>
    /// <remarks>
    /// 整段登记为「读盘核对」（<see cref="StageProgress.Checking"/>）：命中候选时这里要把整个文件
    /// 读一遍，一个几 GB 的文件在 NAS 上就是几十秒，期间既不推字节也不等任何东西——不报出来的话
    /// 屏幕上是一动不动的 "1 object starting upload"，而它连压缩都还没开始。
    /// </remarks>
    private async Task<BlobContent?> ProbeForDedupAsync(
        PlannedFile file, string localPath, int headBytes, LocalDedupResolver localResolver,
        StageTracker uploadTracker, CancellationToken ct)
    {
        uploadTracker.BeginChecking();
        try
        {
            var length = new FileInfo(localPath).Length;
            var head = await hasher.HeadHashAsync(localPath, headBytes, ct);
            var may = localResolver.MayDeduplicate(length, head);
            localResolver.NoteInFlight(length, head);
            return may ? await ReadContentIdentityAsync(localPath, headBytes, ct) : null;
        }
        finally
        {
            // 必须是 finally：这一路会抛（文件读不开、被取消），漏掉一次配对，这一栏就在余下的
            // 运行里卡在虚高的数字上——preparing 在这个项目里正是这么栽过一次（见 StagingArea）。
            uploadTracker.EndChecking();
        }
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
        StageTracker uploadTracker, RunState state, CancellationToken ct)
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
            state.Staging, ct, uploadTracker);

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
        BlobAddressScheme addressing, VolumeUploadScope uploadScope, StageTracker uploadTracker, RunState state,
        string sourceLabel, CancellationToken ct)
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
            await ClearLeftoverVolumesAsync(request, blobRef, staged.Files.Count, uploadTracker, ct);
            await VolumeBlobIO.UploadAsync(
                uploader, request.Account, request.Container, blobRef, staged.Files,
                request.DataTier, request.Options.Upload, ct, meta, uploadScope,
                onVolumeUploaded: staging.ReleaseFile,   // 传完一卷就把它从临时盘上撤掉
                label: sourceLabel);                     // 界面上显示源文件路径，不是内容寻址的 blob 名
            // 记在整件传完之后：中途失败会把整轮备份带失败，那时这个数字根本不会被用到，
            // 而按卷边传边记会让一次重试把同一批字节记两遍。
            state.AddUploaded(sizes.Sum());
            return (staged.Files.Count, sizes);
        }
        finally
        {
            uploadTracker.EndUpload();
        }
    }

    /// <summary>
    /// 多卷归档上传前，先抹掉这个地址上可能残留的旧卷。
    /// <para>
    /// 走到这里意味着本地权威判定"这个 ref 上不该有东西"。云端却仍可能有：上一次运行传到一半
    /// 倒了（收尾的写索引/写信息文件是最后才做的，那批已落地的卷因此既不在任何索引里也不在本地
    /// 状态里），或者**本轮**这一件活撞上瞬时错误、在挂起闸门前等过一轮又重来了一次。
    /// 上传是 if-missing 的（<see cref="IBlobUploader.UploadIfMissingAsync"/> 用 If-None-Match
    /// 交给服务端判），已经在的卷会被跳过——于是云上那一族卷成了**两次压缩的混合体**。
    /// </para>
    /// <para>
    /// 从前这里对不加密的备份直接早退，依据是"同样的输入配同样的参数，7z 压出来的卷逐字节相同"。
    /// 这条依据对单文件 blob 那条路**不成立**，实测（7-Zip 26.00）：
    /// </para>
    /// <para>
    /// 单文件走的是 <c>-si</c> 从 stdin 读（<see cref="CompressStreamingAsync"/>），而我们喂给它的
    /// stdin 是一根**管道**。7z 拿不到源文件的 mtime，就把归档成员的 kMTime 属性写成**压缩那一刻**
    /// 的时间。两次压缩因此差在：末卷里的 8 字节 FILETIME，以及首卷 32 字节签名头里那两个覆盖
    /// 尾部头的 CRC。压缩数据本身逐字节相同——可正因为首卷的 CRC 校验的是末卷的头，把第一次的
    /// 首卷和第二次的末卷拼在一起，7z 直接 <c>Headers Error / Can't open as archive</c>。
    /// 索引却声称这个 blob 好好的：静默的数据损坏，而不是少传一次。
    /// （对照组：pack 那条路按**文件名**压，mtime 取自磁盘上的文件，两次产出确实逐字节相同——
    /// 见 SevenZipDeterminismTests。加密则两条路都不确定：AES 每次换随机 salt/IV。）
    /// </para>
    /// <para>
    /// 所以判据只剩"是不是多卷"，不再问加不加密。单卷不必清：它是一份完整、自洽的归档，
    /// 跳过它与传一遍新的结果一致。多卷才有"半旧半新"这种拼不起来的形状，而多卷都是大文件，
    /// 一次列举加几次删除相对于要传的字节数可以忽略——反过来，对每个新 blob 都先列一遍，
    /// 首次备份就是几十万次白问。
    /// </para>
    /// </summary>
    private async Task ClearLeftoverVolumesAsync(
        BackupRequest request, string blobRef, int volumeCount, StageTracker uploadTracker, CancellationToken ct)
    {
        if (volumeCount <= 1)
            return;

        // 登记在早退之后：单卷时这里什么都不做，那种情况下在屏幕上闪一栏出来纯属噪声。
        // 严格说这一段查的是云上的卷而不是本地文件，仍归进「核对」那一栏——单给它一栏不值当，
        // 要说的就一件事：这件活正在核对，不在传。
        uploadTracker.BeginChecking();
        try
        {
            var cc = factory.CreateServiceClient(request.Account).GetBlobContainerClient(request.Container);
            await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, blobRef, ct))
            {
                // 按前缀列举会连带捞到碰撞避让的兄弟（data/{hash}~1 及其分卷），那是**另一份内容**、
                // 由别的索引条目引用着，误删就是真丢数据。IsVolumeOf 只认这个归档自己的卷。
                if (VolumeBlobIO.IsVolumeOf(blobRef, b.Name))
                    await cc.GetBlobClient(b.Name).DeleteIfExistsAsync(cancellationToken: ct);
            }
        }
        finally
        {
            uploadTracker.EndChecking();
        }
    }

    private async Task<EntryOverride> BuildOverrideAsync(
        string localPath, string fullHash, int headBytes, CancellationToken ct)
    {
        var info = new FileInfo(localPath);
        var head = await hasher.HeadHashAsync(localPath, headBytes, ct);
        return new EntryOverride(fullHash, head, info.Length, new DateTimeOffset(info.LastWriteTimeUtc));
    }

    /// <summary>
    /// 把一段活放到挂起闸门后面跑：撞上瞬时错误就在闸门前等，放行了原样重来一遍，直到成功、
    /// 或者闸门失去耐心把这轮运行降级为挂起。
    /// <para>
    /// <paramref name="body"/> 就是重试的**单位**，所以它必须整段可重入：重来一遍不能留下上一遍
    /// 的半成品，也不能把同一件事记两次。单文件 blob 传的是一整件，pack 传的是一**组**——
    /// 两个调用点因此传进来的东西大小差着一个量级，但闸门那套等法完全一样，不该抄第二遍。
    /// </para>
    /// </summary>
    /// <param name="ct">**运行本身**那个取消令牌，不是别的。瞬时判据要拿它区分"网络抖了一下"和
    /// "用户按了取消"——传错了的话，取消会被当成抖动吞掉，按钮就静悄悄失效了。</param>
    private static async Task WithPauseAsync(BackupRunControl? control, Func<Task> body, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await body();
                // 一段活干成了就把连败清零：闸门的耐心是"从第一次不顺算起还没好过"，
                // 中间成功过一次却不清零的话，几个钟头里零星抖几下也会攒够耐心把运行判挂起。
                control?.Gate.ReportSuccess();
                return;
            }
            catch (Exception ex) when (control is not null && TransientErrors.IsTransient(ex, ct))
            {
                if (!await control.Gate.WaitAsync(ex, ct))
                    throw new BackupSuspendedException(SuspendReason.AutoSuspended, ex.Message);
            }
        }
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
    /// <param name="storeOnly">这一箱的压法，装箱时按可压缩性定死并随箱传到这里（见 <see cref="WorkItem"/>）。
    /// 这里**不重新推导**：规则按路径匹配，而进来的一箱按定义已经是同质的，重推一遍只会多一处
    /// 可能与规划器走岔的判断。切分产生的每一小组、以及成员变化后重压的那一组，都沿用同一个值。</param>
    private async Task ProcessPackAsync(
        BackupRequest request, IReadOnlyList<PlannedFile> pool, bool storeOnly,
        BlobAddressScheme addressing, LocalDedupResolver localResolver,
        BackupInfoFile info, ConcurrentDictionary<string, StorageRef> storageByPath,
        ConcurrentDictionary<string, string> tailByPath,
        ConcurrentDictionary<string, EntryOverride> overrides, ConcurrentDictionary<string, string> postDiffUnreadable,
        VolumeUploadScope uploadScope, Action<long> onItem, StageTracker uploadTracker,
        RunState state, BackupRunControl? control, CancellationToken ct)
    {
        var plan = request.Options.Plan;
        var threshold = plan.SingleFileThresholdBytes;
        var headBytes = request.Options.Diff.HeadHashBytes;
        var maxAttempts = Math.Max(1, request.Options.ProcessingMaxAttempts);
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new List<PlannedFile>(pool);

        while (queue.Count > 0)
        {
            // 取出目录中未处理、不越界的一组（至少一个）。三条界共用 GroupIsFull——这是交给 7z
            // 之前的最后一道，其中 MaxPackPathBytes 那条直接决定 argv 会不会撑爆（E2BIG）。
            var group = new List<PlannedFile>();
            long bytes = 0;
            long pathBytes = 0;
            var take = 0;
            while (take < queue.Count)
            {
                var f = queue[take];
                if (group.Count > 0 && GroupingPlanner.GroupIsFull(group.Count, bytes, pathBytes, f, plan)) break;
                group.Add(f); bytes += f.Length; pathBytes += GroupingPlanner.EntryArgBytes(f.Path); take++;
            }
            queue.RemoveRange(0, group.Count);

            // 包号在重试**之外**领，一组一个，领定了就不再变。放进重试里的话，闸门每放行一次
            // 这一组就换一个号：上一次尝试已经传上去的卷再没有任何索引引用得到，只在容器里占着
            // 地方，info.Packs 里还各留一条指向孤儿的记录。
            var packId = state.NextPackId();
            // 这些 PlannedFile 全部由 ToPlannedFile(PackEntry) 而来，FullHash 按构造非空——
            // 延后计算只发生在单文件 blob 上，那条路不产生 pack。
            var members = group.Select(f => new PackEntry(f.Path, f.Path, f.FullHash!, f.Length)).ToList();

            // 「压这一组 + 传上去」是重试的单位：抖一下就把这一组从头做一遍，前面几组不受牵连。
            // 整段可重入——包号不变，所以重压的产出盖回同一族卷（传之前先清残留，见
            // UploadStagedPackAsync）。journal append 与 oplog 写**不**在重试单位之内（见下方调用处的
            // 注释）：它们发生在云端已确认之后，重来一遍只会把上传字节和索引成员表算重/算错，而不是
            // 让"这一组"重新可重入。变化成员的重新入队也留在外面：那会动 queue 和 attempts，
            // 重来一遍就会把同一个成员排两次队、把重试次数记重。
            async Task<(List<PackEntry> Changed, IReadOnlyList<PackEntry> Recorded, IReadOnlyList<long> Volumes)> AttemptAsync()
            {
                // 这份快照离 diff 可能已隔了几小时：封箱之后这个包还要在有界队列里排队，前面挤着多少
                // 活、消费者有几个，都不归它管。期间一个成员完全可能被删掉（构建产物）或被收回权限，
                // 而 Stat 会就此抛出，让整轮备份倒在与本分支所修完全相同的形状上。不另起机制：读不到
                // 就把快照记成 null，交给下面既有的"排除成员"路径处理（与"内容在压缩期间变了"同一条
                // 路：排除出归档 → 重取新内容 → 仍读不开则降级）。
                // 逐成员 stat：一箱几百个成员，在 NAS 上不是白干的。与压缩后那一遍同报「读盘核对」。
                uploadTracker.BeginChecking();
                Dictionary<string, (long Mtime, long Length, int Mode)?> before;
                try
                {
                    before = members.ToDictionary(m => m.Path, m => TryStat(Local(request, m.Path)));
                }
                finally
                {
                    uploadTracker.EndChecking();
                }
                var (staged, missing) = await CompressPackTolerantAsync(
                    request, packId, members, storeOnly, uploadTracker, state, ct);
                // 这一箱的归档由本次迭代持有：本轮怎么结束都还回去。下面从这里到用完之间有一整段
                // 会抛的代码（压缩后重校验里那次重算 hash，取消时抛的 OperationCanceledException 不在
                // 那层 catch 的收集范围里），从前一穿出去这份账就永远挂在单例上了——而它是产出的背压
                // 闸门，攒够就把所有运行的压缩一起卡住。用完仍会立刻显式 Release，这里只兜异常路径。
                using var held = staging.Hold(staged);

                // 被 7z 丢出归档的成员必须**直接**判为排除，不能指望下面的比对发现：那段比对看的是
                // 元数据与内容 hash，而权限被收回并不改 mtime/length——比对会说"这个成员没变"，
                // 于是一个缺成员的 pack 就被原样上传，索引却声称它在里面。
                var changed = members.Where(m => missing.Contains(m.EntryName)).ToList();

                // 压缩后重校验：元数据变且内容 hash 变 → 该成员在压缩期间变化。
                //
                // 整段登记为「读盘核对」：逐成员 stat 已经不便宜，撞上一个变过的大成员还要把它整读一遍
                // 重算 hash。这一段跑在出了暂存段、还没登记任何在途卷的时候，一个进度事件都不发——
                // 不报出来的话屏幕上就是几十秒不动的 "1 object starting upload"。
                uploadTracker.BeginChecking();
                try
                {
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
                }
                finally
                {
                    uploadTracker.EndChecking();
                }

                if (changed.Count == 0)
                {
                    var vols = await UploadStagedPackAsync(
                        request, packId, staged!, uploadScope, uploadTracker, state, members.Count, ct);
                    return (changed, members, vols);   // 空表：这一组干干净净地成了一个 pack
                }

                // 丢弃本次归档；稳定成员照常成 pack；变化成员以新 hash 处理。
                // staged 为 null 只可能是整组成员都被 7z 丢掉（连空归档都没留下），此时无物可释放。
                if (staged is not null)
                    staging.Release(staged);
                var stable = members.Where(m => !changed.Contains(m)).ToList();
                if (stable.Count > 0)
                {
                    var staged2 = await CompressPackAsync(request, packId, stable, storeOnly, uploadTracker, state, ct);
                    var vols2 = await UploadStagedPackAsync(
                        request, packId, staged2, uploadScope, uploadTracker, state, stable.Count, ct);
                    return (changed, stable, vols2);
                }
                return (changed, [], []);   // 整组成员都被判为变化/读不开：没有稳定成员可记
            }

            List<PackEntry> changedMembers = [];
            IReadOnlyList<PackEntry> recordedMembers = [];
            IReadOnlyList<long> recordedVolumes = [];
            await WithPauseAsync(control, async () =>
                (changedMembers, recordedMembers, recordedVolumes) = await AttemptAsync(), ct);

            // journal append 与 oplog 写挪到这里、退出重试单位之后：上面 AttemptAsync 一旦成功返回，
            // 云端已经确认了这次上传，闸门不会再让这一组重来——RecordPackAsync/LogFileAsync 因而
            // 只会跑这一次，不会像挪进去之前那样，因为它们自己抛出瞬时错误（比如本地盘 IOException）
            // 而触发整组重压：重压会把已传的字节在 state.AddUploaded 里算第二遍（速度/ETA 失真），
            // 单卷 pack 还会被 UploadIfMissing 当"已存在"跳过，导致这次重压出的新 Members/VolumeSizes
            // 记进索引，而容器里躺着的还是上一次的归档——两者从此对不上，只有 check/repair 才会发现。
            // 整组成员都读不开、没有任何东西可记（recordedMembers 为空）时自然跳过，不必特判。
            if (recordedMembers.Count > 0)
            {
                await RecordPackAsync(
                    request, packId, recordedMembers, recordedVolumes, storeOnly, info, storageByPath, control, ct);
                foreach (var m in recordedMembers) await LogFileAsync(request, m.Path, ct);
            }

            // 无论这一组里有多少成员被排除出稳定 pack（内容变化、还是读不开)，这次分组迭代都对应
            // total 里预留的一个槽位，必须**恰好上报一次**——即便 stable.Count == 0（整组成员一起
            // 读不开，Finding 2 命中的最坏情形），否则 uploaded 永远追不上 total，完工也显示不了 100%。
            // 反过来，onItem() 放在这里而不是 foreach(changed) 内部的每个成员上，也避免了同一组里
            // 多个成员一起失败时被重复计数（该组只占一个槽位，不是每个成员各占一个）。
            // 剩余时间的销账同理：整组的原始字节一次记清，哪怕组里没剩下一个稳定成员——
            // 这一组的活确实做完了，工作量不销就永远悬在那里，剩余时间收不到 0。
            //
            // 也正因为它在重试**之外**：这一组抖了几次、重压了几遍，账都只销一次。放进去的话，
            // 一次抖动就让 uploaded 多涨一格，最后越过 total，速度和剩余时间跟着一起失真。
            onItem(bytes);

            foreach (var m in changedMembers)
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
                    // 闸门是另一回事：它不吞异常，只是撞上瞬时错误时等一等再把**同一件**活重来一遍。
                    // 这件活是从池子里掉出来的一个单文件，与消费循环里那条单文件路径同一个形状，
                    // 因此也用同一个重试单位——不放进闸门的话，这一件抖一下就能带倒整轮备份。
                    await WithPauseAsync(control, () => HandleBlobAsync(
                        request, new PlannedFile(m.Path, newLen, newHash), addressing, localResolver,
                        storageByPath, tailByPath, overrides, postDiffUnreadable, uploadScope, static _ => { },
                        uploadTracker, state, control, ct), ct);
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
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, bool storeOnly,
        StageTracker uploadTracker, RunState state, CancellationToken ct)
    {
        var remaining = members.ToList();
        var missing = new HashSet<string>(StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            try
            {
                return (await CompressPackAsync(request, packId, remaining, storeOnly, uploadTracker, state, ct), missing);
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
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, bool storeOnly,
        StageTracker uploadTracker, RunState state, CancellationToken ct)
    {
        var entries = members.Select(m => m.EntryName).ToList();
        return staging.StageAsync((compressTemp, token) => CompressAsync(
            request, compressTemp, packId, entries, storeOnly, token),
            state.Staging, ct, uploadTracker);
    }

    /// <returns>该 pack 各分卷的字节尺寸（按 .001..N 顺序；供记录，核验分卷完整性/尺寸用）。</returns>
    private async Task<IReadOnlyList<long>> UploadStagedPackAsync(
        BackupRequest request, string packId, StagedItem staged, VolumeUploadScope uploadScope,
        StageTracker uploadTracker, RunState state, int memberCount, CancellationToken ct)
    {
        var sizes = staged.Files.Select(f => new FileInfo(f).Length).ToList(); // Release 前先取尺寸
        var blobName = $"packs/{packId}.7z";
        uploadTracker.BeginUpload();   // 闸门与在途登记见 VolumeUploadScope，都在每卷那一层
        try
        {
            // 与单文件 blob 同一条纪律（见 ClearLeftoverVolumesAsync）：多卷才做，做的是不让
            // 这一族卷混进上一次尝试的产物。pack 号本轮唯一，所以残留只可能来自**本轮自己的**
            // 重试——而重试正是挂起闸门每次放行都要走的那条路。包的成员按文件名压，两次产出
            // 通常逐字节相同，但"通常"不是能拿来赌数据的东西：成员的 mtime 在两次尝试之间变过
            // （内容没变，因此重校验不会把它排除）就足以让归档头不同，拼起来一样打不开。
            await ClearLeftoverVolumesAsync(request, blobName, staged.Files.Count, uploadTracker, ct);
            await VolumeBlobIO.UploadAsync(
                uploader, request.Account, request.Container, blobName, staged.Files,
                request.DataTier, request.Options.Upload, ct, scope: uploadScope,
                onVolumeUploaded: staging.ReleaseFile,   // 传完一卷就把它从临时盘上撤掉
                // 一箱装着几百个文件，列不下——报包号与成员数。
                label: $"pack {packId} ({memberCount} files)");
            state.AddUploaded(sizes.Sum());   // 时机同单文件路径：整件传完才记
        }
        finally
        {
            uploadTracker.EndUpload();
            staging.Release(staged);
        }
        return sizes;
    }

    /// <param name="storeOnly">这一箱的压法，记进 <see cref="PackInfo.StoreOnly"/>。死重压实与修复重压会
    /// 重写同一个 packId 的归档，那时手上只有存活成员和一个包号、没有当初那份规则——不记在包上，
    /// 一个 store-only 包挨过一次版本退役就被重压成默认压法了，而且没有任何征兆。</param>
    private static async Task RecordPackAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, IReadOnlyList<long> volumeSizes,
        bool storeOnly, BackupInfoFile info, ConcurrentDictionary<string, StorageRef> storageByPath,
        BackupRunControl? control, CancellationToken ct)
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
            StoreOnly = storeOnly,
        };
        lock (info.Packs)
            info.Packs[packId] = packInfo;

        // journal：pack 已经传完确认。成员表要记全，恢复时得靠它重建 PackInfo——
        // 信息文件是最后才提交的，崩溃时它里面根本没有这个包。
        // 同样传 CancellationToken.None：这次运行的 ct 是 Task 9 挂起/取消要取消的那一个，
        // 而此刻整箱已经在云上确认了，取消这个写入救不回任何东西，只会让下次恢复以为
        // 这箱没传过、白白重传一次；写到一半被取消还可能留下半截行，拖累下一条记录。
        if (control is not null)
            await control.RecordPackAsync(
                packId,
                [.. members.Select(m => new JournalMember(m.Path, m.EntryName, m.FullHash, m.Length))],
                volumeSizes, storeOnly, CancellationToken.None);
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
            var kind = c.Current.Kind == EntryKind.File ? "file" : "symlink";
            // 按**最终写进索引的**长度判，而不是 diff 时看到的：内容在处理中缩成空文件时，
            // 覆盖条目（override）才是这一条的真相。
            var length = ov?.Length ?? c.Current.Length;
            entries.Add(new IndexEntry
            {
                Path = c.Path,
                Kind = kind,
                Length = length,
                Mtime = ov?.Mtime ?? c.Current.ModifiedAt,
                Permissions = c.Current.Permissions,
                HeadHash = ov?.HeadHash ?? c.HeadHash,
                // 尾部 hash 的优先级：本次上传的单文件 blob 用压缩那一遍算得的值（最权威——那是
                // 真正压进归档的字节）；否则用 diff 算出来的；再否则继承上一版本条目。
                // 中间这一档是新加的：打包成员从前一项都没有，于是只能按三项去重，与单文件 blob
                // 那条路的四项判据不一致。diff 现在给未变文件也补算（见 BackupDiffer.UnchangedAsync），
                // 所以老备份跑一轮就把这一项补齐了。
                TailHash = tailByPath.GetValueOrDefault(c.Path) ?? c.TailHash ?? c.Previous?.TailHash,
                FullHash = ov?.FullHash ?? c.FullHash,
                Target = c.Current.Target,
                // 零长度的普通文件一律不带存储引用——包括**从上一版本沿用来的**那些。
                // 老备份里的空文件是照常压缩上传过的，而一个从不变化的空文件（.gitkeep、
                // __init__.py、锁文件……）每轮都判 Unchanged，CarriedStorage 会把那条老引用
                // 一代代传下去：若它当初就记错了 raw 标志，用户没有任何理由去碰这个文件，
                // 它也就永远好不了。在这里断掉，下一次备份即自愈，老 blob 随后由保留清理回收。
                Storage = kind == "file" && length == 0
                    ? null
                    : storageByPath.GetValueOrDefault(c.Path) ?? c.CarriedStorage,
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

}
