using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>备份分组管理（PRD 2.2）。组须含至少一个备份。</summary>
public interface IGroupService
{
    Task<IReadOnlyList<Group>> ListAsync(CancellationToken ct = default);

    Task<Group?> GetAsync(int id, CancellationToken ct = default);

    Task<Group> CreateAsync(string name, IEnumerable<GroupMember> members, CancellationToken ct = default);

    Task<Group?> UpdateAsync(int id, string name, IEnumerable<GroupMember> members, CancellationToken ct = default);

    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
}
