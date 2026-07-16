using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 备份任务业务实现。骨架阶段仅做任务记录的持久化，
/// 真正触发上传的逻辑（调用 IAzureStorageService）等需求明确后再接。
/// </summary>
public class BackupService(AppDbContext db) : IBackupService
{
    public async Task<IReadOnlyList<BackupJob>> ListJobsAsync(CancellationToken ct = default) =>
        await db.BackupJobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).ToListAsync(ct);

    public async Task<BackupJob?> GetJobAsync(int id, CancellationToken ct = default) =>
        await db.BackupJobs.FindAsync([id], ct);

    public async Task<BackupJob> CreateJobAsync(CreateBackupJobRequest request, CancellationToken ct = default)
    {
        var job = new BackupJob
        {
            Name = request.Name,
            SourcePath = request.SourcePath,
            ContainerName = request.ContainerName,
            Status = BackupJobStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.BackupJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }
}
