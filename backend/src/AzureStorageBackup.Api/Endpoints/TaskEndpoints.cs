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
        group.MapPost("/{id:int}/run", async (int id, IScheduledTaskService svc, TaskDispatcher dispatcher, IKeyringHealth keyring, CancellationToken ct) =>
        {
            // 手动触发计划的备份/检查/清理，与 /backup-configs/{id}/run 同性质，须同样闸门（设计 §3.3）。
            // 缺了这道闸门时：dispatcher 内部解密备份密码抛出，被 DispatchAsync 的 catch 吞成一条日志，
            // 端点照样推进 LastRunAt 并返回 200，UI 的「Run now」显示成功——什么都没做却报成功。
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
