namespace AzureStorageBackup.Api.Models;

/// <summary>Operation log levels (PRD 5, filterable by level).</summary>
public enum OperationLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>One operation log entry (PRD 5). Source makes filtering by origin (a given backup, say) possible.</summary>
public class LogEntry
{
    public int Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public OperationLogLevel Level { get; set; }

    /// <summary>The origin, such as "backup:photos", "scheduler" or "restore:photos".</summary>
    public string Source { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Ephemeral: info/debug diagnostics, cleared automatically once expired (14 days by default, PRD 3.6).
    /// false = durable (task start/end/error audit records, kept until the backup is deleted or cleared
    /// manually by time).
    /// </summary>
    public bool Ephemeral { get; set; }
}
