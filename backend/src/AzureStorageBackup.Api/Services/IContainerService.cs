using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>一个 container 及其备份存在情况。</summary>
public record ContainerInfo(string Name, BackupPresence Backup)
{
    /// <summary>
    /// 本地库里占着这个 container 的那条备份配置的名字；没人占着则为 null。
    /// <para>
    /// <see cref="Backup"/> 只说得出「云端信息文件在不在」，而那个文件是备份的最后一步才写的：
    /// 首次备份跑到一半的 container 里已经躺着这一轮的数据，云端却还什么标记都没有。占用的权威
    /// 在本地——库里那条配置从创建的那一刻就存在，不必等任何云端产物（<c>ContainerEndpoints</c> 填入）。
    /// </para>
    /// </summary>
    public string? InUseBy { get; init; }
}

/// <summary>账户下的 container 管理（列举/创建/删除）与备份发现。</summary>
public interface IContainerService
{
    Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(Account account, CancellationToken ct = default);

    Task CreateContainerAsync(Account account, string name, CancellationToken ct = default);

    Task DeleteContainerAsync(Account account, string name, CancellationToken ct = default);
}
