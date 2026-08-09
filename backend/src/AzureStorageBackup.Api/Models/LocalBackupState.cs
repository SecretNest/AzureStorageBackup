namespace AzureStorageBackup.Api.Models;

/// <summary>
/// The locally authoritative info-file cache (design §3.3). The info file is not normally read from the
/// cloud — it may sit in the Cold tier, where reading its content costs a retrieval fee.
/// The serialised info file is kept locally alongside its cloud ETag; a backup writes with <c>If-Match</c>
/// for optimistic concurrency against external changes (another machine, a recreated container), and on
/// conflict the local copy is cleared and resynced.
/// </summary>
public class LocalBackupState
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Container { get; set; } = string.Empty;

    /// <summary>The serialised info-file bytes (IndexSerializer output, uncompressed).</summary>
    public byte[] InfoBytes { get; set; } = [];

    /// <summary>The ETag of the cloud info-file blob, used for If-Match on the next write.</summary>
    public string ETag { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
