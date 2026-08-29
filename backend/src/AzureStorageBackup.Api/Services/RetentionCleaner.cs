using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

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
    DeadWeightCompactor? compactor = null, ILocalIndexCache? indexCache = null, TrackedInfoStore? trackedInfo = null,
    BackupJournalStore? journals = null)
{
    /// <summary>Standalone cleanup: reads the info file itself (preferring the locally authoritative copy).</summary>
    public async Task<CleanupReport> CleanupAsync(
        Account account, string container, string? password, CleanupOptions options, CancellationToken ct = default,
        StagingArea.StagingLease? lease = null, bool sweepOrphans = false)
    {
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

        var container_ = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        var deleted = new HashSet<int>(toDelete);

        var identity = info.Backup.CreatedAt.UtcTicks;
        long freedBytes = 0;

        // Delete the second-level index of each retired version (cloud + local cache) and remove it from the info file.
        foreach (var v in info.Versions.Where(v => deleted.Contains(v.Version)))
        {
            // Every volume, not just the base name: deleting only the first one of a split index would leave the
            // rest behind as objects nothing references — invisible to the retention report, and reclaimed only if
            // somebody later runs a sweep.
            foreach (var n in VolumeBlobIO.VolumeNames(v.IndexBlob, v.IndexVolumes))
            {
                var indexBlob = container_.GetBlobClient(n);
                // Ask for the size once before deleting. An index is not a negligibly small thing — a version index of a
                // few hundred thousand entries can be tens of MB compressed, and missing it makes "how much space was
                // freed" noticeably too low. One HEAD per retired version, and retired versions are usually a single
                // digit, so the cost is negligible.
                var indexBytes = await TrySizeOfAsync(indexBlob, ct);
                if ((await WithRetryAsync(t => indexBlob.DeleteIfExistsAsync(cancellationToken: t), ct)).Value)
                    freedBytes += indexBytes;
            }
            if (indexCache is not null)
                await indexCache.RemoveAsync(account.Id, container, v.Version, ct);
        }
        info.Versions.RemoveAll(v => deleted.Contains(v.Version));

        // Collect the data blobs and packs the remaining versions still reference, plus the still-live members of each pack (for dead-weight compaction).
        //
        // This section **reads one index per retained version**, and the orphan sweep cannot do without it: without
        // knowing what the retained versions reference there is no way to judge whether a listed object is an orphan.
        // But be honest about the cost — what is read is the locally authoritative copy (ILocalIndexCache, zero cloud
        // traffic), except SQLite stores serialized bytes, so even a hit has to rebuild the whole index back into
        // objects: measured in this repo, a 500,000-entry index costs about 0.9 s / 350 MB of allocation per read,
        // while the in-process LRU above it (VersionIndexMemoryCache) holds only 2 by default. So a large backup
        // keeping a dozen-odd versions pays a dozen-odd seconds of CPU plus several GB of transient allocation for one nightly "standalone cleanup" — that is the real price of sweeping for orphans every night.
        var referencedBlobs = new HashSet<string>(StringComparer.Ordinal);
        var referencedPacks = new HashSet<string>(StringComparer.Ordinal);
        var liveByPack = new Dictionary<string, Dictionary<string, LivePackMember>>(StringComparer.Ordinal);
        foreach (var v in info.Versions)
        {
            var vi = indexCache is not null
                ? await indexCache.ReadAsync(account, container, v.Version, identity, v.IndexBlob, password, v.IndexVolumes, ct)
                : await store.ReadIndexAsync(account, container, v.IndexBlob, password, v.IndexVolumes, ct);
            foreach (var e in vi.Entries)
            {
                if (e.Storage is null)
                    continue;
                if (e.Storage.Kind == "pack")
                {
                    referencedPacks.Add(e.Storage.Ref);
                    if (e.FullHash is not null)
                    {
                        var members = liveByPack.TryGetValue(e.Storage.Ref, out var m)
                            ? m
                            : liveByPack[e.Storage.Ref] = new Dictionary<string, LivePackMember>(StringComparer.Ordinal);
                        // Group by entryName (unique within a pack): identical content at different paths dedups to the same fullHash but is still two members, so hash cannot be the key.
                        var entryName = e.Storage.EntryName ?? e.Path;
                        members[entryName] = new LivePackMember(entryName, e.Length, e.FullHash);
                    }
                }
                else
                {
                    referencedBlobs.Add(e.Storage.Ref);
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
            if (referencedPacks.Contains(packId) || active.Packs.Contains(packId))
                continue;
            if ((await WithRetryAsync(t => container_.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: t), ct)).Value)
            {
                deletedPacks.Add(packId);
                freedBytes += blob.Properties.ContentLength ?? 0;
            }
        }
        var prunedFromInfo = info.Packs.Keys
            .Where(id => !referencedPacks.Contains(id) && !active.Packs.Contains(id)).ToList();
        foreach (var packId in prunedFromInfo)
            info.Packs.Remove(packId);

        // Delete data blobs that are no longer referenced (enumerating the data/ prefix). Volume names data/{hash}.NNN are normalized back to the base name before comparing,
        // so a still-referenced volume is not deleted by mistake (§7; otherwise data loss).
        await foreach (var blob in container_.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", ct))
        {
            var baseRef = BaseRef(blob.Name);
            if (referencedBlobs.Contains(baseRef) || active.Blobs.Contains(baseRef))
                continue;
            if ((await WithRetryAsync(t => container_.GetBlobClient(blob.Name).DeleteIfExistsAsync(cancellationToken: t), ct)).Value)
            {
                deletedBlobs.Add(baseRef);
                freedBytes += blob.Properties.ContentLength ?? 0;
            }
        }

        // Dead-weight compaction (§6): recompress in place the still-live packs whose dead weight exceeds the
        // threshold. Checked **only when a version really retired** — dead weight is piled up by "a member is no
        // longer referenced by any retained version", and only retirement makes it grow.
        //
        // An orphan sweep on its own (sweepOrphans true, yet not a single version retired) must never drag it along:
        // when compaction fails or gives up, DeadWeightCompactor just writes the same DeadBytes back (see its catch
        // and the "member missing locally" branch), so the next round's judgement comes out identical. Hung off a
        // nightly scheduled cleanup, that means the same packs get downloaded, recompressed and re-uploaded every
        // night, forever — whereas before this, it happened once after one retirement.
        if (compactor is not null && toDelete.Count > 0)
            await compactor.CompactAsync(
                account, container_, password, info, liveByPack,
                options.DataTier, options.VolumeBytes, options.DeadWeightThreshold,
                options.LocalRoot, options.AllowRepackDownload, ct, lease);

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
