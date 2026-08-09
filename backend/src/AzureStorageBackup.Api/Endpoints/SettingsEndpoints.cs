using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>Global settings endpoints (PRD 3/4): defaults for new backups, plus log retention and concurrency. A singleton.</summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");

        group.MapGet("/", async (IGlobalSettingsService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(ct)));

        group.MapPut("/", async (GlobalSettings body, IGlobalSettingsService svc, CancellationToken ct) =>
            Results.Ok(await svc.UpsertAsync(body, ct)));

        return app;
    }
}
