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
        app.MapGet("/api/system/browse", (string? path, int? offset, int? limit, PathBoundary boundary) =>
        {
            var start = string.IsNullOrWhiteSpace(path) ? DefaultBrowseStart(boundary) : path!;

            if (PathBoundaryGuard.Blocked(boundary, start) is { } outside)
                return outside;

            if (!Directory.Exists(start))
                return Results.NotFound(new { error = $"Directory '{start}' does not exist." });

            // 分页请求（传了 offset 或 limit）与老的一次性请求走同一段代码，区别只在切片。
            // 老调用方（PathBrowser）不传这两个参数，行为与从前完全一致：最多 MaxBrowseEntries 项，
            // 超出则 Truncated。
            var paged = offset is not null || limit is not null;
            var skip = Math.Max(0, offset ?? 0);
            var take = paged ? Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize) : MaxBrowseEntries;

            // 目录与文件分开枚举：isDir 因此免费得到，不必对每一项 stat。名字先全部收上来
            // （20 万个字符串是可以接受的），排完序再只对**当前页**取属性——原先那版先收集
            // 再排序，截断发生在收集阶段，于是截断之后的顺序是随机的，也就没法分页。
            List<string> dirs;
            List<string> files;
            try
            {
                dirs = Directory.EnumerateDirectories(start).ToList();
                files = Directory.EnumerateFiles(start).ToList();
            }
            // DirectoryNotFoundException 派生自 IOException，必须先于更宽的分支单独捕获，
            // 否则 Directory.Exists 与这里之间的 TOCTOU 窗口里目录被删会报成 403 而不是 404。
            catch (DirectoryNotFoundException)
            {
                return Results.NotFound(new { error = $"Directory '{start}' does not exist." });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 目录不可读（权限不足）或读取失败（挂载点掉线）：docker 卷挂载场景下是常态，
                // 给一个干净的 403 而不是裸 500。
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
                        // 绝对路径，原样可作为下一次 `?path=` 或 localRoot 送回。
                        full,
                        isDir,
                        // 软链的 Length 是 lstat 值（链接自身的字节数），不是目标文件的大小。
                        isDir ? null : info.Length,
                        info.LastWriteTimeUtc,
                        // 软链可能指向根外：返回但标记，前端灰显不可点。
                        !boundary.IsInside(full)));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // 单项 stat 失败（目录 mode 为 r--：可 readdir、不可 stat 子项）跳过该项，
                    // 但要计数并随响应返回——静默跳过会让这种目录渲染成「空目录」，用户看不出差别。
                    skipped++;
                }
            }

            // 上级到根为止。不能用 Path.GetFullPath 词法折叠 `..`——PathBoundary 也刻意
            // 避开这一点（见 PathBoundary.ResolveReal 的文档）：若 start 途经符号链接，
            // 词法折叠算出的上级和真实文件系统上级会不一致，把用户悄悄传送到错误的目录
            // （<root>/link -> <root>/a/b 时，词法折叠给出 <root>，真实上级是 <root>/a）。
            // ResolveReal 才是与 IsInside 一致的真实路径来源；但真实路径可能带着 RealRoot
            // 前缀（根自身是软链时），不能直接展示给用户，所以算出真实上级后要经
            // ToDisplayPath 换回 ConfiguredRoot 视角，绝不把 RealRoot 泄漏到响应里。
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
    /// 未传 <c>path</c> 时的默认起点：配了根就从根开始，否则从文件系统根开始。
    /// <see cref="PathBoundary.ConfiguredRoot"/> 恒为绝对路径（相对配置在
    /// <see cref="PathBoundary"/> 构造时就已归一化），所以可以直接当 start 用，
    /// 不会撞上 <see cref="PathBoundary.IsInside"/> 只认绝对输入这条规则。
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
/// 浏览结果。Parent 为 null 表示已在根（或边界）处，不能再往上。
/// <para><c>Skipped</c>：读不出属性因而未列出的子项数（典型成因是目录 mode 为 <c>r--</c>——
/// 可 readdir、不可 stat 子项）。与 <c>Truncated</c> 同一用途：少给了东西必须说出来。</para>
/// <para><c>Total</c>：该目录的子项总数（不受分页影响）；<c>Offset</c>：本页起始位置。
/// 分页请求恒不置 <c>Truncated</c>——它的意思是「还有东西但拿不到了」，而分页拿得到。</para>
/// </summary>
public record BrowseResponse(
    string Path, string? Parent, bool Truncated, int Skipped, int Total, int Offset,
    IReadOnlyList<BrowseEntry> Entries);

/// <summary>
/// OutsideRoot=true 表示该项（通常是指向根外的软链）不可选，但仍列出以免用户困惑。
/// <para>
/// F8（给 picker UI 任务的实现者）：<see cref="Length"/> 底层是 <c>FileInfo.Length</c>，
/// 对符号链接是 lstat 值——链接自身存的目标路径字符串长度（通常几十字节），不是目标
/// 文件的真实大小。一个指向 4 GB 文件的软链会报 ~30 字节。方向是安全的（不会把目标
/// 内容大小泄漏出去），但 UI 不能把这个字段当成目标文件的真实大小来显示/排序。
/// </para>
/// </summary>
public record BrowseEntry(
    string Name, string FullPath, bool IsDirectory,
    long? Length, DateTimeOffset ModifiedAt, bool OutsideRoot);
