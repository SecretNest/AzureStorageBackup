namespace AzureStorageBackup.Api.Services;

/// <summary>container 中备份信息文件的存在情况。</summary>
public enum BackupPresence
{
    None,
    Plain,
    Encrypted
}

/// <summary>
/// 信息记录文件的约定与发现判定。
/// 约定：container 中若存在索引文件，则视为本工具的备份 container（PRD 1.3）。
/// 加密备份用不同文件名；两者都存在时用非加密（PRD 1.6）。
/// </summary>
public static class BackupDiscovery
{
    public const string IndexBlobName = "azurestoragebackup.index.json";
    public const string EncryptedIndexBlobName = "azurestoragebackup.index.json.enc";

    public static BackupPresence Determine(bool hasPlainIndex, bool hasEncryptedIndex)
    {
        if (hasPlainIndex)
            return BackupPresence.Plain; // 两者都在时优先非加密（PRD 1.6）
        if (hasEncryptedIndex)
            return BackupPresence.Encrypted;
        return BackupPresence.None;
    }
}
