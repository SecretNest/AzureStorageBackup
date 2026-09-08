using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>How one path changed relative to the previous version.</summary>
public enum ChangeKind
{
    /// <summary>Not present in the previous version → added.</summary>
    Added,

    /// <summary>Content changed; needs reprocessing/re-upload.</summary>
    Modified,

    /// <summary>Content unchanged, only mtime/permissions changed → update index metadata only, no re-upload.</summary>
    MetadataOnly,

    /// <summary>Could not be opened this run (in use / no permission / read error). Neither a change nor a deletion:
    /// the index carries the previous version's entry forward and stamps UnreadableAt on it — it must never be treated as deleted.</summary>
    Unreadable,

    /// <summary>Completely unchanged (length+mtime+permissions all match, never hashed).</summary>
    Unchanged,

    /// <summary>Present in the previous version, absent this time → deleted.</summary>
    Deleted,
}

/// <summary>The diff result for one path, carrying the resolved hashes/storage needed to build the new index entry.</summary>
public sealed record FileChange(
    string Path,
    ChangeKind Kind,
    ScannedEntry? Current,
    IndexEntry? Previous,
    string? HeadHash,
    string? FullHash,
    StorageRef? CarriedStorage,
    /// <summary>Why the read failed (ex.Message). Non-null only when Kind == Unreadable.</summary>
    string? UnreadableReason = null,
    /// <summary>
    /// Tail hash. The fourth component of content identity, used together with fullHash + length + head for dedup and collision checks.
    /// <para>
    /// On the single-file blob path it falls out of the compression pass for free (the run ledger's SetTailAsync), so what it is really here
    /// for is **pack members** — they used to have none of it, so they could only dedup on three components. Since the criterion is four components
    /// everywhere else, it should be four here too; the two paths should not each have their own standard.
    /// </para>
    /// <para>
    /// **Not backfilled for unchanged files**: such a file pays no IO at all, so reading it for this one component is a random read conjured out of
    /// nothing (close to an hour for 500k small files on a NAS spinning disk), while the hardening it buys has vanishingly small marginal value. Pack
    /// members in old indexes therefore stay missing it forever, and dedup treats it as "missing means it does not take part in the check".
    /// </para>
    /// </summary>
    string? TailHash = null);

public sealed record DiffOptions
{
    /// <summary>How many leading bytes headHash covers (4KB by default, M4 decision §13.3).</summary>
    public int HeadHashBytes { get; init; } = 4096;
}

/// <summary>
/// What a diff adds up to: it hands back no list of changes at all, only these three numbers — every change it made
/// went out through the callback as it was decided. ChangedFiles/ChangedBytes count
/// Added+Modified only (uncompressed, before grouping; deletions/metadata-only excluded, §4); Emitted is how many
/// changes went through the callback, which is the only remaining way for a caller to know the diff really covered
/// everything it was given.
/// </summary>
public sealed record DiffTotals(int ChangedFiles, long ChangedBytes, int Emitted);

