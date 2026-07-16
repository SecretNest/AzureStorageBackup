using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>一个 container 及其备份存在情况。</summary>
public record ContainerInfo(string Name, BackupPresence Backup);

/// <summary>账户下的 container 管理（列举/创建/删除）与备份发现。</summary>
public interface IContainerService
{
    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(Account account, CancellationToken ct = default);

    Task CreateContainerAsync(Account account, string name, CancellationToken ct = default);

    Task DeleteContainerAsync(Account account, string name, CancellationToken ct = default);
}
