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
