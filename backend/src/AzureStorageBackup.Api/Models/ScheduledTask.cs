namespace AzureStorageBackup.Api.Models;

/// <summary>Task target kind: a single backup, or a group.</summary>
public enum TaskTargetKind
{
    Backup = 0,
    Group = 1
}

/// <summary>Task type (PRD 2.3, 9).</summary>
public enum ScheduledTaskType
{
    Backup = 0,
    Check = 1,
    Cleanup = 2
}

/// <summary>
/// Scheduled task configuration (PRD 2.3). Stores configuration only; the scheduled execution lands in M6.
/// When the target is a backup it uses (AccountId, ContainerName); when it is a group, GroupId.
/// </summary>
public class ScheduledTask
{
    public int Id { get; set; }

    public TaskTargetKind TargetKind { get; set; }

    // Target is a backup
    public int? AccountId { get; set; }
    public string? ContainerName { get; set; }

    // Target is a group
    public int? GroupId { get; set; }

    public ScheduledTaskType TaskType { get; set; }
    public string CronExpression { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Check level for a scheduled check task (only meaningful when TaskType=Check). Defaults to "existence + size" on the cloud side and "content hash" on the local side.</summary>
    public CloudCheckLevel CheckCloudLevel { get; set; } = CloudCheckLevel.ExistenceSize;
    public LocalCheckLevel CheckLocalLevel { get; set; } = LocalCheckLevel.Content;

    /// <summary>Target rehydration tier when a Content-level check hits Archive (null = do not rehydrate).</summary>
    public StorageTier? CheckRehydrateTier { get; set; }

    /// <summary>Timestamp of the last firing (maintained by the scheduler); used to work out whether the next one is due, and to keep a restart from replaying it.</summary>
    public DateTimeOffset? LastRunAt { get; set; }
}
