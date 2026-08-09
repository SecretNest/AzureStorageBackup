namespace AzureStorageBackup.Api.Services;

/// <summary>A discovered backup: account, container, and whether it is encrypted.</summary>
public record DiscoveredBackup(int AccountId, string AccountName, string ContainerName, BackupPresence Presence);

/// <summary>
/// Aggregates container discovery across every account to list the backups that exist (PRD 2.1).
/// Triggered manually; never refreshed automatically.
/// </summary>
public interface IBackupInventoryService
{
    Task<IReadOnlyList<DiscoveredBackup>> ListAsync(CancellationToken ct = default);
}
