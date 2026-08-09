namespace AzureStorageBackup.Api.Models;

/// <summary>A backup configuration's persistent status (decision 2). Transient states (backing up, restoring, checking, repairing…) are derived by the runners and never stored.</summary>
public enum BackupStatus
{
    Normal = 0,
    Error = 1,
}