/// <summary>
/// Version comparison engine (M4 design §4.2): lazy two-level hashing.
/// Decide on length+mtime+permissions first; only files with "same length but changed mtime/permissions" get a headHash,
/// and only if that differs is fullHash computed. Avoids re-reading every file on every backup.
/// <para>
/// The comparison is a **merge of two path-ordered cursors**: the scan on one side, the previous version's index on the
/// other, walked in lockstep. It used to load the whole previous index into a dictionary, keep a set of every path seen
/// and return a list of every change — three structures that each grew with the file count, which is what put a
/// multi-million-file backup into the swap. The merge holds one entry from each side and pushes every change straight
/// out through the callback instead.
/// </para>
/// </summary>
public sealed class BackupDiffer(IFileHasher hasher)
{
    /// <summary>
    /// Compares the scan against the previous version, delivering every change to <paramref name="onChange"/> as it is
    /// decided and returning only the totals.
    /// <para>
    /// Both cursors **must** be in <see cref="string.CompareOrdinal(string, string)"/> path order. Neither side
    /// orders by the path text: the scan cursor reads the work database by <c>path_key</c> and the catalog reads its
    /// entries by <c>path_key</c> too, both of which are the path's UTF-16 big-endian bytes, whose byte order *is*
    /// ordinal order (the two part company at the first surrogate pair — see the remarks on <see cref="RunWorkDb"/>).
    /// So this costs the callers nothing, and it is what lets the diff run in constant memory. A cursor that breaks
    /// it is refused by name rather than diffed wrongly.
    /// </para>
    /// </summary>
    /// <param name="current">The scanned entries, in ordinal path order.</param>
    /// <param name="previous">The previous version's entries, in ordinal path order; null on a first run.</param>
    /// <param name="unreadable">What the scan could not read, from the scan summary.</param>
    /// <param name="tracker">
    /// On a first backup this step reads every file end to end to hash it, which can run for hours. Without it the UI is
    /// a 0% that never moves, and the user has no way to tell whether it is working or hung.
    /// </param>
    /// <param name="onChange">
    /// Invoked once per change, and it is now the **only** way out: the caller sees the scanned entries in scan order as
    /// they are classified (so the orchestrator can push settled work to the compress/upload side while the diff is
    /// still running — a first backup's diff takes hours, and during those hours not one byte used to go over the
    /// network), followed by the Unreadable entries the unreadable pass synthesizes. Deletions arrive in between, as
    /// soon as the previous cursor passes a path over.
    /// </param>
    /// <param name="fullHashDeferred">
    /// Which entries may skip computing the full-content hash here (the orchestrator passes "everything classified as a
    /// single-file blob"). On that path the hash falls out of the compression read for free and then overwrites the
    /// value recorded here — having the diff read it too means reading every large file end to end twice. For a 100 GB
    /// file, that saves a full 100 GB of reads. Only applies to classifications that are "already known to have
    /// changed" (Added, and Modified due to a length change); the two-level hashing path for "same length, changed
    /// mtime" is **unaffected** — the fullHash there is exactly what decides whether it is MetadataOnly or a real
    /// Modified, and skipping it would re-upload every unchanged file.
    /// </param>
    public async Task<DiffTotals> DiffAsync(
        string rootPath,
        IAsyncEnumerable<ScannedEntry> current,
        IAsyncEnumerable<IndexEntry>? previous,
        IReadOnlyList<UnreadablePath> unreadable,
        DiffOptions? options,
        CancellationToken ct,
        StageTracker? tracker,
        Func<FileChange, CancellationToken, Task> onChange,
        Func<ScannedEntry, bool>? fullHashDeferred)
    {
        options ??= new DiffOptions();
        var root = Path.GetFullPath(rootPath);

        var unreadableDirs = new List<string>();
        var unreadableFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in unreadable)
        {
            if (u.IsDirectory)
                unreadableDirs.Add(u.Path);
            else
                unreadableFiles.Add(u.Path);
        }

        // The one structure in here that is not O(1): previous-version entries the scan did not have **and** that lie
        // under something the scan reported unreadable. They cannot be settled on the spot — a previous entry under an
        // unreadable directory becomes Unreadable, not Deleted, and that verdict is only reachable once the merge is
        // done — so they wait here. It is bounded by the size of the unreadable subtrees: empty on a healthy run, and
        // at its worst (an unreadable root) the whole previous index, which is precisely what the old implementation
        // held unconditionally on *every* run. So this is strictly smaller than what it replaces.
        var parkedUnderUnreadable = new List<IndexEntry>();
        // Both bounded by unreadable.Count — a handful of paths on any run worth finishing.
        var emittedUnreadable = new HashSet<string>(StringComparer.Ordinal);
        var scannedUnreadableFiles = new HashSet<string>(StringComparer.Ordinal);

        var changedFiles = 0;
        long changedBytes = 0;
        var emitted = 0;

        var currentOrder = new AscendingPaths("scan");
        var previousOrder = new AscendingPaths("previous version");

        await using var cur = current.GetAsyncEnumerator(ct);
        await using var prev = previous?.GetAsyncEnumerator(ct);

        var haveCur = await NextCurrentAsync();
        var havePrev = await NextPreviousAsync();

