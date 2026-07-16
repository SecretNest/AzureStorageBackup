namespace AzureStorageBackup.Api.Services;

/// <summary>一个已发现的备份：账户 + container + 加密与否。</summary>
public record DiscoveredBackup(int AccountId, string AccountName, string ContainerName, BackupPresence Presence);

/// <summary>
/// 聚合所有账户的 container 发现，列出已存在的备份（PRD 2.1）。
/// 手动触发，不自动刷新。
/// </summary>
public interface IBackupInventoryService
{
    Task<IReadOnlyList<DiscoveredBackup>> ListAsync(CancellationToken ct = default);
}
