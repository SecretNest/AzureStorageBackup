using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>The global notification configuration endpoints (PRD 4.2). A singleton configuration: GET reads, PUT stores, and test sends one using the supplied configuration.</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications");

        group.MapGet("/", async (INotificationConfigService svc, CancellationToken ct) =>
            Results.Ok(NotificationResponse.From(await svc.GetAsync(ct))));

        group.MapPut("/", async (NotificationRequest req, INotificationConfigService svc, CancellationToken ct) =>
            Results.Ok(NotificationResponse.From(await svc.UpsertAsync(req.ToConfig(), ct))));

        // Send one using the supplied (unsaved) configuration
        group.MapPost("/test", async (NotificationRequest req, INotificationSender sender, CancellationToken ct) =>
        {
            try
            {
                await sender.SendAsync(req.ToConfig(), "Test notification", "This is a test from AzureStorageBackup.", ct);
                return Results.Ok(new { success = true, error = (string?)null });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { success = false, error = ex.Message });
            }
        });

        return app;
    }
}
