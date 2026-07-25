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
            var start = string.IsNullOrWhiteSpace(path) ? DefaultBrowseStart(boundary) : path!;

            if (PathBoundaryGuard.Blocked(boundary, start) is { } outside)
                return outside;

            if (!Directory.Exists(start))
                return Results.NotFound(new { error = $"Directory '{start}' does not exist." });

            var entries = new List<BrowseEntry>();
            var truncated = false;
            var skipped = 0;

            // Directory.Exists 为 true 不代表目录可读：实测在这台机器上，UnauthorizedAccessException
            // 在拿到迭代器的那一刻（GetEnumerator，底层已经在开 fd）就抛出，比 foreach 文档里说的
            // 「第一次 MoveNext」更早；后续某一项读到一半失败（例如挂载点掉线）则会在 MoveNext
            // 上抛出。两处都落在原来那层只包住单项处理的 try 之外，UnauthorizedAccessException/
            // IOException 会直接冲出 handler，变成裸 500。这里手动驱动迭代器，把「目录本身读不了」
            // 和「某一项读不了」分开处理，前者不能让请求裸奔成 500。
            IEnumerator<string> iterator;
            try
            {
                iterator = Directory.EnumerateFileSystemEntries(start).GetEnumerator();
            }
            // B3：DirectoryNotFoundException 派生自 IOException，必须先于下面那个更宽的
            // IOException 分支单独捕获——否则 Directory.Exists 检查和这里取迭代器之间的
            // TOCTOU 窗口里目录被删掉，会报成「读不了」(403) 而不是「不存在」(404)，
            // 状态码对不上真实原因。窗口本身天然存在、无法消除，这里只保证报对状态码。
            catch (DirectoryNotFoundException)
            {
                return Results.NotFound(new { error = $"Directory '{start}' does not exist." });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return Results.Json(
                    new { error = $"Directory '{start}' could not be read." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            using var _ = iterator;
            while (true)
            {
                string item;
                try
                {
                    if (!iterator.MoveNext())
                        break;
                    item = iterator.Current;
                }
                // 同上：目录在迭代中途被整个删掉（而不是权限问题/挂载点掉线）也要报 404。
                catch (DirectoryNotFoundException)
                {
                    return Results.NotFound(new { error = $"Directory '{start}' does not exist." });
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // 目录本身不可读（权限不足）或中途读取失败（如挂载点掉线）：
                    // 用户点进了自己没有权限、或另一个 uid 拥有的目录——docker 卷挂载
                    // 场景下是常态，不是异常情况，给一个干净的 403 而不是裸 500。
                    return Results.Json(
                        new { error = $"Directory '{start}' could not be read." },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                if (entries.Count >= MaxBrowseEntries)
                {
                    truncated = true;
                    break;
                }

                try
                {
                    var info = new FileInfo(item);
                    var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    var name = Path.GetFileName(item);
                    entries.Add(new BrowseEntry(
                        name,
                        // 绝对路径，原样可作为下一次 `?path=` 或 localRoot 送回（picker 就是这么
                        // 用的）。ConfiguredRoot 恒为绝对路径，所以 start 也一定是绝对的。
                        item,
                        isDir,
                        // 软链的 Length 是 lstat 值（链接自身的字节数，通常几十字节），
                        // 不是目标文件的大小——不会把目标内容的大小泄漏出去，但前端picker
                        // 展示这个字段时不能当成目标文件真实大小来用。
                        isDir ? null : info.Length,
                        info.LastWriteTimeUtc,
                        // 软链可能指向根外：返回但标记，前端灰显不可点
                        !boundary.IsInside(item)));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // 单项读取失败跳过该项，不让整个请求失败。真实成因是「目录可读但不可执行」
                    // （mode `r--`）：readdir 拿得到名字，对子项 stat 却被拒，FileInfo.Attributes
                    // 在这种目录下会**抛** UnauthorizedAccessException，而不是返回 -1 哨兵值。
                    // 跳过是对的（少给一项好过整个列表 403），但静默跳过会让这种目录渲染成
                    // 「空目录」，用户完全看不出差别——所以计数并随响应返回，与 Truncated 同理。
                    skipped++;
                }
            }

            // 目录在前，各自按名称排序
            entries.Sort((a, b) => a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

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

            return Results.Ok(new BrowseResponse(start, parent, truncated, skipped, entries));
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
/// 可 readdir、不可 stat 子项）。与 <c>Truncated</c> 同一用途：少给了东西必须说出来，
/// 否则这种目录看起来就是个普通空目录。</para>
/// </summary>
public record BrowseResponse(
    string Path, string? Parent, bool Truncated, int Skipped, IReadOnlyList<BrowseEntry> Entries);

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
