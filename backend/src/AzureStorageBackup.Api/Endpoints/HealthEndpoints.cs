using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // 存活探针：进程是否在跑。
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
            .WithName("Health")
            .WithTags("Health");

        // 就绪探针：依赖（Azure Storage）是否可连通。
        app.MapGet("/api/health/ready", async (IAzureStorageService storage, CancellationToken ct) =>
        {
            var storageOk = await storage.CanConnectAsync(ct);
            var body = new { status = storageOk ? "ready" : "degraded", storage = storageOk };
            return storageOk ? Results.Ok(body) : Results.Json(body, statusCode: 503);
        })
        .WithName("HealthReady")
        .WithTags("Health");

        return app;
    }
}
