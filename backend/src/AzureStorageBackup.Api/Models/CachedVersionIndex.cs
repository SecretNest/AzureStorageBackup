namespace AzureStorageBackup.Api.Models;

/// <summary>
/// A locally cached second-level version index (design §3.3 local state cache). Speeds up backup comparison / cleanup reference scans —
/// the large version indexes are normally read locally, avoiding a download and extraction of the cloud index on every backup. The cloud info file is still authoritative.
/// A version index is immutable once written, so it is cached by (AccountId, Container, Version);
/// <see cref="IdentityTicks"/> = the backup's creation timestamp, used to spot a container that was deleted and rebuilt (version numbers get reused but the content differs).
/// </summary>
public class CachedVersionIndex
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Container { get; set; } = string.Empty;
    public int Version { get; set; }

    /// <summary>Backup identity (Backup.CreatedAt.UtcTicks from the info file); a mismatch means the cache entry is stale.</summary>
    public long IdentityTicks { get; set; }

    /// <summary>The serialized version index bytes (IndexSerializer output, uncompressed).</summary>
    public byte[] Bytes { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
