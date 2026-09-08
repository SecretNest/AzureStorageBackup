namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The arithmetic behind the stratified sample the local-root preview compares a new directory against: which length
/// bucket an entry falls into, how much of the budget each bucket gets, and which positions inside a bucket are
/// picked.
/// <para>
/// It lives on its own — apart from both the verdict logic (<see cref="LocalRootMigration"/>) and the storage that
/// answers it (<see cref="VersionCatalog.SampleAsync"/>) — because the sample used to be computed by walking a whole
/// in-memory index and is now computed by SQL over the catalog. Those are two very different ways of getting at the
/// rows, and they must not become two different ideas of <em>which</em> rows: the quota rules below are the only
/// thing that decides whether the one large file in an index of small ones is ever looked at, and a second
/// hand-written copy of them is exactly how that guarantee quietly stops holding on one side.
/// </para>
/// </summary>
internal static class SamplePlan
{
    /// <summary>Four buckets by length: empty / &lt;1MB / 1–100MB / &gt;100MB.</summary>
    public const int BucketCount = 4;

    private const long SmallCeiling = 1L * 1024 * 1024;          // <1MB
    private const long MediumCeiling = 100L * 1024 * 1024;       // 1–100MB

    public static int BucketOf(long length) => length switch
    {
        0 => 0,
        < SmallCeiling => 1,
        < MediumCeiling => 2,
        _ => 3,
    };

    /// <summary>
    /// The same partition as <see cref="BucketOf"/>, as a SQL fragment to AND into a caller's <c>WHERE</c>. The
    /// bounds are interpolated constants, never user input, so there is nothing here a string could smuggle in.
    /// </summary>
    public static string Predicate(int bucket) => bucket switch
    {
        0 => "length = 0",
        1 => $"length > 0 AND length < {SmallCeiling}",
        2 => $"length >= {SmallCeiling} AND length < {MediumCeiling}",
        _ => $"length >= {MediumCeiling}",
    };

    /// <summary>
    /// Hands out the sample budget by each bucket's share of the pool, then gives the leftovers from empty or
    /// underfilled buckets back to the buckets that can still take more, so no budget is wasted.
    /// <para>
    /// <b>A non-empty bucket is guaranteed 1</b>: on pure proportion, in a pool of "500 small files + 1 large file"
    /// the large bucket rounds down to a quota of 0, so that one large file is never sampled — and large files are
    /// exactly the ones worth a look (when the wrong disk is mounted they are often precisely the batch that is
    /// missing). Four buckets consume at most 4 guaranteed slots, negligible against a cap of 200.
    /// </para>
    /// </summary>
    /// <param name="counts">How many comparable entries each bucket holds.</param>
    /// <param name="max">The sample cap. A pool at or below it is taken whole.</param>
    public static int[] Quotas(IReadOnlyList<int> counts, int max)
    {
        var quota = new int[counts.Count];
        var pool = 0;
        for (var i = 0; i < counts.Count; i++)
            pool += counts[i];

        if (pool <= max)
        {
            for (var i = 0; i < counts.Count; i++)
                quota[i] = counts[i];
            return quota;
        }

        for (var i = 0; i < counts.Count; i++)
            quota[i] = counts[i] == 0
                ? 0
                : Math.Clamp((int)((long)max * counts[i] / pool), 1, counts[i]);

        var assigned = quota.Sum();

        // The guarantee can push the total past the cap (when max is smaller than the number of non-empty buckets).
        // Claw back from the fattest bucket, leaving that guaranteed 1 alone — clawing it to 0 drops the whole
        // bucket, which is exactly what the guarantee prevents.
        while (assigned > max)
        {
            var fattest = -1;
            for (var i = 0; i < counts.Count; i++)
                if (quota[i] > 1 && (fattest < 0 || quota[i] > quota[fattest])) fattest = i;
            if (fattest < 0) break;   // every bucket is down to its guaranteed slot; nothing left to claw back
            quota[fattest]--;
            assigned--;
        }

        while (assigned < max)
        {
            var grew = false;
            for (var i = 0; i < counts.Count && assigned < max; i++)
            {
                if (quota[i] >= counts[i]) continue;
                quota[i]++;
                assigned++;
                grew = true;
            }
            if (!grew) break;   // every bucket is full (cannot happen while pool > max; belt and braces)
        }

        return quota;
    }

    /// <summary>
    /// The positions to take inside one bucket, evenly spaced across the whole bucket rather than taken from its
    /// head: index order approximates directory order, so taking the head piles the whole sample into the first
    /// subdirectory, and a half-right migration like "only one of the subdirectories got mounted" is exactly what
    /// slips through.
    /// <para>
    /// Ascending and distinct, which is what lets the catalog turn each one straight into an <c>OFFSET</c>.
    /// </para>
    /// </summary>
    /// <param name="available">How many entries the bucket holds.</param>
    /// <param name="quota">How many of them to pick.</param>
    public static IEnumerable<int> Offsets(int available, int quota)
    {
        if (quota <= 0) yield break;
        if (quota >= available)
        {
            for (var i = 0; i < available; i++)
                yield return i;
            yield break;
        }

        for (var i = 0; i < quota; i++)
            yield return (int)((long)i * available / quota);
    }
}
