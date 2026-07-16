using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>备份配置管理端点（PRD §11 向导产物的持久化）。响应不含密码；更新时空密码保留原值。</summary>
public static class BackupConfigEndpoints
{
    public static IEndpointRouteBuilder MapBackupConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backup-configs").WithTags("BackupConfigs");

        group.MapGet("/", async (IBackupConfigService svc, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ct);
            return Results.Ok(list.Select(BackupConfigResponse.From));
        });

        group.MapGet("/{id:int}", async (int id, IBackupConfigService svc, CancellationToken ct) =>
        {
            var c = await svc.GetAsync(id, ct);
            return c is null ? Results.NotFound() : Results.Ok(BackupConfigResponse.From(c));
        })
        .WithName("GetBackupConfig");

        group.MapPost("/", async (BackupConfigRequest req, IBackupConfigService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.LocalRoot))
                return Results.BadRequest(new { error = "LocalRoot is required." });
            if (string.IsNullOrWhiteSpace(req.ContainerName))
                return Results.BadRequest(new { error = "ContainerName is required." });

            var created = await svc.CreateAsync(req.ToConfig(), ct);
            return Results.CreatedAtRoute("GetBackupConfig", new { id = created.Id }, BackupConfigResponse.From(created));
        });

        group.MapPut("/{id:int}", async (int id, BackupConfigRequest req, IBackupConfigService svc, CancellationToken ct) =>
        {
            var existing = await svc.GetAsync(id, ct);
            if (existing is null)
                return Results.NotFound();

            var update = req.ToConfig();
            // 空密码表示保留原值（不清除加密）
            if (string.IsNullOrEmpty(req.Password))
                update.Password = existing.Password;

            var result = await svc.UpdateAsync(id, update, ct);
            return result is null ? Results.NotFound() : Results.Ok(BackupConfigResponse.From(result));
        });

        group.MapDelete("/{id:int}", async (int id, IBackupConfigService svc, CancellationToken ct) =>
        {
            var ok = await svc.DeleteAsync(id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // 启动一次备份（后台运行，进度轮询）
        group.MapPost("/{id:int}/run", async (int id, IBackupConfigService svc, BackupRunner runner, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
                return Results.NotFound();

            var state = runner.Start(id);
            return Results.Accepted($"/api/backup-configs/{id}/run", BackupRunResponse.From(state));
        });

        // 查询运行进度/状态
        group.MapGet("/{id:int}/run", (int id, BackupRunner runner) =>
        {
            var state = runner.Get(id);
            return state is null ? Results.NotFound() : Results.Ok(BackupRunResponse.From(state));
        });

        return app;
    }
}
