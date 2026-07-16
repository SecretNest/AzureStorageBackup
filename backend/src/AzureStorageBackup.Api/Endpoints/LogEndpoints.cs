using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>操作日志端点（PRD 5）：按等级/来源/时间过滤查询，可清空。</summary>
public static class LogEndpoints
{
    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/logs").WithTags("Logs");

        group.MapGet("/", async (
            IOperationLog log,
            OperationLogLevel? minLevel,
            string? source,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            CancellationToken ct) =>
        {
            var entries = await log.QueryAsync(minLevel, source, from, to, Math.Clamp(limit ?? 200, 1, 1000), ct);
            return Results.Ok(entries.Select(e => new LogEntryResponse(e.Id, e.Timestamp, e.Level, e.Source, e.Message)));
        });

        group.MapDelete("/", async (IOperationLog log, CancellationToken ct) =>
        {
            await log.ClearAsync(ct);
            return Results.NoContent();
        });

        return app;
    }
}

public record LogEntryResponse(int Id, DateTimeOffset Timestamp, OperationLogLevel Level, string Source, string Message);
