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
    /// <param name="durable">
    /// null=按级别自动判定（Warning 及以上长存，其余短存）；true=长存（审计，保留至删备份/手工清）；false=短存(14 天)。
    /// </param>
    Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default, bool? durable = null);

    Task<IReadOnlyList<LogEntry>> QueryAsync(
        OperationLogLevel? minLevel, string? source, DateTimeOffset? from, DateTimeOffset? to, int limit,
        CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);

    /// <summary>删除某备份（account+container）的全部日志（删除备份时调用，PRD 3.6"长存日志保留至删除备份"）。
    /// 按 accountId 精确限定：<c>BackupConfig</c> 在 (AccountId, ContainerName) 上唯一索引，不同 account 可有
    /// 同名 container，绝不能用 container-only 匹配（会跨 account 误删审计日志）。</summary>
    Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default);

    /// <summary>手工清理：删除早于 cutoff 的**全部**日志（长存+短存，PRD 3.6"指定时间早于此全删"）。</summary>
    Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>短存日志保留清理（PRD 3.6）：删除超期(默认 14 天)的短存(ephemeral)日志；长存不受影响。</summary>
    Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default);
}

public sealed class OperationLogService(AppDbContext db) : IOperationLog
{
    public async Task AppendAsync(
        OperationLogLevel level, string source, string message, CancellationToken ct = default, bool? durable = null)
    {
        db.LogEntries.Add(new LogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Source = source,
            Message = message,
            Ephemeral = !(durable ?? level >= OperationLogLevel.Warning),
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

    public async Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default)
    {
        // 来源形如 "backup:{accountId}/{container}"、"check:{accountId}/{container}"、
        // "restore:{accountId}/{container}"、"schedule:{accountId}/{container}"（§5.3）。冒号在 accountId 前，
        // 故 ":{accountId}/{container}" 后缀精确定位该 account 的该 container，绝不匹配其他 account 的同名 container。
        // 项目尚未上线，格式可自由演进：改版前遗留的旧格式行 "{op}:{container}"（无 account 维度、无法安全归属
        // 某个 account）故意不做兜底匹配——宁可留下少量孤儿旧日志，也不能有跨 account 误删的风险。
        var suffix = $":{accountId}/{container}";
        await db.LogEntries
            .Where(e => e.Source.EndsWith(suffix))
            .ExecuteDeleteAsync(ct);
    }

    public async Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => await db.LogEntries.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync(ct);

    public async Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default)
    {
        var days = maxAgeDays is > 0 ? maxAgeDays.Value : 14; // 默认 14 天
        var cutoff = now.AddDays(-days);
        // 仅删短存(ephemeral)日志；长存(审计)日志保留至删除备份或手工清。
        await db.LogEntries.Where(e => e.Ephemeral && e.Timestamp < cutoff).ExecuteDeleteAsync(ct);
    }
}
