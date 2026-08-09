namespace AzureStorageBackup.Api.Services;

/// <summary>A changed file taking part in planning (Added/Modified from the diff).</summary>
/// <param name="FullHash">Full-content hash; <c>null</c> = deferred to the compression pass.
/// Only entries taking the single-file blob route may be null — on that route the hash shares one read with compression (<c>StreamAndStageAsync</c>),
/// and the value it computes **overwrites** the one the diff recorded, so reading the whole file again during the diff is pure waste;
/// meanwhile the content address <c>data/{hash}</c> gets no second chance to be filled in, so once a deferred value actually reaches
/// the addressing branch of <see cref="GroupingPlanner.Plan"/> it is rejected on the spot (the packing branch does tolerate null —
/// a symlink has no content hash to begin with).</param>
public sealed record PlannedFile(string Path, long Length, string? FullHash);

public sealed record PlanOptions
{
    /// <summary>Anything above this size skips grouping and is handled as a single file (default 5M, M4 §6).</summary>
    public long SingleFileThresholdBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Per-group cap (pre-compression, default 100M).</summary>
    public long GroupCapBytes { get; init; } = 100 * 1024 * 1024;

    /// <summary>
    /// Per-group member count cap (default 20,000). <see cref="GroupCapBytes"/> cannot hold this end down: the smaller the files,
    /// the more members the same 100 MB swallows — one-byte files would fit by the hundreds of millions, and a pack's member list is held **in its entirety**
    /// in memory (compression needs it, re-verification needs it, retry after failure needs it too).
    /// <para>
    /// The 20,000 figure comes from measured 7z memory: about <b>1.3 KB per member</b> of metadata (independent of compression level,
    /// -mx1 and -mx9 follow the same curve), plus about 0.4 KB for our own <see cref="PlannedFile"/>,
    /// roughly 1.7 KB per member in total. 20,000 ≈ 51 MB per pack.
    /// </para>
    /// <para>
    /// For files averaging 5 KB or more this limit is **a no-op** (the 100 MB byte cap is hit first), so it does not change how ordinary
    /// backups get packed — it only takes effect when the reason it exists actually shows up.
    /// </para>
    /// </summary>
    public int MaxPackMembers { get; init; } = 20_000;

    /// <summary>
    /// Cap on the bytes a group's member paths occupy on the 7z command line (default 1 MB).
    /// <para>
    /// This one is about a **hard failure**, not memory: member paths are passed to 7z one by one as argv
    /// (see <c>SevenZipCompressor.CompressAsync</c>), and going over gets a flat <c>E2BIG</c> from the kernel — compression fails on the spot.
    /// Measured on this machine, the argv ceiling for a single exec is <b>1.73 MB</b> (ARG_MAX 2 MB / stack 8 MB):
    /// with 52-character relative paths, 34,218 members passed and 34,375 failed.
    /// </para>
    /// <para>
    /// The limit must be set in **bytes**, not member count: the wall moves as paths get longer. Within the same 1.73 MB, 52-character paths
    /// fit thirty-odd thousand, 150-character ones only twelve thousand, 500-character ones only three thousand-odd. A fixed member count still hits the wall on long paths.
    /// The default leaves about 40% headroom for environment variables and other arguments.
    /// </para>
    /// <para>
    /// We do not switch to <c>@listfile</c> to get around it: a list file is line-separated and Linux paths may contain newlines — splitting wrong shows up as
    /// the backup uploading fewer files, with no error at all. Nor can we append in batches: this project uses <c>-v</c> volumes, and 7z refuses to update a multi-volume archive.
    /// </para>
    /// </summary>
    public long MaxPackPathBytes { get; init; } = 1_000_000;

    /// <summary>Cross-path packing list (gitignore syntax): matches are allowed to pack **across directories** instead of being split per directory.
    /// Built for hash-sharded directory trees (Emby/Jellyfin metadata, Git objects, all kinds of caches — enormous numbers of directories with only a few files each):
    /// in that shape, splitting per directory drives the pack count toward the file count and zeroes out the whole point of grouped packing (merging small files, reducing blob count),
    /// while every pack costs one 7z process plus one billed upload request. Empty by default = everything packs per directory, matching historical behavior.</summary>
    public IgnoreRuleSet? CrossDirGroup { get; init; }

    /// <summary>Don't-group list (gitignore syntax): matches are handled as single files.</summary>
    public IgnoreRuleSet? DontGroup { get; init; }

