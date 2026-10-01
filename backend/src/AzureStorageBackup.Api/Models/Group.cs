namespace AzureStorageBackup.Api.Models;

/// <summary>A backup group (PRD 2.2). A group holds at least one backup, and a scheduled task runs them in sequence.</summary>
public class Group
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public List<GroupMember> Members { get; set; } = [];
}

/// <summary>A group member: one backup, identified by (AccountId, ContainerName).</summary>
public class GroupMember
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public int AccountId { get; set; }
    public string ContainerName { get; set; } = string.Empty;
    /// <summary>
    /// Where this member stands in the group, from 0: the order the group was saved in, which is the order a
    /// scheduled task runs its members in and the order every list shows them in. One order for all three — the
    /// members used to be sorted by (AccountId, ContainerName) on the way in, which the picker never showed (it
    /// lists backups by name), so the sequence a group ran in could not be read off any screen.
    /// </summary>
    public int Position { get; set; }
}
