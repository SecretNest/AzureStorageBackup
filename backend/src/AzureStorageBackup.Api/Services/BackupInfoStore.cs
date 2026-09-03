using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

public sealed class BackupInfoStore(IBlobClientFactory factory, IArchiveCodec codec) : IBackupInfoStore
{
    public async Task<BackupInfoFile?> ReadInfoAsync(
        Account account, string container, string? password, CancellationToken ct = default)
        => (await ReadInfoWithETagAsync(account, container, password, ct))?.Info;

    public async Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(
        Account account, string container, string? password, CancellationToken ct = default)
    {
        var cc = Container(account, container);

        // Prefer the unencrypted one (PRD 1.6). Unencrypted means compression only (empty password); encrypted uses the backup password.
        var plain = cc.GetBlobClient(BackupDiscovery.IndexBlobName);
        if ((await plain.ExistsAsync(ct)).Value)
            return await ReadWithETagAsync(plain, password: null, ct);

        var enc = cc.GetBlobClient(BackupDiscovery.EncryptedIndexBlobName);
        if ((await enc.ExistsAsync(ct)).Value)
            return await ReadWithETagAsync(enc, password, ct);

        return null;
    }

    /// <summary>
    /// Unconditional write (no ETag precondition). **Must remain a pure delegation to <see cref="WriteInfoConditionalAsync"/>**:
    /// "the info file's blob name is decided solely by WriteInfoConditionalAsync, from whether password is empty" is the invariant
    /// the reset-password endpoint leans on when it checks only the <c>Backup.Encrypted</c> flag instead of going by a decryption
    /// result (see the reset-password comment in BackupConfigEndpoints). Assembling a blob name here would silently break it.
    /// </summary>
    public Task WriteInfoAsync(
        Account account, string container, BackupInfoFile info, string? password, AccessTier? tier = null, CancellationToken ct = default)
        => WriteInfoConditionalAsync(account, container, info, password, tier, ifMatch: null, ct);

    public async Task<string> WriteInfoConditionalAsync(
        Account account, string container, BackupInfoFile info, string? password, AccessTier? tier, string? ifMatch, CancellationToken ct = default)
    {
        var cc = Container(account, container);
        var name = string.IsNullOrEmpty(password)
            ? BackupDiscovery.IndexBlobName
            : BackupDiscovery.EncryptedIndexBlobName;

        var json = IndexSerializer.SerializeInfoFile(info);
        var encoded = await codec.EncodeAsync(json, password, ct);
        return await WriteAtomicAsync(cc, name, encoded, password, b => IndexSerializer.DeserializeInfoFile(b), tier, ifMatch, ct);
    }

    /// <summary>
    /// Above this many **encoded** bytes, the index is written as volumes instead of one blob. Encoded, not raw:
    /// that is what actually goes over the wire, and an index compresses well, so the split point sits a long way
    /// past the raw size that first suggests it.
    /// <para>
    /// 64 MB is about eleven seconds at the 4-6 MB/s this project measured as the ceiling of one connection to
    /// Azure — comfortably inside the network timeout even on a line several times worse. A Put Blob that times out
    /// restarts from zero, so the point is to keep any single request short, not to make the volumes as large as
    /// they could be.
    /// </para>
    /// </summary>
    internal const int DefaultIndexVolumeBytes = 64 * 1024 * 1024;

    /// <summary>Overridable so a test can cross the threshold without building an index hundreds of MB wide. Per instance, so nothing global is mutated.</summary>
    internal int IndexVolumeBytes { get; init; } = DefaultIndexVolumeBytes;

    public Task<VersionIndex> ReadIndexAsync(
        Account account, string container, string indexBlob, string? password, int volumes = 1, CancellationToken ct = default)
        // volumes <= 1 takes VolumeNames straight back to [indexBlob], which is the path every index written
        // before info format 5 goes down — no probing, no extra request.
        => ReadIndexCoreAsync(Container(account, container), VolumeBlobIO.VolumeNames(indexBlob, volumes), password, progress: null, sizes: null, ct);

