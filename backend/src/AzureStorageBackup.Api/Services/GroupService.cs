using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

public class GroupService(AppDbContext db) : IGroupService
{
    public async Task<IReadOnlyList<Group>> ListAsync(CancellationToken ct = default) =>
        await db.Groups
            .Include(g => g.Members.OrderBy(m => m.Position))
            .AsNoTracking()
            // NOCASE: SQLite compares by code point by default, which sorts every uppercase letter before every lowercase one (see BackupConfigService.ListAsync).
            .OrderBy(g => EF.Functions.Collate(g.Name, "NOCASE")).ToListAsync(ct);

    public async Task<Group?> GetAsync(int id, CancellationToken ct = default) =>
        await db.Groups
            .Include(g => g.Members.OrderBy(m => m.Position))
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == id, ct);

    public async Task<Group> CreateAsync(string name, IEnumerable<GroupMember> members, CancellationToken ct = default)
    {
        var list = Sequence(members);
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
        var list = Sequence(members);
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

    /// <summary>
    /// Members in the order they were given, numbered from 0, with a backup named twice kept at its first place.
    /// The order a group is saved in is its run order (see <see cref="GroupMember.Position"/>), so nothing here
    /// may sort: the request's order is the one the operator arranged.
    /// </summary>
    private static List<GroupMember> Sequence(IEnumerable<GroupMember> members)
    {
        var seen = new HashSet<(int, string)>();
        var list = new List<GroupMember>();
        foreach (var m in members)
        {
            if (!seen.Add((m.AccountId, m.ContainerName)))
                continue;
            m.Position = list.Count;
            list.Add(m);
        }
        return list;
    }

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
