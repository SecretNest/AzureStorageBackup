using System.Reflection;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// 系统信息端点：路径（PRD 第 6 章，供用户做 docker 卷映射）与版本（第 7 章）。
/// </summary>
public static class SystemEndpoints
{
    /// <summary>单次浏览返回的条目上限。超出即截断并在响应里标明，不静默少给。</summary>
    private const int MaxBrowseEntries = 2000;

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/paths", (IConfiguration config) =>
        {
            var keysPath = config["DataProtection:KeysPath"];
            if (string.IsNullOrWhiteSpace(keysPath))
                keysPath = "keys";

            var dataPath = ParseDataSource(config.GetConnectionString("Sqlite")) ?? "data/app.db";

            // 备份引擎实际使用的临时区（供 docker 卷映射，PRD 6）。
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

        // 密钥环状态与待重设计数（设计 §3.3），供顶部横幅与恢复清单使用。
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

            // 必须逐条试解，不能按全局状态一刀切：恢复流程会经过「账户已全部重设、备份密码仍是旧密文」
            // 的中间态，此时状态仍是 Lost，但 accountsPending 必须归零，否则前端的顺序依赖
            // （账户未清零则禁用备份密码重设）会把恢复流程锁死（见 SecretAvailability）。
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

        // 本地目录浏览（设计 §6）。懒加载，只返回直接子项。
        app.MapGet("/api/system/browse", (string? path, PathBoundary boundary) =>
        {
            var start = string.IsNullOrWhiteSpace(path)
                ? boundary.ConfiguredRoot ?? Path.GetPathRoot(Path.GetFullPath("/")) ?? "/"
                : path;

            if (PathBoundaryGuard.Blocked(boundary, start) is { } outside)
                return outside;

            if (!Directory.Exists(start))
                return Results.NotFound(new { error = $"Directory '{start}' does not exist." });

            var entries = new List<BrowseEntry>();
            var truncated = false;

            foreach (var item in Directory.EnumerateFileSystemEntries(start))
            {
                if (entries.Count >= MaxBrowseEntries)
                {
                    truncated = true;
                    break;
                }

                try
                {
                    var info = new FileInfo(item);
                    var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(new BrowseEntry(
                        Path.GetFileName(item),
                        item,
                        isDir,
                        isDir ? null : info.Length,
                        info.LastWriteTimeUtc,
                        // 软链可能指向根外：返回但标记，前端灰显不可点
                        !boundary.IsInside(item)));
                }
                catch (Exception)
                {
                    // 单项读取失败（权限不足等）跳过该项，不让整个请求失败
                }
            }

            // 目录在前，各自按名称排序
            entries.Sort((a, b) => a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // 上级到根为止
            var parent = Path.GetDirectoryName(Path.GetFullPath(start).TrimEnd(Path.DirectorySeparatorChar));
            if (parent is not null && !boundary.IsInside(parent))
                parent = null;

            return Results.Ok(new BrowseResponse(start, parent, truncated, entries));
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

/// <summary>浏览结果。Parent 为 null 表示已在根（或边界）处，不能再往上。</summary>
public record BrowseResponse(
    string Path, string? Parent, bool Truncated, IReadOnlyList<BrowseEntry> Entries);

/// <summary>OutsideRoot=true 表示该项（通常是指向根外的软链）不可选，但仍列出以免用户困惑。</summary>
public record BrowseEntry(
    string Name, string FullPath, bool IsDirectory,
    long? Length, DateTimeOffset ModifiedAt, bool OutsideRoot);
