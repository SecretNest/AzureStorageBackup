using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// 本地路径边界闸门（设计 §4）。越界返回 409 + <c>path_outside_root</c>。
/// 每次操作都校验，不只在设置时——配置可能来自旧版本、手工改库或 /import。
/// </summary>
public static class PathBoundaryGuard
{
    public static IResult? Blocked(PathBoundary boundary, string path) =>
        boundary.IsInside(path)
            ? null
            : Results.Json(
                new
                {
                    error = $"Path '{path}' is outside the configured root '{boundary.ConfiguredRoot}'.",
                    code = "path_outside_root",
                },
                statusCode: StatusCodes.Status409Conflict);
}
