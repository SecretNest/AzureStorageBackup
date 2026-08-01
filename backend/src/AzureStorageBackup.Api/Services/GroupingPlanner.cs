namespace AzureStorageBackup.Api.Services;

/// <summary>参与规划的变更文件（来自 diff 的 Added/Modified）。</summary>
/// <param name="FullHash">全文哈希；<c>null</c> = 延后到压缩那一遍再算。
/// 只有走单文件 blob 的条目允许为空——那条路上 hash 与压缩共用同一遍读（<c>StreamAndStageAsync</c>），
/// 算出来的值还会**覆盖** diff 记下的那个，所以 diff 阶段再整个读一遍纯属白读；
/// 而 <c>data/{hash}</c> 这个内容地址没有第二次补算的机会，因此延后的值一旦真走到
/// <see cref="GroupingPlanner.Plan"/> 的寻址那一支，会被当场拒绝（装箱那一支则容忍空值——
/// symlink 本来就没有内容 hash）。</param>
public sealed record PlannedFile(string Path, long Length, string? FullHash);

public sealed record PlanOptions
{
    /// <summary>超过此尺寸不入组，单文件处理（默认 5M，M4 §6）。</summary>
    public long SingleFileThresholdBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>单组上限（压缩前，默认 100M）。</summary>
    public long GroupCapBytes { get; init; } = 100 * 1024 * 1024;

    /// <summary>
    /// 单组成员数上限（默认 2 万）。<see cref="GroupCapBytes"/> 管不住这一头：文件越小，
    /// 同样 100 MB 装进去的成员越多——1 字节的文件能装进上亿个，而那一箱的成员表是**整件**
    /// 拿在手上的（压缩要它、重校验要它、失败重试也要它）。
    /// <para>
    /// 定在 2 万的依据是实测的 7z 内存：成员元数据约 <b>1.3 KB/个</b>（与压缩级别无关，
    /// -mx1 与 -mx9 同曲线），加上我们自己的 <see cref="PlannedFile"/> 约 0.4 KB，
    /// 合计约 1.7 KB/成员。2 万 ≈ 51 MB/箱。
    /// </para>
    /// <para>
    /// 对平均 ≥ 5 KB 的文件这条界是**空操作**（100 MB 的字节上限先到），所以它不改变常规备份的
    /// 装箱结果——只在它存在的理由真出现时才生效。
    /// </para>
    /// </summary>
    public int MaxPackMembers { get; init; } = 20_000;

    /// <summary>
    /// 单组成员路径在 7z 命令行上占的字节上限（默认 1 MB）。
    /// <para>
    /// 这一条治的是**硬故障**，不是内存：成员路径是逐个作为 argv 传给 7z 的
    /// （见 <c>SevenZipCompressor.CompressAsync</c>），超了内核直接 <c>E2BIG</c>，压缩当场失败。
    /// 实测这台机器上单次 exec 的 argv 上限是 <b>1.73 MB</b>（ARG_MAX 2 MB / stack 8 MB），
    /// 52 字符的相对路径下 34,218 个成员通过、34,375 个失败。
    /// </para>
    /// <para>
    /// 必须按**字节**而不是按成员数设界：墙的位置随路径长度缩水。同样 1.73 MB，52 字符的路径
    /// 能放三万多个，150 字符的只剩一万二，500 字符的只剩三千多。固定成员数在长路径下照样撞。
    /// 默认留了约 40% 余量给环境变量与其它参数。
    /// </para>
    /// <para>
    /// 不改用 <c>@listfile</c> 绕开：列表文件按行分隔，而 Linux 路径里可以有换行——切错的表现是
    /// 备份少传文件、还不报错。也不能分批追加：本项目用 <c>-v</c> 分卷，7z 拒绝更新多卷归档。
    /// </para>
    /// </summary>
    public long MaxPackPathBytes { get; init; } = 1_000_000;

    /// <summary>跨路径打包列表（gitignore 语法）：命中者允许**跨目录**装箱，而不是按目录切分。
    /// 为散列分片目录（Emby/Jellyfin 元数据、Git objects、各类缓存——目录极多、每个目录没几个文件）
    /// 而设：那种结构下按目录切分会让包数逼近文件数，分组打包的意义（合并小文件、减少 blob 数）归零，
    /// 而每个包都是一次 7z 进程加一次计费的上传请求。默认空 = 全部按目录打包，与历史行为一致。</summary>
    public IgnoreRuleSet? CrossDirGroup { get; init; }

    /// <summary>不分组列表（gitignore 语法）：命中者单文件处理。</summary>
    public IgnoreRuleSet? DontGroup { get; init; }

    /// <summary>pack 编号起点（编排器传"现有最大 pack 号 + 1"以避免冲突）。</summary>
    public int FirstPackNumber { get; init; } = 1;
}

