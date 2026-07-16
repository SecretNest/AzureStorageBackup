using Cronos;

namespace AzureStorageBackup.Api.Services;

/// <summary>标准 5 段 cron 求值（M6）。非法表达式视为永不触发（由调度器跳过并记录）。</summary>
public static class CronSchedule
{
    /// <summary>给定时间之后的下一次触发（不含 after 本身）；非法表达式返回 null。</summary>
    public static DateTimeOffset? NextOccurrence(string cron, DateTimeOffset after, TimeZoneInfo tz)
    {
        var expr = TryParse(cron);
        return expr?.GetNextOccurrence(after, tz);
    }

    /// <summary>自 lastRun 起是否已到达一次计划触发时刻（&lt;= now）。</summary>
    public static bool IsDue(string cron, DateTimeOffset lastRun, DateTimeOffset now, TimeZoneInfo tz)
    {
        var next = NextOccurrence(cron, lastRun, tz);
        return next is { } n && n <= now;
    }

    private static CronExpression? TryParse(string cron)
    {
        try { return CronExpression.Parse(cron, CronFormat.Standard); }
        catch (CronFormatException) { return null; }
    }
}
