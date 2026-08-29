using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Repair result: the paths repaired, the paths ruled unrecoverable, and the orphan blob names reclaimed by deletion (§4.8).</summary>
public sealed record RepairReport(
    IReadOnlyList<string> Repaired, IReadOnlyList<string> Unrecoverable, IReadOnlyList<string> DeletedOrphans);

/// <summary>
/// Repair blobs that are corrupt / missing / short of volumes in the cloud from **local files** (an explicit action,
/// PRD check): if the local file is still there and its content hash matches → recompress and **fully replace** that
/// blob (deleting all the old volumes first); the mtime inside the archive is irrelevant (what gets displayed is the
/// index metadata, and restore resets timestamps/permissions afterwards). If the local file is gone or its hash
/// changed while the cloud copy is bad → mark that file **unrecoverable** in the versions concerned.
/// Because blobs/packs are shared across versions: after a repair the volume count/sizes are updated in every
/// referencing version, and a pack is recompressed as a whole from the surviving members of all versions.
/// </summary>
public sealed class BackupRepairer(
    IBlobClientFactory factory,
    IBackupInfoStore store,
    IFileCompressor compressor,
    IFileHasher hasher,
    IBlobUploader uploader,
    string tempRoot,
    // Repair and backup share the same physical temp disk: compression goes through the same global lock, and the
    // temporary footprint counts against the same budget. tempRoot is still kept — the compose-side intermediate
    // inputs need somewhere to live, and they are accounted for through ReserveAsync.
    StagingArea staging,
    INotifier? notifier = null,
    IOperationLog? opLog = null,
    BackupChecker? checker = null,
    TrackedInfoStore? trackedInfo = null,
    ILocalIndexCache? indexCache = null,
    // The other "don't call me an orphan" list, exactly as in the retention sweep: a suspended run's uploads are
    // in the cloud but in no version index, and only the journal records that they exist.
    BackupJournalStore? journals = null)
{
    /// <param name="dontCompress">
    /// The configured "don't compress" rules (the very same set as BackupEngineOptions.DontCompress). When repairing
    /// a single-file blob, StoreOnly is derived from them per repaired path, so the recompressed archive uses the same
    /// compression mode a fresh backup would write for that file. null = no rules (compress everything).
    /// </param>
    public async Task<RepairReport> RepairAsync(
        Account account, string container, string? password, string localRoot, int? version,
        CheckOptions checkOptions, AccessTier dataTier, long? volumeBytes, IgnoreRuleSet? dontCompress,
        // The plan's per-file selection: null = everything the pre-check finds. A path left out is deferred
        // entirely — not repaired, not marked unrecoverable, not touched. Deselection is a scheduling decision;
        // unrecoverable is a verdict, and nobody asked for one.
        IReadOnlyCollection<string>? onlyPaths = null,
        Action<StageProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        // Local-authoritative read first (same as the orchestrator/checker): if it is local, zero cloud reads; if
        // not, read the cloud and backfill. The write side already goes through trackedInfo.
        // Hold one staging seat for the duration of the repair: both the recompressed archives and the compose
        // directory that assembles members land on the same physical temp disk as the backup's, and the quota is
        // split evenly across the runs currently in flight (see StagingArea).
        using var lease = staging.AcquireLease();

        // The user clicked Repair and deserves to hear that, not "Check started" from the internal pre-check.
        await Record(NotificationEvents.CheckStart, $"repair:{account.Id}/{container}",
            $"Repair started: {container}", "from matching local files", ct);

        var info = (trackedInfo is not null
                ? await trackedInfo.LoadAsync(account, container, password, ct)
                : await store.ReadInfoAsync(account, container, password, ct))
            ?? throw new InvalidOperationException("No backup found in container.");
        if (info.Versions.Count == 0)
            throw new InvalidOperationException("Backup has no versions.");
        var target = version is { } v
            ? info.Versions.FirstOrDefault(x => x.Version == v) ?? throw new InvalidOperationException($"Version {v} not found.")
            : info.Versions[^1];

        // Find the blobs that are bad in the cloud: run the checker (at the chosen depth) over the target version.
        // Orphan listing is left to the deletion step, which recomputes it itself (TOCTOU-safe).
        var report = await (checker ?? throw new InvalidOperationException("Repair requires a checker."))
            // sentinelPath: null deliberately. The sentinel's only effect on a check is to demote the local axis,
            // and this check already pins it to None — so there is nothing here for it to demote, and passing one
            // would just be a value with no consequence. Repair itself needs no gate either: it rebuilds bad cloud
            // blobs *from* local content, one direction only, so an unmounted source means "nothing is repairable
            // from local" and the run does less, never something wrong.
            // notify: false — this check is an implementation detail of the repair, and pushing "Check started"
            // for it made a user who had just clicked Repair wonder what was running. The repair announces
            // itself below; a silent pre-check leaves exactly one story told.
            .CheckAsync(account, container, password, target.Version,
                checkOptions with { Local = LocalCheckLevel.None, ListOrphans = false }, localRoot, null, ct,
                notify: false,
                // The pre-check's stages surface under the repair's own name: a user watching "Cloud: N volumes"
                // concluded a check had started instead of their repair. Same work, but the label must say whose.
                onProgress: onProgress is null ? null : d => onProgress(d with { Stage = "Assessing" }));
        var badFindings = report.Findings
            .Where(f => f.Cloud == CloudState.MissingOrBad && f.Ref is not null)
            .ToList();
        var badBlobs = badFindings
            .Where(f => onlyPaths is null || onlyPaths.Contains(f.Path))
            .Select(f => f.Ref!).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        // The plan's selection semantics: unticked = mark damaged and leave it to the next backup version. No
        // probing, no hashing, no upload for a deferred blob — just the mark, applied to every path referencing
        // it in every version, which is what restore substitution and the heal-on-next-backup path key off.
        var deferredBlobs = badFindings
            .Select(f => f.Ref!).Distinct(StringComparer.Ordinal)
            .Where(r => !badBlobs.Contains(r)).ToHashSet(StringComparer.Ordinal);
        // Selected paths whose blob the pre-check found healthy: healed by other means (the backup's own healing
        // upload of a dedup-excluded twin, an earlier repair the marks never caught up with). The verdict is
        // overturned by the evidence, so the marks come off — this is what lets the deferred-repair loop converge
        // after a heal it did not itself perform.
        var healedPaths = onlyPaths is null
            ? []
            : report.Findings.Where(f => f.Cloud == CloudState.Ok && onlyPaths.Contains(f.Path))
                .Select(f => f.Path).ToList();

        // The same addressing scheme as the backup path: repairing a single-file blob has to reproduce exactly the
        // collision-detection metadata a fresh backup would write (§ defect 2).
        var addressing = new BlobAddressScheme(password, info.Backup.KdfSalt);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var repaired = new List<string>();
        var unrecoverable = new List<string>();
        var deletedOrphans = new List<string>();
        // The long half of the run, displayed the way the backup's rows are: per damaged object, with the local
        // read (the hash gate), the rebuild and the upload each visible — a 100 GB file's repair has an honest
        // floor of one full read plus one compression, and this is what makes that floor look like work.
        using var tracker = onProgress is null
            ? null
            : new StageTracker("Repairing", badBlobs.Count, onProgress, speedWhileInFlight: true);
        // Headline completion by source bytes, the same reasoning as restore's: one object can be a 100 GB file
        // or a small one, and an object count says nothing about how much of the evening is left. The per-blob
        // workload is the recorded source length (packs: their recorded original bytes).
        long WorkOf(string badRef) => badFindings.Where(f => f.Ref == badRef).Sum(f => f.Length);

        if (badBlobs.Count > 0 || deferredBlobs.Count > 0 || healedPaths.Count > 0)
        {
            // Load every version index (pack members are aggregated across versions, and after a repair the sizes
            // are synced / paths marked unrecoverable). Loaded for marking-only and unmarking-only runs too: an
            // empty selection ("mark everything for the next version") is all marks and no repairs.
            var indexes = new Dictionary<int, VersionIndex>();
            foreach (var ver in info.Versions)
                indexes[ver.Version] = await store.ReadIndexAsync(account, container, ver.IndexBlob, password, ver.IndexVolumes, ct);

            var changedVersions = new HashSet<int>();
            if (tracker is not null)
                foreach (var badRef in badBlobs)
                    tracker.Enqueue(WorkOf(badRef));
            foreach (var badRef in badBlobs)
            {
                tracker?.Touch(badRef);
                if (badRef.StartsWith("packs/", StringComparison.Ordinal))
                    await RepairPackAsync(account, cc, badRef, info, indexes, localRoot, password, dataTier, volumeBytes,
                        repaired, unrecoverable, changedVersions, lease, ct, tracker);
                else
                    await RepairBlobAsync(account, cc, badRef, indexes, localRoot, password, addressing, dataTier, volumeBytes,
                        dontCompress, repaired, unrecoverable, changedVersions, lease, ct, tracker);
                tracker?.Advance(0, WorkOf(badRef));
            }

            foreach (var path in healedPaths)
            {
                foreach (var (vnum, idx) in indexes)
                    ClearUnrecoverable(idx, path, changedVersions, vnum);
                // Reported as repaired: what the caller asked ("make this path whole") is true, whoever did the work.
                repaired.Add(path);
            }

            // Deferred blobs: the mark and nothing else. Every path referencing the blob, in every version that
            // does — the same aggregation the repair paths use, minus all the work.
            foreach (var deferredRef in deferredBlobs)
            {
                var bareRef = deferredRef.StartsWith("packs/", StringComparison.Ordinal)
                    ? deferredRef["packs/".Length..^".7z".Length]
                    : deferredRef;
                foreach (var (vnum, idx) in indexes)
                    foreach (var e in idx.Entries)
                        if (e.Storage is { } s && s.Ref == bareRef)
                            MarkUnrecoverable(idx, e.Path, unrecoverable, changedVersions, vnum);
            }

            // Persist the changed version indexes + info file (through the local-authoritative state machine, which
            // keeps the ETag/cache consistent so the next backup does not hit a 412).
            var identity = info.Backup.CreatedAt.UtcTicks;
            foreach (var vnum in changedVersions)
            {
                await store.WriteIndexAsync(account, container, vnum, indexes[vnum], password, ct: ct);
                if (indexCache is not null)
                    await indexCache.PutAsync(account.Id, container, vnum, identity, indexes[vnum], ct);
            }
            if (trackedInfo is not null)
                await trackedInfo.WriteAsync(account, container, info, password, tier: null, ct: ct);
            else
                await store.WriteInfoAsync(account, container, info, password, ct: ct);
        }

        // Orphan reclamation (§4.8): done after the repair writes have landed — the reference set is built **again**
        // right before deleting (TOCTOU-safe).
        if (checkOptions.ListOrphans)
            await DeleteOrphansAsync(account, container, cc, password, deletedOrphans, ct);

        await Record(NotificationEvents.CheckSuccess, $"repair:{account.Id}/{container}",
            $"Repair finished: {container}",
            $"{repaired.Distinct().Count()} repaired, {unrecoverable.Distinct().Count()} unrecoverable, {deletedOrphans.Count} orphan(s) deleted", ct);
        if (unrecoverable.Count > 0)
            await Record(NotificationEvents.UnrecoverableError, $"repair:{account.Id}/{container}",
                $"Unrecoverable files after repair: {container}", string.Join(", ", unrecoverable.Distinct().Take(20)), ct);

        return new RepairReport(repaired.Distinct().ToList(), unrecoverable.Distinct().ToList(), deletedOrphans);
    }

    /// <summary>
    /// Delete the orphan blobs that no retained version references (§4.8). **TOCTOU-safe**: immediately before
    /// deleting, the info file + every version index are **re-read** to build the reference set (so it reflects the
    /// changes this repair just landed). If the complete reference set cannot be built (the info file is gone, or some
    /// version index fails to read) → **give up on deleting**, log a Warning, delete not a single one. The info file /
    /// the indexes / any referenced volume are never deleted (they are all inside the reference set).
    /// </summary>
    private async Task DeleteOrphansAsync(
        Account account, string container, BlobContainerClient cc, string? password, List<string> deletedOrphans, CancellationToken ct)
    {
        HashSet<string> referenced;
        try
        {
            var freshInfo = await store.ReadInfoAsync(account, container, password, ct)
                ?? throw new InvalidOperationException("Info file not found.");
            referenced = await (checker ?? throw new InvalidOperationException("Repair requires a checker."))
                .BuildReferencedSetAsync(account, container, password, freshInfo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (opLog is not null)
                await opLog.AppendAsync(OperationLogLevel.Warning, $"repair:{account.Id}/{container}",
                    $"Orphan cleanup abandoned: could not build the full reference set ({ex.Message}). No blobs were deleted.", ct, durable: true);
            return;
        }

        var active = journals is null
            ? ActiveJournalRefs.Empty
            : await journals.LoadActiveRefsAsync(account.Id, container, ct);
        await foreach (var b in cc.GetBlobsAsync(cancellationToken: ct))
        {
            // Referenced by a retained version, or held by an active journal (a suspended run's uploads — deleting
            // them makes the eventual resume re-upload everything it had already sent): both mean "not an orphan".
            if (referenced.Contains(b.Name) || BackupChecker.JournalProtected(b.Name, active))
                continue;
            // Best effort per blob: one failed orphan deletion only logs a Warning and moves on without
            // interrupting the rest (only blobs outside the reference set get here, so valid data is never deleted).
            try
            {
                await cc.GetBlobClient(b.Name).DeleteIfExistsAsync(cancellationToken: ct);
                deletedOrphans.Add(b.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (opLog is not null)
                    await opLog.AppendAsync(OperationLogLevel.Warning, $"repair:{account.Id}/{container}",
                        $"Failed to delete orphan blob {b.Name}: {ex.Message}", ct, durable: true);
            }
        }
    }

    /// <summary>Repair a single-file data blob: rebuild and replace it from the local file at any referencing path (hash-verified), then update the sizes in every referencing version.</summary>
    private async Task RepairBlobAsync(
        Account account, BlobContainerClient cc, string blobRef, Dictionary<int, VersionIndex> indexes, string localRoot,
        string? password, BlobAddressScheme addressing, AccessTier dataTier, long? volumeBytes,
        IgnoreRuleSet? dontCompress, List<string> repaired,
        List<string> unrecoverable, HashSet<int> changedVersions,
        StagingArea.StagingLease lease, CancellationToken ct, StageTracker? tracker = null)
    {
        // The entries across all versions that reference this blob (identical content at different paths can yield several).
        var refs = indexes.SelectMany(kv => kv.Value.Entries
                .Where(e => e.Storage is { Kind: "blob" } s && s.Ref == blobRef)
                .Select(e => (Version: kv.Key, Entry: e)))
            .ToList();
        if (refs.Count == 0)
            return;
        var entry0 = refs[0].Entry;
        var fullHash = entry0.FullHash;
        var raw = entry0.Storage!.Raw;

        // Find a local file with matching content at any of the referencing paths, and record its backup-relative
        // path along the way: DontCompress matches on paths, so StoreOnly has to be derived from the path actually
        // taken for the recompression (when several paths with identical content share one blob, a fresh backup
        // derives it from the one actually uploaded too).
        string? localSource = null;
        string? sourcePath = null;
        foreach (var (_, e) in refs)
        {
            var local = Path.Combine(localRoot, e.Path.Replace('/', Path.DirectorySeparatorChar));
            // e.Path comes from the cloud index, which after /import is attacker-controlled (design §5): an entry
            // that escapes localRoot turns "is there a local file whose content hash equals X" into a probeable
            // confirmation oracle, and once probed it would also upload the content of that out-of-root file to the
            // cloud. Skip this candidate path — the other legitimate references to the same content can still be
            // tried, and when none of them is usable it falls through to "mark unrecoverable" as usual.
            if (!PathBoundary.IsWithin(localRoot, local))
                continue;
            if (await LocalMatchesAsync(local, fullHash, entry0.Length, ct, tracker))
            {
                localSource = local;
                sourcePath = e.Path;
                break;
            }
        }

        if (localSource is null)
        {
            // Local cannot supply it → every entry referencing this blob is unrecoverable in its own version.
            foreach (var (vnum, e) in refs)
                MarkUnrecoverable(indexes[vnum], e.Path, unrecoverable, changedVersions, vnum);
            return;
        }
        // The collision-detection metadata must match a fresh backup exactly: reuse the length/head/tail already
        // recorded on the entry (the content has not changed — it passed the fullHash check — so those values are
        // unchanged) rather than recomputing them here, which would risk disagreeing with the metadata of the other
        // references to the same content if the headBytes setting has drifted.
        // When head/tail are null (a legacy index entry missing the fields) they are passed through as-is: Metadata
        // omits the key rather than writing an empty string — an empty string would make later dedup treat identical
        // content as a collision and report it falsely (see BlobAddressScheme.Metadata).
        //
        // Which one to take: the order of refs depends on dictionary enumeration order, so refs[0] may well be the
        // legacy entry missing head/tail while a sibling reference to the same content has both — going by refs[0]
        // would throw away collision protection we already hold, needlessly widening the window of degraded
        // protection. Prefer the entry that has both; only when there is none fall back to entry0 (the content is
        // identical, so length/head/tail ought to be the same on every referencing entry anyway).
        var metaEntry = refs.Select(r => r.Entry)
            .FirstOrDefault(e => e.HeadHash is not null && e.TailHash is not null) ?? entry0;
        var meta = new Dictionary<string, string>(
            addressing.Metadata(fullHash!, metaEntry.Length, metaEntry.HeadHash, metaEntry.TailHash));
        if (raw)
            // The raw route's blob IS the source file, whose full hash is already verified this very repair — the
            // uploader uses it verbatim instead of buffering and rehashing (see BlobUploader's caller-supplied-label rule).
            meta[VolumeIdentity.MetaKey] = fullHash!;
        // The same derivation as a fresh backup (BackupOrchestrator.HandleBlobAsync): a path that hits DontCompress is stored, not compressed.
        var storeOnly = dontCompress?.MatchesFileOrAncestorDir(sourcePath!) ?? false;
        var newSizes = await ReplaceBlobAsync(
            account, cc, blobRef, localSource, raw, dataTier, volumeBytes, password, meta, storeOnly, lease, ct, tracker);

        // Omitting the metadata = this object's collision protection is weakened (in keyed mode it switches to the
        // narrow v1 check value, degrading to fullHash + length rather than to no protection at all — when head/tail
        // are unknown, Metadata already emits v1, see BlobAddressScheme).
        // That is the correct handling in itself (writing empty strings would be worse), but leaving no trace makes
        // the degradation invisible: record an auditable Warning.
        if (opLog is not null && (metaEntry.HeadHash is null || metaEntry.TailHash is null))
        {
            var missing = metaEntry.HeadHash is null
                ? (metaEntry.TailHash is null ? "head and tail" : "head")
                : "tail";
            await opLog.AppendAsync(OperationLogLevel.Warning, $"repair:{account.Id}/{cc.Name}",
                $"Collision guard degraded for {blobRef}: no index entry records the {missing} hash, " +
                "so the repaired object was published without the omitted collision metadata.", ct, durable: true);
        }

        // Update the volume count/sizes in every referencing version (the content is unchanged, so the ref stays the same).
        foreach (var (vnum, e) in refs)
        {
            var idx = indexes[vnum];
            var i = idx.Entries.IndexOf(e);
            idx.Entries[i] = e with { Storage = e.Storage! with { Volumes = newSizes.Count, VolumeSizes = [.. newSizes] } };
            changedVersions.Add(vnum);
            ClearUnrecoverable(idx, e.Path, changedVersions, vnum);
        }
        repaired.AddRange(refs.Select(r => r.Entry.Path));
    }

    /// <summary>Repair a pack: aggregate the surviving members across all versions, rebuild from local (hash-verified)
    /// whichever ones can be obtained, then recompress the whole pack and replace it; the members that cannot be
    /// obtained are marked unrecoverable in the versions that reference them.</summary>
    private async Task RepairPackAsync(
        Account account, BlobContainerClient cc, string packBlobRef, BackupInfoFile info, Dictionary<int, VersionIndex> indexes,
        string localRoot, string? password, AccessTier dataTier, long? volumeBytes,
        List<string> repaired, List<string> unrecoverable, HashSet<int> changedVersions,
        StagingArea.StagingLease lease, CancellationToken ct, StageTracker? tracker = null)
    {
        var packId = packBlobRef["packs/".Length..^".7z".Length];

        // Aggregate the members referencing this pack across all versions: entryName → (fullHash, the versions + paths referencing it).
        var members = new Dictionary<string, (string? Hash, long Length, List<(int Version, string Path)> Refs)>(StringComparer.Ordinal);
        foreach (var (vnum, idx) in indexes)
            foreach (var e in idx.Entries)
                if (e.Storage is { Kind: "pack" } s && s.Ref == packId && s.EntryName is { } en)
                {
                    if (!members.TryGetValue(en, out var m))
                        m = members[en] = (e.FullHash, e.Length, []);
                    m.Refs.Add((vnum, e.Path));
                }

        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        var composeDir = Path.Combine(work, "compose");
        // The compose directory holds the **raw** content of every member obtainable from local, and that disk is
        // the same one the backup stages on. The upper bound is the total raw member bytes recorded for this pack:
        // what actually gets assembled can only be less (members that cannot be obtained are ruled unrecoverable).
        using var composeReservation = await staging.ReserveAsync(
            info.Packs.TryGetValue(packId, out var sizeHint) ? sizeHint.OriginalBytes : 0, lease, ct);

        Directory.CreateDirectory(composeDir);
        try
        {
            var available = new List<string>();
            foreach (var (entryName, m) in members)
            {
                var local = Path.Combine(localRoot, entryName.Replace('/', Path.DirectorySeparatorChar));
                // entryName comes from the cloud index, which after /import is attacker-controlled (design §5).
                // This single check guards both sides: the read side's local (an out-of-root confirmation oracle +
                // re-uploading out-of-root content), and the write side's
                // dest = Path.Combine(composeDir, <the same relative fragment>) — both concatenate the very same
                // string, so they are in or out of bounds identically and one test covers them. An out-of-bounds
                // member is handled as "not obtainable from local": it takes the else branch and is marked
                // unrecoverable, never quietly counted as an available member.
                if (!PathBoundary.IsWithin(localRoot, local))
                {
                    foreach (var (vnum, path) in m.Refs)
                        MarkUnrecoverable(indexes[vnum], path, unrecoverable, changedVersions, vnum);
                    continue;
                }
                if (await LocalMatchesAsync(local, m.Hash, m.Length, ct, tracker))
                {
                    var dest = Path.Combine(composeDir, entryName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(local, dest, overwrite: true);
                    available.Add(entryName);
                }
                else
                {
                    foreach (var (vnum, path) in m.Refs)
                        MarkUnrecoverable(indexes[vnum], path, unrecoverable, changedVersions, vnum);
                }
            }

            if (available.Count == 0)
            {
                info.Packs.Remove(packId); // The whole pack cannot be rebuilt from local; every member is already marked unrecoverable
                return;
            }

            // Recompress from the members that are available and replace the same packId: upload the new volumes
            // over the old ones first, then delete the leftover old volumes (no longer "wipe it empty first").
            // Through StagingArea: compression therefore shares the one global lock with backup (the two no longer
            // chew CPU at the same time), its output counts against the same budget, and it keeps the per-volume
            // release — each volume is deleted once uploaded, so the peak is only the volumes not yet uploaded.
            var staged = await staging.StageAsync(
                async (compressTemp, token) =>
                {
                    // The compression mode comes from the value the pack itself recorded (PackInfo.StoreOnly); the
                    // don't-compress rules are not re-run: this rewrites the archive of the same packId in place, so
                    // the repaired pack should match the one originally written.
                    var result = await compressor.CompressAsync(
                        new CompressionRequest(composeDir, available, Path.Combine(compressTemp, packId + ".7z"),
                            password, VolumeBytes: volumeBytes,
                            StoreOnly: info.Packs.TryGetValue(packId, out var packInfo) && packInfo.StoreOnly), token);
                    return result.VolumeFiles;
                }, lease, ct);
            List<long> newSizes;
            try
            {
                newSizes = staged.Files.Select(f => new FileInfo(f).Length).ToList(); // grab the sizes before releasing
                await VolumeBlobIO.ReplaceAsync(uploader, account, cc, packBlobRef, staged.Files, dataTier, retry: null, ct, tracker: tracker);
            }
            finally
            {
                staging.Release(staged);
            }

            if (info.Packs.TryGetValue(packId, out var pi))
                info.Packs[packId] = pi with
                {
                    Members = available.Select(en => members[en].Hash!).ToList(),
                    Volumes = newSizes.Count,
                    VolumeSizes = newSizes,
                };
            foreach (var (vnum, path) in available.SelectMany(en => members[en].Refs))
                ClearUnrecoverable(indexes[vnum], path, changedVersions, vnum);
            repaired.AddRange(available.SelectMany(en => members[en].Refs.Select(r => r.Path)));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Replace a single-file blob with newly uploaded content: upload the new volumes over the old ones
    /// first, then delete the leftover old volumes (no longer "wipe it empty first"). Returns the new volume sizes.
    /// <paramref name="metadata"/> is the collision-detection metadata matching a fresh backup (len/head/tail, or
    /// v/v1 when encrypted); <paramref name="storeOnly"/> is likewise derived by the caller from the configured
    /// DontCompress rules, matching a fresh backup.</summary>
    private async Task<IReadOnlyList<long>> ReplaceBlobAsync(
        Account account, BlobContainerClient cc, string blobRef, string localSource, bool raw, AccessTier dataTier,
        long? volumeBytes, string? password, IReadOnlyDictionary<string, string> metadata, bool storeOnly,
        StagingArea.StagingLease lease, CancellationToken ct, StageTracker? tracker = null)
    {
        if (raw)
        {
            // A raw upload (uncompressed) still carries the collision-detection metadata, with the raw marker
            // layered on top — same as the backup path's UploadNewAsync.
            var rawMeta = new Dictionary<string, string>(metadata) { ["raw"] = "1" };
            await VolumeBlobIO.ReplaceAsync(uploader, account, cc, blobRef, [localSource], dataTier, retry: null, ct, rawMeta, tracker);
            return [new FileInfo(localSource).Length];
        }

        {
            var srcDir = Path.GetDirectoryName(localSource)!;
            var entry = Path.GetFileName(localSource);
            // The original password must be passed to the recompression, otherwise objects in an encrypted backup
            // get silently rewritten as plaintext 7z (a confidentiality defect).
            // StoreOnly is derived per path by the caller from the configured DontCompress rules (the same set as
            // BackupOrchestrator.HandleBlobAsync), so the repaired archive uses the same compression mode a fresh
            // backup would write for that file.
            // (The pack path does not do this: a pack's compression mode is fixed at packing time and recorded in
            // PackInfo.StoreOnly, see RepairPackAsync — that reads the value recorded on the pack instead of
            // re-running the rules.)
            // The source file is fed straight to 7z, with no compose-style intermediates, so only the archive output
            // needs accounting — StageAsync covers all of it: the global compression lock, the budget, and the
            // per-volume release.
            var staged = await staging.StageAsync(
                async (compressTemp, token) =>
                {
                    var result = await compressor.CompressAsync(
                        new CompressionRequest(srcDir, [entry], Path.Combine(compressTemp, "b.7z"), password,
                            VolumeBytes: volumeBytes, StoreOnly: storeOnly), token);
                    return result.VolumeFiles;
                }, lease, ct);
            try
            {
                var sizes = staged.Files.Select(f => new FileInfo(f).Length).ToList(); // grab the sizes before releasing
                await VolumeBlobIO.ReplaceAsync(uploader, account, cc, blobRef, staged.Files, dataTier, retry: null, ct, metadata, tracker);
                return sizes;
            }
            finally
            {
                staging.Release(staged);
            }
        }
    }

    /// <summary>
    /// Whether this local file can serve as a repair source: it exists, and its content hash matches what the cloud
    /// recorded.
    /// <para>
    /// Unreadable (locked / permissions revoked / media read error) always counts as **cannot** — content we cannot
    /// obtain must not be used to overwrite the cloud. This guard cannot be skipped: a repair runs precisely after a
    /// check reported problems, so the odds of an unreadable local file are far from low (the checker now reports
    /// such files as Missing, and the user goes straight from reading the report to clicking repair). On top of that
    /// the outer per-blob loop has no backstop, so one throw fails the **whole repair operation** midway — by then
    /// the already-repaired blobs have long since been uploaded, but their index changes are all written back only
    /// after the loop, so that part of the work is lost along with it.
    /// </para>
    /// When it returns false the caller takes one of the two existing paths: the single-file path keeps trying the
    /// other references to the same content, and the grouped path marks that member unrecoverable outright — exactly
    /// the same handling as "there is no such file locally".
    /// </summary>
    private async Task<bool> LocalMatchesAsync(
        string local, string? expectedHash, long expectedLength, CancellationToken ct, StageTracker? tracker = null)
    {
        if (expectedHash is null || !File.Exists(local))
            return false;
        try
        {
            // The length answers first: a file of a different length cannot hash-match, and skipping the read is
            // not a micro-optimization — in the field the candidate was a ~100 GB appended file, and "give this
            // file up" cost a full disk scan of it before repair could conclude what a stat already knew.
            if (new FileInfo(local).Length != expectedLength)
                return false;
            // The read is registered as an in-flight item, so the hash gate over a 100 GB candidate shows as
            // moving bytes rather than a frozen row — the exact silence a user once read as a hang.
            tracker?.BeginItem(local, local, expectedLength);
            try
            {
                var progress = tracker?.ItemProgress(local);
                return await hasher.FullHashAsync(local, ct, progress) == expectedHash;
            }
            finally
            {
                tracker?.EndItem(local, 0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void MarkUnrecoverable(
        VersionIndex index, string path, List<string> unrecoverable, HashSet<int> changedVersions, int vnum)
    {
        if (!index.UnrecoverablePaths.Contains(path))
        {
            index.UnrecoverablePaths.Add(path);
            changedVersions.Add(vnum);
        }
        unrecoverable.Add(path);
    }

    /// <summary>The inverse of <see cref="MarkUnrecoverable"/>: a path repaired in this run sheds the verdict a
    /// previous run recorded. The mark is a verdict, and a verdict overturned must come off the record — left in
    /// place, it outlives the damage, and restore keeps routing the healed file through version substitution as
    /// if it were still lost.</summary>
    private static void ClearUnrecoverable(VersionIndex index, string path, HashSet<int> changedVersions, int vnum)
    {
        if (index.UnrecoverablePaths.Remove(path))
            changedVersions.Add(vnum);
    }

    private async Task Record(NotificationEvents evt, string source, string title, string body, CancellationToken ct)
    {
        if (opLog is not null)
            await opLog.AppendAsync(EventLog.LevelOf(evt), source, $"{title} — {body}", ct, durable: true);
        if (notifier is not null)
            await notifier.NotifyAsync(evt, title, body, ct);
    }
}
