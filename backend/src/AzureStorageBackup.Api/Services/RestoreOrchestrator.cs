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
/// or the index contains a duplicate Path (no way to tell which one is authoritative, so neither is written).
/// RestoredDirs = the number of empty directories **actually created successfully** (escaping/failed ones don't count).</summary>
public sealed record RestoreResult(int Version, int RestoredFiles, int SkippedFiles, int RestoredDirs, int FailedFiles);

/// <summary>
/// Restore orchestrator (M5, PRD 1.5): reads the info file plus the second-level index, downloads data blobs / packs and extracts them with 7z,
/// writes them back under the local root, and restores permissions/mtime and empty folders. "Overwrite only when changed" — skip if the local file already has the same hash.
/// </summary>
public sealed class RestoreOrchestrator(
    IBlobClientFactory factory,
    IBackupInfoStore store,
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

        var version = request.Version is { } v
            ? info.Versions.FirstOrDefault(x => x.Version == v)
              ?? throw new InvalidOperationException($"Version {v} not found.")
            : info.Versions[^1];

        var index = await store.ReadIndexAsync(request.Account, request.Container, version.IndexBlob, request.Password, version.IndexVolumes, ct);

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

        // The effective entry per path: by default the one from this version; a substituted path uses the same-path entry from the chosen version (content + metadata both from that version).
        var byPath = IndexByPath(index.Entries, phase, out var duplicatePaths);
        failed += duplicatePaths; // Index entries with a duplicate Path: neither is written, each such path counts as one failure, and the whole restore is not aborted.
        var resolved = new HashSet<string>(StringComparer.Ordinal); // the substitution paths that actually resolved
        foreach (var grp in request.Substitutions.GroupBy(kv => kv.Value))
        {
            var sv = info.Versions.FirstOrDefault(x => x.Version == grp.Key);
            if (sv is null)
                continue; // the substitute version was deleted by retention cleanup → the whole group falls back to being skipped
            var srcIndex = await store.ReadIndexAsync(request.Account, request.Container, sv.IndexBlob, request.Password, sv.IndexVolumes, ct);
            // The substitute source version's index also comes from the cloud and can equally well contain duplicate Paths; a substitution path that can't be resolved
            // falls back to the existing "intent declared but the substitute isn't available" skip semantics (the TryGetValue below simply doesn't find it).
            var srcByPath = IndexByPath(srcIndex.Entries, phase, out _);
            foreach (var kv in grp)
                if (srcByPath.TryGetValue(kv.Key, out var se))
                {
                    byPath[kv.Key] = se;
                    resolved.Add(kv.Key);
                }
        }

        // Selective restore (requirement B): narrow the effective set down to the paths the user selected. The filter takes effect before grouping,
        // so each pack is still downloaded only once but only the selected members get written — unselected members never enter fileEntries at all, so no over-restore.
        HashSet<string>? selected = request.SelectedPaths is null
            ? null
            : new HashSet<string>(request.SelectedPaths, StringComparer.Ordinal);
        if (selected is not null)
            foreach (var key in byPath.Keys.Where(k => !selected.Contains(k)).ToList())
                byPath.Remove(key);

        // Unrecoverable with no substitute that "resolved successfully" → skip (declaring the intent but not having the substitute available also falls back to skipping, not erroring).
        // Under selective restore, only the selected unrecoverable paths are counted.
        var unresolved = index.UnrecoverablePaths
            .Where(p => !resolved.Contains(p) && (selected is null || selected.Contains(p)))
            .ToHashSet(StringComparer.Ordinal);
        skipped += unresolved.Count;

        // Empty folders (restore has to recreate them) — selective restore only targets the selected files, it does not rebuild the entire empty-directory tree.
        // These come from the cloud index as well: a directory name containing .. would be created outside the target root, so escaping directory entries are skipped, not created.
        // The check operates on the **resolved real path**: CreateDirectory follows symlinks in the intermediate path segments,
        // and a single link left inside the root by a previous restore (or by the user) that points outside is enough to make a directory that "looks like it is inside the root"
        // land outside it.
        var restoredDirs = 0;
        if (selected is null)
            foreach (var dir in index.EmptyDirs)
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

        // symlinks and files are handled separately
        var fileEntries = new List<IndexEntry>();
        foreach (var e in byPath.Values)
        {
            if (unresolved.Contains(e.Path))
                continue;
            if (e.Kind == "symlink")
            {
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
            else
            {
                fileEntries.Add(e);
            }
        }

        // A 0-byte file has no storage reference to group by — the backup side never produces one for it (see BackupOrchestrator.IsEmptyFile),
        // because it has no content that needs storing. Its entire information content is "length is zero", so the file is created directly from that here.
        // This goes before the grouping: the Where(e => e.Storage is not null) below filters them out, and without this block
        // the restored tree would be **silently missing a few files**, while every content-comparison check would still pass.
        foreach (var e in fileEntries.Where(IsEmptyFileEntry))
        {
            switch (await TryCreateEmptyFileAsync(request, realRoot, e, phase, ct))
            {
                case EmptyFileOutcome.Created: restored++; break;
                case EmptyFileOutcome.Unchanged: skipped++; break;
                default: failed++; break;
            }
        }

        // Group by storage: the same pack is downloaded/extracted only once. Groups download concurrently (PRD 3.4), each with its own temp subdirectory to avoid collisions.
        var work = NewTempDir();
        var rehydrated = new System.Collections.Concurrent.ConcurrentBag<string>(); // base names of the blobs that were rehydrated; re-archived once we're done
        using var gate = new SemaphoreSlim(Math.Max(1, request.DownloadConcurrency));
        try
        {
            var groups = fileEntries.Where(e => e.Storage is not null).GroupBy(e => StorageKey(e.Storage!)).ToList();
            // The total is only known once grouping is done (the same pack is downloaded once), which is why the tracker can't be built any earlier.
            var tracker = onProgress is null
                ? null
                : new StageTracker("Restoring", groups.Count, onProgress, speedWhileInFlight: true) { Clock = Clock };

            // Declare two units of work: how many source bytes will be written out (after extraction), and how many bytes will come over the wire (compressed).
            // Reporting progress by group count alone is distorted — one group can be a single 100 GB file, or a box of several hundred small ones.
            // The download total **must only be reported if every single group can answer it**: handing out an undersized denominator when an old index lacks volume sizes
            // makes the percentage run high the whole way and then sit stuck at 100%, which is worse than showing nothing.
            var groupWork = groups.ToDictionary(g => g.Key, g => g.Sum(e => e.Length), StringComparer.Ordinal);
            var downloadSizes = groups.ToDictionary(
                g => g.Key, g => TransferLabel.DownloadBytesOf(g.First().Storage!, info), StringComparer.Ordinal);
            var downloadTotalKnown = downloadSizes.Values.All(b => b > 0);
            foreach (var g in groups)
                tracker?.Enqueue(groupWork[g.Key], downloadTotalKnown ? downloadSizes[g.Key] : 0);

            var tasks = groups.Select(async g =>
            {
                try
                {
                    return await RestoreGroupAsync(
                        container, request, realRoot, work, g.ToList(), gate, rehydrated, phase, tracker,
                        downloadSizes[g.Key], ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    phase?.Report($"Group failed ({g.Key}): {ex.Message}");
                    return (Restored: 0, Skipped: 0, Failed: g.Count());
                }
                finally
                {
                    // Counting and in-flight are separate concerns: a group occupies exactly one slot. Work units are likewise retired in one go — failed groups have to retire too,
                    // otherwise the remaining amount never reaches zero and the ETA hangs there forever.
                    tracker?.Advance(0, groupWork[g.Key]);
                }
            });
            var counts = await Task.WhenAll(tasks);
            tracker?.Complete(); // without forcing a terminal state, the last group's bytes get squashed by the throttle and never go out
            restored += counts.Sum(c => c.Restored);
            skipped += counts.Sum(c => c.Skipped);
            failed += counts.Sum(c => c.Failed);
        }
        finally
        {
            TryDelete(work);
        }

        // After the restore, put the rehydrated blobs back into Archive (keeping the backup's original tier; best effort).
        if (request.ReArchiveAfterRestore && !rehydrated.IsEmpty)
        {
            phase?.Report($"Re-archiving {rehydrated.Distinct().Count()} object(s)…");
            foreach (var baseRef in rehydrated.Distinct())
                await SetTierForVolumesAsync(container, baseRef, AccessTier.Archive, ct);
        }

        return new RestoreResult(version.Version, restored, skipped, restoredDirs, failed);
    }

    private async Task<(int Restored, int Skipped, int Failed)> RestoreGroupAsync(
        BlobContainerClient container, RestoreRequest request, string? realRoot, string work,
        List<IndexEntry> group, SemaphoreSlim gate, System.Collections.Concurrent.ConcurrentBag<string> rehydrated,
        IProgress<string>? phase, StageTracker? tracker, long downloadBytes, CancellationToken ct)
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
                    await EnsureOnlineAsync(container, blobName, request.RehydrateTier, MapPriority(request.RehydratePriority), request.RehydratePollSeconds, phase, ct);
                    rehydrated.Add(blobName);
                    // Only reopen the window once rehydration is done and we're actually about to download — the same rhythm as the original BeginItem.
                    tracker?.BeginItem(blobName, TransferLabel.For(storage, needed), downloadBytes);
                    firstVolume = await VolumeBlobIO.DownloadAsync(container, blobName, groupDir, ct, itemProgress);
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
                tracker?.BeginPacking();
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
                            var streamed = await TryStreamRestoredFileAsync(request, realRoot, e, firstVolume, phase, ct);
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
    private static bool TryWriteRestoredFile(RestoreRequest request, string? realRoot, IndexEntry entry, string sourceFile, IProgress<string>? phase)
    {
        try
        {
            WriteRestoredFile(request, realRoot, entry, sourceFile);
            return true;
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
        IProgress<string>? phase, CancellationToken ct)
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
                written = await compressor.ExtractToStreamAsync(firstVolume, entryName: null, request.Password, sink, ct);
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
    /// rename the existing local file to {name}.bak-{ts} to preserve the old content, then write the restored content under the original name (the old content is never lost).</summary>
    private static void WriteRestoredFile(RestoreRequest request, string? realRoot, IndexEntry entry, string sourceFile)
    {
        var dest = Path.Combine(request.TargetRoot, ToLocal(entry.Path));

        // The index comes from the cloud (possibly any container imported via /import): an entry path containing .. would be written outside the target root.
        // The check operates on the **resolved real path** — a purely lexical check can't stop "create the link first, then write through it":
        // a symlink entry in the index (restored before file entries) points outside the root, after which <root>/link/x is lexically
        // entirely inside the root, yet File.Copy follows the link and lands outside it.
        // Skip that entry rather than aborting the whole restore — consistent with the existing per-group fault-tolerance semantics.
        if (!WriteStaysInsideRoot(realRoot, dest))
            throw new UnsafeRestorePathException(entry.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (request.Conflict == RestoreConflictMode.RenameKeep && File.Exists(dest))
            RestoreConflict.RenameExisting(dest, DateTimeOffset.UtcNow);
        File.Copy(sourceFile, dest, overwrite: true);
        ApplyMetadata(dest, entry);
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

    /// <summary>
    /// Indexes by Path, with **every** entry under a duplicated Path taking no effect — when two entries contradict each other there is no telling which is authoritative,
    /// so we would rather write neither than guess. The index comes from the cloud (<c>/import</c> can import any container), and a duplicate Path is the index
    /// contradicting itself; handle it under the existing per-entry fault-tolerance principle: fail only the duplicated path, and never let <c>ToDictionary</c>'s
    /// <see cref="ArgumentException"/> abort the whole restore.
    /// </summary>
    private static Dictionary<string, IndexEntry> IndexByPath(
        List<IndexEntry> entries, IProgress<string>? phase, out int duplicateCount)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
            if (!seen.Add(e.Path))
                duplicates.Add(e.Path);

        var map = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        foreach (var e in entries)
            if (!duplicates.Contains(e.Path))
                map[e.Path] = e;

        foreach (var p in duplicates)
            phase?.Report($"Skipped duplicate index entry (ambiguous which version is authoritative): {p}");
        duplicateCount = duplicates.Count;
        return map;
    }

    private static string StorageKey(StorageRef s) => s.Kind == "pack" ? "pack:" + s.Ref : "blob:" + s.Ref;



    /// <summary>Ensures an archive (including all of its volumes) has been rehydrated out of Archive and is downloadable: starts rehydration for the ones that haven't, then polls until all are ready.</summary>
    private static RehydratePriority MapPriority(RestoreRehydratePriority p) =>
        p == RestoreRehydratePriority.High ? RehydratePriority.High : RehydratePriority.Standard;

    private static async Task EnsureOnlineAsync(
        BlobContainerClient container, string baseRef, AccessTier tier, RehydratePriority priority, int pollSeconds,
        IProgress<string>? phase, CancellationToken ct)
    {
        var vols = new List<string>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, ct))
            vols.Add(b.Name);

        // Start rehydration for the volumes that haven't started yet (standard priority; all volumes, not just the first).
        // Note: this deliberately does not reuse BlobRehydration.BeginAsync (which swallows SetAccessTierAsync exceptions per volume) —
        // this method holds the download concurrency gate and polls indefinitely, so a failed rehydration request has to propagate quickly as a restore failure,
        // otherwise it would hang indefinitely while holding the gate after the exception got swallowed.
        foreach (var name in vols)
        {
            var props = (await container.GetBlobClient(name).GetPropertiesAsync(cancellationToken: ct)).Value;
            if (props.AccessTier == "Archive" && string.IsNullOrEmpty(props.ArchiveStatus))
                await container.GetBlobClient(name).SetAccessTierAsync(tier, rehydratePriority: priority, cancellationToken: ct);
        }

        // Poll until no volume is in Archive any more (rehydration complete, on the order of hours).
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pending = 0;
            foreach (var name in vols)
            {
                var props = (await container.GetBlobClient(name).GetPropertiesAsync(cancellationToken: ct)).Value;
                if (props.AccessTier == "Archive")
                    pending++;
            }
            if (pending == 0)
                return;
            phase?.Report($"Waiting for rehydration of {baseRef} — {pending} volume(s) still archived…");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, pollSeconds)), ct);
        }
    }

    /// <summary>Sets all volumes of an archive to the given tier (best effort, used to re-archive after a restore).</summary>
    private static async Task SetTierForVolumesAsync(BlobContainerClient container, string baseRef, AccessTier tier, CancellationToken ct)
    {
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, ct))
        {
            try { await container.GetBlobClient(b.Name).SetAccessTierAsync(tier, cancellationToken: ct); }
            catch { /* best effort */ }
        }
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
