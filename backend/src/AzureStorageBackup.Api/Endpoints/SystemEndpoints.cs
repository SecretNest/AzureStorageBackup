using System.Reflection;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// System information endpoints: paths (PRD chapter 6, so the user can set up docker volume mappings) and version (chapter 7).
/// </summary>
public static class SystemEndpoints
{
    /// <summary>Cap on the entries a single browse returns. Beyond it we truncate and say so in the response — never silently hand back fewer.</summary>
    private const int MaxBrowseEntries = 2000;

    private const int DefaultPageSize = 500;
    private const int MaxPageSize = 2000;

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/paths", (IConfiguration config) =>
        {
            var keysPath = config["DataProtection:KeysPath"];
            if (string.IsNullOrWhiteSpace(keysPath))
                keysPath = "keys";

            var dataPath = ParseDataSource(config.GetConnectionString("Sqlite")) ?? "data/app.db";

            // The temp area the backup engine actually uses (for docker volume mapping, PRD 6).
            var tempPath = config["Backup:TempPath"];
            if (string.IsNullOrWhiteSpace(tempPath))
                tempPath = Path.Combine(Path.GetTempPath(), "azurestoragebackup");

            return Results.Ok(new Dictionary<string, string>
            {
                ["keysPath"] = SafeFullPath(keysPath),
                ["dataPath"] = SafeFullPath(dataPath),
                ["tempPath"] = SafeFullPath(tempPath),
                ["compressTempPath"] = SafeFullPath(Path.Combine(tempPath, "compress")),
                ["stagedTempPath"] = SafeFullPath(Path.Combine(tempPath, "staged")),
                ["restoreTempPath"] = SafeFullPath(Path.Combine(tempPath, "restore")),
                ["verboseLogPath"] = SafeFullPath(Path.Combine(tempPath, "verbose-logs")),
            });
        })
        .WithTags("System");

        app.MapGet("/api/system/version", () =>
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            return Results.Ok(new Dictionary<string, string> { ["version"] = version });
        })
        .WithTags("System");

        // Keyring status and the pending-reset counts (design §3.3), used by the top banner and the recovery checklist.
        app.MapGet("/api/system/keyring", async (
            IKeyringHealth keyring, AppDbContext db, IEncryptionService encryption, CancellationToken ct) =>
        {
            if (keyring.Status == KeyringStatus.Healthy)
                return Results.Ok(new
                {
                    status = nameof(KeyringStatus.Healthy),
                    accountsPending = 0,
                    backupConfigsPending = 0,
                });

            // Every secret has to be probed one by one; the global status cannot be applied wholesale: the recovery flow passes
            // through an intermediate state of "all accounts reset, backup passwords still the old ciphertext" where the status
            // is still Lost but accountsPending must drop to zero, or the frontend's ordering dependency (backup password reset
            // stays disabled while accounts are non-zero) deadlocks the recovery flow (see SecretAvailability).
            var accounts = await db.Accounts.AsNoTracking()
                .Select(a => new { a.AccountKeyProtected, a.ProxyPasswordProtected }).ToListAsync(ct);
            var backupPasswords = await db.BackupConfigs.AsNoTracking()
                .Where(c => c.PasswordProtected != null && c.PasswordProtected != "")
                .Select(c => c.PasswordProtected!).ToListAsync(ct);

            return Results.Ok(new
            {
                status = nameof(KeyringStatus.Lost),
                accountsPending = accounts.Count(a =>
                    SecretAvailability.Unreadable(encryption, a.AccountKeyProtected)
                    || SecretAvailability.Unreadable(encryption, a.ProxyPasswordProtected)),
                backupConfigsPending = backupPasswords.Count(p => SecretAvailability.Unreadable(encryption, p)),
            });
        })
        .WithTags("System");

        // Local directory browsing (design §6). Lazy: only the immediate children are returned.
        app.MapGet("/api/system/browse", (string? path, int? offset, int? limit, PathBoundary boundary) =>
        {
            var start = string.IsNullOrWhiteSpace(path) ? DefaultBrowseStart(boundary) : path!;

            if (PathBoundaryGuard.Blocked(boundary, start) is { } outside)
                return outside;

            if (!Directory.Exists(start))
                return Results.NotFound(new { error = $"Directory '{start}' does not exist." });

            // Paged requests (offset or limit passed) share the same code as the old one-shot request; the only difference is the slice.
            // Old callers (PathBrowser) pass neither parameter and behave exactly as before: at most MaxBrowseEntries entries,
            // Truncated beyond that.
            var paged = offset is not null || limit is not null;
            var skip = Math.Max(0, offset ?? 0);
            var take = paged ? Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize) : MaxBrowseEntries;

            // Directories and files are enumerated separately: isDir comes for free that way, with no stat per entry. Collect all
            // the names first (200,000 strings is acceptable), sort them, and only then read attributes for the **current page** —
            // the earlier version collected first and sorted afterwards, with truncation happening during collection, so the order
            // after truncation was random and paging was impossible.
            List<string> dirs;
            List<string> files;
            try
            {
                dirs = Directory.EnumerateDirectories(start).ToList();
                files = Directory.EnumerateFiles(start).ToList();
            }
            // DirectoryNotFoundException derives from IOException and must be caught separately, ahead of the broader branch,
            // or a directory deleted in the TOCTOU window between Directory.Exists and here gets reported as 403 instead of 404.
            catch (DirectoryNotFoundException)
            {
                return Results.NotFound(new { error = $"Directory '{start}' does not exist." });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // The directory is unreadable (not enough permissions) or the read failed (mount point dropped out): both are
                // routine with docker volume mounts, so give a clean 403 rather than a bare 500.
                return Results.Json(
                    new { error = $"Directory '{start}' could not be read." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            dirs.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
            files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));

            var total = dirs.Count + files.Count;
            var ordered = dirs.Select(d => (Full: d, IsDir: true))
                .Concat(files.Select(f => (Full: f, IsDir: false)))
                .Skip(skip)
                .Take(take)
                .ToList();

            var truncated = !paged && total > MaxBrowseEntries;
            var entries = new List<BrowseEntry>(ordered.Count);
            var skipped = 0;

            foreach (var (full, isDir) in ordered)
            {
                try
                {
                    var info = new FileInfo(full);
                    entries.Add(new BrowseEntry(
                        Path.GetFileName(full),
                        // Absolute path, usable as-is as the next `?path=` or as a localRoot.
                        full,
                        isDir,
                        // A symlink's Length is the lstat value (the bytes of the link itself), not the size of the target file.
                        isDir ? null : info.Length,
                        info.LastWriteTimeUtc,
                        // A symlink may point outside the root: return it but mark it, and the frontend greys it out as unclickable.
                        !boundary.IsInside(full)));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // A per-entry stat failure (directory mode r--: readdir works, stat on the children does not) skips that entry,
                    // but it has to be counted and returned with the response — skipping silently renders such a directory as "empty" and the user cannot tell the difference.
                    skipped++;
                }
            }

            // The parent stops at the root. Path.GetFullPath must not be used to fold `..` lexically — PathBoundary
            // deliberately avoids that too (see PathBoundary.ResolveReal's docs): if start goes through a symlink, the
            // lexically folded parent and the real filesystem parent disagree, quietly teleporting the user into the wrong
            // directory (with <root>/link -> <root>/a/b, lexical folding gives <root>, while the real parent is <root>/a).
            // ResolveReal is the source of real paths that agrees with IsInside; but a real path may carry the RealRoot
            // prefix (when the root itself is a symlink) and must not be shown to the user directly, so once the real parent
            // is computed, run it through ToDisplayPath to get back to the ConfiguredRoot view — never leak RealRoot into the response.
            var real = PathBoundary.ResolveReal(start);
            var realParent = real is null
                ? null
                : Path.GetDirectoryName(real.TrimEnd(Path.DirectorySeparatorChar));
            var parent = realParent is not null && boundary.IsInside(realParent)
                ? boundary.ToDisplayPath(realParent)
                : null;

            return Results.Ok(new BrowseResponse(start, parent, truncated, skipped, total, skip, entries));
        })
        .WithTags("System");

        return app;
    }

    /// <summary>
    /// The default start when no <c>path</c> is passed: begin at the root if one is configured, otherwise at the filesystem root.
    /// <see cref="PathBoundary.ConfiguredRoot"/> is always absolute (a relative configuration is normalised in the
    /// <see cref="PathBoundary"/> constructor), so it can be used as start directly without running into
    /// <see cref="PathBoundary.IsInside"/>'s rule that only absolute input is accepted.
    /// </summary>
    private static string DefaultBrowseStart(PathBoundary boundary) =>
        boundary.ConfiguredRoot ?? Path.GetPathRoot(Path.GetFullPath("/")) ?? "/";

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

