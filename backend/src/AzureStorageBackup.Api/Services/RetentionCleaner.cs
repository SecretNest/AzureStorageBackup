using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using Microsoft.Extensions.Logging;

namespace AzureStorageBackup.Api.Services;

/// <summary>Cleanup options: the retention policy plus the data tier / volume size / threshold that dead-weight compaction needs.</summary>
public sealed record CleanupOptions
{
    public required RetentionPolicy Retention { get; init; }
    public AccessTier DataTier { get; init; } = AccessTier.Hot;
    public long? VolumeBytes { get; init; }

    /// <summary>Dead-weight compaction threshold (30% by default, M4 §6).</summary>
    public double DeadWeightThreshold { get; init; } = 0.30;

    /// <summary>Local source root: when repacking for dead weight, members are filled in from local files first.</summary>
    public string? LocalRoot { get; init; }

    /// <summary>Whether downloading the cloud pack is allowed to fill in members that are missing locally (a per-data-tier switch; false by default for Archive).</summary>
    public bool AllowRepackDownload { get; init; } = true;

    /// <summary>The global upload memory limit, handed to dead-weight compaction as the task's own budget
    /// (<see cref="UploadMemoryBudget"/>). 0 = never hold a volume in memory.</summary>
    public long UploadMemoryLimitBytes { get; init; } = 1024L * 1024 * 1024;
}

/// <summary>
/// What a retention cleanup actually deleted. Cleanup used to do its work in silence: how many versions were retired and how much space was freed, nobody knew once it was over.
/// <para>
/// Packs and data blobs are counted separately, because they are two different storage shapes (a crate of small
/// files vs. a single large file), and merging them into one number hides which side is churning. Both are counted
/// by **deduplicated base name**: a split pack is several objects in the container,
/// <c>packs/{id}.7z.001…NNN</c>, and reporting object counts would turn one pack into dozens.
/// </para>
/// </summary>
public sealed record CleanupReport(int RetiredVersions, int DeletedPacks, int DeletedBlobs, long FreedBytes)
{
    public static readonly CleanupReport Empty = new(0, 0, 0, 0);

    public bool IsEmpty => RetiredVersions == 0 && DeletedPacks == 0 && DeletedBlobs == 0 && FreedBytes == 0;
}

