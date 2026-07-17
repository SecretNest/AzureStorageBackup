namespace AzureStorageBackup.Api.Models;

public record TaskRequest(
    TaskTargetKind TargetKind,
    int? AccountId,
    string? ContainerName,
    int? GroupId,
    ScheduledTaskType TaskType,
    string CronExpression,
    bool Enabled,
    CloudCheckLevel? CheckCloudLevel = null,
    LocalCheckLevel? CheckLocalLevel = null,
    StorageTier? CheckRehydrateTier = null)
{
    public ScheduledTask ToEntity() => new()
    {
        TargetKind = TargetKind,
        AccountId = AccountId,
        ContainerName = ContainerName,
        GroupId = GroupId,
        TaskType = TaskType,
        CronExpression = CronExpression,
        Enabled = Enabled,
        CheckCloudLevel = CheckCloudLevel ?? CloudCheckLevel.ExistenceSize,
        CheckLocalLevel = CheckLocalLevel ?? LocalCheckLevel.Content,
        CheckRehydrateTier = CheckRehydrateTier,
    };
}

public record TaskResponse(
    int Id,
    TaskTargetKind TargetKind,
    int? AccountId,
    string? ContainerName,
    int? GroupId,
    ScheduledTaskType TaskType,
    string CronExpression,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRunAt,
    CloudCheckLevel CheckCloudLevel,
    LocalCheckLevel CheckLocalLevel,
    StorageTier? CheckRehydrateTier)
{
    public static TaskResponse From(ScheduledTask t) => new(
        t.Id, t.TargetKind, t.AccountId, t.ContainerName, t.GroupId,
        t.TaskType, t.CronExpression, t.Enabled, t.CreatedAt, t.LastRunAt,
        t.CheckCloudLevel, t.CheckLocalLevel, t.CheckRehydrateTier);
}
