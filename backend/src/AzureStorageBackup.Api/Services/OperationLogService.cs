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

    /// <summary>保留清理（PRD 3.6）：删除超期或超出最大条数的记录；两个上限"达到之一即删"。</summary>
    Task TrimAsync(int? maxEntries, int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default);
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

    public async Task TrimAsync(int? maxEntries, int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default)
    {
        if (maxAgeDays is int days and > 0)
        {
            var cutoff = now.AddDays(-days);
            await db.LogEntries.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        }

        if (maxEntries is int max and > 0)
        {
            // 保留最新 max 条：删除 Id 小于"最新 max 条中最小 Id"的记录。
            var minKeepId = await db.LogEntries
                .OrderByDescending(e => e.Id)
                .Take(max)
                .MinAsync(e => (int?)e.Id, ct) ?? 0;
            await db.LogEntries.Where(e => e.Id < minKeepId).ExecuteDeleteAsync(ct);
        }
    }
}
