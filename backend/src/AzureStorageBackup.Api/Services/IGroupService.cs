using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Backup group management (PRD 2.2). A group must hold at least one backup.</summary>
public interface IGroupService
{
    Task<IReadOnlyList<Group>> ListAsync(CancellationToken ct = default);

    Task<Group?> GetAsync(int id, CancellationToken ct = default);

    Task<Group> CreateAsync(string name, IEnumerable<GroupMember> members, CancellationToken ct = default);

    Task<Group?> UpdateAsync(int id, string name, IEnumerable<GroupMember> members, CancellationToken ct = default);

    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
}
