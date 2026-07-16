using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>计划任务的增删改查（PRD 2.3）。仅管理配置；执行调度在 M6。</summary>
public interface IScheduledTaskService
{
    Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default);

    Task<ScheduledTask?> GetAsync(int id, CancellationToken ct = default);

    Task<ScheduledTask> CreateAsync(ScheduledTask task, CancellationToken ct = default);

    Task<ScheduledTask?> UpdateAsync(int id, ScheduledTask update, CancellationToken ct = default);

    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
}
