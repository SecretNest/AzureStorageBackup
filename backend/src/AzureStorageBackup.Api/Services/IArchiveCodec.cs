namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Single-content archive coding: compress (and optionally encrypt) a span of bytes into one archive blob,
/// and the inverse.
/// Used for the info file and the second-level indexes (M4 design §13.4).
/// An empty password means compression only; a non-empty one means 7z AES-256 with header encryption
/// (openable on another machine with the backup password).
/// </summary>
public interface IArchiveCodec
{
    Task<byte[]> EncodeAsync(byte[] content, string? password, CancellationToken ct = default);
    Task<byte[]> DecodeAsync(byte[] archive, string? password, CancellationToken ct = default);

    /// <summary>
    /// Same encoding as <see cref="EncodeAsync"/> — same "content" entry name, same switches — but the payload
    /// is read from <paramref name="inputPath"/> and the archive is written to <paramref name="archivePath"/>
    /// instead of round-tripping through byte arrays. Exists because an index can run into the hundreds of MB,
    /// too large to justify holding twice in memory just to pass it to the byte-array member.
    /// </summary>
    Task EncodeFileAsync(string inputPath, string archivePath, string? password, CancellationToken ct = default);

    /// <summary>The file-based inverse of <see cref="EncodeFileAsync"/>: an archive produced by either encode member
    /// decodes here, since both share the same "content" entry name.</summary>
    Task DecodeFileAsync(string archivePath, string outputPath, string? password, CancellationToken ct = default);
}