    /// <summary>Don't-compress list (gitignore syntax): matches are stored, not compressed (<c>-mx0</c>).
    /// Packing uses it to split one directory into a "compressed pack" and a "stored pack" filled separately — a pack has exactly one compression mode,
    /// and mixing them would make the rule effectively nonexistent for the files that got packed (historical behavior: the whole pack always got <c>-mx9</c>).
    /// <para>
    /// If both sides are non-empty each becomes its own pack, with **no minimum-member fallback**: in an incremental backup a directory may have just two changed files this round,
    /// and "two packs holding one member each" is an acceptable normal case, not worth introducing an exception that would make packing results hard to predict.
    /// </para>
    /// <para>
    /// It shares one decision method with the single-file blob route (<c>BackupOrchestrator.HandleBlobAsync</c>),
    /// and the two must agree: when the same file changes routes because its size crossed the threshold, its compression mode should not change with it.
    /// </para></summary>
    public IgnoreRuleSet? DontCompress { get; init; }

    /// <summary>Starting pack number (the orchestrator passes "highest existing pack number + 1" to avoid collisions).</summary>
    public int FirstPackNumber { get; init; } = 1;
}

/// <summary>Single-file blob: content-addressed at data/{fullHash}.</summary>
public sealed record BlobEntry(string Path, string FullHash)
{
    public string Ref => "data/" + FullHash;
}

/// <summary>Pack member: entryName is the entry name inside the archive (= the full relative path, so restore can locate it).</summary>
public sealed record PackEntry(string Path, string EntryName, string FullHash, long Length);

/// <param name="GroupKey">The orchestrator uses this to file packs into the same processing pool (packs within a pool can be recombined incrementally, and pools run concurrently).
/// When packing per directory it is the directory path; when packing across paths each pack is its own pool, which keeps concurrency without stuffing tens of thousands of
/// cross-directory files into a single serial pool.</param>
/// <param name="StoreOnly">This pack is stored, not compressed (<c>-mx0</c>): every member matched <see cref="PlanOptions.DontCompress"/>.
/// The compression mode is fixed once by the planner and travels with the pack all the way — downstream (compression, dead-weight compaction, repair recompression) no longer derives it itself,
/// otherwise a change to the rules would silently switch an old pack to a different compression mode the next time the archive is rewritten.</param>
public sealed record PlannedPack(string PackId, IReadOnlyList<PackEntry> Members, string GroupKey, bool StoreOnly = false)
{
    public long OriginalBytes => Members.Sum(m => m.Length);
}

public sealed record BackupPlan(IReadOnlyList<BlobEntry> Blobs, IReadOnlyList<PlannedPack> Packs);

/// <summary>Which route an entry should take.</summary>
public enum FileCategory
{
    /// <summary>Single-file blob: over-sized, or matched the don't-group list. The moment the change verdict is in it can be compressed and uploaded, waiting for nobody.</summary>
    SingleFile,

    /// <summary>Merged per directory: the pack can only be sealed once **the entire directory** has finished diffing (unchanged files, unreadable ones, and ones whose content did not actually change all stay out of the pack).</summary>
    DirectoryGroup,

    /// <summary>Merged across directories: fill packs while diffing, in scan order, sealing each as it fills up.</summary>
    CrossDirectoryGroup,
}

/// <summary>The classification result for one entry. <see cref="GroupKey"/> only has a value when merging per directory (= the immediate parent directory).</summary>
public sealed record FileClass(FileCategory Category, string? GroupKey);

/// <summary>
/// The classification of every scanned entry. <see cref="DirectoryCandidates"/> gives how many candidate members each directory group has —
/// the pipeline uses it to know "how many entries in this directory are still un-diffed", and thereby when to seal the pack.
/// </summary>
public sealed record Classification(
    IReadOnlyDictionary<string, FileClass> ByPath,
    IReadOnlyDictionary<string, int> DirectoryCandidates);

/// <summary>
/// Grouping planner (M4 design §6): decides whether a changed file goes to a single-file blob or into a grouped pack.
/// Over-sized / matched the don't-group list → single file; the remaining small files in the same directory (excluding subdirectories) are merged into a pack,
/// split by the per-group cap. A pure function; it performs no actual compression or upload.
/// </summary>
public sealed class GroupingPlanner
{
    /// <summary>The bytes one member occupies on the 7z command line: the path's UTF-8 length + the trailing NUL.
    /// Counted in UTF-8 rather than characters — a CJK path takes up to three bytes per character, so counting characters underestimates by a factor of two,
    /// and the consequence of underestimating is <c>E2BIG</c>: compression fails on the spot.</summary>
    public static long EntryArgBytes(string path) =>
        System.Text.Encoding.UTF8.GetByteCount(path) + 1;

