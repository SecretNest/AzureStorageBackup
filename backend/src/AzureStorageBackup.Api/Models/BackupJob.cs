namespace AzureStorageBackup.Api.Models;

/// <summary>
/// 一次备份任务的记录。骨架阶段仅含基础字段，具体字段随需求补充。
/// </summary>
public class BackupJob
{
    public int Id { get; set; }

    /// <summary>任务名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>本地待备份的源路径。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>目标 Azure Blob 容器名。</summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>任务状态。</summary>
    public BackupJobStatus Status { get; set; } = BackupJobStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public enum BackupJobStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}
