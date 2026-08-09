using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>CRUD for scheduled tasks (PRD 2.3). Configuration only; execution scheduling is M6.</summary>
public interface IScheduledTaskService
{
    Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default);

    Task<ScheduledTask?> GetAsync(int id, CancellationToken ct = default);

    Task<ScheduledTask> CreateAsync(ScheduledTask task, CancellationToken ct = default);

    Task<ScheduledTask?> UpdateAsync(int id, ScheduledTask update, CancellationToken ct = default);

    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Record the last firing time (used by the scheduler).</summary>
    Task SetLastRunAsync(int id, DateTimeOffset when, CancellationToken ct = default);
}