    /// <param name="sizes">Per-volume byte counts when the caller knows them (the write that just produced them), so each
    /// download can show sent / total on screen; null when it does not, and the stream is booked with an unknown total.</param>
    private async Task<VersionIndex> ReadIndexCoreAsync(
        BlobContainerClient cc, IReadOnlyList<string> names, string? password, StageTracker? progress,
        IReadOnlyList<long>? sizes, CancellationToken ct)
    {
        if (names.Count == 1)
        {
            var blob = cc.GetBlobClient(names[0]);
            byte[]? content = null;
            await TransferAsync(progress, $"{names[0]} verify", sizes?[0] ?? 0,
                async p => content = await DownloadAsync(blob, p, ct));
            return IndexSerializer.DeserializeIndex(await codec.DecodeAsync(content!, password, ct));
        }

        // Volumes are concatenated **before** decoding: the codec ran once over the whole payload, so a volume on
        // its own is a fragment of one archive, not an archive.
        var buffer = new MemoryStream();
        for (var i = 0; i < names.Count; i++)
        {
            var blob = cc.GetBlobClient(names[i]);
            byte[]? part = null;
            await TransferAsync(progress, $"{names[i]} ({i + 1}/{names.Count}) verify", sizes?[i] ?? 0,
                async p => part = await DownloadAsync(blob, p, ct));
            buffer.Write(part!, 0, part!.Length);
        }
        return IndexSerializer.DeserializeIndex(await codec.DecodeAsync(buffer.ToArray(), password, ct));
    }

    public async Task<(string Name, int Volumes)> WriteIndexAsync(
        Account account, string container, int version, VersionIndex index, string? password, AccessTier? tier = null,
        CancellationToken ct = default, StageTracker? progress = null)
    {
        var cc = Container(account, container);
        var name = $"indexes/v{version}.json" + (string.IsNullOrEmpty(password) ? "" : ".enc");

        // Serializing and encoding a few million entries is seconds of CPU before the first byte moves; the name on
        // the row says what is being produced in the meantime.
        progress?.Touch(name);
        var json = IndexSerializer.SerializeIndex(index);
        var encoded = await codec.EncodeAsync(json, password, ct);
        if (encoded.Length <= IndexVolumeBytes)
        {
            // Small enough to stay one blob, which keeps the overwhelming majority of backups on exactly the layout
            // they had before this feature existed.
            Plan(progress, [encoded.Length, encoded.Length, encoded.Length]);
            await WriteAtomicAsync(cc, name, encoded, password, b => IndexSerializer.DeserializeIndex(b), tier, ifMatch: null, ct, progress);
            return (name, 1);
        }

        var volumes = (encoded.Length + IndexVolumeBytes - 1) / IndexVolumeBytes;
        var names = VolumeBlobIO.VolumeNames(name, volumes);
        var sizes = new long[volumes];
        for (var i = 0; i < volumes; i++)
            sizes[i] = Math.Min(IndexVolumeBytes, encoded.Length - i * IndexVolumeBytes);
        // Every volume goes up once and comes back once for verification.
        Plan(progress, [.. sizes, .. sizes]);
        for (var i = 0; i < names.Count; i++)
        {
            var offset = i * IndexVolumeBytes;
            var length = (int)sizes[i];
            var blob = cc.GetBlobClient(names[i]);
            await TransferAsync(progress, $"{names[i]} ({i + 1}/{volumes}) upload", length, p =>
            {
                var options = new BlobUploadOptions { ProgressHandler = p };
                if (tier is { } t)
                    options.AccessTier = t;
                return blob.UploadAsync(new MemoryStream(encoded, offset, length), options, ct);
            });
        }

        // Read the whole set back and deserialize it, the same guarantee WriteAtomicAsync gives the single-blob
        // path. There is no temp-then-rename dance here and none is needed: the version does not exist until the
        // info file commits, several steps later, so a half-written set is an orphan rather than a corrupt version.
        // Verifying still matters — silent truncation would otherwise surface at restore time.
        VerifyRoundTrip(await ReadIndexCoreAsync(cc, names, password, progress, sizes, ct), index);
        return (name, volumes);
    }

    /// <summary>Declare the stage's size once the encoded length is known: one entry per transfer, each its byte
    /// count. The count is the item total the percentage is read off, and the bytes are the workload the
    /// remaining-time estimate extrapolates over — declared per transfer so the sum lands exactly on what
    /// <see cref="TransferAsync"/> writes off, and the workload reaches zero when the last one completes.</summary>
    private static void Plan(StageTracker? progress, IReadOnlyList<long> transfers)
    {
        if (progress is null)
            return;
        progress.SetTotal(transfers.Count);
        foreach (var bytes in transfers)
            progress.Enqueue(work: bytes);
    }

