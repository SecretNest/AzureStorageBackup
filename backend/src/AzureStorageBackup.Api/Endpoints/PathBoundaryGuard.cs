using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// The local path boundary gate (design §4). Out of bounds returns 409 with <c>path_outside_root</c>.
/// Validated on every operation, not only when settings are saved — a configuration may come from an
/// older version, a hand-edited database, or /import.
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