/// <summary>单文件 blob：内容寻址到 data/{fullHash}。</summary>
public sealed record BlobEntry(string Path, string FullHash)
{
    public string Ref => "data/" + FullHash;
}

/// <summary>pack 成员：entryName 为归档内条目名（= 完整相对路径，供还原定位）。</summary>
public sealed record PackEntry(string Path, string EntryName, string FullHash, long Length);

/// <param name="GroupKey">编排器据此把 pack 归入同一个处理池（池内可增量重组、池间并发）。
/// 按目录打包时是目录路径；跨路径打包时每个 pack 自成一池，既保持并发又不必把成千上万个
/// 跨目录文件塞进同一个串行池里。</param>
public sealed record PlannedPack(string PackId, IReadOnlyList<PackEntry> Members, string GroupKey)
{
    public long OriginalBytes => Members.Sum(m => m.Length);
}

public sealed record BackupPlan(IReadOnlyList<BlobEntry> Blobs, IReadOnlyList<PlannedPack> Packs);

/// <summary>一个条目该走哪条路。</summary>
public enum FileCategory
{
    /// <summary>单文件 blob：超尺寸或命中不分组列表。变更判定一出来就能立刻压缩上传，不等任何人。</summary>
    SingleFile,

    /// <summary>按目录合并：要等**整个目录**都 diff 完才能封箱（未变的、读不开的、内容其实没变的都不进包）。</summary>
    DirectoryGroup,

    /// <summary>跨目录合并：按扫描顺序边 diff 边填包、填满即封。</summary>
    CrossDirectoryGroup,
}

/// <summary>一个条目的归类结果。<see cref="GroupKey"/> 仅按目录合并时有值（= 直接父目录）。</summary>
public sealed record FileClass(FileCategory Category, string? GroupKey);

/// <summary>
/// 全部扫描条目的归类。<see cref="DirectoryCandidates"/> 给出每个目录组有多少个候选成员——
/// 流水线据此知道"这个目录还差几个没 diff 完"，从而确定封箱时机。
/// </summary>
public sealed record Classification(
    IReadOnlyDictionary<string, FileClass> ByPath,
    IReadOnlyDictionary<string, int> DirectoryCandidates);

/// <summary>
/// 分组规划（M4 设计 §6）：决定变更文件走单文件 blob 还是分组 pack。
/// 超尺寸/命中不分组列表 → 单文件；其余同一目录（不含子目录）小文件合并成 pack，
/// 受单组上限拆分。纯函数，不做实际压缩/上传。
/// </summary>
public sealed class GroupingPlanner
{
    /// <summary>一个成员在 7z 命令行上占的字节：路径的 UTF-8 长度 + 结尾的 NUL。
    /// 按 UTF-8 而不是字符数——中日韩路径一个字符最多三字节，按字符数记会低估两倍，
    /// 而低估的后果是 <c>E2BIG</c>：压缩当场失败。</summary>
    public static long EntryArgBytes(string path) =>
        System.Text.Encoding.UTF8.GetByteCount(path) + 1;

    /// <summary>
    /// 已经攒了这些的一组，再收下 <paramref name="next"/> 会不会越界。
    /// <para>
    /// 三条界谁先到算谁，**装箱的每一处都必须用这一个判断**：规划器那个纯函数、编排器里边 diff
    /// 边填的跨目录累加、以及压缩前 <c>ProcessPackAsync</c> 的重新切分。三处但凡有一处口径不同，
    /// 「实际产出与规划器一致」那条不变量就破了，而破了之后最先出事的是去重与保留清理
    /// （它们按成员分组认包）。
    /// </para>
    /// </summary>
    public static bool GroupIsFull(
        int members, long bytes, long pathBytes, PlannedFile next, PlanOptions options) =>
        bytes + next.Length > options.GroupCapBytes
        || members + 1 > options.MaxPackMembers
        || pathBytes + EntryArgBytes(next.Path) > options.MaxPackPathBytes;

