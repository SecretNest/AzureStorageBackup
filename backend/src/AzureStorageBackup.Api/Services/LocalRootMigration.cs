using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The verdict logic for migrating the local root path (design docs/configuration.md).
///
/// **Static and dependency-free** on purpose: it does pure computation plus read-only filesystem access, and never touches
/// the database, the cloud, or decryption. The account/password/cloud info needed to fetch the sample is prepared by the
/// endpoint, which then hands the sample in. That way the whole tiering logic can be unit-tested away from HTTP, EF and
/// Azure — feed it a list of entries and a temp directory and you are done.
/// </summary>
public static class LocalRootMigration
{
    /// <summary>Sampling cap. 200 entries is enough to pin down "the wrong directory was typed in" without turning a preview into a full scan.</summary>
    public const int DefaultSampleSize = 200;

    /// <summary>How many mismatching example paths the report lists at most.</summary>
    public const int MaxExamples = 10;

    private const double OkThreshold = 0.95;
    private const double RejectThreshold = 0.05;

    /// <summary>
    /// Compare the new root against a sample of the baseline version and return a verdict. **Pure query**: read-only
    /// filesystem access, changes nothing, safely re-entrant — apply relies on running it a second time to cover the
    /// race between preview and apply.
    ///
    /// The caller is responsible for having done the path validation (exists / is a directory / inside the boundary) and the busy check first.
    ///
    /// Whether a comparison is possible depends **only on whether a baseline exists**, and has nothing to do with what the
    /// config's current root is or whether it is empty: a config imported without a SourceRootHint has an empty-string root,
    /// yet every one of its versions landed in the local catalog at import time — and that is exactly the case where the user
    /// is most likely guessing at a mount point, the last one deserving a free pass on the grounds of "we never recorded an old root".
    /// </summary>
    /// <param name="sample">The stratified sample of the latest version, from
    /// <see cref="VersionCatalog.SampleAsync"/>. Null when it cannot be fetched at all (no versions / no catalog);
    /// empty when the version holds nothing comparable (every entry carried over from an earlier one, whose recorded
    /// size and mtime were never guaranteed to match the disk). Both are "there is nothing to compare against", not a
    /// 0% match.</param>
    public static LocalRootPreviewResponse Inspect(string newRoot, IReadOnlyList<IndexEntry>? sample)
    {
        if (sample is null)
            return NoBaseline("This backup has no version index available to compare against.");
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
}
