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
            AppDbContext db, IKeyringHealth keyring, AuthGate gate, HttpContext ctx, CancellationToken ct) =>
        {
            var dbOk = await db.Database.CanConnectAsync(ct);
            var keyringOk = keyring.Status == KeyringStatus.Healthy;
            var ready = dbOk && keyringOk;
            var status = ready ? "ready" : "degraded";

            // 探针必须匿名可达（否则编排层判定容器不健康并反复重启），但匿名调用者只该拿到
            // 状态码——逐项布尔会告诉陌生人「这台正处于密钥环恢复模式」。
            var detailed = !gate.Required || ctx.User.Identity?.IsAuthenticated == true;
            object body = detailed
                ? new { status, database = dbOk, keyring = keyringOk }
                : new { status };

            return ready ? Results.Ok(body) : Results.Json(body, statusCode: 503);
        })
        .WithName("HealthReady")
        .WithTags("Health")
        .AllowAnonymous();

        return app;
    }
}
