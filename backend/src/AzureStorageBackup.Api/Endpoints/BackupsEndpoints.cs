using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>已发现备份的列表端点（PRD 2.1）。手动触发，不自动刷新。</summary>
public static class BackupsEndpoints
{
    public static IEndpointRouteBuilder MapBackupsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/backups", async (IBackupInventoryService inventory, CancellationToken ct) =>
            Results.Ok(await inventory.ListAsync(ct)))
            .WithTags("Backups");

        return app;
    }
}
