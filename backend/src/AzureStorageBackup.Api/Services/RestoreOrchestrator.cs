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
/// FailedFiles = 未能还原的条目数：所在存储分组下载/解压失败、条目会写到目标根之外
/// （含 symlink 与空目录条目）、条目本身畸形导致写入抛错、symlink 条目缺 Target，
/// 或索引中出现重复 Path（无法判断哪条权威，两条都不写）。
/// RestoredDirs = **实际创建成功**的空目录数（越界/失败的不计）。</summary>
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
    /// <summary>测试注入的毫秒时间源，原样转给内部建的 <see cref="StageTracker"/>（见其上同名字段的注释）。
    /// 生产为 null，走真实墙钟。用来让"下载结束就摘掉在途标记、解压期间不再算在途"这类
    /// 时序断言摆脱 200ms 节流窗口——注入后每次查询时间都保证前进，节流因此永不生效，
    /// 每一次状态变化都会被发布出来，断言不必赌真实时钟是否恰好跨过节流窗口。</summary>
    internal Func<long>? Clock { get; init; }

    /// <param name="onProgress">阶段进度（正在还原哪个包、完成多少组、多快）。此前只有 phase 那条
    /// 自由文本，且它承载的其实是错误流，说不出"还剩多少"。</param>
    public async Task<RestoreResult> RunAsync(
        RestoreRequest request, CancellationToken ct = default, IProgress<string>? phase = null,
        Action<StageProgress>? onProgress = null)
    {
        var source = $"restore:{request.Account.Id}/{request.Container}";
        await Record(NotificationEvents.RestoreStart, source, $"Restore started: {request.Container}", request.TargetRoot, ct);
        try
        {
            var result = await RunCoreAsync(request, phase, onProgress, ct);
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

    private async Task<RestoreResult> RunCoreAsync(
        RestoreRequest request, IProgress<string>? phase, Action<StageProgress>? onProgress, CancellationToken ct)
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

        // 目标根解析一次，全程复用（与 PathBoundary 同款单例思路：request.TargetRoot 本轮不变，
        // 没必要让每个条目、每个文件条目两次地重新走一遍 lstat）。per-目标路径的解析仍在
        // WriteStaysInsideRoot/LinkStaysInsideRoot 里逐条目进行——那一处必须每次都重新算，
        // 因为它要抓的正是「本轮还原期间新建的链接」。
        var realRoot = PathBoundary.ResolveReal(request.TargetRoot);

        var restored = 0;
        var skipped = 0;
        var failed = 0;

        // 逐路径生效条目：默认取本版本；被替代的路径改用指定版本的同路径条目（内容+元数据取该版本）。
        var byPath = IndexByPath(index.Entries, phase, out var duplicatePaths);
        failed += duplicatePaths; // 重复 Path 的索引条目：两条都不写，各算一次失败，不中断整次还原。
        var resolved = new HashSet<string>(StringComparer.Ordinal); // 真正解析成功的替代路径
        foreach (var grp in request.Substitutions.GroupBy(kv => kv.Value))
        {
            var sv = info.Versions.FirstOrDefault(x => x.Version == grp.Key);
            if (sv is null)
                continue; // 替代版本已被保留清理删除 → 该组全部回落跳过
            var srcIndex = await store.ReadIndexAsync(request.Account, request.Container, sv.IndexBlob, request.Password, ct);
            // 替代来源版本的索引同样来自云端，同样可能有重复 Path；解析不到的替代路径
            // 走既有的「声明了意图但替代不可得」回落跳过语义（下面的 TryGetValue 找不到）。
            var srcByPath = IndexByPath(srcIndex.Entries, phase, out _);
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
                if (!WriteStaysInsideRoot(realRoot, dest))
                {
                    // 与 symlink 路径同一原则（C3）：安全检查触发必须可见，且必须计入失败——
                    // 只走 phase 上报会让一份只含越界 EmptyDirs 的恶意索引把 FailedFiles 冻在 0。
                    phase?.Report($"Skipped unsafe directory entry (escapes the target root): {dir}");
                    failed++;
                    continue;
                }

                // 畸形目录条目（中间段是文件等）只失败它自己，不中断整次还原。
                try
                {
                    Directory.CreateDirectory(dest);
                    restoredDirs++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    phase?.Report($"Failed to create directory '{dir}': {ex.Message}");
                    failed++;
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
                    outcome = RestoreSymlink(request.TargetRoot, realRoot, e);
                }
                catch (OperationCanceledException)
                {
                    throw;
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
                    case SymlinkOutcome.Malformed:
                        // entry.Target 缺失：与「未变」不是一回事——未变是无事发生，
                        // 这条是没能还原，必须让用户看得见、算进失败，而不是套上
                        // 「已是最新」的名义悄悄计成 Skipped（M3）。
                        phase?.Report($"Skipped malformed symlink entry (missing target): {e.Path}");
                        failed++;
                        break;
                    default:
                        // 安全检查触发必须可见：与「未变」同样静默会让用户完全看不到被拦下的条目。
                        phase?.Report(UnsafeRestorePathException.MessageFor(e.Path));
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
            // 总数只有在分完组之后才知道（同一个 pack 只下一次），所以 tracker 在这里才建得起来。
            var tracker = onProgress is null
                ? null
                : new StageTracker("Restoring", groups.Count, onProgress, speedWhileInFlight: true) { Clock = Clock };
            var tasks = groups.Select(async g =>
            {
                try { return await RestoreGroupAsync(container, request, realRoot, work, g.ToList(), gate, rehydrated, phase, tracker, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    phase?.Report($"Group failed ({g.Key}): {ex.Message}");
                    return (Restored: 0, Skipped: 0, Failed: g.Count());
                }
                finally
                {
                    tracker?.Advance(0); // 计数与在途分开：一个组恰好占一个槽位
                }
            });
            var counts = await Task.WhenAll(tasks);
            tracker?.Complete(); // 不强制产出终态，最后一组的字节会被节流压住再也发不出去
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
        BlobContainerClient container, RestoreRequest request, string? realRoot, string work,
        List<IndexEntry> group, SemaphoreSlim gate, System.Collections.Concurrent.ConcurrentBag<string> rehydrated,
        IProgress<string>? phase, StageTracker? tracker, CancellationToken ct)
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
            if (!WriteStaysInsideRoot(realRoot, dest))
            {
                phase?.Report(UnsafeRestorePathException.MessageFor(e.Path));
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
        // 在途标记要在**拿到闸门之后**才打：所有组的委托一开始就会被枚举执行到第一个真正的
        // await，若在那之前标记，几千个包会一股脑全算"正在还原"——既与事实不符
        // （同时只跑 DownloadConcurrency 个），每次快照还要复制一份几千项的数组。
        tracker?.BeginItem(blobName);
        try
        {
            // 工厂而不是单个 IProgress<long>：见 VolumeBlobIO.DownloadAsync 上的注释——
            // 多卷共用一个实例会在"小卷后接大卷"时把大卷首次上报的基线算错，整卷漏计一段
            // （上限是前一卷的大小），不是虚高。
            // tracker 为 null（没人接进度）时整个表达式退化为 null，DownloadAsync 不挂回调。
            Func<IProgress<long>>? itemProgress = tracker is null ? null : tracker.ItemProgress;

            string firstVolume;
            try
            {
                try
                {
                    firstVolume = await VolumeBlobIO.DownloadAsync(container, blobName, groupDir, ct, itemProgress);
                }
                catch (RequestFailedException ex) when (ex.ErrorCode == "BlobArchived" || ex.Status == 409)
                {
                    // Archive 未活化：发起活化并轮询到就绪，这一等按 EnsureOnlineAsync 自己的注释
                    // 是"几小时级"。在途标记的窗口现在是测速时钟的分母——「网线上有几条流」，
                    // 活化排队和轮询期间网线上什么都没有，标记不摘的话虚拟时钟会照样走上几个
                    // 小时，心跳把速度硬拖到 0，界面报"卡住"，而备份其实在正确地等 Azure。
                    // 摘掉的只是测速窗口的标记，不是进度信号本身：EnsureOnlineAsync 自己会在
                    // 每次轮询时把 "Waiting for rehydration of {baseRef} — N volume(s) still
                    // archived…" 报到 phase 上，操作员看得到组的动向，不会以为它消失了。
                    // 已知的粗糙之处：phase 的顶行（RestoreRunner 里的 state.Phase）是所有并发组
                    // 共用的一个槽，多组同跑时这条消息会被别的组顶掉，只在 state.Events 里留底；
                    // 但轮询每 RehydratePollSeconds 重报一次，它自己会再回来。这是既有的进度模型，
                    // 不是这里引入的。
                    tracker?.EndItem(blobName, 0);
                    await EnsureOnlineAsync(container, blobName, request.RehydrateTier, MapPriority(request.RehydratePriority), request.RehydratePollSeconds, phase, ct);
                    rehydrated.Add(blobName);
                    // 活化完成、真正要下载了才重新打开窗口——与最初 BeginItem 同一节奏。
                    tracker?.BeginItem(blobName);
                    firstVolume = await VolumeBlobIO.DownloadAsync(container, blobName, groupDir, ct, itemProgress);
                }
            }
            finally
            {
                // 下载一结束（成功，或两次都失败向上抛）就把在途标记摘掉：字节已经在下载过程中
                // 边传边计过了，测速窗口不该被随后不占网线的解压/写盘时间继续拖长。
                // 走到这里时标记可能已经被上面 catch 块摘过一次（活化路径先摘再重打）——
                // EndItem 对不在集合里的项是安全的空操作（ConcurrentDictionary.TryRemove 返回
                // false，后面 _bytes += 0 与 PublishIfDue 照跑，不影响任何计数），这里不需要
                // 区分是否已经摘过，反正传的是 0 字节，摘第二次没有副作用。
                // 下面外层 finally 里那句兜底 EndItem(blobName, 0) 因此在正常路径下不会二次生效——
                // EndItem 本身**不是**幂等的（_bytes += bytes 和 PublishIfDue 都在 TryRemove 之外
                // 无条件跑），兜底调用能安全重复，只是因为它传的是 0 字节；真传了非零字节的第二次
                // 调用会悄悄把这批字节多计一遍。
                tracker?.EndItem(blobName, 0);
            }

            // 下载已经摘出在途窗口，但解压/算 hash/写盘这段本地 CPU 工作不能就此从界面上消失——
            // 没有它，一个大 pack 解压的几十秒里 ActiveItems 空、preparing/queued 也都是 0，
            // 界面冻在下载刚结束那一刻的快照上，跟卡死一模一样（b6db78a 已经为压缩段修过同一个
            // 问题，这里是它在还原/校验侧的对称件）。BeginPacking/EndPacking 不影响测速分母
            // （那个窗口只认 BeginItem/EndItem），单纯是"正在准备"这个信号的载体。
            try
            {
                // BeginPacking 挪进 try：它现在会在 _gate 下调用 publish(...)，非心跳路径故意让
                // publish 抛出的异常继续往外传（见 StageProgress.cs 里 BeginPacking 的说明）。
                // 留在 try 外面的话，一旦这里抛出，_inPacking 加了却没有配对的 EndPacking，
                // preparing 会在余下的运行里卡在虚高的数字上；挪进来就有下面这个 finally 兜底。
                tracker?.BeginPacking();
                if (storage.Kind == "blob")
                {
                    // 单文件 blob：内容就是一个文件（raw=原始字节；否则 7z 里唯一条目）。
                    // 内容寻址去重时同一 blob 可被多个路径引用 → 第一条写好之后，其余从它复制。
                    // 非 raw 的那条直接从归档流到目标：先解压到临时目录再复制，等于把同样的字节
                    // 写两遍盘（一个 20 GB 的 blob 就是 40 GB 的写入 + 20 GB 的临时空间）。
                    string? content = storage.Raw ? firstVolume : null;
                    foreach (var e in needed)
                    {
                        if (content is null)
                        {
                            var streamed = await TryStreamRestoredFileAsync(request, realRoot, e, firstVolume, phase, ct);
                            if (streamed is null)
                            {
                                failedEntries++;
                                continue;
                            }
                            // 后续引用从这一份复制。它在目标根内、内容已按长度和 hash 核对过。
                            content = streamed;
                            restored++;
                        }
                        else if (TryWriteRestoredFile(request, realRoot, e, content, phase))
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
                        if (TryWriteRestoredFile(request, realRoot, e, source, phase))
                            restored++;
                        else
                            failedEntries++;
                    }
                }
            }
            finally
            {
                tracker?.EndPacking();
            }
        }
        finally
        {
            // 兜底摘除：正常路径下载结束时已经在上面的 finally 里摘过一次（真正的字节也已经
            // 边传边计完）。这里传 0 字节纯粹是防御——万一 BeginItem 之后、进下载 try 之前
            // 抛出异常，在途集合不能漏摘。EndItem 本身不是幂等的（见上面那处同样的说明），
            // 这句在正常路径下之所以不会二次生效、不会重复计数，纯粹是因为它传的字节数是 0。
            tracker?.EndItem(blobName, 0);
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
        try
        {
            return await hasher.FullHashAsync(dest, ct) != entry.FullHash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目标位置那个文件读不开，就无从判断它是否已经是要还原的内容——保守地当作「需要还原」。
            // 真去写它若同样失败，TryWriteRestoredFile 的逐文件兜底会记一条并继续；
            // 而在这里抛出会被**整组**的 catch 接住，让同一个包里其它文件也一并还原不了——
            // 一个文件的权限问题不该有那么大的爆炸半径。
            return true;
        }
    }

    /// <summary>
    /// 写一个条目，把失败圈在这一条上：越界、以及畸形条目（如 Path 为 ""/"." 使目标就是一个目录，
    /// File.Copy 会抛 UnauthorizedAccess/IOException）都只让本条目失败并上报，
    /// 绝不冒泡到分组处理器——那会把整组合法条目一起判失败。返回是否写入成功。
    /// </summary>
    private static bool TryWriteRestoredFile(RestoreRequest request, string? realRoot, IndexEntry entry, string sourceFile, IProgress<string>? phase)
    {
        try
        {
            WriteRestoredFile(request, realRoot, entry, sourceFile);
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

    /// <summary>
    /// 把单文件 blob 从归档直接流到目标，不经过临时解压目录。成功返回写好的目标路径，失败返回 null
    /// （错误已上报，只圈在这一条上，与 <see cref="TryWriteRestoredFile"/> 同样的容错语义）。
    /// </summary>
    private async Task<string?> TryStreamRestoredFileAsync(
        RestoreRequest request, string? realRoot, IndexEntry entry, string firstVolume,
        IProgress<string>? phase, CancellationToken ct)
    {
        var dest = Path.Combine(request.TargetRoot, ToLocal(entry.Path));
        // 越界判定必须在**任何**写动作之前：临时件也是写，也会跟随链接落到根外。
        if (!WriteStaysInsideRoot(realRoot, dest))
        {
            phase?.Report(UnsafeRestorePathException.MessageFor(entry.Path));
            return null;
        }

        // 先写同目录的临时件、核对无误再顶上去：直接往 dest 写的话，一次中途失败
        // （网络断、归档坏、取消）留下的就是一个半截的、覆盖掉用户原文件的东西。
        var part = dest + ".asb-part";
        // 临时件同样要过边界判定：索引（可能来自 /import 的任意容器）里放一条
        // `<某文件>.asb-part -> /etc/cron.d/x` 的 symlink 条目，软链先于文件条目还原，
        // 之后 FileStream 会跟随它把归档内容写到根外——只查 dest 挡不住这一条。
        if (!WriteStaysInsideRoot(realRoot, part))
        {
            phase?.Report(UnsafeRestorePathException.MessageFor(entry.Path));
            return null;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            var hasher = new StreamingHasher(0, 0);
            long written;
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var sink = new HashingStream(hasher, file))
            {
                // 不带成员名：去重之后归档里的条目名来自**最先上传这份内容**的那个路径，
                // 未必等于当前索引条目的 Path；单文件归档只有一个成员，整个输出就是它的内容。
                written = await compressor.ExtractToStreamAsync(firstVolume, entryName: null, request.Password, sink, ct);
            }

            // `7z x -so` 取不到成员时输出为空却**退出码 0**，所以退出码不能作为通过依据——
            // 长度和 hash 才是。归档里若不止一个条目，内容会首尾相接，长度这一关同样拦得住。
            if (written != entry.Length)
            {
                throw new IOException(
                    $"archive yielded {written} byte(s) for '{entry.Path}' but the index says {entry.Length}");
            }
            if (entry.FullHash is not null && hasher.FullHash != entry.FullHash)
                throw new IOException($"archive content for '{entry.Path}' does not match the hash in the index");

            if (request.Conflict == RestoreConflictMode.RenameKeep && File.Exists(dest))
                RestoreConflict.RenameExisting(dest, DateTimeOffset.UtcNow);
            File.Move(part, dest, overwrite: true);
            ApplyMetadata(dest, entry);
            return dest;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(part);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFile(part);
            phase?.Report($"Failed to restore '{entry.Path}': {ex.Message}");
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>把还原内容写到目标路径。RenameKeep 且目标已存在（能进到这一步即内容不同或无法比较）→
    /// 先把现有本地文件改名为 {name}.bak-{ts} 保留旧内容，再写还原内容到原名（旧内容永不丢失）。</summary>
    private static void WriteRestoredFile(RestoreRequest request, string? realRoot, IndexEntry entry, string sourceFile)
    {
        var dest = Path.Combine(request.TargetRoot, ToLocal(entry.Path));

        // 索引来自云端（可能是 /import 导入的任意容器）：条目路径含 .. 时会写到目标根之外。
        // 判定作用在**解析后的真实路径**上——纯词法判定挡不住「先建链接再穿过它写」：
        // 索引里一条 symlink 条目（先于文件条目还原）指向根外，之后 <root>/link/x 词法上
        // 完全在根内，File.Copy 却会跟随链接落到根外。
        // 跳过该条目而不是中断整次还原——与既有的逐组容错语义一致。
        if (!WriteStaysInsideRoot(realRoot, dest))
            throw new UnsafeRestorePathException(entry.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (request.Conflict == RestoreConflictMode.RenameKeep && File.Exists(dest))
            RestoreConflict.RenameExisting(dest, DateTimeOffset.UtcNow);
        File.Copy(sourceFile, dest, overwrite: true);
        ApplyMetadata(dest, entry);
    }

    /// <summary>symlink 条目的还原结果。三者互不相同，不能互相顶替：
    /// 「未变」是无事发生；「越界」是安全检查被触发，用户必须看得见；
    /// 「畸形」（M3）是条目本身缺 Target，没能还原，同样必须可见——不能套上
    /// 「未变」的名义悄悄计成 Skipped（那意味着链接已经是对的，畸形条目并非如此）。</summary>
    private enum SymlinkOutcome
    {
        Created,
        Unchanged,
        Unsafe,
        Malformed,
    }

    private SymlinkOutcome RestoreSymlink(string targetRoot, string? realRoot, IndexEntry entry)
    {
        if (entry.Target is null)
            return SymlinkOutcome.Malformed;

        var dest = Path.Combine(targetRoot, ToLocal(entry.Path));

        // 同 WriteRestoredFile：索引条目路径含 .. 或穿过一条指向根外的链接时，
        // 链接会被建到目标根之外，拦下。
        // 注意这里用的是「只解析父目录」的版本：entry.Target 指向根外是**合法**的
        // （备份如实记录了原本的绝对软链，还原它是对的），被禁止的只是「穿过链接写」。
        if (!LinkStaysInsideRoot(realRoot, dest))
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
    /// <paramref name="realRoot"/> 是目标根**自身**解析后的真实路径，由调用方（<see cref="RunCoreAsync"/>）
    /// 在本轮还原开始时算**一次**并全程复用——<c>request.TargetRoot</c> 本轮不变，没必要让
    /// 每个条目（文件条目还两次）重新走一遍 lstat（对照 <see cref="PathBoundary"/> 对同一个值
    /// 的「单例：构造时解析一次」）。<paramref name="dest"/> 这一侧**必须**每次都重新解析，
    /// 不能一并缓存：它是本轮还原期间可能被新建/改变的候选路径，缓存会让「先建链接再穿过它写」
    /// 这条攻击面探测不到。还原到一个本身经软链到达的目录（<c>/data -&gt; /mnt/disk1/data</c>）
    /// 必须继续可用，所以根也必须解析，不能只解析候选路径。
    /// </para>
    /// <para>解析失败（成环 / 含 \0 / 空串）一律判越界——失败关闭。</para>
    /// </summary>
    private static bool WriteStaysInsideRoot(string? realRoot, string dest)
    {
        var realDest = PathBoundary.ResolveReal(dest);
        return realRoot is not null && realDest is not null && PathBoundary.IsWithin(realRoot, realDest);
    }

    /// <summary>
    /// 建 symlink 前的越界判定：<paramref name="realRoot"/> 同 <see cref="WriteStaysInsideRoot"/>——
    /// 本轮还原开始时解析一次、全程复用；末段按名字拼接、**不解析**。
    /// <para>
    /// 末段不能解析，因为创建/删除链接本身不跟随末段（<c>symlinkat</c>/<c>unlinkat</c> 语义），
    /// 而且合法备份里那条指向根外的绝对软链在第二次还原时，末段就是它自己——
    /// 解析末段会把「重复还原一条合法链接」误判成越界。父目录仍然必须**每次重新**解析：
    /// 链接建在哪个目录里，取决于路径中间段跟随后的真实位置，这一段可能在本轮还原期间改变。
    /// </para>
    /// </summary>
    private static bool LinkStaysInsideRoot(string? realRoot, string dest)
    {
        var parent = Path.GetDirectoryName(dest);
        if (string.IsNullOrEmpty(parent))
            return false;

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

    /// <summary>
    /// 按 Path 建索引，重复 Path 的**所有**条目一律不生效——两条互相矛盾时无法判断哪条权威，
    /// 宁可都不写也不猜。索引来自云端（<c>/import</c> 可导入任意容器），重复 Path 是索引自身
    /// 矛盾，按既有的逐条目容错原则处理：只让重复的路径失败，不能让 <c>ToDictionary</c> 的
    /// <see cref="ArgumentException"/> 中止整次还原。
    /// </summary>
    private static Dictionary<string, IndexEntry> IndexByPath(
        List<IndexEntry> entries, IProgress<string>? phase, out int duplicateCount)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
            if (!seen.Add(e.Path))
                duplicates.Add(e.Path);

        var map = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
            if (!duplicates.Contains(e.Path))
                map[e.Path] = e;

        foreach (var p in duplicates)
            phase?.Report($"Skipped duplicate index entry (ambiguous which version is authoritative): {p}");
        duplicateCount = duplicates.Count;
        return map;
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
    : Exception(UnsafeRestorePathException.MessageFor(entryPath))
{
    /// <summary>
    /// 共享的消息拼接：异常构造器与仅需一行文案的上报点（<see cref="RestoreOrchestrator"/>
    /// 里越界目录/symlink/文件条目的 phase 上报）都用它，后者不必为了取一个字符串
    /// 而分配一个异常对象。
    /// </summary>
    public static string MessageFor(string entryPath) => $"Restore entry path escapes the target root: {entryPath}";
}
