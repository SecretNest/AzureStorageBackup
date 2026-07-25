using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // 存活探针：进程是否在跑。
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
            .WithName("Health")
            .WithTags("Health")
            .AllowAnonymous();

        // 就绪探针：仅检查本地依赖——SQLite 可连、密钥环可用。不访问云端（运行期零云读）。
        app.MapGet("/api/health/ready", async (
            AppDbContext db, IKeyringHealth keyring, CancellationToken ct) =>
        {
            var dbOk = await db.Database.CanConnectAsync(ct);
            var keyringOk = keyring.Status == KeyringStatus.Healthy;
            var body = new
            {
                status = dbOk && keyringOk ? "ready" : "degraded",
                database = dbOk,
                keyring = keyringOk,
            };
            return dbOk && keyringOk ? Results.Ok(body) : Results.Json(body, statusCode: 503);
        })
        .WithName("HealthReady")
        .WithTags("Health")
        .AllowAnonymous();

        return app;
    }
}
