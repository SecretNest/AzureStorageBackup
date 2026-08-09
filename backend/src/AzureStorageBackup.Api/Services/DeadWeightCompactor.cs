using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>A member inside a pack that is still referenced by a live version. Identified by entryName (the archive entry name, unique within a pack) —
/// identical content at different paths dedups to the same fullHash but is still **two** independent members, so fullHash cannot serve as identity or compaction would lose one of them.</summary>
public sealed record LivePackMember(string EntryName, long Length, string FullHash);

/// <summary>
/// Dead-weight compaction (M4 design §6): a member inside a pack becomes dead weight once it is deleted/changed and no live version references it any more.
/// When the dead-weight ratio (by original size) exceeds the threshold, that pack is **recompressed in place** — keeping the still-live members, discarding the dead ones, overwriting the blob of the same packId (and deleting the old volumes).
/// Because packs are referenced by packId+entryName and live members keep their entryName, no version index needs rewriting. Triggered only when a version retires (that is the only time dead weight grows).
///
/// Where member content comes from: **local files first** (those whose content matches, confirmed by hash); for members missing locally — if downloading is allowed (a per-data-tier switch)
/// the cloud pack is downloaded and extracted to fill them in, otherwise **repacking this pack is abandoned** (the dead weight is kept). When every member is available locally no download is needed at all (so Archive can be compacted too).
/// </summary>
/// <param name="staging">
/// Compaction and backup share the same physical temp disk, so its compression has to take the same global lock and
/// its temporary footprint has to count against the same budget. It used to have its own tempRoot and no constraints
/// at all: while a backup was held back by the staging cap, compaction could keep writing to the disk, with neither
/// side aware the other existed. tempRoot is still kept — the **input-side** intermediates from compose/extraction
/// still need somewhere to live, and they are accounted for via <see cref="StagingArea.ReserveAsync"/>.
/// </param>
public sealed class DeadWeightCompactor(
    IBlobUploader uploader, IFileCompressor compressor, IFileHasher hasher, string tempRoot,
    StagingArea staging, ILogger<DeadWeightCompactor>? logger = null)
{
    /// <param name="liveByPack">packId → (fullHash → still-live member), derived by the cleaner scanning the retained versions' indexes.</param>
    /// <param name="lease">
    /// The caller's staging seat. When a backup compacts as it wraps up it must pass **its own** seat — taking
    /// another one inflates the denominator and shrinks the quota computed for the other backups running in parallel. A cleanup job running on its own takes one for itself.
    /// </param>
    public async Task CompactAsync(
        Account account, BlobContainerClient container, string? password, BackupInfoFile info,
        IReadOnlyDictionary<string, Dictionary<string, LivePackMember>> liveByPack,
        AccessTier dataTier, long? volumeBytes, double threshold,
        string? localRoot, bool allowDownload, CancellationToken ct,
        StagingArea.StagingLease? lease = null)
    {
        foreach (var packId in info.Packs.Keys.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var packInfo = info.Packs[packId];
            var live = liveByPack.GetValueOrDefault(packId);
            var liveBytes = live?.Values.Sum(m => m.Length) ?? 0;
            var deadBytes = Math.Max(0, packInfo.OriginalBytes - liveBytes);
            var ratio = packInfo.OriginalBytes == 0 ? 0 : (double)deadBytes / packInfo.OriginalBytes;

            if (ratio <= threshold)
            {
                if (packInfo.DeadBytes != deadBytes)
                    info.Packs[packId] = packInfo with { DeadBytes = deadBytes };
                continue;
            }

            // Every member is dead weight → the whole pack is unreferenced, left to retention cleanup to delete, not handled here.
            if (live is null || live.Count == 0)
                continue;

            try
            {
                var newSizes = await RecompactAsync(
                    account, container, password, packId, live, localRoot, dataTier, allowDownload, volumeBytes,
                    packInfo.StoreOnly, lease, ct);
                if (newSizes.Count > 0)
                {
                    info.Packs[packId] = packInfo with
                    {
                        Members = [.. live.Values.Select(m => m.FullHash)],
                        OriginalBytes = liveBytes,
                        DeadBytes = 0,
                        Volumes = newSizes.Count,
                        VolumeSizes = [.. newSizes],
                    };
                    logger?.LogInformation(
                        "Compacted pack {Pack}: dropped {Dead} bytes of dead weight ({Ratio:P0})", packId, deadBytes, ratio);
                }
                else
                {
                    // Members missing locally and downloading not allowed → give up on repacking, only record the dead weight.
                    info.Packs[packId] = packInfo with { DeadBytes = deadBytes };
                    logger?.LogInformation(
                        "Dead-weight compaction skipped for pack {Pack}: missing members not available locally and download disabled for this tier",
                        packId);
                }
            }
            catch (Exception ex)
            {
                info.Packs[packId] = packInfo with { DeadBytes = deadBytes };
                logger?.LogWarning(ex, "Dead-weight compaction failed for pack {Pack}", packId);
            }
        }
    }

    /// <returns>Non-empty = recompressed (the sizes of the new volumes); empty = recompression abandoned (members
    /// missing locally with downloading not allowed, or a member name in the index escaping the compose directory).</returns>
    /// <param name="storeOnly">How this pack was compressed originally (<see cref="PackInfo.StoreOnly"/>). Compaction
    /// **rewrites in place** the archive of the same packId, and without carrying this along, a store-only pack would come
    /// out of compaction using the default compression — and compaction runs automatically after a version retires, so
    /// nobody would ever see that change. Nor do we re-evaluate the do-not-compress rules here: once the rules have changed,
    /// that would quietly switch an old pack's compression at its next compaction, whereas the value recorded on the pack is stable.</param>
    private async Task<IReadOnlyList<long>> RecompactAsync(
        Account account, BlobContainerClient container, string? password, string packId,
        Dictionary<string, LivePackMember> live, string? localRoot, AccessTier dataTier,
        bool allowDownload, long? volumeBytes, bool storeOnly, StagingArea.StagingLease? lease, CancellationToken ct)
    {
        var baseRef = $"packs/{packId}.7z";
        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        var composeDir = Path.Combine(work, "compose");

        // EntryName comes from the cloud index, which after /import is attacker-controlled (design §5). If it contains
        // `..` after ToLocal, or is absolute, every Path.Combine below lands outside the target directory:
        //   - LocalPath → an existence probe outside localRoot (no hash gate, a pure oracle);
        //   - CopyInto's dest → an **arbitrary write** outside composeDir, with content extracted out of the pack;
        //   - the fill-in branch's source → a read outside extractDir.
        // All three concatenate the same string, so they escape or not together, and one check at the entry settles it.
        // The remedy is **abandoning compaction for the whole pack**, not "skip that member": skipping would quietly
        // lose a member that is still referenced, whereas abandoning merely keeps the dead weight — returning [] takes
        // exactly the existing safe no-op path (member missing locally and downloading not allowed). Compaction is pure optimization anyway; better not done at all.
        if (live.Values.Any(m => !PathBoundary.IsWithin(composeDir, Path.Combine(composeDir, ToLocal(m.EntryName)))))
        {
            logger?.LogWarning(
                "Dead-weight compaction skipped for pack {Pack}: an index entry name escapes the compose directory",
                packId);
            return [];
        }

        // Optimization: first decide by mere existence whether any member is missing locally; if some are and downloading is not allowed, give up straight away (without hashing anything).
        var hasAbsentLocal = live.Values.Any(m => !File.Exists(LocalPath(localRoot, m.EntryName)));
        if (hasAbsentLocal && !allowDownload)
            return [];

        // The compose directory will hold the **original** content of every live member, and this disk is the same one backups stage on.
        // Reserve first, act second: without a reservation, one compaction can fill the disk while a backup is being held back by the staging cap.
        using var composeReservation = await staging.ReserveAsync(live.Values.Sum(m => m.Length), lease, ct);

        Directory.CreateDirectory(composeDir);
        try
        {
            // Local files whose content matches are used directly (confirmed by hash, even when length/time/permissions all agree); the rest have to be filled in from the cloud pack.
            var needFromPack = new List<string>();
            foreach (var member in live.Values)
            {
                var localPath = LocalPath(localRoot, member.EntryName);
                if (localPath is not null && File.Exists(localPath)
                    && await hasher.FullHashAsync(localPath, ct) == member.FullHash)
                    CopyInto(composeDir, member.EntryName, localPath);
                else
                    needFromPack.Add(member.EntryName);
            }

            IDisposable? downloadReservation = null;
            try
            {
                if (needFromPack.Count > 0)
                {
                    if (!allowDownload)
                        return [];

                    // The downloaded pack volumes (compressed) plus the members extracted from them all land in work.
                    // Reserve another block of the live members' total length: only the missing ones get extracted and the compressed volumes are smaller, so that figure covers it.
                    downloadReservation = await staging.ReserveAsync(live.Values.Sum(m => m.Length), lease, ct);

                    // Download and extract the old pack to pull out the members that are missing locally.
                    var extractDir = Path.Combine(work, "x");
                    var firstVolume = await VolumeBlobIO.DownloadAsync(container, baseRef, work, ct);
                    await compressor.ExtractAsync(firstVolume, extractDir, password, ct);
                    foreach (var entryName in needFromPack)
                        CopyInto(composeDir, entryName, Path.Combine(extractDir, ToLocal(entryName)));
                }

                // Recompress the still-live members into a new archive replacing the same packId: overwrite-upload the new volumes first, delete the residual old ones after (no more delete-first).
                // Via StagingArea: the compression therefore shares the same global lock as backups (no two sides
                // chewing CPU at once), its output counts against the same budget, and it keeps the per-volume release — delete each volume as it finishes uploading, so the peak is only the volumes not yet sent.
                var staged = await staging.StageAsync(
                    async (compressTemp, token) =>
                    {
                        var result = await compressor.CompressAsync(
                            new CompressionRequest(composeDir, [.. live.Values.Select(m => m.EntryName)],
                                Path.Combine(compressTemp, packId + ".7z"), password,
                                VolumeBytes: volumeBytes, StoreOnly: storeOnly), token);
                        return result.VolumeFiles;
                    }, lease, ct);
                try
                {
                    var sizes = staged.Files.Select(f => new FileInfo(f).Length).ToList(); // take the sizes before releasing
                    await VolumeBlobIO.ReplaceAsync(
                        uploader, account, container, baseRef, staged.Files, dataTier, retry: null, ct);
                    return sizes;
                }
                finally
                {
                    staging.Release(staged);
                }
            }
            finally
            {
                downloadReservation?.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string? LocalPath(string? localRoot, string entryName) =>
        localRoot is null ? null : Path.Combine(localRoot, ToLocal(entryName));

    private static void CopyInto(string composeDir, string entryName, string source)
    {
        var dest = Path.Combine(composeDir, ToLocal(entryName));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: true);
    }

    private static string ToLocal(string entryName) => entryName.Replace('/', Path.DirectorySeparatorChar);
}
