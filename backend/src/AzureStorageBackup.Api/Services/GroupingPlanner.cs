namespace AzureStorageBackup.Api.Services;

/// <summary>参与规划的变更文件（来自 diff 的 Added/Modified）。</summary>
public sealed record PlannedFile(string Path, long Length, string FullHash);

public sealed record PlanOptions
{
    /// <summary>超过此尺寸不入组，单文件处理（默认 5M，M4 §6）。</summary>
    public long SingleFileThresholdBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>单组上限（压缩前，默认 100M）。</summary>
    public long GroupCapBytes { get; init; } = 100 * 1024 * 1024;

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

public sealed record PlannedPack(string PackId, IReadOnlyList<PackEntry> Members)
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
        var groupable = new List<PlannedFile>();

        foreach (var file in files)
        {
            var singleFile = file.Length >= options.SingleFileThresholdBytes
                || (options.DontGroup?.IsIgnored(file.Path) ?? false);

            if (singleFile)
                blobs.Add(new BlobEntry(file.Path, file.FullHash));
            else
                groupable.Add(file);
        }

        var packs = BuildPacks(groupable, options);
        return new BackupPlan(blobs, packs);
    }

    private static IReadOnlyList<PlannedPack> BuildPacks(List<PlannedFile> groupable, PlanOptions options)
    {
        var packs = new List<PlannedPack>();
        var packNumber = options.FirstPackNumber;

        // 按直接父目录分组，目录内按路径排序，保证确定性编号。
        var byDir = groupable
            .GroupBy(f => Directory(f.Path), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var dir in byDir)
        {
            var current = new List<PackEntry>();
            long currentBytes = 0;

            foreach (var file in dir.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                // 累加超过单组上限 → 封存当前 pack，另起一个。
                if (current.Count > 0 && currentBytes + file.Length > options.GroupCapBytes)
                {
                    packs.Add(new PlannedPack(PackId(packNumber++), current));
                    current = [];
                    currentBytes = 0;
                }

                current.Add(new PackEntry(file.Path, file.Path, file.FullHash, file.Length));
                currentBytes += file.Length;
            }

            if (current.Count > 0)
                packs.Add(new PlannedPack(PackId(packNumber++), current));
        }

        return packs;
    }

    private static string Directory(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    private static string PackId(int number) => "p" + number.ToString("D4");
}
