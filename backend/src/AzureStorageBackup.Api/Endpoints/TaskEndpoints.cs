using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>Scheduled task management endpoints (PRD 2.3). Execution scheduling is M6.</summary>
public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tasks").WithTags("Tasks");

        group.MapGet("/", async (IScheduledTaskService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListAsync(ct)).Select(TaskResponse.From)));

        group.MapGet("/{id:int}", async (int id, IScheduledTaskService svc, CancellationToken ct) =>
        {
            var t = await svc.GetAsync(id, ct);
            return t is null ? Results.NotFound() : Results.Ok(TaskResponse.From(t));
        })
        .WithName("GetTask");

        group.MapPost("/", async (TaskRequest req, IScheduledTaskService svc, CancellationToken ct) =>
        {
            try
            {
                var created = await svc.CreateAsync(req.ToEntity(), ct);
                return Results.CreatedAtRoute("GetTask", new { id = created.Id }, TaskResponse.From(created));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPut("/{id:int}", async (int id, TaskRequest req, IScheduledTaskService svc, CancellationToken ct) =>
        {
            try
            {
                var updated = await svc.UpdateAsync(id, req.ToEntity(), ct);
                return updated is null ? Results.NotFound() : Results.Ok(TaskResponse.From(updated));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/{id:int}", async (int id, IScheduledTaskService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // Run once immediately ("Run now"; the scheduler reuses the same dispatcher)
        group.MapPost("/{id:int}/run", async (int id, IScheduledTaskService svc, TaskDispatcher dispatcher, IKeyringHealth keyring, CancellationToken ct) =>
        {
            // Manually triggering a scheduled backup, check or cleanup is the same kind of action as
            // /backup-configs/{id}/run and needs the same gate (design §3.3).
            // Without it: decrypting the backup password inside the dispatcher throws, DispatchAsync's catch
            // reduces it to a log line, the endpoint advances LastRunAt and returns 200 anyway, and the UI's
            // "Run now" reports success — success for having done nothing at all.
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var task = await svc.GetAsync(id, ct);
            if (task is null)
                return Results.NotFound();

            await dispatcher.DispatchAsync(task, ct);
            await svc.SetLastRunAsync(id, DateTimeOffset.UtcNow, ct);
            return Results.Ok(TaskResponse.From((await svc.GetAsync(id, ct))!));
        });

        return app;
    }
}
