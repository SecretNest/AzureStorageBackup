using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// 备份任务相关端点。骨架阶段提供增查，随需求补充执行/取消/进度等。
/// </summary>
public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backups").WithTags("Backups");

        group.MapGet("/", async (IBackupService service, CancellationToken ct) =>
        {
            var jobs = await service.ListJobsAsync(ct);
            return Results.Ok(jobs.Select(BackupJobResponse.From));
        })
        .WithName("ListBackupJobs");

        group.MapGet("/{id:int}", async (int id, IBackupService service, CancellationToken ct) =>
        {
            var job = await service.GetJobAsync(id, ct);
            return job is null ? Results.NotFound() : Results.Ok(BackupJobResponse.From(job));
        })
        .WithName("GetBackupJob");

        group.MapPost("/", async (CreateBackupJobRequest request, IBackupService service, CancellationToken ct) =>
        {
            var job = await service.CreateJobAsync(request, ct);
            return Results.CreatedAtRoute("GetBackupJob", new { id = job.Id }, BackupJobResponse.From(job));
        })
        .WithName("CreateBackupJob");

        return app;
    }
}
