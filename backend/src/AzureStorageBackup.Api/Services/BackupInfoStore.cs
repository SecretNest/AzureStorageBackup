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
        return await WriteAtomicAsync(cc, name, json, password, b => IndexSerializer.DeserializeInfoFile(b), tier, ifMatch, ct);
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

    public async Task<VersionIndex> ReadIndexAsync(
        Account account, string container, string indexBlob, string? password, int volumes = 1, CancellationToken ct = default)
    {
        var cc = Container(account, container);
        // volumes <= 1 takes VolumeNames straight back to [indexBlob], which is the path every index written
        // before info format 5 goes down — no probing, no extra request.
        var names = VolumeBlobIO.VolumeNames(indexBlob, volumes);
        if (names.Count == 1)
            return IndexSerializer.DeserializeIndex(await DownloadDecodeAsync(cc.GetBlobClient(names[0]), password, ct));

        // Volumes are concatenated **before** decoding: the codec ran once over the whole payload, so a volume on
        // its own is a fragment of one archive, not an archive.
        var buffer = new MemoryStream();
        foreach (var n in names)
        {
            var part = (await cc.GetBlobClient(n).DownloadContentAsync(ct)).Value.Content.ToArray();
            buffer.Write(part, 0, part.Length);
        }
        return IndexSerializer.DeserializeIndex(await codec.DecodeAsync(buffer.ToArray(), password, ct));
    }

    public async Task<(string Name, int Volumes)> WriteIndexAsync(
        Account account, string container, int version, VersionIndex index, string? password, AccessTier? tier = null, CancellationToken ct = default)
    {
        var cc = Container(account, container);
        var name = $"indexes/v{version}.json" + (string.IsNullOrEmpty(password) ? "" : ".enc");

        var json = IndexSerializer.SerializeIndex(index);
        var encoded = await codec.EncodeAsync(json, password, ct);
        if (encoded.Length <= IndexVolumeBytes)
        {
            // Small enough to stay one blob, which keeps the overwhelming majority of backups on exactly the layout
            // they had before this feature existed.
            await WriteAtomicAsync(cc, name, json, password, b => IndexSerializer.DeserializeIndex(b), tier, ifMatch: null, ct);
            return (name, 1);
        }

        var volumes = (encoded.Length + IndexVolumeBytes - 1) / IndexVolumeBytes;
        var names = VolumeBlobIO.VolumeNames(name, volumes);
        for (var i = 0; i < names.Count; i++)
        {
            var offset = i * IndexVolumeBytes;
            var length = Math.Min(IndexVolumeBytes, encoded.Length - offset);
            var options = new BlobUploadOptions();
            if (tier is { } t)
                options.AccessTier = t;
            await cc.GetBlobClient(names[i]).UploadAsync(new MemoryStream(encoded, offset, length), options, ct);
        }

        // Read the whole set back and deserialize it, the same guarantee WriteAtomicAsync gives the single-blob
        // path. There is no temp-then-rename dance here and none is needed: the version does not exist until the
        // info file commits, several steps later, so a half-written set is an orphan rather than a corrupt version.
        // Verifying still matters — silent truncation would otherwise surface at restore time.
        VerifyRoundTrip(await ReadIndexAsync(account, container, name, password, volumes, ct), index);
        return (name, volumes);
    }

    /// <summary>Cheap structural check that what came back is what went up; a mismatch means a volume is short or out of order.</summary>
    private static void VerifyRoundTrip(VersionIndex readBack, VersionIndex written)
    {
        if (readBack.Entries.Count != written.Entries.Count || readBack.Version != written.Version)
            throw new InvalidOperationException(
                $"Index volume verification failed: wrote version {written.Version} with {written.Entries.Count} entries, read back version {readBack.Version} with {readBack.Entries.Count}.");
    }

    /// <summary>
    /// Atomic write: encode → write a temp blob → download and verify (decode + deserialize) → write the real name (with tier, optionally If-Match) → delete the temp. Returns the new ETag of the real name.
    /// ifMatch non-empty and something changed externally → the real write throws RequestFailedException(412) and never touches the old content (§8).
    /// </summary>
    private async Task<string> WriteAtomicAsync(
        BlobContainerClient cc, string finalName, byte[] json, string? password, Action<byte[]> verify,
        AccessTier? tier, string? ifMatch, CancellationToken ct)
    {
        var encoded = await codec.EncodeAsync(json, password, ct);
        var temp = cc.GetBlobClient(finalName + ".writing." + Guid.NewGuid().ToString("N"));
        try
        {
            await temp.UploadAsync(BinaryData.FromBytes(encoded), overwrite: true, ct);

            verify(await DownloadDecodeAsync(temp, password, ct));

            var options = new BlobUploadOptions();
            if (tier is { } t)
                options.AccessTier = t;
            if (ifMatch is not null)
                options.Conditions = new BlobRequestConditions { IfMatch = new ETag(ifMatch) };

            var resp = await cc.GetBlobClient(finalName).UploadAsync(BinaryData.FromBytes(encoded).ToStream(), options, ct);
            return resp.Value.ETag.ToString();
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

    private async Task<byte[]> DownloadDecodeAsync(BlobClient blob, string? password, CancellationToken ct)
    {
        var content = (await blob.DownloadContentAsync(ct)).Value.Content.ToArray();
        return await codec.DecodeAsync(content, password, ct);
    }

    private BlobContainerClient Container(Account account, string container) =>
        factory.CreateServiceClient(account).GetBlobContainerClient(container);
}
