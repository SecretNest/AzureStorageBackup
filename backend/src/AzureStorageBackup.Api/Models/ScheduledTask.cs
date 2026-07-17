namespace AzureStorageBackup.Api.Models;

/// <summary>任务目标类型：单个备份 或 组。</summary>
public enum TaskTargetKind
{
    Backup = 0,
    Group = 1
}

/// <summary>任务类型（PRD 2.3、9）。</summary>
public enum ScheduledTaskType
{
    Backup = 0,
    Check = 1,
    Cleanup = 2
}

/// <summary>
/// 计划任务配置（PRD 2.3）。仅存配置；调度执行在 M6。
/// 目标为备份时用 (AccountId, ContainerName)，为组时用 GroupId。
/// </summary>
public class ScheduledTask
{
    public int Id { get; set; }

    public TaskTargetKind TargetKind { get; set; }

    // 目标为备份
    public int? AccountId { get; set; }
    public string? ContainerName { get; set; }

    // 目标为组
    public int? GroupId { get; set; }

    public ScheduledTaskType TaskType { get; set; }
    public string CronExpression { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>计划检查任务的检查级别（仅 TaskType=Check 有意义）。默认云端「存在+尺寸」、本地「内容 hash」。</summary>
    public CloudCheckLevel CheckCloudLevel { get; set; } = CloudCheckLevel.ExistenceSize;
    public LocalCheckLevel CheckLocalLevel { get; set; } = LocalCheckLevel.Content;

    /// <summary>Content 级遇 Archive 的活化目标 tier（null=不活化）。</summary>
    public StorageTier? CheckRehydrateTier { get; set; }

    /// <summary>上次触发时刻（调度器维护）；用于计算下次是否到期，避免重启后重放。</summary>
    public DateTimeOffset? LastRunAt { get; set; }
}
