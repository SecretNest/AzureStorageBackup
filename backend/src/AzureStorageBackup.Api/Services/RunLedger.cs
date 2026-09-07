using System.Runtime.CompilerServices;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>A file whose content changed during processing: the settled hash/metadata override the diff-time index
/// entry (§9). Public and out here, rather than private to the orchestrator, because the ledger is now what holds
/// them.</summary>
public sealed record EntryOverride(string FullHash, string? HeadHash, long Length, DateTimeOffset Mtime);

/// <summary>
/// Everything a run knows about the version it is building, in the run's own scratch database instead of in memory.
/// <para>
/// It replaces four path-keyed dictionaries the orchestrator used to carry — the storage each path was uploaded to,
/// the tail hash the compression pass produced, the identity a file that changed mid-run settled on, and the paths
/// that stopped being readable after the diff had passed them — plus the diff's own list of changes that they were
/// applied to at the end. All five grew with the file count, and on a multi-million-file backup that is the memory
/// that put the process into swap; here the growth is a table, and the finish streams.
/// </para>
/// <para>
/// <b>Reads see committed writes.</b> Writes are batched onto the work database's writer task, so a value written a
/// moment ago may not be visible yet. The pipeline's mid-run questions (<see cref="StorageAsync"/>,
/// <see cref="HasOverrideAsync"/>, <see cref="IsPostDiffUnreadableAsync"/>) are asked after the stage that answers
/// them has settled, and <see cref="FlushAsync"/> is what makes that boundary real — the same thing the dictionaries
/// got for free, and the one thing that has to be said out loud now.
/// </para>
/// </summary>
public sealed class RunLedger(RunWorkDb work)
{
    /// <summary>One draft row per change, in the order the diff emitted them — which is the order the index has to
    /// be written back in.</summary>
    public ValueTask SeedAsync(int seq, FileChange change, CancellationToken ct) =>
        work.SeedDraftAsync(seq, change, ct);

    /// <summary>Where this run put the path (<c>storageByPath[path] = …</c>).</summary>
    public ValueTask SetStorageAsync(string path, StorageRef storage, CancellationToken ct) =>
        work.UpdateDraftStorageAsync(path, storage, ct);

    /// <summary>The tail hash that fell out of the compression pass (<c>tailByPath[path] = …</c>).</summary>
    public ValueTask SetTailAsync(string path, string tailHash, CancellationToken ct) =>
        work.SetDraftTailAsync(path, tailHash, ct);

    /// <summary>The identity the file settled on after it changed under the run (<c>overrides[path] = …</c>).</summary>
    public ValueTask SetOverrideAsync(string path, EntryOverride ov, CancellationToken ct) =>
        work.SetDraftOverrideAsync(path, ov.FullHash, ov.HeadHash, ov.Length, ov.Mtime, ct);

    /// <summary>Readable at diff time, unreadable when the upload stage reopened it
    /// (<c>postDiffUnreadable[path] = reason</c>).</summary>
    public ValueTask MarkPostDiffUnreadableAsync(string path, string reason, CancellationToken ct) =>
        work.SetDraftPostDiffUnreadableAsync(path, reason, ct);

    public Task<StorageRef?> StorageAsync(string path, CancellationToken ct) => work.DraftStorageAsync(path, ct);

    public Task<bool> HasOverrideAsync(string path, CancellationToken ct) => work.DraftHasOverrideAsync(path, ct);

    public Task<bool> IsPostDiffUnreadableAsync(string path, CancellationToken ct) =>
        work.DraftPostDiffUnreadableAsync(path, ct);

    public Task<long> PostDiffUnreadableCountAsync(CancellationToken ct) =>
        work.DraftPostDiffUnreadableCountAsync(ct);

    /// <summary>Commits everything written so far, so the reads above and the finish below see it.</summary>
    public Task FlushAsync(CancellationToken ct) => work.FlushAsync(ct);

    /// <summary>The paths the diff could not read, in emission order — what the operator is warned about, one line
    /// each. A path that only became unreadable later is <em>not</em> one of these; it is counted by
    /// <see cref="PostDiffUnreadableCountAsync"/>, exactly as the run's result adds the two rather than folding one
    /// into the other.</summary>
    public IAsyncEnumerable<string> UnreadablePathsAsync(CancellationToken ct) =>
        work.DraftPathsOfKindAsync(ChangeKind.Unreadable, ct);

    /// <summary>The same paths with the reason the system gave for each — "in use", "permission denied" and "device
    /// read error" want different things done about them, and the warning quotes the words verbatim. Streamed for the
    /// same reason as <see cref="UnreadablePathsAsync"/>: a dropped share makes this list as long as the share.</summary>
    public IAsyncEnumerable<(string Path, string Reason)> UnreadableAsync(CancellationToken ct) =>
        work.DraftPathsAndReasonsOfKindAsync(ChangeKind.Unreadable, ct);

    /// <summary>How many of them lie under one unreadable directory: that warning reports the subtree it took with
    /// it, rather than repeating itself for every file below.</summary>
    public Task<int> UnreadableUnderAsync(string dir, CancellationToken ct) =>
        work.DraftKindUnderCountAsync(ChangeKind.Unreadable, dir, ct);

    /// <summary>How many there are altogether — the figure the run's result reports beside the paths that only
    /// stopped being readable later. The empty prefix is the whole tree, not a directory named "".</summary>
    public Task<int> UnreadableCountAsync(CancellationToken ct) =>
        work.DraftKindUnderCountAsync(ChangeKind.Unreadable, "", ct);

