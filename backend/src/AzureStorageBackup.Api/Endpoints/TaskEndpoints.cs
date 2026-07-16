using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>计划任务管理端点（PRD 2.3）。调度执行在 M6。</summary>
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

        // 立即执行一次（"Run now"；调度器复用同一 dispatcher）
        group.MapPost("/{id:int}/run", async (int id, IScheduledTaskService svc, TaskDispatcher dispatcher, CancellationToken ct) =>
        {
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
