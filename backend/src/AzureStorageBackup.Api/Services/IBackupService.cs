using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 备份任务的业务编排。骨架阶段提供任务的增查，
/// 实际的备份执行/调度/文件筛选等随需求补充。
/// </summary>
public interface IBackupService
{
    Task<IReadOnlyList<BackupJob>> ListJobsAsync(CancellationToken ct = default);

    Task<BackupJob?> GetJobAsync(int id, CancellationToken ct = default);

    Task<BackupJob> CreateJobAsync(CreateBackupJobRequest request, CancellationToken ct = default);
}
