using System.Reflection;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// 系统信息端点：路径（PRD 第 6 章，供用户做 docker 卷映射）与版本（第 7 章）。
/// </summary>
public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/paths", (IConfiguration config) =>
        {
            var keysPath = config["DataProtection:KeysPath"];
            if (string.IsNullOrWhiteSpace(keysPath))
                keysPath = "keys";

            var dataPath = ParseDataSource(config.GetConnectionString("Sqlite")) ?? "data/app.db";

            var tempPath = config["Paths:Temp"];
            if (string.IsNullOrWhiteSpace(tempPath))
                tempPath = "temp";

            return Results.Ok(new Dictionary<string, string>
            {
                ["keysPath"] = SafeFullPath(keysPath),
                ["dataPath"] = SafeFullPath(dataPath),
                ["tempPath"] = SafeFullPath(tempPath),
            });
        })
        .WithTags("System");

        app.MapGet("/api/system/version", () =>
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            return Results.Ok(new Dictionary<string, string> { ["version"] = version });
        })
        .WithTags("System");

        return app;
    }

    private static string? ParseDataSource(string? conn)
    {
        if (string.IsNullOrWhiteSpace(conn))
            return null;
        try { return new SqliteConnectionStringBuilder(conn).DataSource; }
        catch { return null; }
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
