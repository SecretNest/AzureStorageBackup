using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The verdict logic for migrating the local root path (design docs/configuration.md).
///
/// **Static and dependency-free** on purpose: it does pure computation plus read-only filesystem access, and never touches
/// the database, the cloud, or decryption. The account/password/cloud info needed to fetch the index is prepared by the
/// endpoint, which then hands the baseline in. That way the whole tiering logic can be unit-tested away from HTTP, EF and
/// Azure — feed it a fake index and a temp directory and you are done.
/// </summary>
public static class LocalRootMigration
{
    /// <summary>Sampling cap. 200 entries is enough to pin down "the wrong directory was typed in" without turning a preview into a full scan.</summary>
    public const int DefaultSampleSize = 200;

    private const long SmallCeiling = 1L * 1024 * 1024;          // <1MB
    private const long MediumCeiling = 100L * 1024 * 1024;       // 1–100MB

    /// <summary>How many mismatching example paths the report lists at most.</summary>
    public const int MaxExamples = 10;

    private const double OkThreshold = 0.95;
    private const double RejectThreshold = 0.05;

    /// <summary>
    /// Compare the new root against the baseline index and return a verdict. **Pure query**: read-only filesystem access,
    /// changes nothing, safely re-entrant — apply relies on running it a second time to cover the race between preview and apply.
    ///
    /// The caller is responsible for having done the path validation (exists / is a directory / inside the boundary) and the busy check first.
    ///
    /// Whether a comparison is possible depends **only on whether a baseline exists**, and has nothing to do with what the
    /// config's current root is or whether it is empty: a config imported without a SourceRootHint has an empty-string root,
    /// yet its version indexes all landed in the local cache at import time (BackupConfigEndpoints.cs:110-127) — and that is
    /// exactly the case where the user is most likely guessing at a mount point, the last one deserving a free pass on the grounds of "we never recorded an old root".
    /// </summary>
    /// <param name="baseline">The index of the latest version; pass null when it cannot be fetched (no versions / cache miss).</param>
    public static LocalRootPreviewResponse Inspect(string newRoot, VersionIndex? baseline)
    {
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
        // Half-open intervals: a boundary value falls into the more permissive tier.
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
    /// A single entry comparison. The verdict looks only at "exists + size"; mtime is counted separately but **does not affect
    /// the result** — moving across filesystems often shifts it wholesale, and letting it into the verdict would fail a perfectly correct migration.
    /// </summary>
    private static Outcome Compare(IndexEntry entry, string fullPath, ref int mtimeDiffers)
    {
        // A symlink's IndexEntry.Length is always 0 (LocalFileScanner.ScanDirectory builds the symlink ScannedEntry
        // with a hard-coded length of 0), so comparing size is meaningless;
        // all we confirm is that there is still a link at this position.
        //
        // **Do not add Exists back**: FileInfo.Exists answers false for a link pointing **at a directory** (it asks "is this a
        // file", and the link resolves to a directory), while the scanning side registers a symlink purely on LinkTarget being
        // non-null (LocalFileScanner.ScanDirectory: `var isSymlink = info.LinkTarget is not null;`), so directory links
        // are in the index too. Add that check and every intact directory
        // link is judged Missing, dragging the match rate of a perfectly correct migration down and forcing it onto the force path.
        // A non-null LinkTarget already says "there really is a symlink lying here"; it is null when the path does not exist.
        if (string.Equals(entry.Kind, "symlink", StringComparison.Ordinal))
        {
            var link = new FileInfo(fullPath);
            return link.LinkTarget is not null ? Outcome.Matched : Outcome.Missing;
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return Outcome.Missing;

        // mtime is only comparable once the file actually exists; a one-second tolerance absorbs filesystem timestamp granularity differences.
        if (Math.Abs((info.LastWriteTimeUtc - entry.Mtime.UtcDateTime).TotalSeconds) > 1)
            mtimeDiffers++;

        return info.Length == entry.Length ? Outcome.Matched : Outcome.SizeMismatch;
    }

    private static LocalRootPreviewResponse NoBaseline(string reason) => new(
        nameof(LocalRootVerdict.NoBaseline), Sampled: 0, Matched: 0, Missing: 0,
        SizeMismatch: 0, MtimeDiffers: 0, MatchRate: 0, Reason: reason, Examples: []);

    /// <summary>
    /// Stratified sampling over the index entries. Four buckets by Length (0 / &lt;1MB / 1–100MB / &gt;100MB), each bucket
    /// getting a quota proportional to its share of the entries, and **sampled evenly within the bucket** rather than taken
    /// from the head — index order approximates directory order, so taking the head piles the whole sample into the first
    /// subdirectory, and a half-right migration like "only one of the subdirectories got mounted" is exactly what slips through.
    ///
    /// Entries carrying UnreadableAt are excluded: their size/mtime are carried over from the previous version and were never guaranteed to match the disk.
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

        // Hand out quotas by share, then give the leftovers from empty/underfilled buckets back to the buckets that can still take more, so no sample budget is wasted.
        //
        // **A non-empty bucket is guaranteed 1**: on pure proportion, in an index of "500 small files + 1 large file" the
        // large bucket rounds down to a quota of 0, so that one large file is never sampled — and large files are exactly the
        // ones worth a look (when the wrong disk is mounted they are often precisely the batch that is missing). Four buckets
        // consume at most 4 guaranteed slots, which is negligible against a cap of 200.
        var quota = new int[buckets.Length];
        for (var i = 0; i < buckets.Length; i++)
            quota[i] = buckets[i].Count == 0
                ? 0
                : Math.Clamp((int)((long)max * buckets[i].Count / pool.Count), 1, buckets[i].Count);

        var assigned = quota.Sum();

        // The guarantee can push the total past the cap (when max is smaller than the number of non-empty buckets). Claw back
        // from the fattest bucket, leaving that guaranteed 1 alone — clawing it to 0 drops the whole bucket, which is exactly what the guarantee prevents.
        while (assigned > max)
        {
            var fattest = -1;
            for (var i = 0; i < buckets.Length; i++)
                if (quota[i] > 1 && (fattest < 0 || quota[i] > quota[fattest])) fattest = i;
            if (fattest < 0) break;   // every bucket is down to its guaranteed slot; nothing left to claw back
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
            if (!grew) break;   // every bucket is full (cannot happen while pool.Count > max; belt and braces)
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

    /// <summary>Evenly spaced sampling within a bucket: spread count positions across the whole list instead of taking the first count.</summary>
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
