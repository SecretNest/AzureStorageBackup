using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>一个路径相对上一版本的变更分类。</summary>
public enum ChangeKind
{
    /// <summary>上一版本没有 → 新增。</summary>
    Added,

    /// <summary>内容变了，需重新处理/上传。</summary>
    Modified,

    /// <summary>内容不变，仅 mtime/权限变 → 只更新索引元数据，不重传。</summary>
    MetadataOnly,

    /// <summary>本轮读不开（被占用/无权限/读错误）。既不是变更也不是删除：
    /// 索引沿用上一版本条目并打 UnreadableAt，绝不能被当成删除。</summary>
    Unreadable,

    /// <summary>完全未变（length+mtime+权限一致，未哈希）。</summary>
    Unchanged,

    /// <summary>上一版本有、本次无 → 删除。</summary>
    Deleted,
}

/// <summary>单个路径的 diff 结果，携带构建新索引条目所需的已解析哈希/存储。</summary>
public sealed record FileChange(
    string Path,
    ChangeKind Kind,
    ScannedEntry? Current,
    IndexEntry? Previous,
    string? HeadHash,
    string? FullHash,
    StorageRef? CarriedStorage,
    /// <summary>读失败原因（ex.Message）。仅 Kind == Unreadable 时非空。</summary>
    string? UnreadableReason = null,
    /// <summary>
    /// 尾部 hash。内容身份的第四项，与 fullHash + 长度 + head 一起用于去重与碰撞判定。
    /// <para>
    /// 单文件 blob 那条路上它由压缩那一遍顺手算出（见编排器的 tailByPath），所以这里主要是为
    /// **打包成员**准备的——它们从前一项都没有，于是只能按三项去重。既然判据在别处是四项，
    /// 这里也该是四项，不该两条路各有一套标准。
    /// </para>
    /// <para>
    /// 未变文件也会补算（见 <c>UnchangedAsync</c>）：老索引里的打包成员条目全都缺这一项，
    /// 不补就永远缺——未变文件本来一个字节都不读。补一次之后写进新索引，旧备份就此自愈。
    /// </para>
    /// </summary>
    string? TailHash = null);

public sealed record DiffOptions
{
    /// <summary>headHash 覆盖的头部字节数（默认 4KB，M4 决策 §13.3）。</summary>
    public int HeadHashBytes { get; init; } = 4096;
}

/// <summary>diff 汇总。ChangedFiles/ChangedBytes 仅计 Added+Modified（未压缩、分组前，删除/仅元数据不计，§4）。</summary>
public sealed record DiffResult(
    IReadOnlyList<FileChange> Changes,
    int ChangedFiles,
    long ChangedBytes);

