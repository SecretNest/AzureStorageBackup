using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Reading and writing the info record file and the second-level index inside the container (M4 design §8).
/// Combines JSON serialization + 7z codec + Azure blob; writes are made atomic via "temp blob → verify → switch to the real name".
/// A non-empty password is exactly equivalent to encryption (which uses the .enc file name).
/// </summary>
public interface IBackupInfoStore
{
    /// <summary>Reads the info record file; returns null if it does not exist. Prefers the unencrypted one (PRD 1.6).</summary>
    Task<BackupInfoFile?> ReadInfoAsync(Account account, string container, string? password, CancellationToken ct = default);

    /// <summary>Reads the info record file plus its cloud ETag (used for sync/conflict detection of the locally authoritative cache); returns null if it does not exist.</summary>
    Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account account, string container, string? password, CancellationToken ct = default);

    /// <summary>Atomically writes the info record file (overwriting). An empty tier means the default (Hot).</summary>
    Task WriteInfoAsync(Account account, string container, BackupInfoFile info, string? password, AccessTier? tier = null, CancellationToken ct = default);

    /// <summary>
    /// Atomic write with ETag optimistic concurrency, returning the new ETag. A non-empty ifMatch → <c>If-Match</c> (an external change
    /// throws RequestFailedException 412/409); empty → unconditional overwrite (identical to <see cref="WriteInfoAsync"/>). Used to commit the locally authoritative info file (§3.3).
    /// </summary>
    Task<string> WriteInfoConditionalAsync(Account account, string container, BackupInfoFile info, string? password, AccessTier? tier, string? ifMatch, CancellationToken ct = default);

    /// <summary>
    /// Reads the second-level index at the given blob name. <paramref name="volumes"/> comes from
    /// <see cref="BackupVersion.IndexVolumes"/>; 1 (the default, and what every info file older than format 5 reads
    /// back) is the single-blob layout.
    /// </summary>
    Task<VersionIndex> ReadIndexAsync(Account account, string container, string indexBlob, string? password, int volumes = 1, CancellationToken ct = default);

    /// <summary>
    /// Writes the second-level index of a version and returns its blob name (recorded in the info file's
    /// versions[].indexBlob) together with the number of volumes it was split into (versions[].indexVolumes). An
    /// empty tier means the default.
    /// </summary>
    /// <param name="progress">Where the write reports each transfer it makes, when the caller has a screen to put it
    /// on. Every volume goes up once and comes back once for verification (a single-blob index is three transfers:
    /// temp up, verify down, commit up), and each is booked as it completes with its bytes — at a few million
    /// entries the index is hundreds of MB, and this stage used to be minutes of "Writing index" with nothing moving.</param>
    Task<(string Name, int Volumes)> WriteIndexAsync(Account account, string container, int version, VersionIndex index, string? password, AccessTier? tier = null, CancellationToken ct = default, StageTracker? progress = null);

    /// <summary>
    /// The file-shaped <see cref="WriteIndexAsync"/>: the index has already been serialized to
    /// <paramref name="serializedPath"/> in <see cref="IndexStreamWriter"/> format, and goes up from there —
    /// encoded file to file, uploaded as ranges of that file — so nothing larger than one buffer is ever held in
    /// memory. The blob name, the volume naming and the volume count are identical to what
    /// <see cref="WriteIndexAsync"/> produces for the same content; the two are interchangeable on the wire.
    /// </summary>
    /// <param name="serializedPath">A file in <see cref="IndexStreamWriter"/> format. Read, never modified.</param>
    /// <param name="progress">As <see cref="WriteIndexAsync"/>: one booked transfer per volume up and per volume back.</param>
    Task<(string Name, int Volumes)> WriteIndexFileAsync(Account account, string container, int version, string serializedPath,
        string? password, AccessTier? tier = null, CancellationToken ct = default, StageTracker? progress = null);

    /// <summary>
    /// The file-shaped <see cref="ReadIndexAsync"/>: downloads the volumes, concatenates them, decodes the result
    /// and leaves the serialized index at <paramref name="destPath"/> (overwriting it) for an
    /// <see cref="IndexStreamReader"/> to walk one entry at a time. <paramref name="volumes"/> comes from
    /// <see cref="BackupVersion.IndexVolumes"/>, 1 being the single-blob layout.
    /// </summary>
    Task ReadIndexToFileAsync(Account account, string container, string indexBlob, string? password, int volumes, string destPath,
        CancellationToken ct = default);
}