        while (haveCur)
        {
            ct.ThrowIfCancellationRequested();
            var entry = cur.Current;
            // Publish the current path **before** processing it: when things hang, "which file is it stuck on" is exactly what you need to know.
            tracker?.Touch(entry.Path);

            // The scanner never emits an entry for a path it also reports unreadable, but the unreadable pass below
            // would synthesize a second, content-less change for it if it ever did, and that entry would overwrite the
            // real one's storage in the index. Remembering the overlap costs a set bounded by unreadable.Count.
            if (unreadableFiles.Contains(entry.Path))
                scannedUnreadableFiles.Add(entry.Path);

            // Everything in the previous version that sorts before this entry is gone from the scan.
            while (havePrev && string.CompareOrdinal(prev!.Current.Path, entry.Path) < 0)
            {
                await PassedOverAsync(prev.Current);
                havePrev = await NextPreviousAsync();
            }

            IndexEntry? match = null;
            if (havePrev && string.CompareOrdinal(prev!.Current.Path, entry.Path) == 0)
            {
                match = prev.Current;
                havePrev = await NextPreviousAsync();
            }

            var full = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var kind = entry.Kind == EntryKind.File ? "file" : "symlink";
            var deferFull = fullHashDeferred?.Invoke(entry) ?? false;
            var change = match is null
                ? await AddedAsync(entry, full, options, deferFull, tracker, ct)
                : await CompareAsync(entry, match, full, kind, options, deferFull, tracker, ct);

            if (change.Kind is ChangeKind.Added or ChangeKind.Modified)
            {
                changedFiles++;
                changedBytes += entry.Length;
            }
            // The byte ledger is fed by the read itself (TrackedIdentityAsync's increments), not here:
            // only classifications that actually read content report, chunk by chunk, so unchanged files and
            // deferred full hashes still contribute nothing (counting a 100 GB deferred file in full would
            // spike the speed to tens of GB/s and turn the remaining time into a joke — pinned by
            // Deferred_Files_Do_Not_Inflate_The_Read_Byte_Count), and a large file's read moves the numbers
            // while it runs instead of landing as one lump at the end. Advance only counts the entry.
            tracker?.Advance(0);

            await EmitAsync(change);
            haveCur = await NextCurrentAsync();
        }

        // Whatever is left on the previous cursor is past the end of the scan.
        while (havePrev)
        {
            ct.ThrowIfCancellationRequested();
            await PassedOverAsync(prev!.Current);
            havePrev = await NextPreviousAsync();
        }

        // Paths that were already unreadable during the scan are classified last, from the parked list.
        // For a directory whose contents cannot be listed, its **entire subtree** went unscanned — without this pass
        // every one of those existing entries would be classified as deleted, i.e. one permission failure would wipe a
        // whole subtree out of the index and nobody would notice until a restore came up short. Unreadable ≠ deleted,
        // and this is the most critical place that rule applies.
        // Both loops are O(unreadable.Count × parked.Count): the first factor is a handful of paths, and the second is
        // zero unless one of them was a directory that actually covered something.
        foreach (var u in unreadable)
        {
            ct.ThrowIfCancellationRequested();

            if (u.IsDirectory)
            {
                foreach (var p in parkedUnderUnreadable)
                {
                    if (PathUnder.IsUnder(u.Path, p.Path) && emittedUnreadable.Add(p.Path))
                        await EmitAsync(new FileChange(p.Path, ChangeKind.Unreadable, null, p, null, null, null, u.Reason));
                }
                continue;
            }

            var carried = parkedUnderUnreadable.FirstOrDefault(
                p => string.Equals(p.Path, u.Path, StringComparison.Ordinal));
            if (carried is not null)
            {
                if (emittedUnreadable.Add(carried.Path))
                    await EmitAsync(new FileChange(carried.Path, ChangeKind.Unreadable, null, carried, null, null, null, u.Reason));
            }
            // An unreadable **file** gets an entry even when the previous version has none (brand new and unreadable
            // from the start): there is no content to point at so it will not be in the index, but the operator has to
            // know it was not backed up this run.
            else if (!scannedUnreadableFiles.Contains(u.Path) && emittedUnreadable.Add(u.Path))
            {
                await EmitAsync(new FileChange(u.Path, ChangeKind.Unreadable, null, null, null, null, null, u.Reason));
            }
        }

        return new DiffTotals(changedFiles, changedBytes, emitted);

        async ValueTask<bool> NextCurrentAsync()
        {
            if (!await cur.MoveNextAsync())
                return false;
            currentOrder.Require(cur.Current.Path);
            return true;
        }

        async ValueTask<bool> NextPreviousAsync()
        {
            if (prev is null || !await prev.MoveNextAsync())
                return false;
            previousOrder.Require(prev.Current.Path);
            return true;
        }

        // A previous-version entry the scan did not have. It is a deletion unless something the scan could not read
        // covers it, in which case its real verdict is Unreadable and only the pass after the merge can say so.
        async Task PassedOverAsync(IndexEntry entry)
        {
            if (unreadableFiles.Contains(entry.Path) || unreadableDirs.Exists(d => PathUnder.IsUnder(d, entry.Path)))
            {
                parkedUnderUnreadable.Add(entry);
                return;
            }

            await EmitAsync(new FileChange(entry.Path, ChangeKind.Deleted, null, entry, null, null, null));
        }

