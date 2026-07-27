namespace AzureStorageBackup.Api.Services;

/// <summary>参与规划的变更文件（来自 diff 的 Added/Modified）。</summary>
public sealed record PlannedFile(string Path, long Length, string FullHash);

public sealed record PlanOptions
{
    /// <summary>超过此尺寸不入组，单文件处理（默认 5M，M4 §6）。</summary>
    public long SingleFileThresholdBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>单组上限（压缩前，默认 100M）。</summary>
    public long GroupCapBytes { get; init; } = 100 * 1024 * 1024;

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

/// <summary>
/// 分组规划（M4 设计 §6）：决定变更文件走单文件 blob 还是分组 pack。
/// 超尺寸/命中不分组列表 → 单文件；其余同一目录（不含子目录）小文件合并成 pack，
/// 受单组上限拆分。纯函数，不做实际压缩/上传。
/// </summary>
public sealed class GroupingPlanner
{
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
                blobs.Add(new BlobEntry(file.Path, file.FullHash));
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

            foreach (var file in ordered)
            {
                // 累加超过单组上限 → 封存当前 pack，另起一个。
                if (current.Count > 0 && currentBytes + file.Length > options.GroupCapBytes)
                {
                    Seal(current, groupKey);
                    current = [];
                    currentBytes = 0;
                }

                current.Add(new PackEntry(file.Path, file.Path, file.FullHash, file.Length));
                currentBytes += file.Length;
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