    /// <summary>
    /// Given a group that has accumulated this much, would taking <paramref name="next"/> as well go over a limit.
    /// <para>
    /// Whichever of the three limits is hit first wins, and **every packing site must use this one predicate**: the planner's pure function, the cross-directory
    /// accumulator that fills while diffing inside the orchestrator, and the re-splitting in <c>ProcessPackAsync</c> before compression. If even one of the three disagrees,
    /// the "actual output matches the planner" invariant breaks, and once broken the first things to go wrong are dedup and retention cleanup
    /// (they identify packs by their member grouping).
    /// </para>
    /// </summary>
    public static bool GroupIsFull(
        int members, long bytes, long pathBytes, PlannedFile next, PlanOptions options) =>
        bytes + next.Length > options.GroupCapBytes
        || members + 1 > options.MaxPackMembers
        || pathBytes + EntryArgBytes(next.Path) > options.MaxPackPathBytes;

    /// <summary>
    /// This group **already** cannot take any newcomer — no need to look at who the next one is.
    /// <para>
    /// <see cref="GroupIsFull"/> asks "would taking this one more go over a limit", so it cannot work without a next file.
    /// But two of the three limits do not depend on who the next one is: the member-count one is independent by definition; the path-byte one is because
    /// <see cref="EntryArgBytes"/> is always at least 1 (the path is non-empty, plus that trailing NUL), so once the cap is hit
    /// any newcomer at all would go over. Once either of these holds, the pack was already settled **the instant it filled up**.
    /// </para>
    /// <para>
    /// The byte limit is **deliberately not here**: a symlink's length can be 0 (the diff computes no content hash for it,
    /// and 0-byte ordinary files were kept out of packing long before), and <c>bytes + 0 &gt; GroupCapBytes</c> is false
    /// when the pack filled exactly to the cap. Include it, and the fill-while-diffing route would seal a pack earlier than the planner's pure function,
    /// breaking "actual output matches the planner" on the spot. Better to let the byte limit go on waiting for the next file as before.
    /// </para>
    /// <para>
    /// This predicate only affects **when** a pack is sealed, not **how** packs are divided: when it holds, the next file would necessarily make
    /// <see cref="GroupIsFull"/> hold too, so both routes seal the very same pack.
    /// </para>
    /// </summary>
    public static bool GroupTakesNoMore(int members, long pathBytes, PlanOptions options) =>
        members >= options.MaxPackMembers
        || pathBytes >= options.MaxPackPathBytes;

