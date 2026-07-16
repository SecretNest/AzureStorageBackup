using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>全局通知配置端点（PRD 4.2）。单例配置：GET 读、PUT 存、test 用给定配置试发一条。</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications");

        group.MapGet("/", async (INotificationConfigService svc, CancellationToken ct) =>
            Results.Ok(NotificationResponse.From(await svc.GetAsync(ct))));

        group.MapPut("/", async (NotificationRequest req, INotificationConfigService svc, CancellationToken ct) =>
            Results.Ok(NotificationResponse.From(await svc.UpsertAsync(req.ToConfig(), ct))));

        // 用给定（未保存）配置试发一条
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
