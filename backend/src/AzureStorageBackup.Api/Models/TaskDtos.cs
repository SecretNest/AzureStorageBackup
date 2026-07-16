namespace AzureStorageBackup.Api.Models;

public record TaskRequest(
    TaskTargetKind TargetKind,
    int? AccountId,
    string? ContainerName,
    int? GroupId,
    ScheduledTaskType TaskType,
    string CronExpression,
    bool Enabled)
{
    public ScheduledTask ToEntity() => new()
    {
        TargetKind = TargetKind,
        AccountId = AccountId,
        ContainerName = ContainerName,
        GroupId = GroupId,
        TaskType = TaskType,
        CronExpression = CronExpression,
        Enabled = Enabled
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
    DateTimeOffset CreatedAt)
{
    public static TaskResponse From(ScheduledTask t) => new(
        t.Id, t.TargetKind, t.AccountId, t.ContainerName, t.GroupId,
        t.TaskType, t.CronExpression, t.Enabled, t.CreatedAt);
}
