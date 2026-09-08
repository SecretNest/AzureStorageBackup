using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>Global settings endpoints (PRD 3/4): one row, read whole, written as two halves — Backup defaults and Performance, one per settings page.</summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings").WithTags("Settings");

        group.MapGet("/", async (IGlobalSettingsService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetAsync(ct)));

        // No whole-object PUT: one page saving all fields is how the other page's unsaved (or freshly saved) half got
        // overwritten. Each page has a resource of its own and writes only that.
        group.MapGet("/defaults", async (IGlobalSettingsService svc, CancellationToken ct) =>
            Results.Ok(BackupDefaultsSettings.From(await svc.GetAsync(ct))));
        group.MapPut("/defaults", async (BackupDefaultsSettings body, IGlobalSettingsService svc, CancellationToken ct) =>
            Results.Ok(BackupDefaultsSettings.From(await svc.UpsertDefaultsAsync(body, ct))));

        group.MapGet("/performance", async (IGlobalSettingsService svc, CancellationToken ct) =>
            Results.Ok(PerformanceSettings.From(await svc.GetAsync(ct))));
        group.MapPut("/performance", async (PerformanceSettings body, IGlobalSettingsService svc, CancellationToken ct) =>
            Results.Ok(PerformanceSettings.From(await svc.UpsertPerformanceAsync(body, ct))));

        return app;
    }
}