    /// <summary>
    /// 扫描一结束就能定下的归类。三条判定只看 <c>Path</c> 与 <c>Length</c>——**不需要**任何哈希，
    /// 因此不必等 diff：<see cref="PlannedFile.FullHash"/> 只用来生成 <c>data/{hash}</c> 这个内容地址，
    /// 与"走单文件还是走分组"无关。这正是流水线化的前提。
    /// <para>
    /// 判定顺序与 <see cref="Plan"/> 逐字一致（不分组 &gt; 跨路径 &gt; 按目录），否则同一个文件会
    /// 在归类和装箱两处被分到不同的路上。
    /// </para>
    /// </summary>
    public Classification Classify(IReadOnlyList<ScannedEntry> entries, PlanOptions? options = null)
    {
        options ??= new PlanOptions();

        var byPath = new Dictionary<string, FileClass>(entries.Count, StringComparer.Ordinal);
        var dirCandidates = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.Length >= options.SingleFileThresholdBytes
                || (options.DontGroup?.MatchesFileOrAncestorDir(entry.Path) ?? false))
            {
                byPath[entry.Path] = new FileClass(FileCategory.SingleFile, null);
            }
            else if (options.CrossDirGroup?.MatchesFileOrAncestorDir(entry.Path) ?? false)
            {
                byPath[entry.Path] = new FileClass(FileCategory.CrossDirectoryGroup, null);
            }
            else
            {
                var dir = Directory(entry.Path);
                byPath[entry.Path] = new FileClass(FileCategory.DirectoryGroup, dir);
                dirCandidates[dir] = dirCandidates.GetValueOrDefault(dir) + 1;
            }
        }

        return new Classification(byPath, dirCandidates);
    }

    public BackupPlan Plan(IReadOnlyList<PlannedFile> files, PlanOptions? options = null)
    {
        options ??= new PlanOptions();

        var blobs = new List<BlobEntry>();
        var byDirectory = new List<PlannedFile>();
        var crossDirectory = new List<PlannedFile>();

        foreach (var file in files)
        {
            // 优先级：不分组 > 跨路径打包 > 按目录打包。「不分组」是最强的意思表示——
            // 它说的是"这个文件根本不该和别人合并"，不该被后面的规则翻案。
            if (file.Length >= options.SingleFileThresholdBytes
                || (options.DontGroup?.MatchesFileOrAncestorDir(file.Path) ?? false))
                // data/{hash} 是内容地址，没有 hash 就没有地址。单文件 blob 允许把全文 hash 延后到
                // 压缩那一遍再算（见 PlannedFile.FullHash），但那条路是编排器直接送去压缩的、不经过
                // 这里。真有延后的值走到这里就是接错了线：与其拼出一个 "data/" 的空地址悄悄传上去
                // （要到还原那天才会发现指不到 blob），不如当场炸掉。
                blobs.Add(new BlobEntry(file.Path, file.FullHash
                    ?? throw new InvalidOperationException(
                        $"Cannot address '{file.Path}': its full hash has not been computed yet.")));
            else if (options.CrossDirGroup?.MatchesFileOrAncestorDir(file.Path) ?? false)
                crossDirectory.Add(file);
            else
                byDirectory.Add(file);
        }

        var packs = BuildPacks(byDirectory, crossDirectory, options);
        return new BackupPlan(blobs, packs);
    }

    private static IReadOnlyList<PlannedPack> BuildPacks(
        List<PlannedFile> byDirectory, List<PlannedFile> crossDirectory, PlanOptions options)
    {
        var packs = new List<PlannedPack>();
        var packNumber = options.FirstPackNumber;

        // 按直接父目录分组，目录内按路径排序，保证确定性编号。
        var byDir = byDirectory
            .GroupBy(f => Directory(f.Path), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var dir in byDir)
            Fill(dir.OrderBy(f => f.Path, StringComparer.Ordinal), groupKey: dir.Key);

        // 跨路径：无视目录边界，按完整路径排序后顺序装箱。路径排序天然让同目录的文件相邻，
        // 所以局部性并没有丢——还原一个目录仍然只碰少数几个包——只是包不再因为目录换了就被迫封存。
        Fill(crossDirectory.OrderBy(f => f.Path, StringComparer.Ordinal), groupKey: null);

        return packs;

        void Fill(IEnumerable<PlannedFile> ordered, string? groupKey)
        {
            var current = new List<PackEntry>();
            long currentBytes = 0;
            long currentPathBytes = 0;

            foreach (var file in ordered)
            {
                // 撞到三条界中的任意一条 → 封存当前 pack，另起一个（见 GroupIsFull）。
                if (current.Count > 0 && GroupIsFull(current.Count, currentBytes, currentPathBytes, file, options))
                {
                    Seal(current, groupKey);
                    current = [];
                    currentBytes = 0;
                    currentPathBytes = 0;
                }

                // 这里不拒空：symlink 本来就没有内容 hash（差分对它一律返回 null），而 symlink 是
                // 可以被打进包的——7z 存的是链接本身。延后计算则只发生在单文件 blob 上，不经过装箱。
                current.Add(new PackEntry(file.Path, file.Path, file.FullHash!, file.Length));
                currentBytes += file.Length;
                currentPathBytes += EntryArgBytes(file.Path);
            }

            if (current.Count > 0)
                Seal(current, groupKey);
        }

        void Seal(List<PackEntry> members, string? groupKey)
        {
            var id = PackId(packNumber++);
            // 跨路径的包各自成池：池间是并发的，把成千上万个跨目录文件塞进同一个池会让它们退化成串行。
            packs.Add(new PlannedPack(id, members, groupKey ?? id));
        }
    }

    private static string Directory(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    private static string PackId(int number) => "p" + number.ToString("D4");
}
