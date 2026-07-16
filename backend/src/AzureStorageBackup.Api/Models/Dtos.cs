namespace AzureStorageBackup.Api.Models;

/// <summary>创建备份任务的请求体。骨架占位，字段随需求补充。</summary>
public record CreateBackupJobRequest(string Name, string SourcePath, string ContainerName);

/// <summary>备份任务的响应体。</summary>
public record BackupJobResponse(
    int Id,
    string Name,
    string SourcePath,
    string ContainerName,
    BackupJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public static BackupJobResponse From(BackupJob job) => new(
        job.Id, job.Name, job.SourcePath, job.ContainerName,
        job.Status, job.CreatedAt, job.CompletedAt);
}
