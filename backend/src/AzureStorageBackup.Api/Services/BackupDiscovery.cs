namespace AzureStorageBackup.Api.Services;

/// <summary>Whether a container holds a backup info file.</summary>
public enum BackupPresence
{
    None,
    Plain,
    Encrypted
}

/// <summary>
/// The info file convention and the discovery rule.
/// The convention: a container holding an index file is considered one of this tool's backup containers
/// (PRD 1.3). Encrypted backups use a different filename, and when both exist the unencrypted one wins
/// (PRD 1.6).
/// </summary>
public static class BackupDiscovery
{
    public const string IndexBlobName = "azurestoragebackup.index.json";
    public const string EncryptedIndexBlobName = "azurestoragebackup.index.json.enc";

    public static BackupPresence Determine(bool hasPlainIndex, bool hasEncryptedIndex)
    {
        if (hasPlainIndex)
            return BackupPresence.Plain; // with both present, the unencrypted one wins (PRD 1.6)
        if (hasEncryptedIndex)
            return BackupPresence.Encrypted;
        return BackupPresence.None;
    }
}