/// <summary>
/// Version retention cleanup (M4 §10): retires expired versions and deletes their second-level indexes plus every
/// data blob/pack no longer referenced by a live version; then compacts in place the still-live packs whose dead
/// weight exceeds the threshold (§6, via <see cref="DeadWeightCompactor"/>).
/// Shared by the orchestrator when a backup finishes and by the scheduler's Cleanup job.
/// </summary>
public sealed class RetentionCleaner(
    IBlobClientFactory factory, IBackupInfoStore store, RetentionEvaluator retention,
    DeadWeightCompactor? compactor = null, IVersionCatalogs? catalogs = null, TrackedInfoStore? trackedInfo = null,
    BackupJournalStore? journals = null, BackupBusyTracker? busy = null, ILogger<RetentionCleaner>? logger = null)
{
    /// <summary>Standalone cleanup: reads the info file itself (preferring the locally authoritative copy).</summary>
    public async Task<CleanupReport> CleanupAsync(
        Account account, string container, string? password, CleanupOptions options, CancellationToken ct = default,
        StagingArea.StagingLease? lease = null, bool sweepOrphans = false)
    {
        // A running restore resolved its blob set from the version it is downloading; retiring versions and
        // deleting "unreferenced" objects mid-download 404s it, and the compaction along the way rewrites
        // pack volumes it may be streaming. The cleanup is periodic — a skipped round costs nothing and the
        // next one collects, so with readers active it does not even begin. (Null busy = the direct-construction
        // test path, which stages no concurrent restores; production DI always passes the tracker.)
        if (busy?.HasReaders(account.Id, container) == true)
            return CleanupReport.Empty;

        var info = trackedInfo is not null
            ? await trackedInfo.LoadAsync(account, container, password, ct)
            : await store.ReadInfoAsync(account, container, password, ct);
        // A container that has never committed a version (the first backup was cancelled: blocks already in data/,
        // but not a single version index yet) returns right here, **even when the caller asked for a sweep**. Half of
        // the criterion is "what the retained versions reference", and that half simply cannot be read at this
        // moment: when info is null there is not even an info file, and when Versions is empty there is no way to
        // prove the listed blocks are orphans — deleting all of data/ as orphans deletes something the user really has.
        // These blocks have two other legitimate fates, neither of which comes through here: while the journal is
        // still there it protects them (the criterion honours the journal); and the "config deleted and recreated"
        // branch is caught by **the first run always sweeps** (see BackupRunControl.OpenJournalAsync), which takes
        // the overload below — no gate like this one, and it runs after this round's version is committed, so both halves of the criterion are present.
        return info is not null && info.Versions.Count > 0
            ? await CleanupAsync(account, container, password, options, info, ct, lease, sweepOrphans)
            : CleanupReport.Empty;
    }

    /// <summary>Cleanup when the info file is already in hand (called by the orchestrator after a backup finishes).</summary>
    /// <param name="lease">
    /// The caller's staging seat, passed straight through to dead-weight compaction. When a backup cleans up as it
    /// wraps up it must pass **its own** seat — taking another one inflates the denominator of the even split and shrinks the quota computed for the other backups running in parallel.
    /// </param>
    public async Task<CleanupReport> CleanupAsync(
        Account account, string container, string? password, CleanupOptions options,
        BackupInfoFile info, CancellationToken ct = default, StagingArea.StagingLease? lease = null,
        bool sweepOrphans = false)
    {
        var toDelete = retention.VersionsToDelete(
            info.Versions.Select(v => new VersionRef(v.Version, v.CreatedAt)).ToList(),
            options.Retention, DateTimeOffset.UtcNow);
        // This used to read "no version retired → return immediately". Cancellation and suspension broke that
        // premise: the container can be left with complete blocks that are "already in the cloud, not yet in the
        // index", and in that situation not a single version retires.
        // But we cannot sweep unconditionally either — an orphan sweep lists both the data/ and packs/ prefixes in
        // full, which on a container of hundreds of thousands of objects is not free work, and the vast majority of backups have no orphans at all.
        if (toDelete.Count == 0 && !sweepOrphans)
            return CleanupReport.Empty;

        // The overload above stands down for readers before it even loads the info file, and its two callers
        // (the scheduled Cleanup, the orphan sweeper) hold "CleaningUp" before that check, so no reader can
        // slip in behind it. THIS overload's caller is the backup's own wrap-up tail, and it arrives holding
        // "BackingUp" — a label readers rightly coexist with, which means a bare HasReaders check here would
        // be a check-then-act: a restore could register right after it and still meet the deletes. So the
        // stretch swaps the label to "CleaningUp" atomically with the reader check (and puts it back when
        // done): an active reader skips the round entirely — the next cleanup collects — and no new reader
        // is admitted while versions retire and packs compact. (Null busy = the direct-construction test
        // path, which stages no concurrent restores; production DI always passes the tracker.)
        string? priorActivity = null;
        if (busy is not null && !busy.TryBeginRewrite(account.Id, container, out priorActivity))
            return CleanupReport.Empty;
        try
        {
            return await CleanupLockedAsync(account, container, password, options, info, toDelete, ct, lease, sweepOrphans);
        }
        finally
        {
            busy?.EndRewrite(account.Id, container, priorActivity);
        }
    }

    /// <summary>The destructive body of the overload above, entered only through its rewrite gate.</summary>
    private async Task<CleanupReport> CleanupLockedAsync(
        Account account, string container, string? password, CleanupOptions options,
        BackupInfoFile info, IReadOnlyList<int> toDelete, CancellationToken ct,
        StagingArea.StagingLease? lease, bool sweepOrphans)
    {
        var container_ = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var deleted = new HashSet<int>(toDelete);

        var identity = info.Backup.CreatedAt.UtcTicks;
        long freedBytes = 0;

        // Asked before the first destructive step, not where the catalog is first used: a cleaner wired without one
        // cannot tell what the retained versions still reference, and the failure has to land before it has written
        // or deleted anything rather than half-way through. Null is legitimate for a container with no version at
        // all — the "first backup was cancelled, blocks but no index" sweep — which asks the catalog nothing.
        // Production DI always injects one.
        var catalogs_ = info.Versions.Count > 0
            ? catalogs ?? throw new InvalidOperationException(
                "RetentionCleaner was constructed without an IVersionCatalogs, so it cannot tell what the retained " +
                "versions still reference — and deleting on that basis would delete live data. Production DI always " +
                "provides one; a test that cleans a container holding versions must pass one too.")
            : null;

        // Retirement commits BEFORE it deletes — the same "upload first, delete after" discipline every other
        // destructive path in this codebase follows, and the one place that had it backwards. The old order
        // deleted a retired version's index volumes first and wrote the info file last; a cancellation or crash
        // in between (cleanup runs on every backup's tail, and the orchestrator deliberately swallows a Stop
        // there as a harmless skipped cleanup) left the info file listing versions whose manifests were already
        // gone — restore and check of those versions fail with a missing blob, discovered only when someone
        // needs them. Committed first, a failure merely leaves the retired index behind as an unreferenced blob
        // for a later sweep; the info file never lies. The second info write further down (pack pruning +
        // compaction results) is untouched: one extra conditional write per retiring cleanup is the price of the
        // crash window closing, and retirement is rare.
        var retired = info.Versions.Where(v => deleted.Contains(v.Version)).ToList();
        info.Versions.RemoveAll(v => deleted.Contains(v.Version));
        if (retired.Count > 0)
        {
            if (trackedInfo is not null)
                await trackedInfo.WriteAsync(account, container, info, password, tier: null, ct);
            else
                await store.WriteInfoAsync(account, container, info, password, tier: null, ct);
        }

        // Everything below reads the criterion out of the catalog, so the catalog first has to know every version the
        // info file listed on the way in — the retiring ones included, since what they alone reference is exactly
        // what may go. Migration is lazy, so a container nobody has cleaned since the upgrade pays for its indexes
        // here, once, and afterwards this is three SQL queries against a file.
        //
        // Deliberately **after** the retirement commit above: that commit is this method's first destructive step and
        // has to stay first (see the block comment on it), and nothing between the two touches an index blob the
        // migration below still needs to read.
        var candidates = CleanupCandidates.NoVersions;
        if (catalogs_ is not null)
        {
            // The retained versions are mandatory. Their refs are the whole of what protects live content, so a
            // version whose index cannot be migrated leaves the criterion unable to tell "in use" from "orphan" —
            // and deleting on a half-known set is data loss. A failure here stops the round instead.
            await catalogs_.EnsureVersionsAsync(account, container, info.Versions, identity, password, ct: ct);

            // The retiring ones are best-effort, and can be: their only contribution is **naming** what may go, and
            // a retired version that never reaches the catalog simply leaves the refs it alone held to the orphan
            // half of the criterion below — which is precisely what the old code, which never read a retired index
            // at all, did with them. What this buys is that one unreadable index blob on a version that is retiring
            // anyway cannot block every future cleanup of the container from freeing anything.
            foreach (var v in retired)
            {
                try
                {
                    await catalogs_.EnsureVersionAsync(account, container, v, identity, password, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning(ex,
                        "Retention could not bring retiring version {Version} of container {Container} into the catalog ({Message}); what it alone referenced will be collected as orphans instead.",
                        v.Version, container, ex.Message);
                }
            }

            // Reconcile the catalog against the info file, which is the authority on which versions exist. Anything
            // in the catalog that the info file does not list was retired by an earlier round that died between its
            // cloud deletes and the removal further down — and its rows are actively harmful, because
            // `Referenced` below is read from the whole catalog: those stale refs would protect, for good, exactly
            // the blobs that failed round left behind (the next round retires nothing, so it never reaches the
            // removal that would have cleared them). The old code could not have this problem — it rebuilt the
            // referenced set from `info.Versions` on every pass — so this is what keeps the two equivalent.
            //
            // This round's own retirees are excluded: they must stay until the cloud deletes are done (cloud first),
            // and they leave through the removal below.
            //
            // The same call a run makes at its start, for the same reason — one implementation of "the info file
            // says which versions exist; make the catalog agree" (see IVersionCatalogs.ReconcileAsync).
            var known = new HashSet<int>(info.Versions.Select(v => v.Version));
            known.UnionWith(retired.Select(v => v.Version));
            await catalogs_.ReconcileAsync(account.Id, container, known, ct);

            if (await TryOpenAsync(catalogs_, account.Id, container, ct) is { } catalog)
            {
                await using (catalog)
                    candidates = await CandidatesAsync(catalog, toDelete, ct);
            }
        }

        // Now the second-level indexes of the retired versions. Best-effort per volume:
        // everything here is already unreferenced by the committed info file, so a failed delete is an orphan
        // for a later sweep, never a lie — and one stubborn blob (a stray lease, a transient 5xx past the
        // retries) must not abort the rest of the cleanup.
        foreach (var v in retired)
        {
            // Every volume, not just the base name: deleting only the first one of a split index would leave the
            // rest behind as objects nothing references — invisible to the retention report, and reclaimed only if
            // somebody later runs a sweep.
            foreach (var n in VolumeBlobIO.VolumeNames(v.IndexBlob, v.IndexVolumes))
            {
                var indexBlob = container_.GetBlobClient(n);
                try
                {
                    // Ask for the size once before deleting. An index is not a negligibly small thing — a version index of a
                    // few hundred thousand entries can be tens of MB compressed, and missing it makes "how much space was
                    // freed" noticeably too low. One HEAD per retired version, and retired versions are usually a single
                    // digit, so the cost is negligible.
                    var indexBytes = await TrySizeOfAsync(indexBlob, ct);
                    if ((await WithRetryAsync(t => indexBlob.DeleteIfExistsAsync(cancellationToken: t), ct)).Value)
                        freedBytes += indexBytes;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Unreferenced since the commit above; a later cleanup's delete attempt (or an orphan sweep)
                    // reclaims it. Nothing to record: the committed info file is already telling the truth.
                    _ = ex;
                }
            }
        }

        // The other half of the criterion: content referenced by an active journal. It exists in the cloud but not yet
        // in any index, and only the journal records that it exists — deleting it wastes the next resume, and the user who clicks Resume finds it all has to be uploaded again from scratch.
        var active = journals is not null
            ? await journals.LoadActiveRefsAsync(account.Id, container, ct)
            : ActiveJournalRefs.Empty;

        // Delete packs no longer referenced by any retained version (including volumes packs/{id}.7z.NNN, and orphan packs too). Enumerate the packs/ prefix grouped by packId,
        // so deleting only the base name does not leave volumes behind (§7); the criterion is "not referenced by a retained version", symmetric with the data blob side.
        // Counting deduplicates by base name (a split pack/blob is several objects in the container), while freed bytes are accumulated object by object.
        var deletedPacks = new HashSet<string>(StringComparer.Ordinal);
        var deletedBlobs = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var blob in container_.GetBlobsAsync(BlobTraits.None, BlobStates.None, "packs/", ct))
        {
            var packId = PackIdOf(blob.Name);
            if (!candidates.IsDeletablePack(packId) || active.Packs.Contains(packId))
                continue;
            if ((await WithRetryAsync(t => container_.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: t), ct)).Value)
            {
                deletedPacks.Add(packId);
                freedBytes += blob.Properties.ContentLength ?? 0;
            }
        }
        var prunedFromInfo = info.Packs.Keys
            .Where(id => candidates.IsDeletablePack(id) && !active.Packs.Contains(id)).ToList();
        foreach (var packId in prunedFromInfo)
            info.Packs.Remove(packId);

        // Delete data blobs that are no longer referenced (enumerating the data/ prefix). Volume names data/{hash}.NNN are normalized back to the base name before comparing,
        // so a still-referenced volume is not deleted by mistake (§7; otherwise data loss).
        await foreach (var blob in container_.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", ct))
        {
            var baseRef = BaseRef(blob.Name);
            if (!candidates.IsDeletableBlob(baseRef) || active.Blobs.Contains(baseRef))
                continue;
            if ((await WithRetryAsync(t => container_.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: t), ct)).Value)
            {
                deletedBlobs.Add(baseRef);
                freedBytes += blob.Properties.ContentLength ?? 0;
            }
        }

        // The cloud is done with the retired versions, so now — and not a moment earlier — they leave the catalog:
        // the same "delete in the cloud first, forget locally after" discipline the rest of this file follows, and
        // the order that keeps the candidate set computable while the deletes are still running.
        //
        // A round that dies between the deletes above and this line leaves the catalog remembering versions the info
        // file (committed long before, at the top) no longer lists. That state is **not** self-healing on its own:
        // the next round retires nothing, so it never reaches this line, and those rows would go on protecting the
        // very objects the failed round did not manage to delete. What repairs it is the reconcile before the
        // candidates are computed — it drops every catalog version the info file does not list, which is exactly
        // this leftover — so the next cleanup starts from a catalog that agrees with the info file again.
        foreach (var v in retired)
            await catalogs_!.RemoveVersionAsync(account.Id, container, v.Version, ct);

        // Dead-weight compaction (§6): recompress in place the still-live packs whose dead weight exceeds the
        // threshold. Checked **only when a version really retired** — dead weight is piled up by "a member is no
        // longer referenced by any retained version", and only retirement makes it grow.
        //
        // An orphan sweep on its own (sweepOrphans true, yet not a single version retired) must never drag it along:
        // when compaction fails or gives up, DeadWeightCompactor just writes the same DeadBytes back (see its catch
        // and the "member missing locally" branch), so the next round's judgement comes out identical. Hung off a
        // nightly scheduled cleanup, that means the same packs get downloaded, recompressed and re-uploaded every
        // night, forever — whereas before this, it happened once after one retirement.
        //
        // Its input is read here rather than above because it has to be "what survives": the retired versions left
        // the catalog a few lines ago, so streaming the live members now needs no filter — and the null-forgiveness
        // on the catalogs is sound for the same reason the gate is, since a retirement is what got us here.
        if (compactor is not null && toDelete.Count > 0)
        {
            // The handle is closed again before compaction starts: compaction downloads, recompresses and re-uploads
            // whole packs, which can run for minutes, and holding a SQLite connection open across all of it for a
            // dictionary that was already read would block the container's next writer for no reason.
            Dictionary<string, Dictionary<string, LivePackMember>> liveByPack;
            await using (var catalog = await catalogs_!.OpenAsync(account.Id, container, readOnly: true, ct))
                liveByPack = await LiveByPackAsync(catalog, ct);

            await compactor.CompactAsync(
                account, container_, password, info, liveByPack,
                options.DataTier, options.VolumeBytes, options.DeadWeightThreshold,
                options.LocalRoot, options.AllowRepackDownload, ct, lease,
                uploadMemoryLimitBytes: options.UploadMemoryLimitBytes);
        }

        // The info file is rewritten only when its content really changed. There are only two ways it can change:
        // retirement removed versions, or the orphan sweep dropped packs out of info.Packs (compaction also edits
        // info.Packs, but it only runs when something retired, which the first case already covers).
        //
        // **That last clause is part of this criterion, not an aside**: compaction rewrites
        // Members / OriginalBytes / DeadBytes / VolumeSizes inside info.Packs, and not one word of that enters the
        // criterion here. The only reason nothing is missed today is the `toDelete.Count > 0` on line :200 —
        // compaction can only run when something retired, so the first case is necessarily true. Anyone who loosens
        // the gate at :200 (say "let the orphan sweep compact along the way", or "run compaction as its own
        // scheduled job") must change this at the same time, or compaction's results **live only in memory**: the
        // pack in the cloud has already been rewritten into a smaller crate while the info file still records the old
        // member list and OriginalBytes. The next cleanup recomputes dead weight from that, arrives at the same
        // over-threshold ratio, and so the same packs get recompressed every single time; worse, the recorded member
        // list no longer matches what is actually in the archive, so restore cannot extract the file and check reports it missing.
        // The criterion must not be written as "rewrite whenever a cloud object was deleted" — a deleted orphan was
        // never in info to begin with, so writing it that way means paying for a pointless info-file write on every sweep, and the write on this path is a conditional write with If-Match: one pointless write burns one ETag for nothing.
        if (toDelete.Count > 0 || prunedFromInfo.Count > 0)
        {
            if (trackedInfo is not null)
                await trackedInfo.WriteAsync(account, container, info, password, tier: null, ct);
            else
                await store.WriteInfoAsync(account, container, info, password, tier: null, ct);
        }

        // Dead-weight compaction **rewrites** a pack more tightly, it does not delete, so it is not counted here —
        // reporting it as "deleted N packs" would make the operator think data had been retired.
        return new CleanupReport(toDelete.Count, deletedPacks.Count, deletedBlobs.Count, freedBytes);
    }

    /// <summary>
    /// What this round is allowed to delete from the container, as three sets read out of the catalog instead of the
    /// two that used to be accumulated by walking every retained version's index.
    /// <para>
    /// The old criterion — "delete what no retained version references" — quietly answered two questions at once, and
    /// both are still needed: <see cref="RetiredOnlyBlobs"/>/<see cref="RetiredOnlyPacks"/> are what the retiring
    /// versions alone referenced, and <see cref="Referenced"/> (every ref any version in the catalog names, of either
    /// kind) is what tells a genuine orphan — in the container, in nobody's index — from content in use. Asking for
    /// the first separately is what lets the cloud deletes run <em>before</em> the retired versions leave the
    /// catalog, which is the order the rest of this codebase deletes in.
    /// </para>
    /// </summary>
    internal sealed record CleanupCandidates(
        IReadOnlySet<string> RetiredOnlyBlobs, IReadOnlySet<string> RetiredOnlyPacks, IReadOnlySet<string> Referenced)
    {
        /// <summary>A container with no version in the info file at all: nothing is referenced, so every object in it
        /// is an orphan — exactly what the old loop concluded from an empty <c>referencedBlobs</c>.</summary>
        internal static readonly CleanupCandidates NoVersions = new(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        internal bool IsDeletableBlob(string baseRef) => RetiredOnlyBlobs.Contains(baseRef) || !Referenced.Contains(baseRef);

        internal bool IsDeletablePack(string packId) => RetiredOnlyPacks.Contains(packId) || !Referenced.Contains(packId);
    }

    /// <summary>
    /// Opens the container's catalog read-only, or returns null when it does not have one yet. A missing file is not
    /// an error here: it means no version has ever been imported, which for a cleanup is the same answer as "nothing
    /// is referenced". Only reachable when every version the info file listed is retiring and not one of their
    /// indexes could be read, since migrating a retained version creates the file.
    /// </summary>
    private static async Task<VersionCatalog?> TryOpenAsync(
        IVersionCatalogs catalogs, int accountId, string container, CancellationToken ct)
    {
        try
        {
            return await catalogs.OpenAsync(accountId, container, readOnly: true, ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The deletion criterion, read out of the catalog in three queries — no index is deserialized and nothing but
    /// the ref strings is held in memory.
    /// <para>
    /// <see cref="VersionCatalog.RefsOnlyInAsync"/> is asked per storage kind because the two sides of the container
    /// are compared separately (a pack id and a <c>data/</c> ref are different namespaces, and a pack reference has
    /// never protected a data blob of the same name). <see cref="VersionCatalog.DistinctRefsAsync"/> deliberately is
    /// not: for the orphan half "referenced anywhere, as anything" is the safe direction to err in, and the two
    /// namespaces cannot collide anyway.
    /// </para>
    /// <para>Called with the catalog still holding the retiring versions — that is what makes the difference
    /// computable at all.</para>
    /// </summary>
    internal static async Task<CleanupCandidates> CandidatesAsync(
        VersionCatalog catalog, IReadOnlyCollection<int> retired, CancellationToken ct)
    {
        var blobs = new HashSet<string>(await catalog.RefsOnlyInAsync(retired, "blob", ct), StringComparer.Ordinal);
        var packs = new HashSet<string>(await catalog.RefsOnlyInAsync(retired, "pack", ct), StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        // Kind-agnostic on purpose (see the remarks above): the ref alone is what the candidate sets are compared
        // against. The kind and volume count the query also carries are the orphan sweep's business, not this one's
        // — a ref recorded under two different volume counts arrives twice and lands in the same set entry.
        await foreach (var (_, storageRef, _) in catalog.DistinctRefsAsync(ct))
            referenced.Add(storageRef);
        return new CleanupCandidates(blobs, packs, referenced);
    }

    /// <summary>
    /// The still-live members of every pack, in the nested shape <see cref="DeadWeightCompactor"/> takes: pack id →
    /// member name → member. Streamed straight out of the catalog, which already resolves "the newest version's copy
    /// of a member wins" in its ORDER BY, where the old loop got the same result by letting later versions overwrite
    /// earlier ones in the dictionary.
    /// <para>Called with only the retained versions left in the catalog: dead weight is measured against what
    /// survives, so a retired version's members must already be gone.</para>
    /// </summary>
    internal static async Task<Dictionary<string, Dictionary<string, LivePackMember>>> LiveByPackAsync(
        VersionCatalog catalog, CancellationToken ct)
    {
        var liveByPack = new Dictionary<string, Dictionary<string, LivePackMember>>(StringComparer.Ordinal);
        await foreach (var (packId, entryName, length, fullHash) in catalog.LivePackMembersAsync(ct))
        {
            // Grouped by entry name (unique within a pack): identical content at different paths dedups to the same
            // fullHash but is still two members, so hash cannot be the key.
            var members = liveByPack.TryGetValue(packId, out var m)
                ? m
                : liveByPack[packId] = new Dictionary<string, LivePackMember>(StringComparer.Ordinal);
            members[entryName] = new LivePackMember(entryName, length, fullHash);
        }

        return liveByPack;
    }

    /// <summary>
    /// Runs one cloud operation under the same retry policy the upload path uses.
    /// <para>
    /// Cleanup used to call the cloud bare, with nothing but the SDK's own handful of attempts underneath it. That is
    /// how a three-day backup ended on "Retry failed after 6 tries" — six network timeouts inside one
    /// AggregateException, which <see cref="TransientErrors.IsTransient"/> does recognise, but which nothing here
    /// ever handed to a retry. The upload path has ridden out exactly this shape of blip all along, by backing off
    /// exponentially for as long as two hours.
    /// </para>
    /// <para>
    /// Only point operations are wrapped, not the enumerations around them: a delete is idempotent, so a retry is
    /// free, whereas restarting a listing of a container with hundreds of thousands of objects to recover one bad
    /// page would cost more than it saves — and a listing that really cannot finish is now survivable anyway, since
    /// a failed cleanup no longer condemns the backup that already committed.
    /// </para>
    /// </summary>
    private static Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> op, CancellationToken ct)
        => RetryPolicy.ExecuteAsync(op, options: null, ex => TransientErrors.IsTransient(ex, ct), ct);

    /// <summary>Ask for the size once before deleting. When the blob is already gone (concurrent cleanup, a previous round that died half-way) it counts as 0 rather than aborting the cleanup.</summary>
    private static async Task<long> TrySizeOfAsync(BlobClient blob, CancellationToken ct)
    {
        try
        {
            return (await WithRetryAsync(t => blob.GetPropertiesAsync(cancellationToken: t), ct)).Value.ContentLength;
        }
        catch (RequestFailedException)
        {
            return 0;
        }
    }

    /// <summary>Normalizes a volume name baseRef.NNN (numeric suffix of at least 3 digits) back to its base name; a non-volume name is returned unchanged (§7).
    /// Three digits is the uploader's padding (<c>{index:D3}</c> in VolumeBlobIO.VolumeName), not its width: .999 is followed by .1000.
    /// Requiring exactly three here left .1000 and later un-normalized, so the sweep deleted those volumes of a still-referenced blob as orphans —
    /// keeping .001–.999 while taking the last volume, the one holding the 7z end header, which turns the archive unopenable rather than partial.</summary>
    internal static string BaseRef(string blobName)
    {
        var dot = blobName.LastIndexOf('.');
        if (dot >= 0 && blobName.Length - dot - 1 >= 3)
        {
            foreach (var c in blobName.AsSpan(dot + 1))
                if (!char.IsAsciiDigit(c))
                    return blobName;
            return blobName[..dot];
        }
        return blobName;
    }

    /// <summary>Extracts the packId from a pack blob name (packs/{id}.7z or packs/{id}.7z.NNN).</summary>
    private static string PackIdOf(string blobName)
    {
        var rest = blobName["packs/".Length..];
        var cut = rest.IndexOf(".7z", StringComparison.Ordinal);
        return cut >= 0 ? rest[..cut] : rest;
    }
}
