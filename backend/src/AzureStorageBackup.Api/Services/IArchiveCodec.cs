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
}
