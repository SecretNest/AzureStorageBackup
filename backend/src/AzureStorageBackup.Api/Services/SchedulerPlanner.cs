using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>计划任务是否到期（M6）。启用且自上次运行起已到达一次 cron 触发时刻。</summary>
public static class SchedulerPlanner
{
    public static bool IsDue(ScheduledTask task, DateTimeOffset now, TimeZoneInfo tz)
        => task.Enabled
           && CronSchedule.IsDue(task.CronExpression, task.LastRunAt ?? task.CreatedAt, now, tz);
}
