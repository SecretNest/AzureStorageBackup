using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>Operation log endpoints (PRD 5): query with level, source and time filters, and clear.</summary>
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

        // With `before` given, delete every log older than that time (durable audit entries included, the manual cleanup of PRD 3.6); otherwise clear everything.
        group.MapDelete("/", async (IOperationLog log, DateTimeOffset? before, CancellationToken ct) =>
        {
            if (before is { } cutoff)
                await log.PurgeBeforeAsync(cutoff, ct);
            else
                await log.ClearAsync(ct);
            return Results.NoContent();
        });

        return app;
    }
}

public record LogEntryResponse(int Id, DateTimeOffset Timestamp, OperationLogLevel Level, string Source, string Message);
