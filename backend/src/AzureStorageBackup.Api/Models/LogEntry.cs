namespace AzureStorageBackup.Api.Models;

/// <summary>操作日志级别（PRD 5，支持按等级过滤）。</summary>
public enum OperationLogLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
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
}
