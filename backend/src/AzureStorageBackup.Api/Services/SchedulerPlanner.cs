using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Whether a scheduled task is due (M6): enabled, and a cron firing time has been reached since the last run.</summary>
public static class SchedulerPlanner
{
    public static bool IsDue(ScheduledTask task, DateTimeOffset now, TimeZoneInfo tz)
        => task.Enabled
           && CronSchedule.IsDue(task.CronExpression, task.LastRunAt ?? task.CreatedAt, now, tz);
}
