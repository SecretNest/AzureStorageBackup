using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 备份完整性检查（M5、PRD 2.3），分级、双轴：
/// 云端轴 <see cref="CloudCheckLevel"/>（不查 / 元数据比对 / 存在+尺寸 / 下载重算 hash）；
/// 本地轴 <see cref="LocalCheckLevel"/>（不查 / 存在+尺寸+权限 / 内容 hash）。
/// 本地内容一致（可修复）＝修复的判据；结果按文件给出云端/本地状态，供修复与还原替代。
/// </summary>
public sealed class BackupChecker(
    IBlobClientFactory factory,
    IBackupInfoStore store,
    IFileCompressor? compressor = null,
    IFileHasher? hasher = null,
    string? tempRoot = null,
    INotifier? notifier = null,
    IOperationLog? opLog = null,
    TrackedInfoStore? trackedInfo = null)
{
    /// <summary>测试注入的毫秒时间源，原样转给内部经 <see cref="Track"/> 建的每一个
    /// <see cref="StageTracker"/>（见其上同名字段的注释；与 <see cref="RestoreOrchestrator.Clock"/>
    /// 是同一形状的镜像件）。生产为 null，走真实墙钟。用来让"下载结束就摘掉在途标记、
    /// 解压/算 hash 期间不再算在途"这类时序断言摆脱 200ms 节流窗口——注入后每次查询时间都
    /// 保证前进，节流因此永不生效，每一次状态变化都会被发布出来，断言不必赌真实时钟是否
    /// 恰好跨过节流窗口。</summary>
    internal Func<long>? Clock { get; init; }

    /// <param name="onProgress">
    /// 阶段进度回调（可空）。检查此前完全没有进度：内容级要把整个备份下载重算 hash，
    /// 跑几小时是常态，界面上却只有一个转圈——分不清是在查还是挂死了。
    /// </param>
    public async Task<CheckReport> CheckAsync(
        Account account, string container, string? password, int? version, CheckOptions options, string? localRoot = null,
        CancellationToken ct = default, int downloadConcurrency = 5, Action<StageProgress>? onProgress = null)
    {
        var source = $"check:{account.Id}/{container}";
        await Record(NotificationEvents.CheckStart, source, $"Check started: {container}", "", ct);
        try
        {
            var report = await CheckCoreAsync(account, container, password, version, options, localRoot, downloadConcurrency, onProgress, ct);
            var problems = report.Findings.Count(f => f.Cloud == CloudState.MissingOrBad);
            await Record(
                report.Ok ? NotificationEvents.CheckSuccess : NotificationEvents.CheckFailure, source,
                $"Check {(report.Ok ? "passed" : "failed")}: {container}",
                report.Ok
                    ? $"{report.Findings.Count} file(s) OK"
                    : $"{problems} problem(s), {report.RepairablePaths.Count} repairable from local", ct);
            return report;
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
            await opLog.AppendAsync(EventLog.LevelOf(evt), source, $"{title} — {body}", ct, durable: true);
        if (notifier is not null)
            await notifier.NotifyAsync(evt, title, body, ct);
    }

    /// <summary>阶段跟踪器的构造捷径：没人要进度就一路传 null，不产生任何开销。</summary>
    /// <param name="inFlight">这个阶段会不会登记在途项。只有会的（Verifying）才让测速时钟
    /// 随流启停；不会的（本地/列举/元数据）必须走墙钟，否则虚拟时钟永不前进、速度恒为 0。</param>
    private StageTracker? Track(
        Action<StageProgress>? onProgress, string stage, int total, bool inFlight = false) =>
        onProgress is null ? null : new StageTracker(stage, total, onProgress, inFlight) { Clock = Clock };

    private async Task<CheckReport> CheckCoreAsync(
        Account account, string container, string? password, int? version, CheckOptions options, string? localRoot,
        int downloadConcurrency, Action<StageProgress>? onProgress, CancellationToken ct)
    {
        // 索引里有多少条目，要读完索引才知道 → 总数给 0，界面显示「… so far」而不是一个假百分比。
        var loading = Track(onProgress, "LoadingIndex", 0);
        loading?.Touch(container);

        var info = await store.ReadInfoAsync(account, container, password, ct)
            ?? throw new InvalidOperationException("No backup found in container.");
        if (info.Versions.Count == 0)
            throw new InvalidOperationException("Backup has no versions.");

        var ver = version is { } v
            ? info.Versions.FirstOrDefault(x => x.Version == v)
              ?? throw new InvalidOperationException($"Version {v} not found.")
            : info.Versions[^1];

        var index = await store.ReadIndexAsync(account, container, ver.IndexBlob, password, ct);
        loading?.Advance(0);
        loading?.Complete();

        string? metaIssue = null;
        if (options.Cloud == CloudCheckLevel.Metadata)
        {
            var meta = Track(onProgress, "Metadata", 1);
            metaIssue = await CheckMetadataDriftAsync(account, container, password, info, ct);
            meta?.Advance(0);
            meta?.Complete();
        }

        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);

        // 云端状态（按文件）：只在 ExistenceSize/Content 级实际查数据 blob。
        var cloudBad = new HashSet<string>(StringComparer.Ordinal);
        if (options.Cloud >= CloudCheckLevel.ExistenceSize)
            cloudBad = await CloudCheckAsync(cc, info, index, options, password, downloadConcurrency, onProgress, ct);

        // 本地轴：逐条目对源文件比对。Content 级要把每个文件完整读一遍算 hash，
        // 和备份的 Diffing 一样慢，同样必须逐条报进度。
        var localTracker = Track(onProgress, "Local", index.Entries.Count);
        var findings = new List<FileFinding>(index.Entries.Count);
        foreach (var e in index.Entries)
        {
            localTracker?.Touch(e.Path);
            var refName = e.Storage is { } s ? BlobNameOf(s) : null;
            // 零长度的普通文件在云端**本就不该有**对应对象（备份侧不给它产生存储引用，
            // 见 BackupOrchestrator.IsEmptyFile）。报 NotChecked 会让一整列空文件看起来像是
            // 检查漏掉了它们；它们的云端状态是确定的，就是没问题。
            var cloud = e.Storage is null && e.Kind == "file" && e.Length == 0
                ? CloudState.Ok
                : options.Cloud < CloudCheckLevel.ExistenceSize || e.Storage is null
                    ? CloudState.NotChecked
                    : cloudBad.Contains(e.Path) ? CloudState.MissingOrBad : CloudState.Ok;
            var local = await LocalCheckAsync(e, localRoot, options.Local, ct);
            findings.Add(new FileFinding(e.Path, refName, cloud, local) { UnreadableAt = e.UnreadableAt });
            // 字节只在真的读了文件时才算，否则 Attributes/None 级会报出一个天文数字的"速度"。
            localTracker?.Advance(options.Local == LocalCheckLevel.Content ? e.Length : 0);
        }
        localTracker?.Complete();

        var orphans = options.ListOrphans
            ? await ListOrphansAsync(cc, account, container, password, info, onProgress, ct)
            : [];

        return new CheckReport(ver.Version, findings, metaIssue) { OrphanBlobs = orphans };
    }

    /// <summary>
    /// 云端列表检查（§4.8）：枚举 container 全部 blob 减去引用集 = 孤儿。构不出**完整**引用集
    /// （缺版本索引且云端读失败）→ 放弃列举、记 Warning、返回空（绝不据不完整信息把被引用 blob 当孤儿）。
    /// </summary>
    private async Task<IReadOnlyList<string>> ListOrphansAsync(
        BlobContainerClient cc, Account account, string container, string? password, BackupInfoFile info,
        Action<StageProgress>? onProgress, CancellationToken ct)
    {
        HashSet<string> referenced;
        try
        {
            referenced = await BuildReferencedSetAsync(account, container, password, info, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (opLog is not null)
                await opLog.AppendAsync(OperationLogLevel.Warning, $"check:{account.Id}/{container}",
                    $"Orphan detection skipped: could not build the full reference set ({ex.Message}).", ct, durable: true);
            return [];
        }

        // 容器里有多少 blob 只能边列边知道 → 总数 0，报"已列举多少"。
        var listing = Track(onProgress, "Orphans", 0);
        var orphans = new List<string>();
        await foreach (var b in cc.GetBlobsAsync(cancellationToken: ct))
        {
            listing?.Touch(b.Name);
            if (!referenced.Contains(b.Name))
                orphans.Add(b.Name);
            listing?.Advance(0);
        }
        listing?.Complete();
        return orphans;
    }

    /// <summary>
    /// 构造全部保留版本引用的 blob 名集合：读全部版本的第二级索引（本地权威 store），再调纯函数
    /// <see cref="ReferencedBlobNames"/>。任一版本索引读不到（本地缺且云端读失败）会抛出——调用方据此放弃删除。
    /// </summary>
    public async Task<HashSet<string>> BuildReferencedSetAsync(
        Account account, string container, string? password, BackupInfoFile info, CancellationToken ct = default)
    {
        var indexes = new Dictionary<int, VersionIndex>();
        foreach (var ver in info.Versions)
            indexes[ver.Version] = await store.ReadIndexAsync(account, container, ver.IndexBlob, password, ct);
        return ReferencedBlobNames(info, indexes);
    }

    /// <summary>
    /// **纯函数**：给定信息文件 + 全部保留版本索引，返回一切被引用的 blob 名（删除孤儿的承重安全依据）。涵盖：
    /// 信息文件（明文 + 加密两种命名都保护）；每个版本的 <c>IndexBlob</c>；每个 <see cref="StorageRef"/> 的**全部分卷**
    /// （单文件 blob 按 <see cref="StorageRef.Volumes"/>；pack 按 <see cref="PackInfo.Volumes"/>）——跨全部版本，
    /// 含仅被旧版本引用者。pack 被引用却在 <c>info.Packs</c> 缺元数据 → 无法确定分卷数 → 抛错（迫使放弃删除）。
    /// </summary>
    public static HashSet<string> ReferencedBlobNames(BackupInfoFile info, IReadOnlyDictionary<int, VersionIndex> indexes)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal)
        {
            // 信息文件：两种命名都纳入引用集，任何情况下都不当孤儿删除。
            BackupDiscovery.IndexBlobName,
            BackupDiscovery.EncryptedIndexBlobName,
        };

        // 每个版本的第二级索引 blob（即便某版本索引未在 indexes 中提供，其名也须保护）。
        foreach (var v in info.Versions)
            refs.Add(v.IndexBlob);

        // 每个版本索引的每个存储引用的全部分卷。
        foreach (var idx in indexes.Values)
            foreach (var e in idx.Entries)
            {
                if (e.Storage is not { } s)
                    continue;
                var baseName = BlobNameOf(s);
                var volumes = s.Kind == "pack"
                    ? info.Packs.TryGetValue(s.Ref, out var pi)
                        ? pi.Volumes
                        : throw new InvalidOperationException(
                            $"Pack '{s.Ref}' is referenced but missing from info.Packs; cannot determine its volumes.")
                    : s.Volumes;
                foreach (var name in VolumeBlobIO.VolumeNames(baseName, volumes))
                    refs.Add(name);
            }

        return refs;
    }

    /// <summary>
    /// 云端数据检查，返回**云端已坏的文件路径集**。ExistenceSize：每个 blob/分卷 HEAD 验存在+尺寸；
    /// Content：在此基础上对可读 blob 下载重算 hash（Archive 未活化则跳过，不误判为坏）。
    /// </summary>
    private async Task<HashSet<string>> CloudCheckAsync(
        BlobContainerClient cc, BackupInfoFile info, VersionIndex index, CheckOptions options, string? password,
        int downloadConcurrency, Action<StageProgress>? onProgress, CancellationToken ct)
    {
        var bad = new HashSet<string>(StringComparer.Ordinal);

        // 按 blob 归组（blobName → 该 blob 的条目 + 期望分卷数/尺寸）。
        var groups = index.Entries
            .Where(e => e.Storage is not null)
            .GroupBy(e => BlobNameOf(e.Storage!))
            .ToList();

        // 数的是**存储对象**（包与单文件 blob），不是文件——一个包只查一次。
        // 界面上的单位随之标为 objects，免得和文件数对不上被读成打包没生效。
        var tracker = Track(onProgress, "Cloud", groups.Count);
        var presentGroups = new List<IGrouping<string, IndexEntry>>();
        foreach (var g in groups)
        {
            tracker?.Touch(g.Key);
            var s = g.First().Storage!;
            var (vols, sizes) = ExpectedVolumes(info, s);
            var (present, sizeOk) = await VolumeBlobIO.VerifyVolumesAsync(cc, g.Key, vols, sizes, ct);
            if (!present || !sizeOk)
            {
                foreach (var e in g)
                    bad.Add(e.Path);
            }
            else
            {
                presentGroups.Add(g);
            }
            // HEAD 不下载内容：字节记 0，否则会报出一个与实际流量无关的"速度"。
            tracker?.Advance(0);
        }
        tracker?.Complete();

        if (options.Cloud >= CloudCheckLevel.Content)
        {
            var corrupted = await DeepVerifyAsync(cc, info, presentGroups, options, password, downloadConcurrency, onProgress, ct);
            foreach (var p in corrupted)
                bad.Add(p);
        }

        return bad;
    }

    private static (int Volumes, IReadOnlyList<long> Sizes) ExpectedVolumes(BackupInfoFile info, StorageRef s) =>
        s.Kind == "pack"
            ? info.Packs.TryGetValue(s.Ref, out var pi) ? (pi.Volumes, pi.VolumeSizes) : (1, [])
            : (s.Volumes, s.VolumeSizes);

    /// <summary>深度校验：并发下载解压、重算 fullHash 与索引比对。仅内容不符计入损坏；
    /// Archive 未活化（下载报 archived）不计损坏（无法验证，跳过）。</summary>
    /// <param name="info">只为算出每个对象要拉多少字节（pack 的卷尺寸记在信息文件里，
    /// 不在条目上——压实会改写它）。界面据此显示"传了多少 / 一共多大"。</param>
    private async Task<IReadOnlyList<string>> DeepVerifyAsync(
        BlobContainerClient cc, BackupInfoFile info, List<IGrouping<string, IndexEntry>> presentGroups,
        CheckOptions options, string? password, int downloadConcurrency, Action<StageProgress>? onProgress, CancellationToken ct)
    {
        if (compressor is null || hasher is null || string.IsNullOrEmpty(tempRoot))
            throw new InvalidOperationException("Content check requires compressor/hasher/tempRoot.");

        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        using var gate = new SemaphoreSlim(Math.Max(1, downloadConcurrency));
        // 这是整个检查里唯一真正下载数据的阶段，也是唯一可能跑几小时的阶段。
        var tracker = Track(onProgress, "Verifying", presentGroups.Count, inFlight: true);
        try
        {
            var perGroup = await Task.WhenAll(presentGroups.Select(async g =>
            {
                try { return await VerifyGroupAsync(cc, info, work, g.Key, g.ToList(), options, password, gate, tracker, ct); }
                finally
                {
                    tracker?.Advance(0); // 计数与在途分开：一个组恰好占一个槽位
                }
            }));
            return perGroup.SelectMany(x => x).ToList();
        }
        finally
        {
            tracker?.Complete(); // 不强制产出终态，最后一组的字节会被节流压住再也发不出去
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task<IReadOnlyList<string>> VerifyGroupAsync(
        BlobContainerClient cc, BackupInfoFile info, string work, string blobName, List<IndexEntry> members,
        CheckOptions options, string? password, SemaphoreSlim gate, StageTracker? tracker, CancellationToken ct)
    {
        var corrupted = new List<string>();
        var groupDir = Path.Combine(work, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(groupDir);
        await gate.WaitAsync(ct);
        // 在途标记要在**拿到闸门之后**才打：所有组的委托一开始就会被枚举执行到第一个真正的
        // await，若在那之前标记，几千个包会一股脑全算"正在校验"（见 RestoreOrchestrator 同处注释）。
        // 名字用**源文件路径**（pack 用包号+成员数），不是内容寻址的 blob 名——与上传/还原侧同一形状。
        tracker?.BeginItem(
            blobName,
            TransferLabel.For(members[0].Storage!, members),
            TransferLabel.DownloadBytesOf(members[0].Storage!, info));
        try
        {
            // 工厂而不是单个 IProgress<long>：见 VolumeBlobIO.DownloadAsync 上的注释——
            // 多卷共用一个实例会在"小卷后接大卷"时把大卷首次上报的基线算错，整卷漏计一段
            // （上限是前一卷的大小），不是虚高。
            Func<IProgress<long>>? itemProgress = tracker is null ? null : () => tracker.ItemProgress(blobName);

            string firstVolume;
            try
            {
                firstVolume = await VolumeBlobIO.DownloadAsync(cc, blobName, groupDir, ct, itemProgress);
            }
            finally
            {
                // 下载一结束（成功或抛出）就摘在途标记：字节已经边传边计过了，测速窗口
                // 不该被随后不占网线的解压、重算 hash 时间继续拖长。这个 finally 只包
                // 下载本身——它不吞异常，下载失败照样穿透到下面两个 catch，两者看到的
                // 异常集合与改动前完全一致。
                tracker?.EndItem(blobName, 0);
            }

            // 下载已经摘出在途窗口，但解压/重算 hash 这段本地 CPU 工作不能就此从界面上消失——
            // 内容级检查最慢的一步就是它，没有这一对，界面会冻在下载刚结束那一刻的快照上，
            // 跟卡死一模一样（同 RestoreOrchestrator.RestoreGroupAsync 同处注释）。
            try
            {
                // BeginPacking 挪进 try：它现在会在 _gate 下调用 publish(...)，非心跳路径故意让
                // publish 抛出的异常继续往外传（见 StageProgress.cs 里 BeginPacking 的说明）。
                // 留在 try 外面的话，一旦这里抛出，_inPacking 加了却没有配对的 EndPacking，
                // preparing 会在余下的运行里卡在虚高的数字上；挪进来就有下面这个 finally 兜底。
                tracker?.BeginPacking();
                // 这段的共同契约是「解压/算 hash 发生在这一件已经退出 ActiveItems 之后」，
                // 与 RestoreOrchestrator 同一形状；这里由 BackupCheckerTests 里同名的
                // Extraction_Starts_After_Item_Is_Removed_From_ActiveItems 钉住（镜像还原侧
                // 那条测试，但各自独立、互不代替），不再需要借还原侧的测试来兜底。
                corrupted.AddRange(members[0].Storage!.Kind == "blob"
                    ? await VerifyBlobAsync(firstVolume, members, password, ct)
                    : await VerifyPackAsync(firstVolume, groupDir, members, password, ct));
            }
            finally
            {
                tracker?.EndPacking();
            }
        }
        catch (RequestFailedException ex) when (IsArchived(ex))
        {
            // Archive 未活化 → 无法下载验证；若指定活化 tier 则发起活化。不计为损坏。
            if (options.RehydrateTier is { } tier)
                await RehydrateAsync(cc, blobName, tier, ct);
        }
        catch
        {
            corrupted.AddRange(members.Select(m => m.Path)); // 其它下载/解压失败 → 整组损坏
        }
        finally
        {
            // 先摘在途再放闸门：反过来的话，后一个组已经开始校验，界面上却还挂着上一个组。
            // EndItem(blobName, 0) 是兜底而非正路：正常情况下载完成时已经在上面的 finally
            // 里摘过一次，字节也已经在下载过程中边传边计完——这里只防 BeginItem 之后、
            // 进下载 try 之前抛异常的边界情况。EndItem 本身不是幂等的（_bytes += bytes 与
            // PublishIfDue 都在 TryRemove 之外无条件跑），这句在正常路径下不会把「解压+算 hash」
            // 的字节再补一次进测速窗口，纯粹是因为它传的字节数是 0，不是因为 EndItem 本身安全重入。
            tracker?.EndItem(blobName, 0);
            gate.Release();
            try { Directory.Delete(groupDir, recursive: true); } catch { /* best effort */ }
        }
        return corrupted;
    }

    /// <summary>
    /// 单文件 blob 的内容校验，**不落盘**：raw 直传的 blob 就是文件本身；否则整个归档只有一个成员，
    /// `x -so` 不带成员名的输出正是它的内容——因此不必先知道条目名。这一点很关键：去重之后，
    /// 归档里的条目名来自**最先上传这份内容**的那个路径，未必等于当前索引条目的 Path。
    /// <para>
    /// 长度与 hash 都要核对。`x -so` 取不到内容时输出为空却退出码 0，光看"没抛异常"会把
    /// 一个空归档判成通过——这正是本项目已经踩过一次的坑（7z 丢成员时退出 1 却静默通过）。
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> VerifyBlobAsync(
        string firstVolume, List<IndexEntry> members, string? password, CancellationToken ct)
    {
        string actualHash;
        long actualLength;
        if (members[0].Storage!.Raw)
        {
            actualHash = await hasher!.FullHashAsync(firstVolume, ct);
            actualLength = new FileInfo(firstVolume).Length;
        }
        else
        {
            var streamHasher = new StreamingHasher(0, 0);
            await using var sink = new HashingStream(streamHasher);
            await compressor!.ExtractToStreamAsync(firstVolume, entryName: null, password, sink, ct);
            actualHash = streamHasher.FullHash;
            actualLength = streamHasher.Length;
        }

        return [.. members
            .Where(e => actualLength != e.Length || (e.FullHash is not null && actualHash != e.FullHash))
            .Select(e => e.Path)];
    }

    /// <summary>
    /// pack 的内容校验，**不落盘**：一次 `x -so`（不带成员名）把整包流出来，按 `l -slt` 给出的
    /// 成员顺序与尺寸切段，逐段算 hash。逐成员各调一次 7z 是不行的——归档是固实的，
    /// 取第 k 个成员要连带把前面 k-1 个也解一遍，一个上千成员的包会退化成 O(N²)。
    /// <para>
    /// 切段依赖"输出顺序 = 列举顺序"这条 7z 行为。它是对的（有测试钉住），但一旦哪个版本上不成立，
    /// 后果是把好包报成坏包，而修复流程会据此重传。所以只要有一段对不上，就退回整包落盘解压
    /// 逐个复核，由**它**给出最终结论：快路径只在正常情况下省事，绝不制造假警报。
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> VerifyPackAsync(
        string firstVolume, string groupDir, List<IndexEntry> members, string? password, CancellationToken ct)
    {
        var listing = await compressor!.ListEntriesAsync(firstVolume, password, ct);
        var files = listing.Where(e => !e.IsDirectory).Select(e => (e.Name, e.Size)).ToList();

        var actual = new Dictionary<string, (long Length, string Hash)>(StringComparer.Ordinal);
        var splitter = new SegmentHashingStream(files, (name, len, hash) => actual.TryAdd(name, (len, hash)));
        await using (splitter)
        {
            await compressor.ExtractToStreamAsync(firstVolume, entryName: null, password, splitter, ct);
            splitter.Finish();
        }

        // 归档吐出的字节比列举出来的多，或有成员没填满 → 切段的前提不成立，别信这一轮的结果。
        var splitTrustworthy = splitter.ExtraBytes == 0 && splitter.CompletedSegments == files.Count;

        var suspect = new List<IndexEntry>();
        var corrupted = new List<string>();
        foreach (var e in members)
        {
            var entryName = SevenZipCli.NormalizeEntryName(e.Storage!.EntryName ?? e.Path);
            if (!actual.TryGetValue(entryName, out var got))
            {
                // 索引说这个成员在包里，包里却根本没有它 —— 确凿的损坏，不必再验内容。
                // （列举里没有 ≠ 快路径不可信：这是内容本身的问题，落盘重解也一样。）
                if (splitTrustworthy && !listing.Any(l => l.Name == entryName))
                    corrupted.Add(e.Path);
                else
                    suspect.Add(e);
                continue;
            }
            if (got.Length != e.Length || (e.FullHash is not null && got.Hash != e.FullHash))
                suspect.Add(e);
        }

        if (suspect.Count > 0)
            corrupted.AddRange(await VerifyPackOnDiskAsync(firstVolume, groupDir, suspect, password, ct));
        return corrupted;
    }

    /// <summary>慢路径：把整包解到磁盘逐个复核。只在流式切段报出问题时才走，用来给出最终结论。</summary>
    private async Task<IReadOnlyList<string>> VerifyPackOnDiskAsync(
        string firstVolume, string groupDir, IReadOnlyList<IndexEntry> members, string? password, CancellationToken ct)
    {
        var extractDir = Path.Combine(groupDir, "x");
        await compressor!.ExtractAsync(firstVolume, extractDir, password, ct);

        var corrupted = new List<string>();
        foreach (var e in members)
        {
            var entryName = e.Storage!.EntryName ?? e.Path;
            var path = Path.Combine(extractDir, entryName.Replace('/', Path.DirectorySeparatorChar));
            // 条目名来自云端索引，/import 之后即攻击者可控（设计 §5）：`..` 能把探测点甩到解压目录
            // 之外，变成一个"某个文件的内容是否等于某个 hash"的确认预言机。越界一律判损坏。
            if (!PathBoundary.IsWithin(extractDir, path)
                || !File.Exists(path)
                || new FileInfo(path).Length != e.Length
                || (e.FullHash is not null && await hasher!.FullHashAsync(path, ct) != e.FullHash))
                corrupted.Add(e.Path);
        }
        return corrupted;
    }

    private static bool IsArchived(RequestFailedException ex) =>
        ex.ErrorCode == "BlobArchived" || ex.Status == 409;

    private static Task RehydrateAsync(BlobContainerClient cc, string baseRef, AccessTier tier, CancellationToken ct) =>
        // 对归档全部分卷发起活化（异步，几小时后需用户重跑检查）；忽略失败（best effort）。
        BlobRehydration.BeginAsync(cc, baseRef, tier, ct);

    /// <summary>本地源文件状态。localRoot 缺失或本地轴关闭 → NotChecked。</summary>
    private async Task<LocalState> LocalCheckAsync(IndexEntry e, string? localRoot, LocalCheckLevel level, CancellationToken ct)
    {
        if (level == LocalCheckLevel.None || string.IsNullOrEmpty(localRoot))
            return LocalState.NotChecked;

        var local = Path.Combine(localRoot, e.Path.Replace('/', Path.DirectorySeparatorChar));

        // e.Path 来自云端索引，/import 之后即攻击者可控（设计 §5）：`..` 或绝对路径能让
        // Path.Combine 把探测点甩到 localRoot 之外，变成一个「文件是否存在 / 内容是否等于
        // 某个 hash」的确认预言机。判越界一律当 Missing——本地拿不出可用副本，既不读它、
        // 也不让它成为修复来源，与「本地文件不在」处置一致。
        if (!PathBoundary.IsWithin(localRoot, local))
            return LocalState.Missing;

        if (e.Kind == "symlink")
        {
            var target = TryLinkTarget(local);
            if (target is null)
                return LocalState.Missing;
            return target == e.Target ? LocalState.Ok : LocalState.Changed;
        }

        if (!File.Exists(local))
            return LocalState.Missing;

        // 本地文件存在却读不出来（被占用/权限被收回/介质读错误）：一律当 Missing——本地拿不出
        // 可用副本，既不读它、也不让它成为修复来源，与上面「越界」「文件不在」的处置一致。
        // 不加这层保护的话，一个读不开的文件会让**整轮检查**崩掉，而"有文件读不开"恰恰是
        // 最需要跑检查的时候：备份刚跳过了它，操作员正想知道云端那份还在不在。
        try
        {
            if (level == LocalCheckLevel.Attributes)
            {
                var permOk = ReadPermissions(local) == e.Permissions;
                return new FileInfo(local).Length == e.Length && permOk ? LocalState.Ok : LocalState.Changed;
            }

            // Content：hash 一致＝可从本地修复。
            if (hasher is null)
                return LocalState.NotChecked;
            var full = await hasher.FullHashAsync(local, ct);
            return full == e.FullHash ? LocalState.Ok : LocalState.Changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return LocalState.Missing;
        }
    }

    private static string? TryLinkTarget(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.LinkTarget;
        }
        catch { return null; }
    }

    private static string ReadPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return "0000";
        var mode = (int)File.GetUnixFileMode(path);
        return Convert.ToString(mode, 8).PadLeft(4, '0');
    }

    /// <summary>元数据漂移检查：云端 info 与本地权威缓存比对（版本数 / 各版本 IndexBlob / CreatedAt）。</summary>
    private async Task<string?> CheckMetadataDriftAsync(
        Account account, string container, string? password, BackupInfoFile cloud, CancellationToken ct)
    {
        if (trackedInfo is null)
            return null; // 无本地缓存可比对
        if (!await trackedInfo.HasLocalAsync(account, container, ct))
            return "No local cache to compare against (backup not synced on this device).";

        var local = await trackedInfo.LoadAsync(account, container, password, ct);
        if (local is null)
            return "Local cache missing while cloud has a backup.";
        if (local.Versions.Count != cloud.Versions.Count)
            return $"Version count differs: local {local.Versions.Count} vs cloud {cloud.Versions.Count}.";
        for (var i = 0; i < cloud.Versions.Count; i++)
        {
            if (local.Versions[i].IndexBlob != cloud.Versions[i].IndexBlob
                || local.Versions[i].CreatedAt != cloud.Versions[i].CreatedAt)
                return $"Version {cloud.Versions[i].Version} metadata differs between local cache and cloud.";
        }
        return null;
    }

    private static string BlobNameOf(StorageRef s) => s.Kind == "pack" ? $"packs/{s.Ref}.7z" : s.Ref;
}
