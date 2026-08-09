using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>The listing endpoint for discovered backups (PRD 2.1). Triggered manually, never refreshed automatically.</summary>
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
