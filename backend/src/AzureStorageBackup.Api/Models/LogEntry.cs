namespace AzureStorageBackup.Api.Models;

/// <summary>操作日志级别（PRD 5，支持按等级过滤）。</summary>
public enum OperationLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>一条操作日志（PRD 5）。Source 便于按来源（如某备份）过滤。</summary>
public class LogEntry
{
    public int Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public OperationLogLevel Level { get; set; }

    /// <summary>来源，如 "backup:photos"、"scheduler"、"restore:photos"。</summary>
    public string Source { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 短存日志：info/debug 级诊断日志，超期(默认 14 天)自动清（PRD 3.6）。
    /// false=长存（任务开始/结束/错误等审计记录，保留至删除备份或手工按时间清）。
    /// </summary>
    public bool Ephemeral { get; set; }
}
