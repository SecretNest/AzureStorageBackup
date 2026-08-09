using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness probe: is the process running.
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
            .WithName("Health")
            .WithTags("Health")
            .AllowAnonymous();

        // Readiness probe: local dependencies only — SQLite connects and the key ring works. No cloud access (zero cloud reads at run time).
        app.MapGet("/api/health/ready", async (
            AppDbContext db, IKeyringHealth keyring, AuthGate gate, HttpContext ctx, CancellationToken ct) =>
        {
            var dbOk = await db.Database.CanConnectAsync(ct);
            var keyringOk = keyring.Status == KeyringStatus.Healthy;
            var ready = dbOk && keyringOk;
            var status = ready ? "ready" : "degraded";

            // The probe must be reachable anonymously (or the orchestrator judges the container unhealthy
            // and restarts it in a loop), but an anonymous caller should get nothing but the status code —
            // the individual booleans would tell a stranger "this instance is in keyring recovery mode".
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
