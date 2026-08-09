using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>Maps events to log levels (the failure kinds → Error).</summary>
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

/// <summary>Recording and querying of the operation log (PRD 5). Filter by level/source/time, and clear.</summary>
public interface IOperationLog
{
    /// <param name="durable">
    /// null = decided automatically from the level (Warning and above durable, the rest ephemeral); true = durable (audit, kept until the backup is deleted or manually purged); false = ephemeral (14 days).
    /// </param>
    Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default, bool? durable = null);

    Task<IReadOnlyList<LogEntry>> QueryAsync(
        OperationLogLevel? minLevel, string? source, DateTimeOffset? from, DateTimeOffset? to, int limit,
        CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);

    /// <summary>Deletes every log of one backup (account+container); called when a backup is deleted (PRD 3.6 "durable logs are kept until the backup is deleted").
    /// Scoped to accountId exactly: <c>BackupConfig</c> is uniquely indexed on (AccountId, ContainerName), so different accounts may own
    /// containers of the same name, and a container-only match is out of the question (it would delete another account's audit logs).</summary>
    Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default);

    /// <summary>Manual purge: deletes **all** logs older than cutoff (durable + ephemeral, PRD 3.6 "everything earlier than the given time is deleted").</summary>
    Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>Ephemeral log retention cleanup (PRD 3.6): deletes ephemeral logs past their age limit (14 days by default); durable logs are untouched.</summary>
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
            .OrderByDescending(e => e.Id) // newest first (Id is monotonic, avoiding ties between identical Timestamps)
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
        // Sources look like "backup:{accountId}/{container}", "check:{accountId}/{container}",
        // "restore:{accountId}/{container}", "schedule:{accountId}/{container}" (§5.3). The colon sits ahead of accountId,
        // so the ":{accountId}/{container}" suffix pins down exactly that container of that account and never matches a same-named container of another account.
        // The project is not live yet, so the format may evolve freely: legacy rows from before the change, "{op}:{container}" (no account dimension, impossible
        // to attribute safely to any account), deliberately get no fallback match — a few orphaned old logs beat any risk of deleting across accounts.
        var suffix = $":{accountId}/{container}";
        await db.LogEntries
            .Where(e => e.Source.EndsWith(suffix))
            .ExecuteDeleteAsync(ct);
    }

    public async Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
        => await db.LogEntries.Where(e => e.Timestamp < cutoff).ExecuteDeleteAsync(ct);

    public async Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default)
    {
        var days = maxAgeDays is > 0 ? maxAgeDays.Value : 14; // 14 days by default
        var cutoff = now.AddDays(-days);
        // Only ephemeral logs are deleted; durable (audit) logs stay until the backup is deleted or manually purged.
        await db.LogEntries.Where(e => e.Ephemeral && e.Timestamp < cutoff).ExecuteDeleteAsync(ct);
    }
}
