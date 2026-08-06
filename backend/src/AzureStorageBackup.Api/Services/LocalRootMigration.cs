using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 迁移本地根路径的判定逻辑（设计 docs/change-local-root-design.md）。
///
/// **静态、无依赖**是刻意的：它只做纯计算加只读文件系统访问，不碰数据库、不连云、不解密。
/// 取索引要用的账户/密码/云端信息由端点备好后把 baseline 传进来。这样整套分档逻辑
/// 能脱离 HTTP、EF 与 Azure 单测——喂一个假索引加一个临时目录就验得完。
/// </summary>
public static class LocalRootMigration
{
    /// <summary>抽样上限。200 条足够把「填错目录」摁住，又不至于让一次 preview 变成全量扫描。</summary>
    public const int DefaultSampleSize = 200;

    private const long SmallCeiling = 1L * 1024 * 1024;          // <1MB
    private const long MediumCeiling = 100L * 1024 * 1024;       // 1–100MB

    /// <summary>报告里最多列几条不匹配的样例路径。</summary>
    public const int MaxExamples = 10;

    private const double OkThreshold = 0.95;
    private const double RejectThreshold = 0.05;

    /// <summary>
    /// 比对新根与基线索引，给出判定。**纯查询**：只读文件系统，不改任何东西，可安全重入
    /// ——apply 正是靠再跑一遍它来兜住 preview 与 apply 之间的竞态。
    ///
    /// 调用方负责在此之前做完路径校验（存在/是目录/边界内）与忙检查。
    /// </summary>
    /// <param name="currentRoot">配置当前的根。为空表示导入时没拿到 SourceRootHint，无基线可比。</param>
    /// <param name="baseline">最新版本的索引；取不到（无版本/缓存缺失）时传 null。</param>
    public static LocalRootPreviewResponse Inspect(string? currentRoot, string newRoot, VersionIndex? baseline)
    {
        if (string.IsNullOrWhiteSpace(currentRoot))
            return NoBaseline("This backup has no local root recorded yet, so there is nothing to compare against.");
        if (baseline is null)
            return NoBaseline("This backup has no version index available to compare against.");

        var sample = Sample(baseline.Entries);
        if (sample.Count == 0)
            return NoBaseline("The latest version index has no comparable entries.");

        var matched = 0;
        var missing = 0;
        var sizeMismatch = 0;
        var mtimeDiffers = 0;
        var examples = new List<string>();

        foreach (var entry in sample)
        {
            var full = Path.Combine(newRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var outcome = Compare(entry, full, ref mtimeDiffers);
            switch (outcome)
            {
                case Outcome.Matched:
                    matched++;
                    break;
                case Outcome.Missing:
                    missing++;
                    if (examples.Count < MaxExamples) examples.Add(entry.Path);
                    break;
                case Outcome.SizeMismatch:
                    sizeMismatch++;
                    if (examples.Count < MaxExamples) examples.Add(entry.Path);
                    break;
            }
        }

        var rate = (double)matched / sample.Count;
        // 区间左闭右开，边界值归入更宽松的一档。
        var verdict = rate >= OkThreshold
            ? LocalRootVerdict.Ok
            : rate >= RejectThreshold
                ? LocalRootVerdict.NeedsConfirm
                : LocalRootVerdict.Rejected;

        return new LocalRootPreviewResponse(
            verdict.ToString(), sample.Count, matched, missing, sizeMismatch, mtimeDiffers,
            rate, Reason: null, examples);
    }

    private enum Outcome { Matched, Missing, SizeMismatch }

    /// <summary>
    /// 单条比对。判定只看「存在 + size」；mtime 单独计数但**不影响结果**
    /// ——跨文件系统搬迁时它经常整体偏移，让它参与判定会把一次完全正确的迁移判成失败。
    /// </summary>
    private static Outcome Compare(IndexEntry entry, string fullPath, ref int mtimeDiffers)
    {
        // symlink 的 IndexEntry.Length 恒为 0（LocalFileScanner.cs:170），比 size 毫无意义，
        // 只确认这个位置上还是个链接。
        //
        // **不许再加 Exists**：FileInfo.Exists 对**指向目录**的链接答 false（它问的是"这是不是个
        // 文件"，而链接解析过去是目录），可扫描侧登记 symlink 只看 LinkTarget 非空
        // （LocalFileScanner.cs:136），目录链接一样在索引里。加上这一项，每一个完好的目录链接
        // 都被判 Missing，把一次完全正确的迁移的匹配率生生压下去、逼进 force 那条路。
        // LinkTarget 非空本身已经说明"这儿确实躺着一个符号链接"，路径不存在时它是 null。
        if (string.Equals(entry.Kind, "symlink", StringComparison.Ordinal))
        {
            var link = new FileInfo(fullPath);
            return link.LinkTarget is not null ? Outcome.Matched : Outcome.Missing;
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return Outcome.Missing;

        // mtime 只在文件确实存在时才有得比；秒级容差吸收文件系统的时间戳粒度差异。
        if (Math.Abs((info.LastWriteTimeUtc - entry.Mtime.UtcDateTime).TotalSeconds) > 1)
            mtimeDiffers++;

        return info.Length == entry.Length ? Outcome.Matched : Outcome.SizeMismatch;
    }

    private static LocalRootPreviewResponse NoBaseline(string reason) => new(
        nameof(LocalRootVerdict.NoBaseline), Sampled: 0, Matched: 0, Missing: 0,
        SizeMismatch: 0, MtimeDiffers: 0, MatchRate: 0, Reason: reason, Examples: []);

    /// <summary>
    /// 从索引条目里分层抽样。按 Length 分四档（0 / &lt;1MB / 1–100MB / &gt;100MB），
    /// 每档按档内条目数占比分名额，**档内等距取样**而非取头部——索引顺序近似目录序，
    /// 取头部会把样本全压在第一个子目录里，那样「只挂上了其中一个子目录」这种半对半错的
    /// 迁移就恰好检不出来。
    ///
    /// 带 UnreadableAt 的条目排除在外：它们的 size/mtime 沿用上一版本，本就不保证与磁盘一致。
    /// </summary>
    public static IReadOnlyList<IndexEntry> Sample(IReadOnlyList<IndexEntry> entries, int max = DefaultSampleSize)
    {
        var pool = entries.Where(e => e.UnreadableAt is null).ToList();
        if (pool.Count <= max)
            return pool;

        var buckets = new List<IndexEntry>[4];
        for (var i = 0; i < buckets.Length; i++) buckets[i] = [];
        foreach (var e in pool)
            buckets[BucketOf(e.Length)].Add(e);

        // 按占比分名额，然后把空档/不足档的余额还给还装得下的档，避免样本白白浪费。
        //
        // **非空档保底 1 个**：纯按占比算，一个「500 个小文件 + 1 个大文件」的索引里，
        // 大文件那档四舍五入下来是 0 个名额，于是唯一那个大文件永远抽不到——而大文件恰恰是
        // 最值得看一眼的（挂错盘时它们往往就是缺的那批）。四档最多占用 4 个保底名额，
        // 对 200 的上限无足轻重。
        var quota = new int[buckets.Length];
        for (var i = 0; i < buckets.Length; i++)
            quota[i] = buckets[i].Count == 0
                ? 0
                : Math.Clamp((int)((long)max * buckets[i].Count / pool.Count), 1, buckets[i].Count);

        var assigned = quota.Sum();

        // 保底可能把总额顶过上限（max 小于非空档数时）。从名额最多的档往回收，
        // 保底的那 1 个不动——收成 0 就等于把整档丢掉，正是保底要防的事。
        while (assigned > max)
        {
            var fattest = -1;
            for (var i = 0; i < buckets.Length; i++)
                if (quota[i] > 1 && (fattest < 0 || quota[i] > quota[fattest])) fattest = i;
            if (fattest < 0) break;   // 各档都只剩保底，收无可收
            quota[fattest]--;
            assigned--;
        }

        while (assigned < max)
        {
            var grew = false;
            for (var i = 0; i < buckets.Length && assigned < max; i++)
            {
                if (quota[i] >= buckets[i].Count) continue;
                quota[i]++;
                assigned++;
                grew = true;
            }
            if (!grew) break;   // 全部档都装满了（pool.Count > max 时不会发生，保险起见）
        }

        var result = new List<IndexEntry>(max);
        for (var i = 0; i < buckets.Length; i++)
            result.AddRange(TakeEvenly(buckets[i], quota[i]));
        return result;
    }

    private static int BucketOf(long length) => length switch
    {
        0 => 0,
        < SmallCeiling => 1,
        < MediumCeiling => 2,
        _ => 3,
    };

    /// <summary>档内等距取样：把 count 个位置均匀铺在整个列表上，而不是取前 count 个。</summary>
    private static IEnumerable<IndexEntry> TakeEvenly(List<IndexEntry> items, int count)
    {
        if (count <= 0) yield break;
        if (count >= items.Count)
        {
            foreach (var e in items) yield return e;
            yield break;
        }

        for (var i = 0; i < count; i++)
            yield return items[(int)((long)i * items.Count / count)];
    }
}