    /// <summary>
    /// The classification that can be settled the moment scanning ends. All three decisions look only at <c>Path</c> and <c>Length</c> — they need **no** hash at all,
    /// so there is no need to wait for the diff: <see cref="PlannedFile.FullHash"/> is only used to build the content address <c>data/{hash}</c>,
    /// and has nothing to do with "single file or grouped". This is precisely what makes pipelining possible.
    /// <para>
    /// The decision order is word for word the same as in <see cref="Plan"/> (don't-group &gt; cross-path &gt; per-directory), otherwise the same file would be
    /// sent down different routes by classification and by packing.
    /// </para>
    /// </summary>
    public Classification Classify(IReadOnlyList<ScannedEntry> entries, PlanOptions? options = null)
    {
        options ??= new PlanOptions();

        var byPath = new Dictionary<string, FileClass>(entries.Count, StringComparer.Ordinal);
        var dirCandidates = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.Length >= options.SingleFileThresholdBytes
                || (options.DontGroup?.MatchesFileOrAncestorDir(entry.Path) ?? false))
            {
                byPath[entry.Path] = new FileClass(FileCategory.SingleFile, null);
            }
            else if (options.CrossDirGroup?.MatchesFileOrAncestorDir(entry.Path) ?? false)
            {
                byPath[entry.Path] = new FileClass(FileCategory.CrossDirectoryGroup, null);
            }
            else
            {
                var dir = Directory(entry.Path);
                byPath[entry.Path] = new FileClass(FileCategory.DirectoryGroup, dir);
                dirCandidates[dir] = dirCandidates.GetValueOrDefault(dir) + 1;
            }
        }

        return new Classification(byPath, dirCandidates);
    }

    public BackupPlan Plan(IReadOnlyList<PlannedFile> files, PlanOptions? options = null)
    {
        options ??= new PlanOptions();

        var blobs = new List<BlobEntry>();
        var byDirectory = new List<PlannedFile>();
        var crossDirectory = new List<PlannedFile>();

        foreach (var file in files)
        {
            // Priority: don't-group > cross-path packing > per-directory packing. "Don't group" is the strongest statement of intent —
            // it says "this file should not be merged with anyone at all", and later rules must not overturn it.
            if (file.Length >= options.SingleFileThresholdBytes
                || (options.DontGroup?.MatchesFileOrAncestorDir(file.Path) ?? false))
                // data/{hash} is a content address, and no hash means no address. Single-file blobs may defer the full-content hash to
                // the compression pass (see PlannedFile.FullHash), but that route is sent straight to compression by the orchestrator and never comes
                // through here. A deferred value actually arriving here means something was wired up wrong: rather than building an empty "data/" address and quietly uploading it
                // (only to be discovered on restore day, pointing at no blob), blow up on the spot.
                blobs.Add(new BlobEntry(file.Path, file.FullHash
                    ?? throw new InvalidOperationException(
                        $"Cannot address '{file.Path}': its full hash has not been computed yet.")));
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

        // Group by immediate parent directory and sort by path within each directory, to guarantee deterministic numbering.
        var byDir = byDirectory
            .GroupBy(f => Directory(f.Path), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        // Each directory is first split by compressibility and then packed separately: a pack can hold only one compression mode, so this cut must land before packing.
        foreach (var dir in byDir)
            foreach (var (storeOnly, files) in SplitByCompressibility(dir, options))
                Fill(files, groupKey: dir.Key, storeOnly);

        // Cross-path: ignore directory boundaries, sort by full path and pack in order. Sorting by path naturally puts files from the same directory next to each other,
        // so locality is not lost — restoring one directory still touches only a handful of packs — it is just that a pack is no longer forced to seal because the directory changed.
        foreach (var (storeOnly, files) in SplitByCompressibility(crossDirectory, options))
            Fill(files, groupKey: null, storeOnly);

        return packs;

        void Fill(IEnumerable<PlannedFile> ordered, string? groupKey, bool storeOnly)
        {
            var current = new List<PackEntry>();
            long currentBytes = 0;
            long currentPathBytes = 0;

            foreach (var file in ordered)
            {
                // Hitting any one of the three limits → seal the current pack and start another (see GroupIsFull).
                if (current.Count > 0 && GroupIsFull(current.Count, currentBytes, currentPathBytes, file, options))
                {
                    Seal(current, groupKey, storeOnly);
                    current = [];
                    currentBytes = 0;
                    currentPathBytes = 0;
                }

                // No null rejection here: a symlink has no content hash to begin with (the diff always returns null for one), and symlinks
                // can be packed — 7z stores the link itself. Deferred computation, meanwhile, only happens for single-file blobs and never goes through packing.
                current.Add(new PackEntry(file.Path, file.Path, file.FullHash!, file.Length));
                currentBytes += file.Length;
                currentPathBytes += EntryArgBytes(file.Path);
            }

            if (current.Count > 0)
                Seal(current, groupKey, storeOnly);
        }

        void Seal(List<PackEntry> members, string? groupKey, bool storeOnly)
        {
            var id = PackId(packNumber++);
            // Cross-path packs each become their own pool: pools run concurrently, and stuffing tens of thousands of cross-directory files into one pool would degrade them to serial.
            packs.Add(new PlannedPack(id, members, groupKey ?? id, storeOnly));
        }
    }

    /// <summary>
    /// Splits a set of files into two lanes by compressibility: the compressed pack first, the store-only pack second, each lane sorted by path internally.
    /// <para>
    /// An empty lane yields nothing, so when <see cref="PlanOptions.DontCompress"/> is empty only the "compressed pack" lane remains and the
    /// packing result is **byte for byte identical** to what it was before this rule existed — ordinary backups are unaffected.
    /// </para>
    /// <para>
    /// The shape "two lanes, sorted by path within each lane" is exactly what lets the orchestrator's fill-while-diffing cross-directory route line up with this pure
    /// function: the scan results are already in ordinal path order, and after being routed into two accumulators by compressibility each lane still holds path order,
    /// which is precisely the result of grouping first and sorting after here. Touching the sort order here means breaking the "actual output matches the planner" invariant.
    /// </para>
    /// </summary>
    private static IEnumerable<(bool StoreOnly, IEnumerable<PlannedFile> Files)> SplitByCompressibility(
        IEnumerable<PlannedFile> files, PlanOptions options)
    {
        var sides = files.ToLookup(f => options.DontCompress?.MatchesFileOrAncestorDir(f.Path) ?? false);
        foreach (var storeOnly in (bool[])[false, true])
            if (sides[storeOnly].Any())
                yield return (storeOnly, sides[storeOnly].OrderBy(f => f.Path, StringComparer.Ordinal));
    }

    private static string Directory(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    private static string PackId(int number) => "p" + number.ToString("D4");
}