/// <summary>
/// The browse result. A null Parent means we are already at the root (or the boundary) and cannot go up any further.
/// <para><c>Skipped</c>: the number of children left out because their attributes could not be read (the typical cause is a
/// directory with mode <c>r--</c> — readdir works, stat on the children does not). Same purpose as <c>Truncated</c>: if we handed back less, we have to say so.</para>
/// <para><c>Total</c>: the total number of children in the directory (unaffected by paging); <c>Offset</c>: where this page starts.
/// A paged request never sets <c>Truncated</c> — that means "there is more but you cannot get at it", and paging can get at it.</para>
/// </summary>
public record BrowseResponse(
    string Path, string? Parent, bool Truncated, int Skipped, int Total, int Offset,
    IReadOnlyList<BrowseEntry> Entries);

/// <summary>
/// OutsideRoot=true means the entry (usually a symlink pointing outside the root) cannot be selected, but it is still listed so the user is not left puzzled.
/// <para>
/// F8 (for whoever implements the picker UI task): <see cref="Length"/> is <c>FileInfo.Length</c> underneath, which for a
/// symlink is the lstat value — the length of the target path string stored in the link itself (usually a few dozen bytes),
/// not the real size of the target file. A symlink pointing at a 4 GB file reports ~30 bytes. The direction is safe (it does
/// not leak the size of the target's contents), but the UI must not display or sort this field as the target file's real size.
/// </para>
/// </summary>
public record BrowseEntry(
    string Name, string FullPath, bool IsDirectory,
    long? Length, DateTimeOffset ModifiedAt, bool OutsideRoot);
