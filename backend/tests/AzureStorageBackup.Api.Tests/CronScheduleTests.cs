using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class CronScheduleTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Fact]
    public void Every_Minute_Is_Due_After_A_Minute_Passes()
    {
        var due = CronSchedule.IsDue("* * * * *", lastRun: Utc(2026, 7, 17, 10, 0), now: Utc(2026, 7, 17, 10, 1), TimeZoneInfo.Utc);
        Assert.True(due);
    }

    [Fact]
    public void Not_Due_Before_Next_Occurrence()
    {
        // 每天 09:00；上次今天 09:00，现在 08:59（今天还没到）→ 未到期
        var due = CronSchedule.IsDue("0 9 * * *", lastRun: Utc(2026, 7, 17, 9, 0), now: Utc(2026, 7, 17, 8, 59), TimeZoneInfo.Utc);
        Assert.False(due);
    }

    [Fact]
    public void Due_When_Scheduled_Time_Reached()
    {
        // 每天 09:00；上次昨天 09:00，现在今天 09:00 → 到期
        var due = CronSchedule.IsDue("0 9 * * *", lastRun: Utc(2026, 7, 16, 9, 0), now: Utc(2026, 7, 17, 9, 0), TimeZoneInfo.Utc);
        Assert.True(due);
    }

    [Fact]
    public void Next_Occurrence_Is_After_Given_Time()
    {
        var next = CronSchedule.NextOccurrence("0 9 * * *", Utc(2026, 7, 17, 10, 0), TimeZoneInfo.Utc);
        Assert.Equal(Utc(2026, 7, 18, 9, 0), next);
    }

    [Fact]
    public void Invalid_Cron_Is_Never_Due()
    {
        Assert.False(CronSchedule.IsDue("not a cron", DateTimeOffset.MinValue, DateTimeOffset.UtcNow, TimeZoneInfo.Utc));
        Assert.Null(CronSchedule.NextOccurrence("nope", DateTimeOffset.UtcNow, TimeZoneInfo.Utc));
    }
}
