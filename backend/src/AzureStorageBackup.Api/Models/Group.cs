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
}
