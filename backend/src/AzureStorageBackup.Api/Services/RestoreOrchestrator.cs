using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>A single restore request. When Version is null, the latest version is restored.</summary>
public sealed record RestoreRequest
{
    public required Account Account { get; init; }
    public required string Container { get; init; }
    public required string TargetRoot { get; init; }
    public string? Password { get; init; }
    public int? Version { get; init; }

    /// <summary>Download concurrency cap (PRD 3.4, default 5).</summary>
    public int DownloadConcurrency { get; init; } = 5;

    /// <summary>Substitution sources for unrecoverable files: path → which version's copy of that file to substitute with (the user picks them one at a time, in bulk if they like).</summary>
    public IReadOnlyDictionary<string, int> Substitutions { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Selective restore (requirement B): null restores the whole version (current behavior); non-null restores exactly these paths and nothing else.
    /// The filter takes effect before grouping — so a pack is still downloaded once and only the selected members are written, never over-restoring the unselected ones.</summary>
    public IReadOnlyList<string>? SelectedPaths { get; init; }

    /// <summary>Conflict handling mode (decision 3). Default OverwriteIfChanged = the current behavior.</summary>
    public RestoreConflictMode Conflict { get; init; } = RestoreConflictMode.OverwriteIfChanged;

    /// <summary>Rehydrate priority for Archive blobs (passed straight through to Azure's RehydratePriority). Default Standard.</summary>
    public RestoreRehydratePriority RehydratePriority { get; init; } = RestoreRehydratePriority.Standard;

    /// <summary>The tier to rehydrate an Archive blob into when we hit one (Archive can't be downloaded directly; it has to be rehydrated first, asynchronously, over hours).</summary>
    public AccessTier RehydrateTier { get; init; } = AccessTier.Hot;

    /// <summary>Rehydration poll interval in seconds (the restore job holds no lock, so it can afford to wait a long time).</summary>
    public int RehydratePollSeconds { get; init; } = 60;

    /// <summary>After the restore finishes, put rehydrated blobs back into Archive (default true, to keep the backup's original tier and avoid paying for hot storage long-term).</summary>
    public bool ReArchiveAfterRestore { get; init; } = true;
}

/// <summary>Restore result. SkippedFiles = skipped because the local copy already holds identical content (overwrite only when changed).
/// FailedFiles = the number of entries that could not be restored: their storage group failed to download/extract, the entry would be written outside the target root
/// (including symlink and empty-directory entries), the entry itself is malformed so the write throws, a symlink entry is missing its Target,
/// the index contains a duplicate Path (no way to tell which one is authoritative, so neither is written),
/// or several entries' paths differ only in case while the restore target folds case, so they would overwrite each other on the way in
/// (likewise for pack members when the extraction directory folds case).
/// RestoredDirs = the number of empty directories **actually created successfully** (escaping/failed ones don't count).</summary>
public sealed record RestoreResult(int Version, int RestoredFiles, int SkippedFiles, int RestoredDirs, int FailedFiles);

/// <summary>
/// Restore orchestrator (M5, PRD 1.5): reads the info file plus the second-level index, downloads data blobs / packs and extracts them with 7z,
/// writes them back under the local root, and restores permissions/mtime and empty folders. "Overwrite only when changed" — skip if the local file already has the same hash.
/// </summary>
public sealed class RestoreOrchestrator(
    IBlobClientFactory factory,
    IBackupInfoStore store,
    IVersionCatalogs catalogs,
    IFileCompressor compressor,
    IFileHasher hasher,
    string tempRoot,
    INotifier? notifier = null,
    IOperationLog? opLog = null)
{
    /// <summary>Test-injected millisecond time source, handed straight through to the <see cref="StageTracker"/> built internally (see the comment on the field of the same name there).
    /// Null in production, meaning the real wall clock. It exists so timing assertions like "the in-flight marker is dropped the moment the download ends, extraction no longer counts as in flight"
    /// can escape the 200ms throttle window — once injected, every time query is guaranteed to move forward, so throttling never kicks in,
    /// every state change gets published, and the assertion doesn't have to gamble on whether the real clock happened to cross the throttle window.</summary>
    internal Func<long>? Clock { get; init; }

    /// <summary>Test-injected replacement for <see cref="PathCaseSensitivity.IsCaseInsensitive"/> (directory → does this filesystem fold case).
    /// Null in production, meaning the real probe. It exists because the interesting half of the behaviour — refusing to merge two paths that differ only in case —
    /// can only be reached on a folding filesystem, and CI runs on ext4; without an injection point that half could never be tested on the machine that runs the tests.</summary>
    internal Func<string, bool>? CaseProbe { get; init; }

    /// <param name="onProgress">Stage progress (which pack is being restored, how many groups are done, how fast). Before this there was only that one free-text
    /// phase string, and what it actually carried was the error stream — it could never say "how much is left".</param>
    public async Task<RestoreResult> RunAsync(
        RestoreRequest request, CancellationToken ct = default, IProgress<string>? phase = null,
        Action<StageProgress>? onProgress = null)
    {
        var source = $"restore:{request.Account.Id}/{request.Container}";
        await Record(NotificationEvents.RestoreStart, source, $"Restore started: {request.Container}", request.TargetRoot, ct);
        try
        {
            var result = await RunCoreAsync(request, phase, onProgress, ct);
            await Record(NotificationEvents.RestoreSuccess, source, $"Restore succeeded: {request.Container}",
                $"Restored {result.RestoredFiles} file(s) to {request.TargetRoot} (version {result.Version})", ct);
            return result;
        }
        catch (Exception ex)
        {
            await Record(NotificationEvents.RestoreFailure, source, $"Restore failed: {request.Container}", ex.Message, ct);
            throw;
        }
    }

    private async Task Record(NotificationEvents evt, string source, string title, string body, CancellationToken ct)
    {
        if (opLog is not null)
            await opLog.AppendAsync(EventLog.LevelOf(evt), source, $"{title} — {body}", ct, durable: true);
        if (notifier is not null)
            await notifier.NotifyAsync(evt, title, body, ct);
    }

    private async Task<RestoreResult> RunCoreAsync(
        RestoreRequest request, IProgress<string>? phase, Action<StageProgress>? onProgress, CancellationToken ct)
    {
        var info = await store.ReadInfoAsync(request.Account, request.Container, request.Password, ct)
            ?? throw new InvalidOperationException("No backup found in container.");
        if (info.Versions.Count == 0)
            throw new InvalidOperationException("Backup has no versions.");

        var version = request.Version is { } requested
            ? info.Versions.FirstOrDefault(x => x.Version == requested)
              ?? throw new InvalidOperationException($"Version {requested} not found.")
            : info.Versions[^1];
        var v = version.Version;

        // Every version this run reads has to be in the catalog **before** the handle is opened, because the handle is
        // read-only and a read-only open is precisely the one that migrates nothing: the target version, plus each
        // version a substitution draws its content from. The identity stamp is the backup's creation timestamp — the
        // same value every other cache in this codebase keys on — so a container that was deleted and rebuilt under
        // the same version numbers is never mistaken for the one already in the file.
        var identity = info.Backup.CreatedAt.UtcTicks;
        await catalogs.EnsureVersionAsync(request.Account, request.Container, version, identity, request.Password, ct);
        var substitutionSources = new HashSet<int>();
        foreach (var source in request.Substitutions.Values.Distinct())
        {
            if (source == v)
            {
                substitutionSources.Add(source); // substituting from the version being restored: already in hand
                continue;
            }

            // The substitute version was deleted by retention cleanup → the whole group falls back to being skipped.
            if (info.Versions.FirstOrDefault(x => x.Version == source) is not { } sourceVersion)
                continue;
            await catalogs.EnsureVersionAsync(
                request.Account, request.Container, sourceVersion, identity, request.Password, ct);
            substitutionSources.Add(source);
        }

        // One handle for the whole restore. VersionCatalog wraps a single connection and is not thread-safe, which is
        // fine here: every read below runs sequentially on this method's own thread of control, and the group tasks
        // that do run concurrently never touch it — they only ever hold entries already read out of it.
        await using var catalog = await catalogs.OpenAsync(request.Account.Id, request.Container, readOnly: true, ct);

        Directory.CreateDirectory(request.TargetRoot);
        var container = factory.CreateServiceClient(request.Account).GetBlobContainerClient(request.Container);

        // Resolve the target root once and reuse it throughout (same singleton reasoning as PathBoundary: request.TargetRoot doesn't change during this run,
        // so there's no point making every entry — and file entries twice over — walk lstat all over again). Per-destination-path resolution still happens
        // entry by entry inside WriteStaysInsideRoot/LinkStaysInsideRoot — that one has to be recomputed every single time,
        // because what it is there to catch is precisely "a link created during this very restore".
        var realRoot = PathBoundary.ResolveReal(request.TargetRoot);

        var restored = 0;
        var skipped = 0;
        var failed = 0;

        // A path the index holds twice contradicts itself, and there is no telling which entry is authoritative, so we
        // would rather write neither than guess. The import kept the first row (the primary key cannot hold the same
        // path twice) and recorded the loss as an issue, which is what still makes the contradiction visible here.
        // Handled under the existing per-entry fault-tolerance principle: fail only the duplicated path, one failure
        // per path however many copies there were, and never abort the whole restore over it.
        var duplicates = await DuplicatePathsAsync(catalog, v, ct);
        foreach (var path in duplicates)
            phase?.Report($"Skipped duplicate index entry (ambiguous which version is authoritative): {path}");
        failed += duplicates.Count;

        // Selective restore (requirement B): narrow the effective set down to the paths the user selected. The filter takes effect before grouping,
        // so each pack is still downloaded only once but only the selected members get written — unselected members never enter a group at all, so no over-restore.
        HashSet<string>? selected = request.SelectedPaths is null
            ? null
            : new HashSet<string>(request.SelectedPaths, StringComparer.Ordinal);

        // The effective entry per path: by default the one from this version; a substituted path uses the same-path
        // entry from the chosen version (content + metadata both from that version). Bounded by the substitution
        // count — a list the user picked by hand — never by the size of the version.
        var substituted = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        foreach (var group in request.Substitutions.GroupBy(kv => kv.Value))
        {
            if (!substitutionSources.Contains(group.Key))
                continue;

            // The substitute source version can equally well contain duplicate Paths, and the same verdict applies:
            // its copy of that path is not something to hand over as authoritative, so the substitution simply does
            // not resolve and falls back to the documented soft skip.
            var sourceDuplicates = await DuplicatePathsAsync(catalog, group.Key, ct);
            foreach (var kv in group)
            {
                if (sourceDuplicates.Contains(kv.Key) || (selected is not null && !selected.Contains(kv.Key)))
                    continue;

                // "Resolved" must mean the substitute's content is actually available, not merely that a
                // same-path entry exists: a version whose own copy is ALSO marked unrecoverable (the /file-versions
                // endpoint filters those, but a raw API caller or a selection gone stale across a repair does not)
                // would ride the normal download pipeline into a hard per-group failure with a message that never
                // says the chosen version is damaged too. Unresolved falls back to the documented soft skip.
                if (await catalog.GetEntryAsync(group.Key, kv.Key, ct) is { } entry
                    && !await catalog.IsUnrecoverableAsync(group.Key, kv.Key, ct))
                    substituted[kv.Key] = entry;
            }
        }

        // Unrecoverable with no substitute that "resolved successfully" → skip (declaring the intent but not having the substitute available also falls back to skipping, not erroring).
        // Under selective restore, only the selected unrecoverable paths are counted. The list is bounded by the damage
        // a check found, not by the file count.
        var unresolved = (await catalog.UnrecoverableAsync(v, ct))
            .Where(p => !substituted.ContainsKey(p) && (selected is null || selected.Contains(p)))
            .ToHashSet(StringComparer.Ordinal);
        skipped += unresolved.Count;

        // Two entries whose paths differ only in case — a source tree on a case-sensitive filesystem may well hold
        // both Foo.txt and foo.txt, with different content and different hashes — collapse onto **one** file when the
        // restore target folds case, and that collapse used to be silent: NeedsRestoreAsync runs for a whole group
        // before anything is written, so at that moment neither destination exists yet and both entries are marked
        // "needed"; the second File.Copy then overwrites the first, and the run still reports both as restored.
        // The verdict is the same one a duplicate Path gets (see above): two entries that contradict each other are
        // both refused and reported — one visible failure beats one file's content silently replaced by another's.
        // Note this sits **after** the selective-restore filter, deliberately: picking exactly one member of a
        // colliding pair leaves a group of one, which restores normally. That is the only way to get the content out
        // onto a folding target, and it needs no special case here.
        // The catalog answers "which paths collide" off its path_fold index and returns the colliding paths alone, so
        // the list stays short even on a five-hundred-thousand-entry version — on a normal backup it is empty, and the
        // filesystem probe is only paid for when it is not.
        var collisions = CaseCollisionGroups(await catalog.CaseCollisionsAsync(v, ct), duplicates, unresolved, selected);
        var collided = new HashSet<string>(StringComparer.Ordinal);
        if (collisions.Count > 0 && ProbeCaseInsensitive(request.TargetRoot))
            foreach (var group in collisions)
            {
                phase?.Report(
                    $"Skipped {group.Count} entries whose paths differ only in case (the restore target is case-insensitive, so they would overwrite each other): {string.Join(", ", group)}");
                collided.UnionWith(group);
                failed += group.Count;
            }

        // Empty folders (restore has to recreate them) — selective restore only targets the selected files, it does not rebuild the entire empty-directory tree.
        // These come from the cloud index as well: a directory name containing .. would be created outside the target root, so escaping directory entries are skipped, not created.
        // The check operates on the **resolved real path**: CreateDirectory follows symlinks in the intermediate path segments,
        // and a single link left inside the root by a previous restore (or by the user) that points outside is enough to make a directory that "looks like it is inside the root"
        // land outside it.
        var restoredDirs = 0;
        if (selected is null)
            foreach (var dir in await catalog.EmptyDirsAsync(v, ct))
            {
                var dest = Path.Combine(request.TargetRoot, ToLocal(dir));
                if (!WriteStaysInsideRoot(realRoot, dest))
                {
                    // Same principle as the symlink path (C3): a security check that fires must be visible, and must count as a failure —
                    // reporting only through phase would let a malicious index containing nothing but escaping EmptyDirs freeze FailedFiles at 0.
                    phase?.Report($"Skipped unsafe directory entry (escapes the target root): {dir}");
                    failed++;
                    continue;
                }

                // A malformed directory entry (an intermediate segment is a file, and so on) fails only itself and does not abort the whole restore.
                try
                {
                    Directory.CreateDirectory(dest);
                    restoredDirs++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    phase?.Report($"Failed to create directory '{dir}': {ex.Message}");
                    failed++;
                }
            }

        // Every path that is out of play, in every pass below: the version contradicts itself about it (duplicate), it
        // is unrecoverable with no substitute, or the case gate refused it.
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        excluded.UnionWith(duplicates);
        excluded.UnionWith(unresolved);
        excluded.UnionWith(collided);

        // Under a selection the cursor is replaced by the selection itself — bounded by what the user picked — so the
        // unselected entries are never read out of the catalog at all, rather than read and then thrown away.
        var selection = selected is null ? null : await catalog.EntriesAtAsync(v, selected, ct);
        var substitutes = substituted.Values.Where(e => !excluded.Contains(e.Path)).ToList();

        IAsyncEnumerable<IndexEntry> ByPath() => EffectiveAsync(
            selection is not null ? Cursor(selection) : catalog.EntriesAsync(v, ct),
            substitutes, excluded, substituted, ct);

        // Symlinks, empty files and download groups are three passes over the cursor, in the same order the single
        // in-memory pass used to run them in. Three scans of a SQLite index are cheaper than the thing they replace:
        // holding every entry of the version in a dictionary purely so the same three questions can be asked of it.
        await foreach (var e in ByPath().WithCancellation(ct))
        {
            if (e.Kind != "symlink")
                continue;

            // A malformed entry (e.g. Path is "" or ".") makes CreateSymbolicLink throw;
            // catch it per entry here, otherwise one dirty entry aborts the whole restore.
            SymlinkOutcome outcome;
            try
            {
                outcome = RestoreSymlink(request.TargetRoot, realRoot, e);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                phase?.Report($"Failed to restore symlink '{e.Path}': {ex.Message}");
                failed++;
                continue;
            }

            switch (outcome)
            {
                case SymlinkOutcome.Created:
                    restored++;
                    break;
                case SymlinkOutcome.Unchanged:
                    skipped++;
                    break;
                case SymlinkOutcome.Malformed:
                    // entry.Target is missing: not the same thing as "unchanged" — unchanged means nothing happened,
                    // whereas this one failed to restore, so the user has to be able to see it and it has to count as a failure, rather than being
                    // quietly counted as Skipped under the guise of "already up to date" (M3).
                    phase?.Report($"Skipped malformed symlink entry (missing target): {e.Path}");
                    failed++;
                    break;
                default:
                    // A security check that fires must be visible: being as silent as "unchanged" would leave the user completely unaware of the entry that got blocked.
                    phase?.Report(UnsafeRestorePathException.MessageFor(e.Path));
                    failed++;
                    break;
            }
        }

        // A 0-byte file has no storage reference to group by — the backup side never produces one for it (see BackupOrchestrator.IsEmptyFile),
        // because it has no content that needs storing. Its entire information content is "length is zero", so the file is created directly from that here.
        // This pass goes before the grouping: the grouping pass only ever sees entries that carry a storage reference, and without this one
        // the restored tree would be **silently missing a few files**, while every content-comparison check would still pass.
        await foreach (var e in ByPath().WithCancellation(ct))
        {
            if (!IsEmptyFileEntry(e))
                continue;

            switch (await TryCreateEmptyFileAsync(request, realRoot, e, phase, ct))
            {
                case EmptyFileOutcome.Created: restored++; break;
                case EmptyFileOutcome.Unchanged: skipped++; break;
                default: failed++; break;
            }
        }

        // Group by storage: the same pack is downloaded/extracted only once. Groups download concurrently (PRD 3.4), each with its own temp subdirectory to avoid collisions.
        var work = NewTempDir();
        // The extraction directory's filesystem matters as much as the target's: a pack is extracted whole with
        // `7z x -y`, so two members differing only in case overwrite **each other while being extracted**, and both
        // entries then go on to copy the very same bytes. Probing `work` rather than tempRoot is deliberate —
        // tempRoot may not exist yet, and a probe in a missing directory conservatively answers "folds", which would
        // arm this gate on every restore. Lazy so the probe is only paid for once a pack group actually gets there.
        var extractDirFolds = new Lazy<bool>(() => ProbeCaseInsensitive(work));
        using var gate = new SemaphoreSlim(Math.Max(1, request.DownloadConcurrency));
        try
        {
            // Declare two units of work: how many source bytes will be written out (after extraction), and how many bytes will come over the wire (compressed).
            // Reporting progress by group count alone is distorted — one group can be a single 100 GB file, or a box of several hundred small ones.
            // The download total **must only be reported if every single group can answer it**: handing out an undersized denominator when an old index lacks volume sizes
            // makes the percentage run high the whole way and then sit stuck at 100%, which is worse than showing nothing.
            // That verdict has to be reached BEFORE the first group is enqueued, and a streaming walk only learns it
            // after the last one — hence this one grouped query up front. It returns a row per storage object, so it is
            // bounded by the number of packs and blobs, never by the entry count.
            var groupWork = new Dictionary<string, long>(StringComparer.Ordinal);
            var downloadSizes = new Dictionary<string, long>(StringComparer.Ordinal);
            await foreach (var (storage, bytes) in catalog.StorageGroupSizesAsync(v, ct))
            {
                var key = CatalogSql.StorageKey(storage);
                groupWork[key] = bytes;
                downloadSizes[key] = TransferLabel.DownloadBytesOf(storage, info);
            }

            // A substitute's content lives in another version, so its object is not among the ones this version
            // references and has to be weighed in separately — including in the all-or-nothing verdict above.
            foreach (var entry in substitutes.Where(e => e.Storage is not null))
            {
                var key = CatalogSql.StorageKey(entry.Storage!);
                if (groupWork.TryAdd(key, entry.Length))
                    downloadSizes[key] = TransferLabel.DownloadBytesOf(entry.Storage!, info);
            }
            var downloadTotalKnown = downloadSizes.Values.All(b => b > 0);

            // The opening denominator is every object the version references; the walk then settles it with SetTotal to
            // the groups actually formed, which is fewer whenever a selection or the case gate took entries out.
            // Opening at that upper bound rather than at zero is what keeps the percentage meaningful while the run is
            // in flight — the same "declare, then settle" the upload stage does with its own growing total.
            var tracker = onProgress is null
                ? null
                : new StageTracker("Restoring", downloadSizes.Count, onProgress, speedWhileInFlight: true) { Clock = Clock };

            // The selection is sorted by storage key in memory (bounded by the selection); the whole-version cursor
            // arrives already ordered by storage object, straight off the entries_storage index. Symlinks are dropped
            // from both: pass one has already restored them, and a tampered index that hands a symlink entry a storage
            // reference would otherwise get it restored twice — once as a link, then once more with the object's
            // content written over it.
            var byStorage = WithoutSymlinksAsync(
                selection is not null
                    ? Cursor(selection
                        .Where(e => e.Storage is not null && !excluded.Contains(e.Path) && !substituted.ContainsKey(e.Path))
                        .OrderBy(e => CatalogSql.StorageKey(e.Storage!), StringComparer.Ordinal))
                    : EffectiveAsync(catalog.EntriesByStorageAsync(v, ct), [], excluded, substituted, ct),
                ct);

            // A substitute whose object the cursor visits anyway joins that group instead of forming a second one —
            // the same pack would otherwise be downloaded and extracted twice for no gain.
            var pendingSubstitutes = substitutes
                .Where(e => e.Storage is not null && e.Kind != "symlink" && !IsEmptyFileEntry(e))
                .GroupBy(e => CatalogSql.StorageKey(e.Storage!), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

            // At most DownloadConcurrency group lists are alive at once, plus the one the cursor is folding. A group's
            // entries stay reachable from its task's suspended state machine for as long as that task exists, so
            // launching one per group as the cursor produced them and only awaiting at the end kept every
            // storage-bearing entry of the version in memory — the exact retention this streaming walk exists to end.
            // The price is that RestoreGroupAsync's "does this need restoring" pre-pass (a hash of each destination
            // that already exists) now runs inside the concurrency window instead of ahead of it for every group at
            // once; that is where the download it precedes waits anyway.
            var inFlight = Math.Max(1, request.DownloadConcurrency);
            var running = new List<Task<(int Restored, int Skipped, int Failed)>>();
            var counts = new List<(int Restored, int Skipped, int Failed)>();
            var groups = 0;
            try
            {
                await foreach (var group in CatalogSql.GroupByStorageAsync(byStorage, ct))
                {
                    if (pendingSubstitutes.Remove(CatalogSql.StorageKey(group[0].Storage!), out var alsoHere))
                        group.AddRange(alsoHere);
                    await LaunchAsync(group);
                }

                // Whatever is left over points at an object this version does not reference at all.
                foreach (var group in pendingSubstitutes.Values)
                    await LaunchAsync(group);
            }
            finally
            {
                // Nothing may still be in the air when this scope unwinds: `work` is deleted and `gate` disposed on the
                // way out, and a group still downloading into either would find them gone. The fault is deliberately
                // not observed here — it is read back below, so a group's failure still surfaces exactly the way
                // Task.WhenAll used to surface it, while an already-unwinding walk is not masked by it.
                try { await Task.WhenAll(running); } catch { /* re-observed below, or already unwinding */ }
            }

            // The denominator settles here, now that the walk knows how many groups there really were.
            tracker?.SetTotal(groups);
            counts.AddRange(await Task.WhenAll(running)); // completed by the finally above; rethrows the first fault
            tracker?.Complete(); // without forcing a terminal state, the last group's bytes get squashed by the throttle and never go out
            restored += counts.Sum(c => c.Restored);
            skipped += counts.Sum(c => c.Skipped);
            failed += counts.Sum(c => c.Failed);

            // Retires a finished group before starting the next, so the window never widens. Retiring reads the
            // finished task's tally, which is also where a fault first surfaces — the walk then unwinds through the
            // finally above, which settles the rest.
            async Task LaunchAsync(List<IndexEntry> group)
            {
                if (running.Count >= inFlight)
                {
                    var finished = await Task.WhenAny(running);
                    running.Remove(finished);
                    counts.Add(await finished);
                }

                groups++;
                running.Add(RunGroupAsync(group));
            }

            async Task<(int Restored, int Skipped, int Failed)> RunGroupAsync(List<IndexEntry> group)
            {
                var key = CatalogSql.StorageKey(group[0].Storage!);
                // The pre-pass answers for every object of the version itself; a substituted group's object is only
                // known from the entries in hand. Whichever it is, the same figure is enqueued and later retired.
                var groupBytes = groupWork.TryGetValue(key, out var planned) ? planned : group.Sum(e => e.Length);
                var downloadBytes = downloadSizes.TryGetValue(key, out var known) ? known : 0;
                tracker?.Enqueue(groupBytes, downloadTotalKnown ? downloadBytes : 0);
                try
                {
                    return await RestoreGroupAsync(
                        container, request, realRoot, work, group, gate, phase, tracker,
                        downloadBytes, extractDirFolds, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    phase?.Report($"Group failed ({key}): {ex.Message}");
                    return (Restored: 0, Skipped: 0, Failed: group.Count);
                }
                finally
                {
                    // Counting and in-flight are separate concerns: a group occupies exactly one slot. Work units are likewise retired in one go — failed groups have to retire too,
                    // otherwise the remaining amount never reaches zero and the ETA hangs there forever.
                    tracker?.Advance(0, groupBytes);
                }
            }
        }
        finally
        {
            TryDelete(work);

            // No re-archive step exists any more: the sources never left Archive (EnsureHotCopyAsync serves
            // the restore from disposable Hot COPIES under restore-tmp/), so ReArchiveAfterRestore has nothing
            // left to do and the old crash window — rehydrated blobs stranded in the billed online tier — has
            // no state to strand. The copies are deleted per group above; RestoreTempSweeper clears whatever a
            // crash leaves behind at the next startup.
        }

        return new RestoreResult(v, restored, skipped, restoredDirs, failed);
    }

    /// <summary>The paths this version holds more than once. The import kept the first row and recorded the rest as
    /// issues rather than swallowing them, which is what still lets a reader tell "the index contradicts itself here"
    /// apart from "this is the entry for that path".</summary>
    private static async Task<HashSet<string>> DuplicatePathsAsync(
        VersionCatalog catalog, int version, CancellationToken ct) =>
        (await catalog.ImportIssuesAsync(version, ct))
            .Where(issue => issue.Issue == "duplicate")
            .Select(issue => issue.Path)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Folds the colliding paths the catalog reported — distinct under <see cref="StringComparer.Ordinal"/>, equal once
    /// case-folded — into the sets that would land on one and the same file if the target filesystem folds case. Paths
    /// already out of play take no part: they are not going to be written either way, so they cannot collide with
    /// anything, and a set left with a single member is no longer a collision at all — which is exactly what makes
    /// "select one side of a colliding pair" restore normally.
    /// <para>Sorted so the reported line reads the same way on every run — the messages end up in the operation log,
    /// and a line whose order wanders between runs is one nobody can diff.</para>
    /// </summary>
    private static List<List<string>> CaseCollisionGroups(
        IReadOnlyList<(string Path, int Version)> colliding, IReadOnlySet<string> duplicates,
        IReadOnlySet<string> unresolved, IReadOnlySet<string>? selected) =>
        [.. colliding
            .Select(c => c.Path)
            .Where(p => !duplicates.Contains(p) && !unresolved.Contains(p) && (selected is null || selected.Contains(p)))
            .GroupBy(p => p.ToUpperInvariant(), StringComparer.Ordinal)
            .Where(g => g.Skip(1).Any())
            .Select(g => g.OrderBy(p => p, StringComparer.Ordinal).ToList())];

    /// <summary>
    /// The version's entries as one stream with this run's substitutions folded in: a substituted path is dropped where
    /// the version has it and the chosen version's entry is handed over instead, and every path that is out of play is
    /// left out entirely. The substitutes ride at the end rather than being spliced into place because their position
    /// buys nothing — symlinks and empty files are each restored independently of one another, and the grouping pass
    /// keys on the storage object rather than on the order entries arrive in.
    /// </summary>
    private static async IAsyncEnumerable<IndexEntry> EffectiveAsync(
        IAsyncEnumerable<IndexEntry> cursor, IReadOnlyCollection<IndexEntry> substitutes,
        IReadOnlySet<string> excluded, IReadOnlyDictionary<string, IndexEntry> substituted,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var entry in cursor.WithCancellation(ct))
            if (!excluded.Contains(entry.Path) && !substituted.ContainsKey(entry.Path))
                yield return entry;

        foreach (var entry in substitutes)
            yield return entry;
    }

    /// <summary>Drops symlink entries from the stream the grouping pass sees. They were restored in the first pass, and
    /// a tampered index (which <c>/import</c> can bring in from any container) may perfectly well give a symlink entry
    /// a storage reference as well — after which the same entry would be restored twice, the second time with the
    /// object's content written over the link that had just been created.</summary>
    private static async IAsyncEnumerable<IndexEntry> WithoutSymlinksAsync(
        IAsyncEnumerable<IndexEntry> entries, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var entry in entries.WithCancellation(ct))
            if (entry.Kind != "symlink")
                yield return entry;
    }

    /// <summary>Adapts an in-memory sequence to the cursor's shape, so a selective restore and a whole-version restore
    /// go through one set of passes instead of two copies of each.</summary>
    private static async IAsyncEnumerable<T> Cursor<T>(IEnumerable<T> items)
    {
        await Task.CompletedTask;
        foreach (var item in items)
            yield return item;
    }

    private async Task<(int Restored, int Skipped, int Failed)> RestoreGroupAsync(
        BlobContainerClient container, RestoreRequest request, string? realRoot, string work,
        List<IndexEntry> group, SemaphoreSlim gate,
        IProgress<string>? phase, StageTracker? tracker, long downloadBytes, Lazy<bool> extractDirFolds,
        CancellationToken ct)
    {
        var skipped = 0;
        var failedEntries = 0;
        var needed = new List<IndexEntry>();
        foreach (var e in group)
        {
            // The boundary check has to come **before** NeedsRestoreAsync: the latter does a File.Exists and a full
            // hash on the destination, so an escaping entry amounts to letting the caller use a single index record to probe the existence and
            // content of any path outside the target root (the answer is visible through the RestoredFiles/SkippedFiles counters). Worse still, if a file with
            // identical content already exists outside the root, it returns false and gets counted as "skipped", so we never reach the check at the write site:
            // neither counted as a failure nor reported — a blocked escape turns into a completely invisible non-event.
            var dest = Path.Combine(request.TargetRoot, ToLocal(e.Path));
            if (!WriteStaysInsideRoot(realRoot, dest))
            {
                phase?.Report(UnsafeRestorePathException.MessageFor(e.Path));
                failedEntries++;
                continue;
            }

            if (await NeedsRestoreAsync(dest, e, request.Conflict, ct))
                needed.Add(e);
            else
                skipped++;
        }
        if (needed.Count == 0)
            return (0, skipped, failedEntries);

        var storage = group[0].Storage!;
        var blobName = storage.Kind == "pack" ? $"packs/{storage.Ref}.7z" : storage.Ref;

        // A separate temp directory per group (concurrency-safe).
        var groupDir = Path.Combine(work, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(groupDir);
        var restored = 0;
        await gate.WaitAsync(ct);
        // The in-flight marker must only be set **after** acquiring the gate: every group's delegate is enumerated and run up to its first real
        // await right at the start, and marking before that would make thousands of packs all count as "restoring" at once — which is both untrue
        // (only DownloadConcurrency of them actually run at a time) and means copying a thousands-of-items array on every snapshot.
        // The name uses the **source file path** (for packs, the pack number + member count), not the content-addressed blob name — the same shape as on the upload side.
        // Use needed rather than group: the skipped ones (local copy already identical) were never part of this transfer to begin with.
        tracker?.BeginItem(blobName, TransferLabel.For(storage, needed), downloadBytes);
        try
        {
            // A factory rather than a single IProgress<long>: see the comment on VolumeBlobIO.DownloadAsync —
            // sharing one instance across volumes gets the baseline of a large volume's first report wrong whenever "a small volume is followed by a large one", under-counting a chunk of that whole volume
            // (bounded by the previous volume's size) rather than over-counting.
            // When tracker is null (nobody is listening for progress) the whole expression degenerates to null and DownloadAsync attaches no callback.
            Func<IProgress<long>>? itemProgress = tracker is null ? null : () => tracker.ItemProgress(blobName);

            string firstVolume;
            try
            {
                try
                {
                    firstVolume = await VolumeBlobIO.DownloadAsync(container, blobName, groupDir, ct, itemProgress);
                }
                catch (RequestFailedException ex) when (ex.ErrorCode == "BlobArchived" || ex.Status == 409)
                {
                    // Archive, not yet rehydrated: start rehydration and poll until it's ready — a wait that, by EnsureOnlineAsync's own comment,
                    // is "on the order of hours". The in-flight marker's window is now the denominator of the speed clock — "how many streams are on the wire" —
                    // and during rehydration queuing and polling there is nothing on the wire at all; leaving the marker set would let the virtual clock keep running for
                    // hours, the heartbeat would drag the speed down to 0, the UI would report "stuck", while the backup is in fact correctly waiting on Azure.
                    // What gets dropped is only the speed-window marker, not the progress signal itself: EnsureOnlineAsync reports
                    // "Waiting for rehydration of {baseRef} — N volume(s) still
                    // archived…" to phase on every poll, so the operator can see the group is moving and won't think it vanished.
                    // Known rough edge: the top line of phase (state.Phase in RestoreRunner) is a single slot shared by every concurrent
                    // group, so with several groups running this message gets bumped by another group and only survives in state.Events;
                    // but polling re-reports it every RehydratePollSeconds, so it comes back on its own. That's the existing progress model,
                    // not something introduced here.
                    tracker?.EndItem(blobName, 0);
                    var tempBase = await EnsureHotCopyAsync(
                        container, blobName, request.RehydrateTier, MapPriority(request.RehydratePriority),
                        request.RehydratePollSeconds, phase, ct);
                    try
                    {
                        // Only reopen the window once the copies are ready and we're actually about to download — the same rhythm as the original BeginItem.
                        tracker?.BeginItem(blobName, TransferLabel.For(storage, needed), downloadBytes);
                        firstVolume = await VolumeBlobIO.DownloadAsync(container, tempBase, groupDir, ct, itemProgress);
                    }
                    finally
                    {
                        // This group's copies have served their purpose the moment the download ends, success or
                        // not; the startup sweeper covers a crash that never reaches this line.
                        await DeleteTempCopiesAsync(container, blobName);
                    }
                }
            }
            finally
            {
                // Drop the in-flight marker the moment the download ends (either successfully, or with both attempts failing and rethrowing): the bytes were
                // counted as they streamed, and the speed window shouldn't keep being stretched by the extraction/disk-write time that follows and uses no network.
                // By the time we get here the marker may already have been dropped once by the catch block above (the rehydration path drops it then re-sets it) —
                // EndItem is a safe no-op for an item that isn't in the set (ConcurrentDictionary.TryRemove returns
                // false, and the subsequent _bytes += 0 and PublishIfDue still run without affecting any counter), so there's no need
                // to distinguish whether it was already dropped: we pass 0 bytes, so dropping it a second time has no side effect.
                // That is also why the fallback EndItem(blobName, 0) in the outer finally below has no second effect on the normal path —
                // EndItem itself is **not** idempotent (_bytes += bytes and PublishIfDue both run unconditionally, outside TryRemove);
                // the fallback call is only safe to repeat because the byte count it passes is 0. A second call that really did pass nonzero bytes
                // would quietly count that batch twice.
                tracker?.EndItem(blobName, 0);
            }

            // The download has left the in-flight window, but the local CPU work of extracting/hashing/writing to disk must not disappear from the UI along with it —
            // without this, for the tens of seconds a large pack takes to extract, ActiveItems is empty and preparing/queued are both 0,
            // so the UI freezes on the snapshot from the instant the download ended, indistinguishable from a hang (b6db78a already fixed the same
            // problem for the compression stage; this is its counterpart on the restore/check side). BeginPacking/EndPacking do not affect the speed denominator
            // (that window only recognizes BeginItem/EndItem), they are purely the carrier for the "preparing" signal.
            try
            {
                // BeginPacking moved inside the try: it now calls publish(...) under _gate, and on the non-heartbeat path an exception thrown by
                // publish is deliberately allowed to propagate (see the notes on BeginPacking in StageProgress.cs).
                // Left outside the try, a throw here would mean _inPacking was incremented with no matching EndPacking,
                // and preparing would sit at an inflated number for the rest of the run; moving it inside gives it the finally below as a backstop.
                // A blob's extraction is counted into the preparing row (the sink below wraps in
                // PackingProgressStream), so the byte total rides along: its one content, extracted once. A
                // pack extracts to disk through 7z's own file IO where nothing of ours sees the bytes, so it
                // declares no total and its row renders as before.
                tracker?.BeginPacking(TransferLabel.Folders(needed.Select(e => e.Path)),
                    storage.Kind == "blob" && !storage.Raw ? needed[0].Length : 0);
                if (storage.Kind == "blob")
                {
                    // Single-file blob: the content is exactly one file (raw = the original bytes; otherwise the sole entry inside the 7z).
                    // With content-addressed dedup the same blob can be referenced by several paths → once the first one is written, the rest are copied from it.
                    // The non-raw case streams straight from the archive to the destination: extracting to a temp directory and then copying would write the same bytes
                    // to disk twice (a 20 GB blob means 40 GB of writes + 20 GB of temp space).
                    string? content = storage.Raw ? firstVolume : null;
                    foreach (var e in needed)
                    {
                        if (content is null)
                        {
                            var streamed = await TryStreamRestoredFileAsync(request, realRoot, e, firstVolume, phase, tracker, ct);
                            if (streamed is null)
                            {
                                failedEntries++;
                                continue;
                            }
                            // Later references copy from this one. It lives inside the target root and its content has already been checked against the length and hash.
                            content = streamed;
                            restored++;
                        }
                        else if (TryWriteRestoredFile(request, realRoot, e, content, phase))
                            restored++;
                        else
                            failedEntries++;
                    }
                }
                else
                {
                    // pack: after extraction, copy by each member's archive entry name.
                    var extractDir = Path.Combine(groupDir, "x");

                    // On a folding extraction filesystem, two members whose names differ only in case have already
                    // overwritten one another by the time extraction finishes, so whichever one we ask for may be
                    // holding the other's bytes — and there is no way to tell which, because pack members are not
                    // re-hashed after being written. Refuse both rather than hand out content that may belong to
                    // another file. The listing costs one archive-header read and is only ever paid for on a folding
                    // temp filesystem, which no Docker deployment has.
                    if (extractDirFolds.Value)
                    {
                        var listed = await compressor.ListEntriesAsync(firstVolume, request.Password, ct);
                        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var twinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var m in listed)
                            if (!m.IsDirectory && !seenNames.Add(m.Name))
                                twinNames.Add(m.Name);

                        if (twinNames.Count > 0)
                        {
                            var blocked = needed
                                .Where(e => twinNames.Contains(
                                    SevenZipCli.NormalizeEntryName(e.Storage!.EntryName ?? e.Path)))
                                .Select(e => e.Path)
                                .ToHashSet(StringComparer.Ordinal);
                            foreach (var p in blocked.OrderBy(x => x, StringComparer.Ordinal))
                                phase?.Report(
                                    $"Cannot restore '{p}': the pack holds another member whose name differs only in case, and the extraction directory is case-insensitive, so the extracted copy cannot be trusted.");
                            failedEntries += blocked.Count;
                            needed.RemoveAll(e => blocked.Contains(e.Path));
                        }
                    }

                    // A duplicated member name is a malformed — after /import, plausibly hostile — archive
                    // (see SevenZipCli.DuplicatedEntryNames): extraction keeps whichever occurrence lands
                    // last, so serving any of them would hand the user bytes no verdict ever covered. The
                    // affected entries fail alone; their companions restore as usual.
                    var packListing = await compressor.ListEntriesAsync(firstVolume, request.Password, ct);
                    var duplicated = SevenZipCli.DuplicatedEntryNames(
                        packListing.Where(l => !l.IsDirectory).Select(l => l.Name));
                    if (duplicated.Count > 0)
                    {
                        var blockedDup = needed.Where(e => duplicated.Contains(
                            SevenZipCli.NormalizeEntryName(e.Storage?.EntryName ?? e.Path))).ToList();
                        foreach (var e in blockedDup)
                            phase?.Report($"{e.Path}: the archive holds more than one member under this name — refused");
                        failedEntries += blockedDup.Count;
                        needed.RemoveAll(e => duplicated.Contains(
                            SevenZipCli.NormalizeEntryName(e.Storage?.EntryName ?? e.Path)));
                        if (needed.Count == 0)
                            return (restored, skipped, failedEntries);
                    }

                    await compressor.ExtractAsync(firstVolume, extractDir, request.Password, ct);

                    foreach (var e in needed)
                    {
                        // The member name inside the archive is EntryName, **not** the entry's own Path. The two used to be identical
                        // (RecordPack filled EntryName from f.Path), so looking up by Path was always correct;
                        // once pack members started being deduped across versions they stopped being identical — when the same content is referenced by another path,
                        // the archive only holds the original member name. Looking up by Path then finds no file in the extraction directory,
                        // so that entry gets recorded as a failure and the content quietly never gets restored.
                        // The checker side (BackupChecker) has been using EntryName ?? Path all along; this brings it in line.
                        // Byte-for-byte equivalent for existing backups, because for those entries EntryName equals Path.
                        var source = Path.Combine(extractDir, ToLocal(e.Storage?.EntryName ?? e.Path));
                        if (TryWriteRestoredFile(request, realRoot, e, source, phase))
                            restored++;
                        else
                            failedEntries++;
                    }
                }
            }
            finally
            {
                tracker?.EndPacking();
            }
        }
        finally
        {
            // Fallback removal: on the normal path the marker was already dropped once in the finally above (and the real bytes were
            // counted as they streamed). Passing 0 bytes here is purely defensive — if an exception is thrown after BeginItem but before
            // entering the download try, the in-flight set must not be left holding the item. EndItem itself is not idempotent (see the same note above),
            // and the only reason this line has no second effect and double-counts nothing on the normal path is that the byte count it passes is 0.
            //
            // Releasing the gate and deleting the temp directory each have to hide behind their own finally after EndItem: EndItem calls into the caller's
            // publish (external code that writes to the database, pushes SSE and the like), which can throw, and exceptions on this path are **deliberately** propagated.
            // Written as three statements in a row, a throw from the first skips the other two entirely — the permit is gone for good, the next group waits on
            // the gate forever, and the whole restore never comes back. The same shape appears in VolumeUploadScope.RunAsync and
            // BackupChecker.VerifyGroupAsync (which is pinned down by A_Broken_Progress_Sink_Does_Not_Wedge_The_Content_Check).
            try
            {
                tracker?.EndItem(blobName, 0);
            }
            finally
            {
                gate.Release();
                try { Directory.Delete(groupDir, recursive: true); } catch { /* best effort */ }
            }
        }
        return (restored, skipped, failedEntries);
    }

    /// <summary>A zero-length regular file entry: it has no storage reference, so it belongs to no download group and has to be created on its own.
    /// Only <c>Kind == "file"</c> counts; a symlink's content is the Target field and it has its own branch.
    /// <para>Deliberately does **not** catch entries with <c>Length &gt; 0</c> but no storage reference: that is a malformed/corrupt index,
    /// and passing an empty file off as it would trade an explicit failure for silent data corruption.</para></summary>
    private static bool IsEmptyFileEntry(IndexEntry e) => e.Storage is null && e.Kind == "file" && e.Length == 0;

    private enum EmptyFileOutcome { Created, Unchanged, Failed }

    /// <summary>
    /// Creates an empty file. It goes through **exactly the same** boundary check, conflict mode and metadata restoration as an entry with content —
    /// an empty file is still a file, and one check fewer is one check fewer; the escape check in particular has to come before any write action.
    /// </summary>
    private async Task<EmptyFileOutcome> TryCreateEmptyFileAsync(
        RestoreRequest request, string? realRoot, IndexEntry entry, IProgress<string>? phase, CancellationToken ct)
    {
        var dest = Path.Combine(request.TargetRoot, ToLocal(entry.Path));
        if (!WriteStaysInsideRoot(realRoot, dest))
        {
            phase?.Report(UnsafeRestorePathException.MessageFor(entry.Path));
            return EmptyFileOutcome.Failed;
        }

        try
        {
            if (!await NeedsRestoreAsync(dest, entry, request.Conflict, ct))
                return EmptyFileOutcome.Unchanged;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (request.Conflict == RestoreConflictMode.RenameKeep && File.Exists(dest))
                RestoreConflict.RenameExisting(dest, DateTimeOffset.UtcNow);
            // There is no content to write, hence no risk of "a mid-way failure leaving a truncated file that has already overwritten the user's original" —
            // no need to land a .asb-part first and swap it in the way entries with content do.
            File.Create(dest).Dispose();
            ApplyMetadata(dest, entry);
            return EmptyFileOutcome.Created;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The same fault-tolerance semantics as every other entry: keep the failure confined to this one entry, don't let one dirty entry abort the whole restore.
            phase?.Report($"Failed to restore '{entry.Path}': {ex.Message}");
            return EmptyFileOutcome.Failed;
        }
    }

    /// <summary><paramref name="dest"/> must be a destination path that has **already passed the boundary check** (see RestoreGroupAsync):
    /// this method does a File.Exists and a full hash on it, and must never operate on a path outside the target root.</summary>
    private async Task<bool> NeedsRestoreAsync(string dest, IndexEntry entry, RestoreConflictMode conflict, CancellationToken ct)
    {
        if (!File.Exists(dest))
            return true;

        // Skip: skip as soon as the target exists (whether or not the content differs).
        if (conflict == RestoreConflictMode.Skip)
            return false;

        // OverwriteIfChanged / RenameKeep: skip if the local content is already identical; if FullHash is missing there is nothing to compare against, so treat it as needing restore.
        if (entry.FullHash is null)
            return true;
        try
        {
            return await hasher.FullHashAsync(dest, ct) != entry.FullHash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If the file at the destination can't be opened, there is no way to tell whether it already holds the content to be restored — conservatively treat it as "needs restore".
            // If actually writing it fails too, TryWriteRestoredFile's per-file backstop records one failure and carries on;
            // whereas throwing here would be caught by the **whole group's** catch, taking every other file in the same pack down with it —
            // one file's permission problem should not have a blast radius that large.
            return true;
        }
    }

    /// <summary>
    /// Writes one entry, keeping the failure confined to that entry: an escape, or a malformed entry (e.g. Path is ""/"." so the destination is a directory and
    /// File.Copy throws UnauthorizedAccess/IOException), only fails this one entry and gets reported.
    /// It must never bubble up to the group handler — that would fail the group's entire set of legitimate entries. Returns whether the write succeeded.
    /// </summary>
    private bool TryWriteRestoredFile(RestoreRequest request, string? realRoot, IndexEntry entry, string sourceFile, IProgress<string>? phase)
    {
        try
        {
            if (WriteRestoredFile(request, realRoot, entry, sourceFile))
                return true;
            phase?.Report($"{entry.Path}: extracted content does not match the recorded length/hash — the destination was left untouched");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnsafeRestorePathException ex)
        {
            phase?.Report(ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            phase?.Report($"Failed to restore '{entry.Path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Streams a single-file blob straight from the archive to the destination, bypassing the temporary extraction directory. Returns the destination path written on success, null on failure
    /// (the error has been reported and is confined to this one entry, the same fault-tolerance semantics as <see cref="TryWriteRestoredFile"/>).
    /// </summary>
    private async Task<string?> TryStreamRestoredFileAsync(
        RestoreRequest request, string? realRoot, IndexEntry entry, string firstVolume,
        IProgress<string>? phase, StageTracker? tracker, CancellationToken ct)
    {
        var dest = Path.Combine(request.TargetRoot, ToLocal(entry.Path));
        // The escape check has to come before **any** write action: the temp file is a write too, and it will follow links out of the root just the same.
        if (!WriteStaysInsideRoot(realRoot, dest))
        {
            phase?.Report(UnsafeRestorePathException.MessageFor(entry.Path));
            return null;
        }

        // Write a temp file in the same directory first, verify it, then swap it in: writing straight to dest means one mid-way failure
        // (network drop, corrupt archive, cancellation) leaves behind something truncated that has already overwritten the user's original file.
        var part = dest + ".asb-part";
        // The temp file has to pass the boundary check as well: put a symlink entry
        // `<somefile>.asb-part -> /etc/cron.d/x` into the index (which may come from any container via /import); symlinks are restored before file entries,
        // and FileStream will then follow it and write the archive content outside the root — checking dest alone does not stop this one.
        if (!WriteStaysInsideRoot(realRoot, part))
        {
            phase?.Report(UnsafeRestorePathException.MessageFor(entry.Path));
            return null;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            var hasher = new StreamingHasher(0, 0);
            long written;
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var sink = new HashingStream(hasher, file))
            {
                // No member name: after dedup the entry name inside the archive comes from the path that **uploaded this content first**,
                // which isn't necessarily the current index entry's Path; a single-file archive has only one member, so the entire output is its content.
                // Counted into the preparing row: 7z writes the content into the sink WE hand it (PackingProgressStream).
                written = await compressor.ExtractToStreamAsync(firstVolume, entryName: null, request.Password,
                    tracker is null ? sink : new PackingProgressStream(tracker, sink), ct);
            }

            // When `7z x -so` can't find the member it produces empty output but **exit code 0**, so the exit code can't be the basis for passing —
            // the length and the hash are. If the archive holds more than one entry the contents get concatenated, and the length gate stops that too.
            if (written != entry.Length)
            {
                throw new IOException(
                    $"archive yielded {written} byte(s) for '{entry.Path}' but the index says {entry.Length}");
            }
            if (entry.FullHash is not null && hasher.FullHash != entry.FullHash)
                throw new IOException($"archive content for '{entry.Path}' does not match the hash in the index");

            if (request.Conflict == RestoreConflictMode.RenameKeep && File.Exists(dest))
                RestoreConflict.RenameExisting(dest, DateTimeOffset.UtcNow);
            File.Move(part, dest, overwrite: true);
            ApplyMetadata(dest, entry);
            return dest;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(part);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFile(part);
            phase?.Report($"Failed to restore '{entry.Path}': {ex.Message}");
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>Writes the restored content to the destination path. RenameKeep with the target already present (getting this far means the content differs or can't be compared) →
    /// rename the existing local file to {name}.bak-{ts} to preserve the old content, then write the restored content under the original name (the old content is never lost).
    /// <para>
    /// Verify-then-swap, the same discipline as the streamed path: the extracted bytes are checked against the
    /// entry's recorded length (and hash, when recorded) INSIDE a same-directory temp file, and only a copy
    /// that passes moves over the destination. A straight overwrite meant a subtle pack corruption 7z's CRC
    /// let through, or an I/O fault partway through the copy, silently replaced the user's good file with a
    /// truncated or wrong one — and the run reported success. Returns false = the content failed verification
    /// (the caller counts the entry failed; the destination is untouched).
    /// </para>
    /// Skip is re-checked at this moment, not only at planning time: an Archive rehydration can put hours
    /// between the two, and a file the user created at this path meanwhile must not be clobbered by a decision
    /// made before it existed.</summary>
    private bool WriteRestoredFile(RestoreRequest request, string? realRoot, IndexEntry entry, string sourceFile)
    {
        var dest = Path.Combine(request.TargetRoot, ToLocal(entry.Path));

        // The index comes from the cloud (possibly any container imported via /import): an entry path containing .. would be written outside the target root.
        // The check operates on the **resolved real path** — a purely lexical check can't stop "create the link first, then write through it":
        // a symlink entry in the index (restored before file entries) points outside the root, after which <root>/link/x is lexically
        // entirely inside the root, yet File.Copy follows the link and lands outside it.
        // Skip that entry rather than aborting the whole restore — consistent with the existing per-group fault-tolerance semantics.
        if (!WriteStaysInsideRoot(realRoot, dest))
            throw new UnsafeRestorePathException(entry.Path);
        if (request.Conflict == RestoreConflictMode.Skip && File.Exists(dest))
            return true; // appeared during the wait; Skip's contract is "the target exists → leave it"

        var actualLength = new FileInfo(sourceFile).Length;
        if (actualLength != entry.Length)
            return false;
        if (entry.FullHash is not null && hasher.FullHashAsync(sourceFile).GetAwaiter().GetResult() != entry.FullHash)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        // Same-directory part file so the final Move is a rename, never a cross-device copy.
        var part = dest + ".asb-part";
        try
        {
            File.Copy(sourceFile, part, overwrite: true);
            if (request.Conflict == RestoreConflictMode.RenameKeep && File.Exists(dest))
                RestoreConflict.RenameExisting(dest, DateTimeOffset.UtcNow);
            File.Move(part, dest, overwrite: true);
        }
        finally
        {
            TryDeleteFile(part);
        }
        ApplyMetadata(dest, entry);
        return true;
    }

    /// <summary>The outcome of restoring a symlink entry. All three differ and none can stand in for another:
    /// "unchanged" means nothing happened; "unsafe" means a security check fired and the user has to be able to see it;
    /// "malformed" (M3) means the entry itself is missing its Target and failed to restore, which has to be equally visible — it must not be
    /// quietly counted as Skipped under the guise of "unchanged" (that would imply the link is already correct, which is not true of a malformed entry).</summary>
    private enum SymlinkOutcome
    {
        Created,
        Unchanged,
        Unsafe,
        Malformed,
    }

    private SymlinkOutcome RestoreSymlink(string targetRoot, string? realRoot, IndexEntry entry)
    {
        if (entry.Target is null)
            return SymlinkOutcome.Malformed;

        var dest = Path.Combine(targetRoot, ToLocal(entry.Path));

        // Same as WriteRestoredFile: when the index entry's path contains .. or passes through a link pointing outside the root,
        // the link would be created outside the target root, so block it.
        // Note this uses the "resolve the parent directory only" variant: entry.Target pointing outside the root is **legitimate**
        // (the backup faithfully recorded an original absolute symlink, and restoring it is correct); what is forbidden is only "writing through a link".
        if (!LinkStaysInsideRoot(realRoot, dest))
            return SymlinkOutcome.Unsafe;

        // Use LinkTarget (lstat underneath) to decide "unchanged", not FileInfo.Exists: the latter is always false for a symlink
        // that **points at a directory**, so such links can never be judged "unchanged" and a second restore inevitably reaches
        // CreateSymbolicLink and throws because it already exists (before this change that aborted the whole restore).
        // LinkTarget is null both when "it isn't a link" and when "it doesn't exist" — exactly the two cases that need recreating.
        var existingLink = new FileInfo(dest).LinkTarget;
        if (existingLink == entry.Target)
            return SymlinkOutcome.Unchanged; // unchanged

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        // An existing link is unlinked directly with File.Delete (no following); an existing regular file is deleted first as well.
        // Path.Exists follows links, so dangling links are covered by existingLink.
        if (existingLink is not null || Path.Exists(dest)) File.Delete(dest);
        File.CreateSymbolicLink(dest, entry.Target);
        return SymlinkOutcome.Created;
    }

    /// <summary>
    /// The escape check before a write (file/directory), operating on the **resolved real path**.
    /// <para>
    /// The purely lexical <see cref="PathBoundary.IsWithin"/> is not enough to hold the line here: restore creates symlink entries **first**
    /// and writes file entries **after**, so one <c>evil -&gt; /etc/cron.d</c> entry plus one <c>evil/x</c> entry in the index is enough to let
    /// <c>&lt;root&gt;/evil/x</c> pass the check as entirely lexically compliant, while <c>File.Copy</c> / <c>CreateDirectory</c>
    /// follow that link and land the content in <c>/etc/cron.d/x</c>. The check has to settle after symlink expansion, exactly the way the kernel does.
    /// </para>
    /// <para>
    /// <paramref name="realRoot"/> is the resolved real path of the target root **itself**, computed **once** by the caller (<see cref="RunCoreAsync"/>)
    /// at the start of this restore and reused throughout — <c>request.TargetRoot</c> doesn't change during the run, so there's no point making
    /// every entry (twice over for file entries) walk lstat again (compare <see cref="PathBoundary"/>'s "singleton: resolve once at construction"
    /// for the same value). The <paramref name="dest"/> side **must** be re-resolved every time
    /// and cannot be cached alongside it: it is a candidate path that may be created/changed during this very restore, and caching would make the
    /// "create the link first, then write through it" attack surface undetectable. Restoring into a directory that is itself reached through a symlink
    /// (<c>/data -&gt; /mnt/disk1/data</c>) has to keep working, so the root must be resolved as well — resolving only the candidate path is not enough.
    /// </para>
    /// <para>A failed resolution (a cycle / contains \0 / an empty string) is always treated as an escape — fail closed.</para>
    /// </summary>
    private static bool WriteStaysInsideRoot(string? realRoot, string dest)
    {
        var realDest = PathBoundary.ResolveReal(dest);
        return realRoot is not null && realDest is not null && PathBoundary.IsWithin(realRoot, realDest);
    }

    /// <summary>
    /// The escape check before creating a symlink: <paramref name="realRoot"/> is the same as in <see cref="WriteStaysInsideRoot"/> —
    /// resolved once at the start of this restore and reused throughout; the final segment is joined by name and **not resolved**.
    /// <para>
    /// The final segment must not be resolved, because creating/deleting a link doesn't follow the final segment itself (<c>symlinkat</c>/<c>unlinkat</c> semantics),
    /// and because on a second restore of that legitimate absolute symlink pointing outside the root, the final segment is that very link —
    /// resolving it would misjudge "re-restoring a legitimate link" as an escape. The parent directory still **must** be re-resolved every time:
    /// which directory the link is created in depends on the real location the intermediate path segments lead to once followed, and that can change during this restore.
    /// </para>
    /// </summary>
    private static bool LinkStaysInsideRoot(string? realRoot, string dest)
    {
        var parent = Path.GetDirectoryName(dest);
        if (string.IsNullOrEmpty(parent))
            return false;

        var realParent = PathBoundary.ResolveReal(parent);
        if (realRoot is null || realParent is null)
            return false;

        // The final segment may be ".."/"." (a malformed entry): leave IsWithin's lexical normalization to close that off.
        return PathBoundary.IsWithin(realRoot, Path.Combine(realParent, Path.GetFileName(dest)));
    }

    private static void ApplyMetadata(string dest, IndexEntry entry)
    {
        File.SetLastWriteTimeUtc(dest, entry.Mtime.UtcDateTime);

        if (!OperatingSystem.IsWindows()
            && !string.IsNullOrEmpty(entry.Permissions) && entry.Permissions != "0000")
        {
            try
            {
                File.SetUnixFileMode(dest, (UnixFileMode)Convert.ToInt32(entry.Permissions, 8));
            }
            catch (FormatException) { /* not an octal permission, ignore */ }
        }
    }

    /// <summary>The real case probe, or the test-injected one. Every probe site goes through here so a test only has to override one thing.</summary>
    private bool ProbeCaseInsensitive(string dir) => (CaseProbe ?? PathCaseSensitivity.IsCaseInsensitive)(dir);

    /// <summary>Ensures an archive (including all of its volumes) has been rehydrated out of Archive and is downloadable: starts rehydration for the ones that haven't, then polls until all are ready.</summary>
    private static RehydratePriority MapPriority(RestoreRehydratePriority p) =>
        p == RestoreRehydratePriority.High ? RehydratePriority.High : RehydratePriority.Standard;

    /// <summary>The cloud directory holding temporary Hot copies of archived volumes during a restore. The
    /// prefix IS the bookkeeping: whatever sits under it is by definition disposable — the restore deletes its
    /// own copies when the group finishes, and <see cref="RestoreTempSweeper"/> clears the whole directory at
    /// startup, which is what makes a crash (or a restore run from another device) leave no bill behind.</summary>
    public const string TempCopyPrefix = "restore-tmp/";

    /// <summary>
    /// Makes an archived family downloadable **without touching the originals**: each volume is Copy-Blob'd to
    /// <see cref="TempCopyPrefix"/>{name} with the requested online tier, and the copies are polled until they
    /// land. This replaced in-place rehydration on the user's call ("不是活化,而是直接复制一个副本来用"): the source
    /// stays in Archive the whole time — its 180-day early-deletion clock never resets, no re-archive step is
    /// owed afterwards — and cleanup collapses to "delete the directory", crash-safe and visible from any
    /// device. Azure prices the copy's read like a rehydration (the same wait, the same priority knob), plus
    /// the copies' online storage for the hours they exist; that trade buys never mutating the backup itself.
    /// <para>Returns the temp base name to download from. Copies that already exist (a retried restore) are
    /// reused rather than re-copied — the sweeper only runs at startup, so nothing else owns them.</para>
    /// </summary>
    private static async Task<string> EnsureHotCopyAsync(
        BlobContainerClient container, string baseRef, AccessTier tier, RehydratePriority priority, int pollSeconds,
        IProgress<string>? phase, CancellationToken ct)
    {
        var vols = new List<string>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, ct))
            if (VolumeBlobIO.IsVolumeOf(baseRef, b.Name))
                vols.Add(b.Name);

        var tempBase = TempCopyPrefix + baseRef;
        // One self-healing loop, because the wait is HOURS (a copy out of Archive is a rehydration under the
        // hood) and a single transient failure hour three must not fail the group ("不要在这个等待时间出错"). Each
        // cycle: volumes with no copy yet get one started, started ones get their status read; any network
        // hiccup on either step is reported and simply retried next cycle. The only hard failures are the
        // service SAYING the copy failed (CopyStatus.Failed/Aborted) and the caller's own cancellation — a
        // stop during the wait leaves pending copies behind for the startup sweeper, by design.
        var started = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pending = 0;
            string? hiccup = null;
            foreach (var name in vols)
            {
                var dest = container.GetBlobClient(TempCopyPrefix + name);
                try
                {
                    if (!started.Contains(name))
                    {
                        if (!(await dest.ExistsAsync(ct)).Value)
                            await dest.StartCopyFromUriAsync(container.GetBlobClient(name).Uri, new BlobCopyFromUriOptions
                            {
                                AccessTier = tier,
                                RehydratePriority = priority,
                            }, ct);
                        // An existing dest is an earlier attempt's copy — adopt it; the status read below judges it.
                        started.Add(name);
                    }
                    var props = (await dest.GetPropertiesAsync(cancellationToken: ct)).Value;
                    if (props.BlobCopyStatus == CopyStatus.Pending)
                        pending++;
                    else if (props.BlobCopyStatus is CopyStatus.Failed or CopyStatus.Aborted)
                        throw new InvalidOperationException(
                            $"Copying {name} out of the archive tier failed: {props.CopyStatusDescription}");
                }
                catch (RequestFailedException ex)
                {
                    // Transient (network, throttling, a 5xx): the copy service-side is unaffected — keep waiting.
                    pending++;
                    hiccup = $"{name}: {ex.Status} {ex.ErrorCode}";
                    started.Remove(name); // re-verify existence next cycle rather than trusting a failed round-trip
                }
            }
            if (pending == 0)
                return tempBase;
            phase?.Report(hiccup is null
                ? $"Waiting for the Hot copies of {baseRef} — {pending} volume(s) still copying out of Archive…"
                : $"Waiting for the Hot copies of {baseRef} — {pending} pending; last error (retrying): {hiccup}");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, pollSeconds)), ct);
        }
    }

    /// <summary>Deletes the temp copies of one family (best effort — the startup sweeper is the backstop).</summary>
    private static async Task DeleteTempCopiesAsync(BlobContainerClient container, string baseRef)
    {
        try
        {
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, TempCopyPrefix + baseRef, default))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();
        }
        catch { /* best effort */ }
    }

    private static string ToLocal(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private string NewTempDir()
    {
        var dir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>A restore entry's destination path escaped TargetRoot (the index was tampered with or came from an untrusted container).</summary>
public sealed class UnsafeRestorePathException(string entryPath)
    : Exception(UnsafeRestorePathException.MessageFor(entryPath))
{
    /// <summary>
    /// Shared message construction: used both by the exception's constructor and by the report sites that only need one line of text
    /// (the phase reports for escaping directory/symlink/file entries inside <see cref="RestoreOrchestrator"/>), so the latter
    /// don't have to allocate an exception object just to get hold of a string.
    /// </summary>
    public static string MessageFor(string entryPath) => $"Restore entry path escapes the target root: {entryPath}";
}