    /// <summary>
    /// One transfer of the index stage, booked on the tracker when there is one: a stream on screen while it runs
    /// (with sent / total, so a stalled upload is visible as such), its bytes counted as the SDK reports them, and
    /// one item ticked off when it completes. Without a tracker it is just the transfer.
    /// </summary>
    /// <param name="bytes">The stream's size, 0 when unknown (a download whose size nobody recorded).</param>
    private static async Task TransferAsync(
        StageTracker? progress, string label, long bytes, Func<IProgress<long>?, Task> transfer)
    {
        if (progress is null)
        {
            await transfer(null);
            return;
        }
        progress.BeginItem(label, label, bytes);
        try
        {
            await transfer(progress.ItemProgress(label));
        }
        finally
        {
            // 0: the bytes were counted piece by piece as they moved (see StageTracker.ItemProgress).
            progress.EndItem(label, 0);
        }
        progress.Advance(0, work: bytes);
    }

    /// <summary>A download that reports as it goes when someone is listening; the plain content call otherwise.</summary>
    private static async Task<byte[]> DownloadAsync(BlobClient blob, IProgress<long>? progress, CancellationToken ct)
    {
        if (progress is null)
            return (await blob.DownloadContentAsync(ct)).Value.Content.ToArray();
        var ms = new MemoryStream();
        await blob.DownloadToAsync(ms, new BlobDownloadToOptions { ProgressHandler = progress }, ct);
        return ms.ToArray();
    }

    /// <summary>Cheap structural check that what came back is what went up; a mismatch means a volume is short or out of order.</summary>
    private static void VerifyRoundTrip(VersionIndex readBack, VersionIndex written)
    {
        if (readBack.Entries.Count != written.Entries.Count || readBack.Version != written.Version)
            throw new InvalidOperationException(
                $"Index volume verification failed: wrote version {written.Version} with {written.Entries.Count} entries, read back version {readBack.Version} with {readBack.Entries.Count}.");
    }

    /// <summary>
    /// Atomic write: write a temp blob → download and verify (decode + deserialize) → write the real name (with tier, optionally If-Match) → delete the temp. Returns the new ETag of the real name.
    /// ifMatch non-empty and something changed externally → the real write throws RequestFailedException(412) and never touches the old content (§8).
    /// Takes the encoded bytes rather than encoding here: the index path has already encoded once to learn whether
    /// it fits in one blob, and encrypting tens of MB a second time bought nothing.
    /// </summary>
    private async Task<string> WriteAtomicAsync(
        BlobContainerClient cc, string finalName, byte[] encoded, string? password, Action<byte[]> verify,
        AccessTier? tier, string? ifMatch, CancellationToken ct, StageTracker? progress = null)
    {
        var temp = cc.GetBlobClient(finalName + ".writing." + Guid.NewGuid().ToString("N"));
        try
        {
            await TransferAsync(progress, $"{finalName} upload", encoded.Length,
                p => temp.UploadAsync(new MemoryStream(encoded, writable: false), new BlobUploadOptions { ProgressHandler = p }, ct));

            byte[]? readBack = null;
            await TransferAsync(progress, $"{finalName} verify", encoded.Length,
                async p => readBack = await DownloadAsync(temp, p, ct));
            verify(await codec.DecodeAsync(readBack!, password, ct));

            var options = new BlobUploadOptions();
            if (tier is { } t)
                options.AccessTier = t;
            if (ifMatch is not null)
                options.Conditions = new BlobRequestConditions { IfMatch = new ETag(ifMatch) };

            string? etag = null;
            await TransferAsync(progress, $"{finalName} commit", encoded.Length, async p =>
            {
                options.ProgressHandler = p;
                var resp = await cc.GetBlobClient(finalName).UploadAsync(new MemoryStream(encoded, writable: false), options, ct);
                etag = resp.Value.ETag.ToString();
            });
            return etag!;
        }
        finally
        {
            await temp.DeleteIfExistsAsync(cancellationToken: ct);
        }
    }

    private async Task<(BackupInfoFile Info, string ETag)> ReadWithETagAsync(BlobClient blob, string? password, CancellationToken ct)
    {
        var resp = (await blob.DownloadContentAsync(ct)).Value;
        var decoded = await codec.DecodeAsync(resp.Content.ToArray(), password, ct);
        return (IndexSerializer.DeserializeInfoFile(decoded), resp.Details.ETag.ToString());
    }

    private BlobContainerClient Container(Account account, string container) =>
        factory.CreateServiceClient(account).GetBlobContainerClient(container);
}
