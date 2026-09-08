using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Repair result: the paths repaired, the paths ruled unrecoverable, and the orphan blob names reclaimed by deletion (§4.8).</summary>
public sealed record RepairReport(
    IReadOnlyList<string> Repaired, IReadOnlyList<string> Unrecoverable, IReadOnlyList<string> DeletedOrphans);

/// <summary>
/// Repair blobs that are corrupt / missing / short of volumes in the cloud from **local files** (an explicit action,
/// PRD check): if the local file is still there and its content hash matches → recompress and **replace** that blob
/// whole via <see cref="VolumeBlobIO.ReplaceAsync"/> — new volumes upload over the old family first (a surviving
/// volume that proves itself by label, length and downloaded bytes is verified in place and skipped; an archived
/// target is deleted-then-written, since Put Blob cannot overwrite an archived blob), and only then are leftovers
/// outside the new set deleted. The mtime inside the archive is irrelevant (what gets displayed is the
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
    // The container's SQLite catalog, in place of every version's whole index in memory. Repair reads its entries
    // out of it (who references this damaged object, which members does this pack have, is this path already
    // marked) and writes its verdicts back as patches — after the rewritten index is in the cloud, never before.
    IVersionCatalogs catalogs,
    INotifier? notifier = null,
    IOperationLog? opLog = null,
    BackupChecker? checker = null,
    TrackedInfoStore? trackedInfo = null,
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
        // The plan's per-file selection: null = everything the pre-check finds. A path in neither list is not
        // even assessed. Ticked = repair now; listed in alsoMarkPaths = mark damaged and leave it to the next
        // backup version. Together they scope the assessment: only their families are probed — in the field an
        // unscoped assessment probed 194,630 volumes for a 4-file repair.
        IReadOnlyCollection<string>? onlyPaths = null,
        IReadOnlyCollection<string>? alsoMarkPaths = null,
        Action<StageProgress>? onProgress = null,
        // The same knob the backup's upload uses (Settings → Upload concurrency): repair volumes ride an
        // identical gate + sliding window. It used to be a serial loop, and a 455.933 GB field repair ran one
        // volume at a time on a link the backup drives with five.
        int uploadConcurrency = 5,
        // Awaited before each object and before each volume: the run row's Pause. Volume-granular (100 MB by
        // default), so a pause answers in seconds even mid-way through a hundred-gigabyte family.
        Func<CancellationToken, Task>? pauseGate = null,
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
                // The pre-check's probing surfaces under the repair's own name: a user watching "Cloud: N
                // volumes" concluded a check had started instead of their repair. Same work, but the label must
                // say whose. Only the Cloud stage is renamed, though — the token decides the UI's unit word, and
                // the check's Local pass (pinned to None here, an instant bookkeeping sweep over the scoped
                // entries) published its entry count under the volumes-unit token: a field report saw "4 of 4
                // volumes" flash at the end of an assessment whose real workload was thousands of probes. That
                // pass says nothing a user can act on, so it is dropped rather than renamed; everything else
                // (LoadingIndex, a Content level's Verifying) passes through under its own honest name and unit.
                onProgress: onProgress is null ? null : d =>
                {
                    if (d.Stage == "Local")
                        return;
                    onProgress(d.Stage == "Cloud" ? d with { Stage = "Assessing" } : d);
                },
                // Scoped to the two lists' union: a path in neither is not assessed at all. Under this scope the
                // deferral formula below keeps its meaning for free — "bad findings not selected" can only be the
                // alsoMark ones, because nothing else was looked at.
                scopePaths: onlyPaths is null ? null : [.. onlyPaths.Concat(alsoMarkPaths ?? [])]);
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
        var assessedScope = onlyPaths is null
            ? null
            : new HashSet<string>(onlyPaths.Concat(alsoMarkPaths ?? []), StringComparer.Ordinal);
        var healedPaths = assessedScope is null
            ? []
            : report.Findings.Where(f => f.Cloud == CloudState.Ok && assessedScope.Contains(f.Path))
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
            // The staged-pool readers are what puts "N volumes (X GB) waiting for uploading" on the repair's
            // in-flight line the way the backup's shows it ("问题2" — the line was missing its segments).
            : new StageTracker("Repairing", badBlobs.Count, onProgress, speedWhileInFlight: true,
                stagedBytes: () => lease.Bytes, stagedFiles: () => lease.Files);
        // The volume transfer machinery of the backup, one gate per run: slots arbitrated by item age, a
        // sliding window per family, per-volume in-flight registration under the source's own label.
        var streams = Math.Max(1, uploadConcurrency);
        var uploadScope = tracker is null ? null : new VolumeUploadScope(new VolumeUploadGate(streams), tracker, streams);
        // Headline completion by source bytes, the same reasoning as restore's: one object can be a 100 GB file
        // or a small one, and an object count says nothing about how much of the evening is left. The per-blob
        // workload is the recorded source length (packs: their recorded original bytes).
        long WorkOf(string badRef) => badFindings.Where(f => f.Ref == badRef).Sum(f => f.Length);

        var failures = new List<(string Ref, string Message)>();
        if (badBlobs.Count > 0 || deferredBlobs.Count > 0 || healedPaths.Count > 0)
        {
            // Every retained version has to be IN the catalog before anything is asked of it: pack members are
            // aggregated across versions, and after a repair the sizes are synced / paths marked unrecoverable in
            // every version that references the object. Ensured for marking-only and unmarking-only runs too — an
            // empty selection ("mark everything for the next version") is all marks and no repairs.
            var identity = info.Backup.CreatedAt.UtcTicks;
            foreach (var ver in info.Versions)
                await catalogs.EnsureVersionAsync(account, container, ver, identity, password, ct);

            // The catalog can hold versions the info file no longer lists: a retired one whose removal never
            // reached it, or one imported against an older info file. The queries below are keyed by storage REF,
            // not by version, so they would hand those versions' entries back too — and marking one writes an index
            // blob into the cloud for a version nothing claims, reports paths no retained version has, and leaves
            // marks the next retention sweep has no reason to clean up. Bounded to what the info file lists, which
            // is exactly what the dictionary of loaded indexes used to be bounded by.
            var versions = info.Versions.Select(v => v.Version).ToHashSet();

            // ONE read-only handle for the whole repair. Read-only deliberately: the container's write lock is
            // taken only inside PersistChangedAsync, for the moment the patches are recorded, and this handle is
            // not the one holding it — a reader alongside a short-lived writer on the same file is what the
            // catalog's WAL mode is for, and each statement on this connection sees whatever the writer last
            // committed, so the marks a persist recorded are visible here immediately afterwards.
            await using var catalog = await catalogs.OpenAsync(account.Id, container, readOnly: true, ct);
            var patches = new RepairPatchSet();

            async Task PersistChangedAsync()
            {
                // THE ORDER IS THE POINT. The cloud index is the source of truth and the catalog is a cache of it,
                // so the cloud goes first: serialize the version with its pending patches applied on the way out,
                // upload that, write the info file — and only then record the same patches in the catalog. Recording
                // them first would leave the catalog claiming marks no index in the cloud carries, and since the
                // version's identity does not change, nothing would ever re-import it to find out otherwise.
                var changed = patches.ChangedVersions.ToList();
                // Serialized to a file, never to a byte[]: at a few million entries an index is hundreds of MB, and
                // holding a whole one in memory is exactly what moving the indexes into the catalog got rid of.
                Directory.CreateDirectory(tempRoot);
                foreach (var vnum in changed)
                {
                    // Named with a fresh guid, and deleted in the finally: two repairs of two containers share this
                    // directory, and a version number alone would have them writing over each other.
                    var serialized = Path.Combine(tempRoot, $"index-{vnum}-{Guid.NewGuid():N}.idx");
                    try
                    {
                        await using (var file = File.Create(serialized))
                            await catalog.SerializeVersionAsync(vnum, file, patches.PatchesFor(vnum), ct);
                        var (indexBlob, indexVolumes) =
                            await store.WriteIndexFileAsync(account, container, vnum, serialized, password, ct: ct);
                        // The rewritten index is not the same length as the one it replaces — a mark makes it
                        // longer, a repaired entry's new volume sizes longer still — so it can cross the split
                        // threshold and come back as N volumes where the recorded number is 1. Every reader takes
                        // versions[].indexVolumes as authoritative (restore, the checker, the lazy catalog
                        // migration, retention's volume deletion), so a stale 1 leaves them reading the first
                        // 64 MiB of an index and calling it the whole thing. Recorded with the index it describes,
                        // in the info-file write immediately below. FindIndex cannot miss: the patched versions are
                        // exactly the ones the info file lists (see the `versions` filter above).
                        var at = info.Versions.FindIndex(v => v.Version == vnum);
                        info.Versions[at] = info.Versions[at] with { IndexBlob = indexBlob, IndexVolumes = indexVolumes };
                    }
                    finally
                    {
                        try { File.Delete(serialized); } catch { /* temp space; nothing further to do about it */ }
                    }
                }

                // Through the local-authoritative state machine, which keeps the ETag consistent so the next backup
                // does not hit a 412.
                if (trackedInfo is not null)
                    await trackedInfo.WriteAsync(account, container, info, password, tier: null, ct: ct);
                else
                    await store.WriteInfoAsync(account, container, info, password, ct: ct);

                if (changed.Count > 0)
                {
                    // The container's single write slot, held for the patching and nothing else — the uploads above
                    // are minutes of work and must not sit inside it. If the patching cannot be written at all, the
                    // versions leave the catalog rather than staying behind as pre-repair rows the cloud has already
                    // moved past — and this call still throws. PersistChangedAsync runs twice: here at the pre-marks
                    // (patches.ChangedVersions.Count > 0 above), with the whole repair loop — RepairBlobAsync,
                    // RepairPackAsync, everything they resolve out of `catalog` — still ahead of it and needing the
                    // marks it just tried to record to actually be there; and again at end-of-run, where a swallowed
                    // failure would report a repair that finished clean over a catalog that does not hold what it
                    // claims. Either way, going on as if the write had landed is the one thing this must not do
                    // (see IVersionCatalogs.ApplyPatchesOrInvalidateAsync).
                    var all = changed.SelectMany(patches.PatchesFor).ToList();
                    await catalogs.ApplyPatchesOrInvalidateAsync(account.Id, container, all, log: null, ct);
                }

                // Recorded in both places now: the catalog answers for them from here on, and keeping them would
                // rewrite the same versions again at the end-of-run persist.
                foreach (var vnum in changed)
                    patches.Forget(vnum);
            }

            // MARKS LAND FIRST (volume-identity.md, designed with the user: "先废了这些文件.修一个,就恢复一个").
            // Every problem path — selected or deferred — is marked and PERSISTED before the first object is
            // touched. From this moment the marks state exactly which content is broken, whatever happens to
            // this run: a backup beside a suspended repair reads them for dedup exclusion and heal-in-passing,
            // restore reads them for substitution. Repairing an object then clears its marks (RepairBlobAsync's
            // per-ref ClearUnrecoverableAsync), and the end-of-run persistence records the clears.
            // Scoped by REF — "which CONTENT is broken" — never by bare path: the same path in an older
            // version references its own, different object, and a path-wide mark voided that intact copy too.
            // Left behind by a failed or suspended repair, the false verdict then soft-skipped restores of
            // healthy history and made /file-versions and the substitution guard refuse the one copy that
            // could recover the file (caught live by the damage-repair chaos storm). The deferred pass below
            // has always scoped by ref; the pre-marks now tell the same truth — including dedup twins: any
            // path, in any version, whose entry references a damaged object is equally broken.
            // Scratch list: the pre-marks are the safety state, not the verdicts — the REPORT's unrecoverable
            // list is owned by the end-of-run marking (deferred paths, and objects whose repair failed), or a
            // successfully repaired path would be reported unrecoverable because it was pre-marked at start.
            var damagedRefs = badFindings.Select(f => BareRefOf(f.Ref!)).ToHashSet(StringComparer.Ordinal);
            var preMarks = new List<string>();
            foreach (var damaged in damagedRefs)
                foreach (var (vnum, e) in await ReferencesAsync(catalog, damaged, versions, ct))
                    await MarkUnrecoverableAsync(catalog, patches, vnum, e.Path, preMarks, ct);
            if (patches.ChangedVersions.Count > 0)
                await PersistChangedAsync();

            // The backup's ledger discipline, taken as-is (it was tuned over many rounds — "你参考下backup"):
            // claim the authoritative per-item transferred reading up front, let each family's landed volumes
            // ride the unfinished ledger ("+X on the cloud"), and fold them into "uploaded" only at the
            // object's own write-off. Without it, per-volume completions inflated "uploaded" past the
            // per-object workDone it is displayed against — the field's "118% of original" mid-object.
            tracker?.SetTransferred(0);
            long uploadedTotal = 0;
            if (tracker is not null)
                foreach (var badRef in badBlobs)
                    tracker.Enqueue(WorkOf(badRef));
            foreach (var badRef in badBlobs)
            {
                if (pauseGate is not null)
                    await pauseGate(ct);
                // In hand before the first publish, or the queue over-reports: without BeginWork the tracker's
                // queued subtraction (enqueued − processed − in hand) still counts the object being worked on,
                // and the screen read "1 object hashing · 4 objects queued" over a four-object repair.
                tracker?.BeginWork();
                // The content-addressed ref means nothing to the person watching (the same rule as
                // ActiveTransfer.Label): the object is named by the path(s) that reference it.
                var faces = badFindings.Where(f => f.Ref == badRef).Select(f => f.Path).Distinct().ToList();
                tracker?.Touch(faces.Count > 1 ? $"{faces[0]} (+{faces.Count - 1} more)" : faces.FirstOrDefault() ?? badRef);
                // Mid-object workload progress: each landed (or verified-skipped) volume books its share, so the
                // byte percentage and the remaining-time estimate move DURING a 100 GB object instead of at its
                // end; the write-off below books only the remainder (never negative — shares are floored).
                long counted = 0;
                void WorkProgress(long share)
                {
                    Interlocked.Add(ref counted, share);
                    tracker?.AdvanceWork(share);
                }
                void Uploaded(long bytes) => Interlocked.Add(ref uploadedTotal, bytes);
                try
                {
                    try
                    {
                        if (badRef.StartsWith("packs/", StringComparison.Ordinal))
                            await RepairPackAsync(account, cc, badRef, info, catalog, versions, localRoot, password, dataTier, volumeBytes,
                                repaired, unrecoverable, patches, lease, ct, tracker, uploadScope, pauseGate, WorkProgress, Uploaded);
                        else
                            await RepairBlobAsync(account, cc, badRef, catalog, versions, localRoot, password, addressing, dataTier, volumeBytes,
                                dontCompress, repaired, unrecoverable, patches, lease, ct, tracker, uploadScope, pauseGate, WorkProgress, Uploaded);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One object's failure must not discard the others' work: everything already repaired has
                        // its replacement volumes in the cloud, and losing the bookkeeping meant a 10-hour run could
                        // end with nothing recorded. The failed object keeps its start-of-run mark (truthful — it is
                        // still broken), the loop moves on, and the run still surfaces the failure after persisting.
                        failures.Add((badRef, ex.Message));
                    }
                    // Written off **before** the item is handed back, the order the backup's ReportItem keeps: EndWork
                    // publishes the state after the last item left hand, and a write-off landing after that publish
                    // puts "1 object queued" on screen for an object that is finished (enqueued − processed − in hand,
                    // with processed one short) — the very over-report BeginWork above exists to prevent. Not in the
                    // finally: a cancelled object was not finished, and must not be counted as if it were.
                    tracker?.Advance(0, Math.Max(0, WorkOf(badRef) - Interlocked.Read(ref counted)));
                    // Transferred and workload settle at the same moment, on the same item — the pairing the
                    // backup's ReportItem states is what keeps the "(N% of original)" readable.
                    tracker?.SetTransferred(Interlocked.Read(ref uploadedTotal));
                }
                finally
                {
                    tracker?.EndWork();
                }
            }
            // The stage's wrap-up must force out the final state (the same rule Complete's own comment
            // states): the last write-off's publish is throttled like any other, and without this the
            // screen keeps the snapshot from just before it — folded bytes never shown as uploaded.
            tracker?.Complete();

            // Healed verdicts span the whole assessed scope — selected AND deferred. The pre-check genuinely
            // re-examined the deferred half (scopePaths is the union), and discarding its Ok verdicts left those
            // paths marked forever: restore kept substituting and dedup kept excluding, deterministically, on
            // every later cycle. Only the selected ones are REPORTED as repaired (that is what the caller asked
            // for); a deferred heal is recorded silently.
            var healedSet = new HashSet<string>(healedPaths, StringComparer.Ordinal);
            foreach (var path in healedPaths)
            {
                // Every retained version is asked, and the ones that hold no such mark answer no and stay out of
                // the changed set — the marks a version does hold are the only reason to rewrite its index.
                foreach (var ver in info.Versions)
                    await ClearUnrecoverableAsync(catalog, patches, ver.Version, path, ct);
                if (onlyPaths!.Contains(path))
                    repaired.Add(path);
            }

            // Deferred blobs: the mark and nothing else. Every path referencing the blob, in every version that
            // does — the same aggregation the repair paths use, minus all the work. Except the paths this very
            // run proved healthy: a Content-level check delivers per-member verdicts inside one pack, and
            // re-marking a healed member because it shares the archive with a damaged one reverts the heal the
            // loop above just applied.
            foreach (var deferredRef in deferredBlobs)
            {
                var bareRef = BareRefOf(deferredRef);
                foreach (var (vnum, e) in await ReferencesAsync(catalog, bareRef, versions, ct))
                    if (!healedSet.Contains(e.Path))
                        await MarkUnrecoverableAsync(catalog, patches, vnum, e.Path, unrecoverable, ct);
            }

            // Persist the changed version indexes + info file — successes and marks alike, whatever failed.
            await PersistChangedAsync();

            if (failures.Count > 0)
                throw new InvalidOperationException(
                    $"{failures.Count} object(s) failed to repair (the rest are recorded; the failed keep their marks). " +
                    $"First: {failures[0].Ref}: {failures[0].Message}");
        }

        // No full-container orphan sweep here any more ("repair只清自己repair的那几个文件,不要对全backup做"):
        // the per-family keep-set trim inside ReplaceAsync already removes every leftover volume of the
        // objects this run actually replaced — exact, scoped, and free. A whole-container sweep needs the
        // full reference set (every retained version's index) and a complete listing, which is minutes of
        // work that can shadow the NEXT repair; full garbage collection belongs to the post-backup cleanup,
        // which owns the machinery (journal protection, reference set) and runs when nothing competes.

        await Record(NotificationEvents.CheckSuccess, $"repair:{account.Id}/{container}",
            $"Repair finished: {container}",
            $"{repaired.Distinct().Count()} repaired, {unrecoverable.Distinct().Count()} unrecoverable, {deletedOrphans.Count} orphan(s) deleted", ct);
        if (unrecoverable.Count > 0)
            await Record(NotificationEvents.UnrecoverableError, $"repair:{account.Id}/{container}",
                $"Unrecoverable files after repair: {container}", string.Join(", ", unrecoverable.Distinct().Take(20)), ct);

        return new RepairReport(repaired.Distinct().ToList(), unrecoverable.Distinct().ToList(), deletedOrphans);
    }

    /// <summary>Repair a single-file data blob: rebuild and replace it from the local file at any referencing path (hash-verified), then update the sizes in every referencing version.</summary>
    private async Task RepairBlobAsync(
        Account account, BlobContainerClient cc, string blobRef, VersionCatalog catalog, IReadOnlySet<int> versions,
        string localRoot,
        string? password, BlobAddressScheme addressing, AccessTier dataTier, long? volumeBytes,
        IgnoreRuleSet? dontCompress, List<string> repaired,
        List<string> unrecoverable, RepairPatchSet patches,
        StagingArea.StagingLease lease, CancellationToken ct, StageTracker? tracker = null,
        VolumeUploadScope? uploadScope = null, Func<CancellationToken, Task>? pauseGate = null,
        Action<long>? workProgress = null, Action<long>? onUploaded = null)
    {
        // The entries across all versions that reference this blob (identical content at different paths can yield
        // several). Materialized rather than streamed: the list is walked several times below and is bounded by how
        // many entries reference one object — in practice at most one per retained version per path.
        var refs = (await ReferencesAsync(catalog, blobRef, versions, ct))
            .Where(r => r.Entry.Storage is { Kind: "blob" })
            .ToList();
        if (refs.Count == 0)
            return;
        var entry0 = refs[0].Entry;
        var fullHash = entry0.FullHash;
        var raw = entry0.Storage!.Raw;

        // The collision-detection metadata must match a fresh backup exactly: reuse the length/head/tail already
        // recorded on the entry (the content has not changed — it passed the fullHash check — so those values are
        // unchanged) rather than recomputing them here, which would risk disagreeing with the metadata of the other
        // references to the same content if the headBytes setting has drifted.
        // When head/tail are null (a legacy index entry missing the fields) they are passed through as-is: Metadata
        // omits the key rather than writing an empty string — an empty string would make later dedup treat identical
        // content as a collision and report it falsely (see BlobAddressScheme.Metadata).
        //
        // Which one to take: refs comes out oldest version first, so refs[0] may well be the legacy entry missing
        // head/tail while a sibling reference to the same content has both — going by refs[0] would throw away
        // collision protection we already hold, needlessly widening the window of degraded protection. Prefer the
        // entry that has both; only when there is none fall back to entry0 (the content is identical, so
        // length/head/tail ought to be the same on every referencing entry anyway).
        var metaEntry = refs.Select(r => r.Entry)
            .FirstOrDefault(e => e.HeadHash is not null && e.TailHash is not null) ?? entry0;
        var meta = new Dictionary<string, string>(
            addressing.Metadata(fullHash!, metaEntry.Length, metaEntry.HeadHash, metaEntry.TailHash));

        // Try the referencing paths as repair sources, one at a time, deriving StoreOnly from the path actually
        // taken (DontCompress matches on paths — same derivation as a fresh backup, and the same rule as when a
        // fresh backup picks which of several identical-content paths it uploads).
        //
        // The two routes verify the source at different moments, and the difference is the whole design:
        // · 7z route — the verdict **rides the production read** ("这种大文件也都是不用压缩的,读两遍而已,不如合并"):
        //   the old hash gate cost one extra end-to-end read of the source before a single volume was produced,
        //   which on a store-only 100 GB media file is the larger half of the repair's local IO. Only the free
        //   stat gate runs up front — a wrong-length candidate cannot hash-match and is rejected before any
        //   production is paid for.
        // · raw route — the gate stays: the upload streams straight from the source under a label that CLAIMS
        //   the recorded hash, so the claim must be proven before the upload exists at all.
        IReadOnlyList<long>? newSizes = null;
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
            if (raw)
            {
                if (!await LocalMatchesAsync(local, fullHash, entry0.Length, ct, tracker))
                    continue;
                // The raw route's blob IS the source file, whose full hash the gate just verified — the uploader
                // uses it verbatim instead of buffering and rehashing (see BlobUploader's caller-supplied-label rule).
                var rawMeta = new Dictionary<string, string>(meta)
                {
                    ["raw"] = "1",
                    [VolumeIdentity.MetaKey] = fullHash!,
                };
                // No per-volume release: the raw route uploads the user's own file, never staged, never
                // charged to the pool — and its label passthrough spares the uploader a rehash.
                tracker?.BeginUpload(blobRef, 1);
                try
                {
                    await VolumeBlobIO.ReplaceAsync(
                        uploader, account, cc, blobRef, [local], dataTier, retry: null, ct, rawMeta,
                        uploadScope, onVolumeUploaded: null, label: e.Path, beforeVolume: pauseGate);
                    newSizes = [new FileInfo(local).Length];
                    onUploaded?.Invoke(newSizes[0]);
                    tracker?.ConfirmUpload(blobRef);
                }
                finally
                {
                    tracker?.EndUpload(blobRef);
                }
                break;
            }
            // The stat gate (kept from LocalMatchesAsync): absent or wrong-length files cannot hash-match, and
            // stat answers that for free — in the field this was a ~100 GB appended file settled instantly.
            if (fullHash is null || !File.Exists(local) || new FileInfo(local).Length != entry0.Length)
                continue;
            var storeOnly = dontCompress?.MatchesFileOrAncestorDir(e.Path) ?? false;
            newSizes = await ReplaceFromVerifiedStreamAsync(
                account, cc, blobRef, local, e.Path, fullHash, entry0.Length, dataTier, volumeBytes, password,
                meta, storeOnly, lease, ct, tracker, uploadScope, pauseGate, workProgress, onUploaded);
            if (newSizes is not null)
                break;
        }

        if (newSizes is null)
        {
            // Local cannot supply it → every entry referencing this blob is unrecoverable in its own version.
            foreach (var (vnum, e) in refs)
                await MarkUnrecoverableAsync(catalog, patches, vnum, e.Path, unrecoverable, ct);
            return;
        }

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

        // Update the volume count/sizes in every referencing version (the content is unchanged, so the ref stays
        // the same). One patch per entry, merged with the clear below into a single change to that path.
        foreach (var (vnum, e) in refs)
        {
            patches.SetStorage(vnum, e.Path, e.Storage! with { Volumes = newSizes.Count, VolumeSizes = [.. newSizes] });
            await ClearUnrecoverableAsync(catalog, patches, vnum, e.Path, ct);
        }
        repaired.AddRange(refs.Select(r => r.Entry.Path));
    }

    /// <summary>Repair a pack: aggregate the surviving members across all versions, rebuild from local (hash-verified)
    /// whichever ones can be obtained, then recompress the whole pack and replace it; the members that cannot be
    /// obtained are marked unrecoverable in the versions that reference them.</summary>
    private async Task RepairPackAsync(
        Account account, BlobContainerClient cc, string packBlobRef, BackupInfoFile info, VersionCatalog catalog,
        IReadOnlySet<int> versions, string localRoot, string? password, AccessTier dataTier, long? volumeBytes,
        List<string> repaired, List<string> unrecoverable, RepairPatchSet patches,
        StagingArea.StagingLease lease, CancellationToken ct, StageTracker? tracker = null,
        VolumeUploadScope? uploadScope = null, Func<CancellationToken, Task>? pauseGate = null,
        Action<long>? workProgress = null, Action<long>? onUploaded = null)
    {
        var packId = packBlobRef["packs/".Length..^".7z".Length];

        // Aggregate the members referencing this pack across all versions: entryName → (fullHash, the versions +
        // paths referencing it). Read to the end before anything else queries the catalog: one catalog is one SQLite
        // connection, and the marking below asks it questions of its own.
        var members = new Dictionary<string, (string? Hash, long Length, List<(int Version, string Path)> Refs)>(StringComparer.Ordinal);
        await foreach (var (vnum, e) in catalog.PackMembersAsync(packId, ct))
            if (versions.Contains(vnum) && e.Storage is { EntryName: { } en })
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
                        await MarkUnrecoverableAsync(catalog, patches, vnum, path, unrecoverable, ct);
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
                        await MarkUnrecoverableAsync(catalog, patches, vnum, path, unrecoverable, ct);
                }
            }

            if (available.Count == 0)
            {
                // The whole pack cannot be rebuilt from local; every member is already marked unrecoverable.
                // The info.Packs entry STAYS: index entries still reference this packId, and removing its
                // record made every later reference-set build throw ("referenced but missing from info.Packs"),
                // which silently and permanently disabled orphan reclamation for the whole container — while an
                // existence-level check saw the still-present pack blob and reported Ok, masking the damage.
                // The marks are the truth here; the pack entry is the bookkeeping that keeps it tellable.
                return;
            }

            // Recompress from the members that are available and replace the same packId: upload the new volumes
            // over the old ones first, then delete the leftover old volumes (no longer "wipe it empty first").
            // Through StagingArea: compression therefore shares the one global lock with backup (the two no longer
            // chew CPU at the same time), its output counts against the same budget, and it keeps the per-volume
            // release — each volume is deleted once uploaded, so the peak is only the volumes not yet uploaded.
            // Through StageAsync's own tracker registration — staging, the room wait, the archive lock and the
            // packing stretch all surface exactly as the backup's do ("问题:in flight的内容比upload少").
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
                }, lease, ct, tracker, packId);
            List<long> newSizes;
            try
            {
                newSizes = staged.Files.Select(f => new FileInfo(f).Length).ToList(); // grab the sizes before releasing
                var packShare = staged.Files.Count > 0
                    ? members.Values.Sum(m => m.Length) / staged.Files.Count
                    : 0;
                tracker?.BeginUpload(packBlobRef, staged.Files.Count);
                try
                {
                    await VolumeBlobIO.ReplaceAsync(
                        uploader, account, cc, packBlobRef, staged.Files, dataTier, retry: null, ct,
                        scope: uploadScope, onVolumeUploaded: f =>
                        {
                            staging.ReleaseFile(f);
                            workProgress?.Invoke(packShare); // approximate per-volume share of the recorded source bytes
                        }, label: packId, beforeVolume: pauseGate);
                    onUploaded?.Invoke(newSizes.Sum()); // sizes were grabbed before the per-volume release deleted the files
                    tracker?.ConfirmUpload(packBlobRef);
                }
                finally
                {
                    tracker?.EndUpload(packBlobRef);
                }
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
                await ClearUnrecoverableAsync(catalog, patches, vnum, path, ct);
            repaired.AddRange(available.SelectMany(en => members[en].Refs.Select(r => r.Path)));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Produce the replacement volumes for a single-file blob while hashing the source read, and — only when
    /// the streamed hash proves the source still is the recorded content — upload them over the damaged
    /// family. Returns the new volume sizes; null = the source turned out changed or unreadable, nothing was
    /// uploaded and the produced volumes were discarded, so the caller can try the next referencing path.
    /// <para>
    /// The verdict is exactly the hash gate's — full xxh128 over the actual bytes against the recorded
    /// fullHash, plus the length — it just rides the production read, the same trick the backup path uses
    /// (the hash falls out of the compression read for free, see BackupOrchestrator.CompressStreamingAsync).
    /// Streaming production also matches the backup's streaming output byte for byte (-si, -mtm=off), so a
    /// retry of an interrupted repair can label-skip volumes this attempt already uploaded.
    /// </para>
    /// Through StagingArea like every producer: the global compression lock, the byte budget and the
    /// per-volume release all apply; the original password is passed through, or objects of an encrypted
    /// backup would be silently rewritten as plaintext 7z.
    /// </summary>
    private async Task<IReadOnlyList<long>?> ReplaceFromVerifiedStreamAsync(
        Account account, BlobContainerClient cc, string blobRef, string localSource, string entryName,
        string expectedHash, long expectedLength, AccessTier dataTier, long? volumeBytes, string? password,
        IReadOnlyDictionary<string, string> metadata, bool storeOnly,
        StagingArea.StagingLease lease, CancellationToken ct, StageTracker? tracker,
        VolumeUploadScope? uploadScope = null, Func<CancellationToken, Task>? pauseGate = null,
        Action<long>? workProgress = null, Action<long>? onUploaded = null)
    {
        // Segments 0/0: only the full hash and the length carry the verdict — the head/tail collision metadata
        // is reused from the index entry (see the metaEntry note in RepairBlobAsync), never recomputed here.
        var streaming = new StreamingHasher(0, 0);
        StagedItem staged;
        try
        {
            // Through StageAsync's own tracker registration — staging, the room wait, the archive lock and the
            // packing stretch surface exactly as the backup's do, instead of the hand-rolled subset that left
            // "waiting for staging room" invisible on the repair's in-flight line.
            staged = await staging.StageAsync(
                async (compressTemp, token) =>
                {
                    var result = await compressor.CompressStreamAsync(
                        new StreamCompressionRequest(entryName, Path.Combine(compressTemp, "b.7z"), password,
                            VolumeBytes: volumeBytes, StoreOnly: storeOnly, ExpectedBytes: expectedLength),
                        async (stdin, tk) =>
                        {
                            await using var source = FileHasher.OpenRead(localSource);
                            await using var sink = new HashingStream(streaming, stdin);
                            await StageTracker.CopyWithPackingProgressAsync(source, sink, tracker, tk);
                            return streaming.Length;
                        }, token);
                    return result.VolumeFiles;
                }, lease, ct, tracker, localSource, expectedLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable mid-production (locked, permissions revoked, media error): the hash gate's verdict for
            // the same event — this candidate cannot supply the content, and one bad file must not fail the
            // whole repair operation (see LocalMatchesAsync for the stakes).
            return null;
        }
        try
        {
            // The verdict, before anything touches the cloud: a source that changed since the backup — same
            // length, different bytes, the one change stat cannot see — must never be uploaded under the
            // recorded content's address, or every version referencing it silently serves the wrong bytes.
            if (streaming.FullHash != expectedHash || streaming.Length != expectedLength)
                return null;
            var sizes = staged.Files.Select(f => new FileInfo(f).Length).ToList();
            // Per-volume release (staging.ReleaseFile): each volume leaves the temp disk the moment it lands,
            // so the pool drains during the upload instead of holding the whole family hostage to the end —
            // the wrap-up Release below then finds most of it already gone (ReleaseFile is idempotent).
            // Each landed volume also books its share of the source workload, which is what keeps the byte
            // percentage and the remaining-time estimate moving through a 100 GB object.
            var share = staged.Files.Count > 0 ? expectedLength / staged.Files.Count : 0;
            // The family ledger bracket, exactly as the backup's UploadStagedBlobAsync writes it: landed
            // volumes ride the unfinished ledger until the object's write-off folds them into uploaded.
            tracker?.BeginUpload(blobRef, staged.Files.Count);
            try
            {
                await VolumeBlobIO.ReplaceAsync(
                    uploader, account, cc, blobRef, staged.Files, dataTier, retry: null, ct, metadata,
                    uploadScope, onVolumeUploaded: f =>
                    {
                        staging.ReleaseFile(f);
                        workProgress?.Invoke(share);
                    }, label: localSource, beforeVolume: pauseGate);
                onUploaded?.Invoke(sizes.Sum());
                tracker?.ConfirmUpload(blobRef);
            }
            finally
            {
                tracker?.EndUpload(blobRef);
            }
            return sizes;
        }
        finally
        {
            staging.Release(staged);
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
            tracker?.BeginItem(local, local, expectedLength, wire: false); // a local read, not a transfer
            try
            {
                // FullHashAsync reports increments, not cumulative values — the increments adapter is the
                // difference between a moving byte count and the field's "stuck at 80.0 KB at 0 B/s".
                var progress = tracker?.ItemProgressFromIncrements(local);
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

    /// <summary>A finding's ref names the cloud object ("packs/{id}.7z" or the data blob name); index entries
    /// store the pack's bare id. One conversion, shared by the pre-mark and deferred-mark passes.</summary>
    private static string BareRefOf(string refName) =>
        refName.StartsWith("packs/", StringComparison.Ordinal)
            ? refName["packs/".Length..^".7z".Length]
            : refName;

    /// <summary>The entries of every RETAINED version that reference one storage ref, materialized. Bounded by how
    /// many entries point at a single object (at most one per path per retained version), which is why this one
    /// query is allowed to be a list where a version's entries as a whole never are.
    /// <para><paramref name="versions"/> is what the info file lists: the catalog may still hold others, and they
    /// are none of a repair's business — see the filter's rationale in <see cref="RepairAsync"/>.</para></summary>
    private static async Task<List<(int Version, IndexEntry Entry)>> ReferencesAsync(
        VersionCatalog catalog, string storageRef, IReadOnlySet<int> versions, CancellationToken ct)
    {
        var refs = new List<(int Version, IndexEntry Entry)>();
        await foreach (var reference in catalog.EntriesReferencingAsync(storageRef, ct))
            if (versions.Contains(reference.Version))
                refs.Add(reference);
        return refs;
    }

    /// <summary>Whether (version, path) stands marked right now: what this run has already decided, and failing
    /// that what the catalog holds. Both halves are needed — the run's own pending marks are not in the catalog
    /// until they have been through the cloud, and the catalog's are not in the patch set at all.</summary>
    private static async Task<bool> IsMarkedAsync(
        VersionCatalog catalog, RepairPatchSet patches, int vnum, string path, CancellationToken ct) =>
        patches.IsMarkedPending(vnum, path) ?? await catalog.IsUnrecoverableAsync(vnum, path, ct);

    /// <summary>Rules a path unrecoverable in one version, and reports it. Already-marked paths raise no patch: the
    /// mark is already the truth there, and raising one would put that version in the changed set and rewrite an
    /// index that has nothing new to say. Reported either way — the report answers "which paths are unrecoverable
    /// after this run", not "which marks this run happened to write".</summary>
    private static async Task MarkUnrecoverableAsync(
        VersionCatalog catalog, RepairPatchSet patches, int vnum, string path, List<string> unrecoverable, CancellationToken ct)
    {
        if (!await IsMarkedAsync(catalog, patches, vnum, path, ct))
            patches.Mark(vnum, path);
        unrecoverable.Add(path);
    }

    /// <summary>The inverse of <see cref="MarkUnrecoverableAsync"/>: a path repaired in this run sheds the verdict a
    /// previous run recorded. The mark is a verdict, and a verdict overturned must come off the record — left in
    /// place, it outlives the damage, and restore keeps routing the healed file through version substitution as
    /// if it were still lost. A version that never held the mark is left alone, so it is not rewritten for nothing.</summary>
    private static async Task ClearUnrecoverableAsync(
        VersionCatalog catalog, RepairPatchSet patches, int vnum, string path, CancellationToken ct)
    {
        if (await IsMarkedAsync(catalog, patches, vnum, path, ct))
            patches.Clear(vnum, path);
    }

    private async Task Record(NotificationEvents evt, string source, string title, string body, CancellationToken ct)
    {
        if (opLog is not null)
            await opLog.AppendAsync(EventLog.LevelOf(evt), source, $"{title} — {body}", ct, durable: true);
        if (notifier is not null)
            await notifier.NotifyAsync(evt, title, body, ct);
    }
}
