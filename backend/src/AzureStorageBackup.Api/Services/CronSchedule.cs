using Cronos;

namespace AzureStorageBackup.Api.Services;

/// <summary>Standard five-field cron evaluation (M6). An invalid expression never fires (the scheduler skips it and logs).</summary>
public static class CronSchedule
{
    /// <summary>The next firing after the given time (excluding `after` itself); null for an invalid expression.</summary>
    public static DateTimeOffset? NextOccurrence(string cron, DateTimeOffset after, TimeZoneInfo tz)
    {
        var expr = TryParse(cron);
        return expr?.GetNextOccurrence(after, tz);
    }

    /// <summary>Whether a scheduled firing time has been reached since lastRun (&lt;= now).</summary>
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
