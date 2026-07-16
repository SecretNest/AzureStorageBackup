using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>全局设置端点（PRD 3/4）：新建备份默认值 + 日志保留/并发。单例。</summary>
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
