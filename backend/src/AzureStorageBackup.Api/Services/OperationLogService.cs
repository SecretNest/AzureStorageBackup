using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>把事件映射到日志级别（失败类 → Error）。</summary>
public static class EventLog
{
    public static OperationLogLevel LevelOf(NotificationEvents evt) => evt switch
    {
        NotificationEvents.BackupFailure
            or NotificationEvents.RestoreFailure
            or NotificationEvents.CheckFailure
            or NotificationEvents.UnrecoverableError => OperationLogLevel.Error,
        _ => OperationLogLevel.Info,
    };
}

/// <summary>操作日志的记录与查询（PRD 5）。按等级/来源/时间过滤，可清空。</summary>
public interface IOperationLog
{
    Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default);

    Task<IReadOnlyList<LogEntry>> QueryAsync(
        OperationLogLevel? minLevel, string? source, DateTimeOffset? from, DateTimeOffset? to, int limit,
        CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}

public sealed class OperationLogService(AppDbContext db) : IOperationLog
{
    public async Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default)
    {
        db.LogEntries.Add(new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Source = source,
            Message = message,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<LogEntry>> QueryAsync(
        OperationLogLevel? minLevel, string? source, DateTimeOffset? from, DateTimeOffset? to, int limit,
        CancellationToken ct = default)
    {
        var q = db.LogEntries.AsNoTracking().AsQueryable();

        if (minLevel is { } lvl)
            q = q.Where(e => e.Level >= lvl);
        if (!string.IsNullOrWhiteSpace(source))
            q = q.Where(e => e.Source == source);
        if (from is { } f)
            q = q.Where(e => e.Timestamp >= f);
        if (to is { } t)
            q = q.Where(e => e.Timestamp <= t);

        return await q
            .OrderByDescending(e => e.Id) // 最新在前（Id 单调，避免同刻 Timestamp 并列）
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        db.LogEntries.RemoveRange(db.LogEntries);
        await db.SaveChangesAsync(ct);
    }
}
