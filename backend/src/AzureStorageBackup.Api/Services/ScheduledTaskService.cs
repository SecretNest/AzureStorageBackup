using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

public class ScheduledTaskService(AppDbContext db) : IScheduledTaskService
{
    public async Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default) =>
        await db.ScheduledTasks.AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct);

    public async Task<ScheduledTask?> GetAsync(int id, CancellationToken ct = default) =>
        await db.ScheduledTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<ScheduledTask> CreateAsync(ScheduledTask task, CancellationToken ct = default)
    {
        Validate(task);
        if (task.CreatedAt == default)
            task.CreatedAt = DateTimeOffset.UtcNow;

        db.ScheduledTasks.Add(task);
        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<ScheduledTask?> UpdateAsync(int id, ScheduledTask update, CancellationToken ct = default)
    {
        Validate(update);

        var existing = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (existing is null)
            return null;

        existing.TargetKind = update.TargetKind;
        existing.AccountId = update.AccountId;
        existing.ContainerName = update.ContainerName;
        existing.GroupId = update.GroupId;
        existing.TaskType = update.TaskType;
        existing.CronExpression = update.CronExpression;
        existing.Enabled = update.Enabled;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        var existing = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (existing is null)
            return false;

        db.ScheduledTasks.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task SetLastRunAsync(int id, DateTimeOffset when, CancellationToken ct = default)
    {
        var existing = await db.ScheduledTasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (existing is null)
            return;

        existing.LastRunAt = when;
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(ScheduledTask t)
    {
        if (string.IsNullOrWhiteSpace(t.CronExpression))
            throw new ArgumentException("CronExpression is required.", nameof(t));

        switch (t.TargetKind)
        {
            case TaskTargetKind.Backup when t.AccountId is null || string.IsNullOrWhiteSpace(t.ContainerName):
                throw new ArgumentException("Backup target requires AccountId and ContainerName.", nameof(t));
            case TaskTargetKind.Group when t.GroupId is null:
                throw new ArgumentException("Group target requires GroupId.", nameof(t));
        }
    }
}
