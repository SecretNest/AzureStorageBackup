using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class SchedulerPlannerTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    private static ScheduledTask Task(string cron, bool enabled = true, DateTimeOffset? lastRun = null) => new()
    {
        CronExpression = cron,
        Enabled = enabled,
        CreatedAt = Utc(2026, 7, 16, 0, 0),
        LastRunAt = lastRun,
    };

    [Fact]
    public void Disabled_Task_Is_Never_Due()
    {
        var task = Task("* * * * *", enabled: false);
        Assert.False(SchedulerPlanner.IsDue(task, Utc(2026, 7, 17, 10, 0), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Enabled_And_Due_Fires()
    {
        var task = Task("0 9 * * *", lastRun: Utc(2026, 7, 16, 9, 0));
        Assert.True(SchedulerPlanner.IsDue(task, Utc(2026, 7, 17, 9, 0), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Enabled_But_Not_Yet_Due()
    {
        var task = Task("0 9 * * *", lastRun: Utc(2026, 7, 17, 9, 0));
        Assert.False(SchedulerPlanner.IsDue(task, Utc(2026, 7, 17, 12, 0), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Null_LastRun_Uses_CreatedAt()
    {
        // Never run; CreatedAt 07-16 00:00, cron daily at 09:00, now 07-17 10:00 → a firing time has passed → due
        var task = Task("0 9 * * *", lastRun: null);
        Assert.True(SchedulerPlanner.IsDue(task, Utc(2026, 7, 17, 10, 0), TimeZoneInfo.Utc));
    }
}
