using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>What a diff produced, as the tests want to look at it: the totals plus every change in emission order.
/// The differ itself no longer hands back such a list — the whole point of the merge is that nothing proportional to
/// the file count is ever resident — but a test over a tree of a dozen files is precisely the caller for which
/// collecting them is free, and asserting against a list is far clearer than asserting inside a callback.</summary>
internal sealed record DiffOutcome(IReadOnlyList<FileChange> Changes, int ChangedFiles, long ChangedBytes)
{
    /// <summary>The single change for one path. A path the diff said nothing about fails here rather than further
    /// down on a null.</summary>
    public FileChange this[string path] => Changes.Single(c => c.Path == path);
}

/// <summary>
/// Scanning and diffing a directory the way the orchestrator does, minus the work database: walk into a
/// <see cref="ListScanSink"/>, sort the entries into the ordinal order both cursors must be in, and drive
/// <see cref="BackupDiffer.DiffAsync"/> over them.
/// <para>
/// The sort is what the run's <c>scan</c> table gives the pipeline for free (it is read by <c>path_key</c>); a test
/// that skipped it would feed the merge an unordered cursor and be refused by name.
/// </para>
/// </summary>
internal static class DiffTestHarness
{
    /// <summary>The scan, entries sorted the way the merge requires.</summary>
    internal static async Task<(List<ScannedEntry> Entries, ScanSummary Summary)> ScanSortedAsync(
        string root, ScanOptions? options = null)
    {
        var sink = new ListScanSink();
        var summary = await new LocalFileScanner().ScanAsync(root, new IgnoreRuleSet([]), sink, options);
        sink.Entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (sink.Entries, summary);
    }

    /// <summary>Scan <paramref name="root"/> and diff it against <paramref name="previous"/>, collecting every change.
    /// <paramref name="previous"/>'s entries are sorted here for the same reason the scan's are: an index written by
    /// a run that hit an unreadable directory carries the carried-forward entries at its tail, out of order.</summary>
    internal static async Task<DiffOutcome> RunDiffAsync(
        this BackupDiffer differ,
        string root,
        VersionIndex? previous,
        DiffOptions? options = null,
        CancellationToken ct = default,
        StageTracker? tracker = null,
        Func<FileChange, CancellationToken, Task>? onChange = null,
        Func<string, bool>? fullHashDeferred = null,
        ScanOptions? scanOptions = null)
    {
        var (entries, summary) = await ScanSortedAsync(root, scanOptions);
        return await differ.RunDiffAsync(
            root, entries, summary.Unreadable, previous, options, ct, tracker, onChange, fullHashDeferred);
    }

    /// <summary>The same, over a scan the caller has already taken (and already ordered).</summary>
    internal static async Task<DiffOutcome> RunDiffAsync(
        this BackupDiffer differ,
        string root,
        IReadOnlyList<ScannedEntry> entries,
        IReadOnlyList<UnreadablePath> unreadable,
        VersionIndex? previous,
        DiffOptions? options = null,
        CancellationToken ct = default,
        StageTracker? tracker = null,
        Func<FileChange, CancellationToken, Task>? onChange = null,
        Func<string, bool>? fullHashDeferred = null)
    {
        var changes = new List<FileChange>();
        var totals = await differ.DiffAsync(
            root,
            entries.ToAsyncEnumerable(),
            previous is null
                ? null
                : previous.Entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToAsyncEnumerable(),
            unreadable, options, ct, tracker,
            async (change, token) =>
            {
                changes.Add(change);
                // Only the changes produced from a **scanned** entry are relayed, which is the contract the
                // orchestrator's own callback works to: the Unreadable and Deleted ones carry no current entry and
                // give a caller nothing to upload.
                if (onChange is not null && change.Current is not null)
                    await onChange(change, token);
            },
            fullHashDeferred is null ? null : entry => fullHashDeferred(entry.Path));

        return new DiffOutcome(changes, totals.ChangedFiles, totals.ChangedBytes);
    }
}