/// <summary>
/// 版本对比引擎（M4 设计 §4.2）：惰性两级哈希。
/// 先靠 length+mtime+权限 判断；仅"length 同但 mtime/权限变"的文件才算 headHash，
/// 再不同才算 fullHash。避免每次备份重读全部文件。
/// </summary>
public sealed class BackupDiffer(IFileHasher hasher)
{
    public async Task<DiffResult> DiffAsync(
        string rootPath,
        ScanResult current,
        VersionIndex? previous,
        DiffOptions? options = null,
        CancellationToken ct = default,
        // 首次备份时这一步要把每个文件完整读一遍算 hash，可以跑几小时。没有它，界面上就是
        // 一个一动不动的 0%，用户无从判断是在干活还是挂死了。
        StageTracker? tracker = null,
        // 每判完一个**扫描到的**条目就回调一次，按扫描顺序（= ordinal 路径序）。
        // 编排器据此边 diff 边把已定局的活推给压缩上传侧，不必等整轮 diff 跑完——首次备份的
        // diff 要几小时，那几小时里网络本来一个字节都没在传。
        // 尾部补出来的 Unreadable/Deleted 条目不回调：它们不产生任何要上传的东西。
        Func<FileChange, CancellationToken, Task>? onChange = null,
        // 哪些路径的全文 hash 可以不在这里算（编排器传"归类为单文件 blob 的"）。
        // 那条路上 hash 是压缩那一遍读顺手算出来的，算完还会覆盖这里记的值——diff 再读一遍
        // 等于把每个大文件从头到尾读两遍。一个 100 GB 的文件，省掉的就是整整 100 GB 的读。
        // 只对"已经确定变了"的判定生效（Added、以及 length 变的 Modified）；
        // "length 同、mtime 变"那条两级哈希路径**不受影响**——那里的 fullHash 正是用来判断
        // 到底是 MetadataOnly 还是真 Modified 的，省掉它会把没变的文件全部重传一遍。
        Func<string, bool>? fullHashDeferred = null)
    {
        options ??= new DiffOptions();
        var root = Path.GetFullPath(rootPath);
        var prevByPath = (previous?.Entries ?? []).ToDictionary(e => e.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var changes = new List<FileChange>();
        var changedFiles = 0;
        long changedBytes = 0;

        foreach (var entry in current.Entries)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(entry.Path);
            // 在**处理之前**就把当前路径亮出来：卡住时最需要知道的正是"卡在哪个文件上"。
            tracker?.Touch(entry.Path);

            var full = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var kind = entry.Kind == EntryKind.File ? "file" : "symlink";
            prevByPath.TryGetValue(entry.Path, out var prev);

            var deferFull = fullHashDeferred?.Invoke(entry.Path) ?? false;
            var change = prev is null
                ? await AddedAsync(entry, full, options, deferFull, ct)
                : await CompareAsync(entry, prev, full, kind, options, deferFull, ct);

            changes.Add(change);
            if (change.Kind is ChangeKind.Added or ChangeKind.Modified)
            {
                changedFiles++;
                changedBytes += entry.Length;
            }
            // 计入已读字节：只有实际读过内容的分类才算，未变的文件根本没打开过，
            // 把它们算进去会让速度看起来虚高得离谱。全文 hash 被延后的（FullHash 为空）同理——
            // 这里只摸了个文件头，一个 100 GB 的文件若按整份计，速度会瞬间冲到几十 GB/s，
            // 剩余时间跟着变成一句笑话。FullHash 是否为空恰好就是"读没读全"的准确指示。
            tracker?.Advance(
                change.Kind is ChangeKind.Added or ChangeKind.Modified or ChangeKind.MetadataOnly
                && change.FullHash is not null
                    ? entry.Length
                    : 0);

            if (onChange is not null)
                await onChange(change, ct);
        }

        // 扫描阶段就读不出来的路径，必须在"判删除"之前登记进 seen。
        // 一个列不出内容的目录，其下**整棵子树**都没能被扫到——若不登记，接下来那段循环会把
        // 这些既有条目一个不剩地判成删除，等于因为一次权限故障就把整棵子树从索引里抹掉，
        // 直到还原时才发现文件没了。读不开 ≠ 删除，这里是这条原则最要紧的一处。
        foreach (var u in current.Unreadable)
        {
            foreach (var prev in PreviousEntriesUnder(prevByPath, u))
            {
                if (seen.Add(prev.Path))
                    changes.Add(new FileChange(prev.Path, ChangeKind.Unreadable, null, prev, null, null, null, u.Reason));
            }

            // 读不开的**文件**即使上一版本没有（全新且从头就读不开），也要记一条：
            // 没有内容可指向、索引里不会有它，但操作员必须知道它本轮没被备份。
            if (!u.IsDirectory && !prevByPath.ContainsKey(u.Path) && seen.Add(u.Path))
                changes.Add(new FileChange(u.Path, ChangeKind.Unreadable, null, null, null, null, null, u.Reason));
        }

        foreach (var prev in prevByPath.Values)
        {
            if (!seen.Contains(prev.Path))
                changes.Add(new FileChange(prev.Path, ChangeKind.Deleted, null, prev, null, null, null));
        }

        return new DiffResult(changes, changedFiles, changedBytes);
    }

