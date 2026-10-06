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

        // Settings → About: the whole Settings area as one file. Secrets only on request, and then only while the
        // keyring can read them — an export that said includesSecrets: true with every key null would be a lie.
        group.MapGet("/export", async (bool? includeSecrets, SettingsTransfer transfer, IKeyringHealth keyring,
            HttpContext http, CancellationToken ct) =>
        {
            var withSecrets = includeSecrets ?? false;
            if (withSecrets && KeyringGuard.Blocked(keyring) is { } blocked)
                return blocked;

            var doc = await transfer.ExportAsync(withSecrets, ct);
            var name = $"asb-settings-{doc.ExportedAt:yyyyMMdd-HHmm}.json";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
            return Results.Json(doc);
        });

        group.MapPost("/import/preview", async (SettingsDocument doc, SettingsTransfer transfer, CancellationToken ct) =>
        {
            try { return Results.Ok(await transfer.PlanAsync(doc, ct)); }
            catch (SettingsImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        group.MapPost("/import", async (SettingsDocument doc, SettingsTransfer transfer, CancellationToken ct) =>
        {
            try { return Results.Ok(await transfer.ImportAsync(doc, ct)); }
            catch (SettingsImportException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        return app;
    }
}