    /// <summary>The run's summary line: paths added, changed and deleted, and the bytes that went with the
    /// deletions.</summary>
    public Task<(int New, int Modified, int Deleted, long DeletedBytes)> ChangeCountsAsync(CancellationToken ct) =>
        work.DraftChangeCountsAsync(ct);

    /// <summary>How many entries <see cref="FinalEntriesAsync"/> will yield, and what they weigh — answered in SQL
    /// over the same rows, so nobody has to stream a million entries to count them.</summary>
    public Task<(long Files, long Bytes)> FinalStatsAsync(CancellationToken ct) => work.DraftFinalStatsAsync(ct);

    public Task<int> FinalEntryCountAsync(CancellationToken ct) => work.DraftFinalCountAsync(ct);

    /// <summary>
    /// The new version's entries, in source order, one row at a time — the whole point of the exercise: the list this
    /// replaces held every entry of the new index in memory at once.
    /// <para>
    /// <c>now</c> is taken once for the pass rather than once per entry, as the code this replaces did. It only ever
    /// stamps <see cref="IndexEntry.UnreadableAt"/> on entries being marked unreadable for the first time, and that
    /// field answers "roughly how old is this stale content" — a question no sub-second difference bears on.
    /// </para>
    /// </summary>
    public async IAsyncEnumerable<IndexEntry> FinalEntriesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await foreach (var row in work.DraftFullOrderedBySeqAsync(ct))
            if (FinalEntry(row, now) is { } entry)
                yield return entry;
    }

    /// <summary>
    /// One draft row as the new version's entry, or null if it does not appear in the new version at all. This is
    /// the orchestrator's old <c>BuildEntries</c> body, per row and in the same order — the order matters, and the
    /// comments below are the reasons it was written that way.
    /// </summary>
    private static IndexEntry? FinalEntry(DraftFullRow row, DateTimeOffset now)
    {
        // Unreadable: carry the previous version's entry forward (including Storage, so nothing is re-uploaded and
        // dedup is unaffected), appending only UnreadableAt. When the previous version does not have the file, the
        // entry is skipped entirely — there is no content to point at.
        // Readable at diff time but unreadable when the compress/upload stage reopens it gets exactly the same
        // treatment: as far as the index is concerned, "this run failed to store the content" is one and the same
        // thing and should not grow a second set of rules.
        // This test must come before the has-no-current one: entries derived from an unreadable directory have **no**
        // current entry (the whole subtree was never scanned at all), and the other way round they would be skipped
        // as "no current state" and vanish from the new index — precisely the silent data loss this ordering fixes.
        if (row.State == DraftState.Unreadable || row.PostDiffReason is not null)
            // An entry that already carries an UnreadableAt keeps its original value: that field answers "since when
            // has this content been unable to update", and refreshing it to now every run erases the answer, leaving
            // only "it wasn't readable just now". Once some run reads it again, the entry is rebuilt normally and
            // the field returns to null.
            return row.Previous is { } previous
                ? previous with { UnreadableAt = previous.UnreadableAt ?? now }
                : null;

        if (row.State == DraftState.Dropped || !row.HasCurrent)
            return null;

        var ov = row.Override;
        // Judge by the length **that finally goes into the index**, not the one the diff saw: when the content
        // shrinks to an empty file during processing, the override is the truth for this entry.
        var length = ov?.Length ?? row.Entry.Length;
        return row.Entry with
        {
            Length = length,
            Mtime = ov?.Mtime ?? row.Entry.Mtime,
            HeadHash = ov?.HeadHash ?? row.Entry.HeadHash,
            // Tail hash precedence: for single-file blobs uploaded this run, use the value computed during the
            // compression pass (the most authoritative — those are the bytes that actually went into the archive);
            // otherwise use what the diff computed; otherwise inherit the previous version's entry. The middle tier
            // is the one that fills gaps: pack members used to have none of these, so they could only dedup on three
            // criteria, inconsistent with the four used on the single-file blob path. It is filled in by BackupDiffer
            // whenever an entry is actually read — added, modified, or metadata-only. An Unchanged entry is **not**
            // one of those and reaches the third tier with the previous version's value, null included: there is
            // deliberately no backfill pass, because it would be a random read conjured out of nothing on files
            // nobody touched (content-identity.md § "An unchanged entry reads nothing").
            TailHash = row.TailSet ?? row.Entry.TailHash ?? row.Previous?.TailHash,
            FullHash = ov?.FullHash ?? row.Entry.FullHash,
            // Only the carried-forward branch above stamps this. A row that reaches here was read this run, and the
            // draft's own columns may still hold the previous version's stamp — this is where it is dropped.
            UnreadableAt = null,
            // Zero-length regular files never carry a storage reference — including the ones **carried forward from
            // the previous version**. Empty files in old backups were compressed and uploaded like everything else,
            // and an empty file that never changes (.gitkeep, __init__.py, lock files, …) is judged Unchanged every
            // run, so the carried storage passes that old reference down generation after generation: if it recorded
            // the wrong raw flag back then, the user has no reason whatsoever to touch that file and it would never
            // get better. Cutting it off here makes the next backup self-heal, and the old blob is subsequently
            // reclaimed by retention cleanup.
            Storage = row.Entry.Kind == "file" && length == 0 ? null : row.Entry.Storage,
        };
    }
}