        async Task EmitAsync(FileChange change)
        {
            emitted++;
            await onChange(change, ct);
        }
    }

    /// <summary>
    /// Guards that a cursor really is in ascending ordinal path order. The merge's correctness rests entirely on that
    /// assumption, and a source that quietly breaks it does not fail — it produces a **wrong diff**: the entries the
    /// merge walked past come back as an Added/Deleted pair, so a file is re-uploaded and its history is dropped, and
    /// nothing anywhere says so. One string comparison per entry buys an exception that names the offending path.
    /// </summary>
    private sealed class AscendingPaths(string source)
    {
        private string? _last;

        public void Require(string path)
        {
            if (_last is not null && string.CompareOrdinal(path, _last) <= 0)
                throw new InvalidOperationException(
                    $"The {source} cursor is not in ascending ordinal path order: '{path}' came after '{_last}'. " +
                    "The diff walks the scan and the previous version in lockstep and cannot recover from a mis-ordered source.");
            _last = path;
        }
    }

    private async Task<FileChange> CompareAsync(
        ScannedEntry entry, IndexEntry prev, string full, string kind, DiffOptions options, bool deferFull,
        StageTracker? tracker, CancellationToken ct)
    {
        // A type change (file<->symlink) counts as a content change.
        if (prev.Kind != kind)
            return await ModifiedAsync(entry, prev, full, options, deferFull, tracker, ct);

        if (entry.Kind == EntryKind.Symlink)
            return entry.Target == prev.Target
                ? Unchanged(entry, prev)
                : new FileChange(entry.Path, ChangeKind.Modified, entry, prev, null, null, null);

        // Different length → changed outright, no head pre-screen needed.
        if (entry.Length != prev.Length)
            return await ModifiedAsync(entry, prev, full, options, deferFull, tracker, ct);

        // Same length, same mtime and same permissions → unchanged, skip hashing entirely.
        if (entry.ModifiedAt == prev.Mtime && entry.Permissions == prev.Permissions)
            return Unchanged(entry, prev);

        // Same length, changed mtime or permissions → ask from cheap to expensive: head 4KB → tail 4KB → whole file.
        // The whole-file pass is the only expensive move here (a 100 GB file means 100 GB of reads), and as soon as the head or the tail
        // disagrees, "the content changed" is already established and that pass never has to be paid for.
        return await TryReadAsync(async () =>
        {
            var head = await hasher.HeadHashAsync(full, options.HeadHashBytes, ct);
            if (head != prev.HeadHash)
                return await DecidedChangedAsync(entry, prev, full, options, deferFull, head, null, tracker, ct);

            // Head matches, so ask the tail. Only ask when **the full hash can be deferred**: files on that path are by definition over the
            // single-file threshold (a few MB up to hundreds of GB), so 4KB may buy out an entire full-file read — a sure win.
            // Pack members are the other way round — they are small, and once classified Modified the fullHash still has to be computed and
            // written into the index, so exiting early saves nothing and just wastes one open + seek.
            // A null prev.TailHash is an old index's entry, written before this field existed; there is nothing to compare against, so skip
            // this probe. It is not backfilled anywhere — Unchanged() carries the null forward on purpose (see content-identity.md
            // § "An unchanged entry reads nothing"): backfilling would be a random read conjured out of nothing, ~5-10 ms per file on a
            // NAS spinning disk, close to an hour for 500,000 files. A file that actually gets modified fills it in naturally.
            if (deferFull && prev.TailHash is not null)
            {
                var tail = await hasher.TailHashAsync(full, options.HeadHashBytes, ct);
                if (tail != prev.TailHash)
                    return await DecidedChangedAsync(entry, prev, full, options, deferFull, head, tail, tracker, ct);
            }

            // Head and tail both match: only a full read can tell "the content really changed" from "it just got touched".
            // This pass **must not** be skipped because of deferFull — skipping it means treating everything as changed, i.e. every
            // touch re-uploads the file.
            //
            // Since the whole file is being read anyway, grab all three segments in one pass: the tail is picked up along the way, no extra IO.
            // Old entries missing a tail get filled in when they land in this branch — but that is free, not a trip made on purpose.
            var id = await TrackedIdentityAsync(entry, full, options, tracker, ct);
            return id.FullHash == prev.FullHash
                ? new FileChange(entry.Path, ChangeKind.MetadataOnly, entry, prev, id.HeadHash, id.FullHash,
                    prev.Storage, TailHash: id.TailHash)
                : new FileChange(entry.Path, ChangeKind.Modified, entry, prev, id.HeadHash, id.FullHash, null,
                    TailHash: id.TailHash);
        }, entry, prev);
    }

    /// <summary>
    /// The head or the tail has already proven the content changed; all that is left is filling the entry in.
    /// <para>
    /// When the full hash can be deferred (single-file blob) it is **not computed** — the compression pass produces it for free and overwrites
    /// this value, and the question "did it change" already has an answer that does not need it. This is exactly the pass the early exit saves:
    /// a 100 GB file whose head or tail moved is settled by reading 4KB, where it used to cost a full read.
    /// </para>
    /// <para>
    /// When it cannot be deferred (pack members) it still gets computed — the index entry needs it and the next diff compares against it. Since
    /// the whole file has to be read, take all three segments in that one pass.
    /// </para>
    /// </summary>
    private async Task<FileChange> DecidedChangedAsync(
        ScannedEntry entry, IndexEntry prev, string full, DiffOptions options, bool deferFull,
        string head, string? tail, StageTracker? tracker, CancellationToken ct)
    {
        if (DeferrableFullHash(entry, deferFull))
            return new FileChange(entry.Path, ChangeKind.Modified, entry, prev, head, null, null, TailHash: tail);

        var id = await TrackedIdentityAsync(entry, full, options, tracker, ct);
        return new FileChange(
            entry.Path, ChangeKind.Modified, entry, prev, id.HeadHash, id.FullHash, null, TailHash: id.TailHash);
    }

    private async Task<FileChange> AddedAsync(
        ScannedEntry entry, string full, DiffOptions options, bool deferFull, StageTracker? tracker, CancellationToken ct)
    {
        if (entry.Kind == EntryKind.Symlink)
            return new FileChange(entry.Path, ChangeKind.Added, entry, null, null, null, null);

        return await TryReadAsync(async () =>
        {
            var id = await IdentityAsync(entry, full, options, deferFull, tracker, ct);
            return new FileChange(
                entry.Path, ChangeKind.Added, entry, null, id.Head, id.Full, null, TailHash: id.Tail);
        }, entry, null);
    }

    private async Task<FileChange> ModifiedAsync(
        ScannedEntry entry, IndexEntry prev, string full, DiffOptions options, bool deferFull, StageTracker? tracker, CancellationToken ct)
    {
        if (entry.Kind == EntryKind.Symlink)
            return new FileChange(entry.Path, ChangeKind.Modified, entry, prev, null, null, null);

        // Record the complete headHash + fullHash (an index entry must carry the source file's hash/size/permissions for later diffs and restore comparison).
        // By the time we get here the content is **known** to have changed (the type flipped, or the length does not match), so fullHash has only two uses left:
        // generating the data/{hash} address, and being written into the index — and on the single-file blob path both of those get redone with the
        // value the compression pass computes. So deferring is lossless.
        return await TryReadAsync(async () =>
        {
            var id = await IdentityAsync(entry, full, options, deferFull, tracker, ct);
            return new FileChange(
                entry.Path, ChangeKind.Modified, entry, prev, id.Head, id.Full, null, TailHash: id.Tail);
        }, entry, prev);
    }

    /// <summary>
    /// Whether this entry's full hash can really be deferred. The point of deferring is to avoid "reading the whole file again just to hash it",
    /// and for 0 bytes that read is free — more importantly, the orchestrator never sends an empty file through compression, so nobody ever comes
    /// back to fill the value in: deferring would leave a forever-null fullHash in the index, the next diff would compare it against a freshly
    /// computed value and necessarily find them unequal, and so every single run would reclassify that empty file as changed.
    /// </summary>
    private static bool DeferrableFullHash(ScannedEntry entry, bool deferFull) => deferFull && entry.Length > 0;

    /// <summary>
    /// This entry did not change at all — **not one byte is read**, everything is carried over from the previous version's entry.
    /// <para>
    /// There used to be a backfill here computing the tail hash for old entries that lacked it, so old backups would self-heal. It was removed:
    /// an unchanged file pays no IO at all, so backfilling it is a random read conjured out of nothing — measured at 0.033 ms/file on SSD, while
    /// one random IO on a NAS spinning disk is 5-10 ms, which for 500k small files is close to an hour. All it buys is moving pack-member dedup
    /// from a three-component criterion to a four-component one, while the real line of defense has always been fullHash (xxh128 over the whole
    /// file), so those 4KB have vanishingly small marginal value. The trade does not pay.
    /// </para>
    /// <para>
    /// So pack members in old indexes stay missing this component forever, and dedup treats it as "missing means it does not take part in the
    /// check" (see <see cref="LocalDedupResolver.TryFindPackMember"/>). Newly written entries all carry it, and any file
    /// that gets modified fills it in naturally.
    /// </para>
    /// </summary>
    private static FileChange Unchanged(ScannedEntry entry, IndexEntry prev) =>
        new(entry.Path, ChangeKind.Unchanged, entry, prev, prev.HeadHash, prev.FullHash, prev.Storage,
            TailHash: prev.TailHash);

    /// <summary>
    /// Which hashes to compute for an entry already known to have changed.
    /// <para>
    /// When the full hash is needed (pack members), **take all three segments in one read**: the full-file pass already goes past the head and
    /// the tail, so calling three separate methods opens the same file three times. On a first backup of a few hundred thousand small files, that
    /// saves a few hundred thousand redundant open + seek pairs.
    /// </para>
    /// <para>
    /// When the full hash is deferred (single-file blob) only the head is computed — **the tail is not computed here**: on that path all three
    /// hash segments fall out of the compression pass for free and overwrite the values from here (see the run ledger's SetTailAsync and
    /// StreamAndStageAsync), so computing it here is a wasted read. The head is still computed; it also answers "can this file be opened right now",
    /// so an unreadable file is classified Unreadable here (carrying the old entry forward) instead of falling over inside compression hours later.
    /// </para>
    /// </summary>
    private async Task<(string? Head, string? Full, string? Tail)> IdentityAsync(
        ScannedEntry entry, string full, DiffOptions options, bool deferFull, StageTracker? tracker, CancellationToken ct)
    {
        if (DeferrableFullHash(entry, deferFull))
            return (await hasher.HeadHashAsync(full, options.HeadHashBytes, ct), null, null);

        // Symlinks and empty files have no content to read; not a single pass has to be paid for.
        if (entry.Kind != EntryKind.File || entry.Length == 0)
            return (await hasher.HeadHashAsync(full, options.HeadHashBytes, ct),
                await hasher.FullHashAsync(full, ct), null);

        var id = await TrackedIdentityAsync(entry, full, options, tracker, ct);
        return (id.HeadHash, id.FullHash, id.TailHash);
    }

    /// <summary>
    /// The one full-content read of a diff decision, registered the way the repair's hash gate registers its
    /// read: the file appears as an in-flight item with its size, and the read feeds cumulative bytes into it
    /// (increments dialect — see ItemProgressFromIncrements). Without this, a first backup's hash of a 100 GB
    /// file is a motionless file name for many minutes, indistinguishable from a hang — the exact silence the
    /// repair side had a field incident over. This is also the diff stage's whole byte ledger now: chunks are
    /// booked as they are read, so the speed is the disk's real pace instead of one lump per finished file.
    /// </summary>
    private async Task<ContentIdentity> TrackedIdentityAsync(
        ScannedEntry entry, string full, DiffOptions options, StageTracker? tracker, CancellationToken ct)
    {
        if (tracker is null)
            return await hasher.ContentIdentityAsync(full, options.HeadHashBytes, ct);
        tracker.BeginItem(entry.Path, entry.Path, entry.Length, wire: false); // a local read, not a transfer
        try
        {
            return await hasher.ContentIdentityAsync(
                full, options.HeadHashBytes, ct, tracker.ItemProgressFromIncrements(entry.Path));
        }
        finally
        {
            tracker.EndItem(entry.Path, 0);
        }
    }

    /// <summary>
    /// A read failure (in use / no permission / device error midway through) must not abort the whole backup run.
    /// Catch exactly these two types, and **do not** write catch(Exception): OperationCanceledException does not derive from them,
    /// and catching too broadly turns a cancellation into "skipped one file" — the backup looks successful while it never finished.
    /// </summary>
    private static async Task<FileChange> TryReadAsync(
        Func<Task<FileChange>> build, ScannedEntry entry, IndexEntry? prev)
    {
        try
        {
            return await build();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileChange(entry.Path, ChangeKind.Unreadable, entry, prev, null, null, null, ex.Message);
        }
    }
}