    /// <summary>某个读不出来的路径覆盖到的上一版本条目：目录取其整棵子树，文件取它自己。</summary>
    private static IEnumerable<IndexEntry> PreviousEntriesUnder(
        Dictionary<string, IndexEntry> prevByPath, UnreadablePath unreadable)
    {
        if (!unreadable.IsDirectory)
            return prevByPath.TryGetValue(unreadable.Path, out var one) ? [one] : [];

        // 根自身读不开时 Path 为 "."（GetRelativePath 对根给出的结果），此时整份索引都在其下。
        if (unreadable.Path is "" or ".")
            return prevByPath.Values;

        var prefix = unreadable.Path + "/";
        return prevByPath.Values.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal));
    }

    private async Task<FileChange> CompareAsync(
        ScannedEntry entry, IndexEntry prev, string full, string kind, DiffOptions options, bool deferFull,
        CancellationToken ct)
    {
        // 类型变更（file<->symlink）视为内容变更。
        if (prev.Kind != kind)
            return await ModifiedAsync(entry, prev, full, options, deferFull, ct);

        if (entry.Kind == EntryKind.Symlink)
            return entry.Target == prev.Target
                ? await UnchangedAsync(entry, prev, options, full, ct)
                : new FileChange(entry.Path, ChangeKind.Modified, entry, prev, null, null, null);

        // length 不同 → 直接变更，无需 head 预筛。
        if (entry.Length != prev.Length)
            return await ModifiedAsync(entry, prev, full, options, deferFull, ct);

        // length 同、mtime 与权限都同 → 未变，完全跳过哈希。
        if (entry.ModifiedAt == prev.Mtime && entry.Permissions == prev.Permissions)
            return await UnchangedAsync(entry, prev, options, full, ct);

        // length 同、mtime 或权限变 → 两级哈希。这里的 fullHash **不能**延后：它正是用来区分
        // "只是 mtime 被碰了一下"（MetadataOnly，不重传）和"内容真变了"（Modified）的唯一依据。
        // 省掉它就只能一律当作变更，等于每次 touch 都把文件重传一遍。
        return await TryReadAsync(async () =>
        {
            var head = await hasher.HeadHashAsync(full, options.HeadHashBytes, ct);
            if (head != prev.HeadHash)
            {
                var changedFull = await hasher.FullHashAsync(full, ct);
                return new FileChange(entry.Path, ChangeKind.Modified, entry, prev, head, changedFull, null);
            }

            var fullHash = await hasher.FullHashAsync(full, ct);
            return fullHash == prev.FullHash
                ? new FileChange(entry.Path, ChangeKind.MetadataOnly, entry, prev, head, fullHash, prev.Storage,
                    // 这一条内容没变、不重传，head/full 刚在上面算过了，只补一个尾部：
                    // 单独 seek 读 4KB，比为它把整个文件再读一遍便宜得多。
                    TailHash: prev.TailHash ?? await hasher.TailHashAsync(full, options.HeadHashBytes, ct))
                : new FileChange(entry.Path, ChangeKind.Modified, entry, prev, head, fullHash, null);
        }, entry, prev);
    }

    private async Task<FileChange> AddedAsync(
        ScannedEntry entry, string full, DiffOptions options, bool deferFull, CancellationToken ct)
    {
        if (entry.Kind == EntryKind.Symlink)
            return new FileChange(entry.Path, ChangeKind.Added, entry, null, null, null, null);

        return await TryReadAsync(async () =>
        {
            var id = await IdentityAsync(entry, full, options, deferFull, ct);
            return new FileChange(
                entry.Path, ChangeKind.Added, entry, null, id.Head, id.Full, null, TailHash: id.Tail);
        }, entry, null);
    }

    private async Task<FileChange> ModifiedAsync(
        ScannedEntry entry, IndexEntry prev, string full, DiffOptions options, bool deferFull, CancellationToken ct)
    {
        if (entry.Kind == EntryKind.Symlink)
            return new FileChange(entry.Path, ChangeKind.Modified, entry, prev, null, null, null);

        // 记录完整的 headHash + fullHash（索引条目须含原文件哈希/尺寸/权限，供后续 diff 与还原比对）。
        // 走到这里已经**确定**内容变了（类型换了，或 length 对不上），fullHash 在这里只剩两个用途：
        // 生成 data/{hash} 地址、以及写进索引——而这两件事单文件 blob 那条路都会用压缩那一遍
        // 算出来的值重做一次。所以延后是无损的。
        return await TryReadAsync(async () =>
        {
            var id = await IdentityAsync(entry, full, options, deferFull, ct);
            return new FileChange(
                entry.Path, ChangeKind.Modified, entry, prev, id.Head, id.Full, null, TailHash: id.Tail);
        }, entry, prev);
    }

    /// <summary>
    /// 这一条的全文 hash 能不能真的延后。延后的意义是免掉"为算 hash 把文件再整个读一遍"，
    /// 而 0 字节那一遍读是免费的——更要紧的是，编排器不会把空文件送进压缩，也就没有人回来
    /// 补上这个值：延后会在索引里留下一个永远为 null 的 fullHash，下一轮 diff 拿它和新算的
    /// 值比对必然不等，于是每一轮都把这个空文件重判成变更。
    /// </summary>
    private static bool DeferrableFullHash(ScannedEntry entry, bool deferFull) => deferFull && entry.Length > 0;

    /// <summary>
    /// 这一条完全没变。照理不该碰盘，但**缺尾部 hash 时补算一次**：老索引里的打包成员条目
    /// 一项都没有（那时只有单文件 blob 存 tail），而未变文件永远走不到会重算 hash 的分支，
    /// 不补就永远缺。补一次写进新索引，旧备份就此自愈，此后再不会读。
    /// <para>
    /// 代价是首次跑新版本时，每个缺这一项的未变文件各读 4KB。之后为零。
    /// 读不开就算了——未变文件的 tail 是锦上添花，不值得为它把一条好端端的条目判成读不开。
    /// </para>
    /// </summary>
    private async Task<FileChange> UnchangedAsync(
        ScannedEntry entry, IndexEntry prev, DiffOptions options, string full, CancellationToken ct)
    {
        var tail = prev.TailHash;
        if (tail is null && entry.Kind == EntryKind.File && entry.Length > 0)
        {
            try
            {
                tail = await hasher.TailHashAsync(full, options.HeadHashBytes, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 补不上就维持缺失。去重那边按"缺失即不参与判定"处理，不会因此误判。
            }
        }
        return new FileChange(
            entry.Path, ChangeKind.Unchanged, entry, prev, prev.HeadHash, prev.FullHash, prev.Storage,
            TailHash: tail);
    }

    /// <summary>
    /// 已确定变更的这一条要算哪些 hash。
    /// <para>
    /// 要算全文时（打包成员）**一遍读拿全三段**：全文那一趟本来就路过头和尾，分别调三个方法
    /// 等于把同一个文件打开三次。首次备份几十万个小文件，省下的就是几十万次多余的 open + seek。
    /// </para>
    /// <para>
    /// 全文被延后时（单文件 blob）只算 head——**尾部这里不算**：那条路的三段 hash 都由压缩
    /// 那一遍顺手算出并覆盖此处的值（见编排器的 tailByPath 与 StreamAndStageAsync），
    /// 在这里算等于白读一次。head 仍要算，它顺带回答了"这个文件此刻打得开吗"，
    /// 读不开的在这里就被判成 Unreadable（沿用旧条目），而不是几小时后倒在压缩里。
    /// </para>
    /// </summary>
    private async Task<(string? Head, string? Full, string? Tail)> IdentityAsync(
        ScannedEntry entry, string full, DiffOptions options, bool deferFull, CancellationToken ct)
    {
        if (DeferrableFullHash(entry, deferFull))
            return (await hasher.HeadHashAsync(full, options.HeadHashBytes, ct), null, null);

        // 符号链接与空文件没有内容可读，一趟都不必付。
        if (entry.Kind != EntryKind.File || entry.Length == 0)
            return (await hasher.HeadHashAsync(full, options.HeadHashBytes, ct),
                await hasher.FullHashAsync(full, ct), null);

        var id = await hasher.ContentIdentityAsync(full, options.HeadHashBytes, ct);
        return (id.HeadHash, id.FullHash, id.TailHash);
    }

    /// <summary>
    /// 读失败（被占用/无权限/读到一半设备错误）不该终止整轮备份。
    /// 精确捕获这两类，**不要**写成 catch(Exception)：OperationCanceledException 不派生自它们，
    /// 写宽了会把取消也变成「跳过一个文件」，备份看起来成功、实际没跑完。
    /// </summary>
    private static async Task<FileChange> TryReadAsync(
        Func<Task<FileChange>> build, ScannedEntry entry, IndexEntry? prev)
    {
        try
        {
            return await build();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileChange(entry.Path, ChangeKind.Unreadable, entry, prev, null, null, null, ex.Message);
        }
    }
}
