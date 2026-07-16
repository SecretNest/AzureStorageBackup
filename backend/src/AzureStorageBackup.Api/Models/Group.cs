namespace AzureStorageBackup.Api.Models;

/// <summary>备份分组（PRD 2.2）。组内含至少一个备份，计划任务对组内备份依次执行。</summary>
public class Group
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public List<GroupMember> Members { get; set; } = [];
}

/// <summary>组成员：一个备份，由 (AccountId, ContainerName) 标识。</summary>
public class GroupMember
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public int AccountId { get; set; }
    public string ContainerName { get; set; } = string.Empty;
}
