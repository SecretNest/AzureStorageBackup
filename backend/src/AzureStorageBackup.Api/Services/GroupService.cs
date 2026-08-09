using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

public class GroupService(AppDbContext db) : IGroupService
{
    public async Task<IReadOnlyList<Group>> ListAsync(CancellationToken ct = default) =>
        await db.Groups
            .Include(g => g.Members.OrderBy(m => m.AccountId).ThenBy(m => m.ContainerName))
            .AsNoTracking()
            // NOCASE: SQLite compares by code point by default, which sorts every uppercase letter before every lowercase one (see BackupConfigService.ListAsync).
            .OrderBy(g => EF.Functions.Collate(g.Name, "NOCASE")).ToListAsync(ct);

    public async Task<Group?> GetAsync(int id, CancellationToken ct = default) =>
        await db.Groups
            .Include(g => g.Members.OrderBy(m => m.AccountId).ThenBy(m => m.ContainerName))
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task<Group> CreateAsync(string name, IEnumerable<GroupMember> members, CancellationToken ct = default)
    {
        var list = SortMembers(members);
        if (list.Count == 0)
            throw new ArgumentException("A group must contain at least one backup.", nameof(members));

        var group = new Group
        {
            Name = name,
            CreatedAt = DateTimeOffset.UtcNow,
            Members = list
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<Group?> UpdateAsync(int id, string name, IEnumerable<GroupMember> members, CancellationToken ct = default)
    {
        var list = SortMembers(members);
        if (list.Count == 0)
            throw new ArgumentException("A group must contain at least one backup.", nameof(members));

        var group = await db.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
            return null;

        group.Name = name;
        db.GroupMembers.RemoveRange(group.Members);
        group.Members = list;

        await db.SaveChangesAsync(ct);
        return group;
    }

    /// <summary>A stable order for group members: by (AccountId, ContainerName), so insertion order does not make the UI jump (§5.6).</summary>
    private static List<GroupMember> SortMembers(IEnumerable<GroupMember> members) =>
        members.OrderBy(m => m.AccountId).ThenBy(m => m.ContainerName, StringComparer.Ordinal).ToList();

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
            return false;

        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
