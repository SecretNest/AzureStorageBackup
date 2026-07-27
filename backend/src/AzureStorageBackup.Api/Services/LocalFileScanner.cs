namespace AzureStorageBackup.Api.Services;

/// <summary>扫描条目类型。symlink 仅当用户选择包含时出现（默认跳过）。</summary>
public enum EntryKind
{
    File,
    Symlink,
}

/// <summary>
/// 扫描出的单个条目（仅元数据，PRD 特别说明 A：path/kind/length/mtime/权限）。
/// 哈希不在此计算——由 diff 引擎按需惰性计算（M4 设计 §4.2），避免每次备份重读全部文件。
/// </summary>
public sealed record ScannedEntry(
    string Path,
    EntryKind Kind,
    long Length,
    DateTimeOffset ModifiedAt,
    string Permissions,
    string? Target = null);

/// <summary>
/// 扫描时读不出来的路径。目录的分量比文件重得多：一个列不出内容的目录意味着**整棵子树**
/// 本轮不可知，绝不能因为"没扫到"就被当成删除——那会把一整棵子树从索引里抹掉，
/// 直到还原时才发现文件没了。
/// </summary>
public sealed record UnreadablePath(string Path, bool IsDirectory, string Reason);

/// <summary>扫描结果：条目 + 空文件夹（还原时需重建）+ 读不出来的路径。</summary>
public sealed record ScanResult(
    IReadOnlyList<ScannedEntry> Entries,
    IReadOnlyList<string> EmptyDirs,
    IReadOnlyList<UnreadablePath> Unreadable);

/// <summary>扫描选项。</summary>
public sealed record ScanOptions
{
    /// <summary>是否包含符号链接（默认跳过，M4 决策）。</summary>
    public bool IncludeSymlinks { get; init; } = false;
}

/// <summary>
/// 本地文件扫描器：遍历本地根，应用 gitignore 忽略规则，产出条目（元数据）+ 空文件夹。
/// symlink 默认跳过。哈希由 diff 阶段惰性计算。
/// </summary>
public sealed class LocalFileScanner
{
    public async Task<ScanResult> ScanAsync(
        string rootPath,
        IgnoreRuleSet ignore,
        ScanOptions? options = null,
        CancellationToken ct = default,
        // 扫描一棵大目录树本身就要几分钟，期间界面上同样什么都看不到。
        // 这里没有"总数"可言——总数正是扫描要算出来的——所以只报已扫条目数与当前目录。
        StageTracker? tracker = null)
    {
        options ??= new ScanOptions();
        var root = Path.GetFullPath(rootPath);

        var entries = new List<ScannedEntry>();
        var emptyDirs = new List<string>();
        var unreadable = new List<UnreadablePath>();

        ScanDirectory(root, root, ignore, options, entries, emptyDirs, unreadable, ct, tracker);

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        emptyDirs.Sort(StringComparer.Ordinal);
        unreadable.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return await Task.FromResult(new ScanResult(entries, emptyDirs, unreadable));
    }

    private void ScanDirectory(
        string dir,
        string root,
        IgnoreRuleSet ignore,
        ScanOptions options,
        List<ScannedEntry> entries,
        List<string> emptyDirs,
        List<UnreadablePath> unreadable,
        CancellationToken ct,
        StageTracker? tracker)
    {
        var keptChildren = 0;
        tracker?.Touch(RelativePath(root, dir));

        // 目录读不出来有**两个**失败点，都要接住，但都不能把循环体也圈进去（那就成了"catch 范围
        // 圈住整个工作单元"，会把处理条目时的失败误判成"目录列不出来"）：
        //   1) EnumerateFileSystemInfos() 本身——它在构造时就打开目录句柄，目录没有读/执行权限
        //      时在这一步抛（不是等到 MoveNext）；
        //   2) 迭代过程中的 MoveNext——目录在扫描途中被删、介质读错误等。
        // 也**不能**图省事把整个目录物化成列表：一个目录下几十万个文件（日志/缓存/素材库）很常见，
        // 那样每个 FileSystemInfo 都要常驻内存，容器里足以 OOM。所以手动驱动迭代器。
        IEnumerator<FileSystemInfo> found;
        try
        {
            found = new DirectoryInfo(dir).EnumerateFileSystemInfos().GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 列不出内容的目录：整棵子树本轮不可知。**绝不能落进 emptyDirs**——那会让还原
            // 重建出一个空目录，其下的文件全部消失得无声无息；也不能什么都不记，
            // 否则 diff 会因为"没扫到"把这些既有条目一律判成删除。
            unreadable.Add(new UnreadablePath(RelativePath(root, dir), IsDirectory: true, ex.Message));
            return;
        }

        using var children = found;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            FileSystemInfo info;
            try
            {
                if (!children.MoveNext())
                    break;
                info = children.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 迭代**中途**才失败：此前已扫到的条目照常留在 entries 里——它们是真读到的，
                // 比沿用旧条目准确。diff 侧按路径登记，已扫到的不会被这条目录标记二次覆盖。
                unreadable.Add(new UnreadablePath(RelativePath(root, dir), IsDirectory: true, ex.Message));
                return;
            }


            var relative = RelativePath(root, info.FullName);
            var isSymlink = info.LinkTarget is not null;
            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;

            if (ignore.IsIgnored(relative, isDirectory))
                continue;

            if (isDirectory && !isSymlink)
            {
                keptChildren++;
                ScanDirectory(info.FullName, root, ignore, options, entries, emptyDirs, unreadable, ct, tracker);
                continue;
            }

            // 单个条目的元数据也可能读不出来（枚举之后被删掉、权限被收回）。同样不能默默跳过：
            // 跳过等于让 diff 判它删除。记一条，让 diff 沿用上一版本的条目。
            try
            {
                if (isSymlink)
                {
                    if (!options.IncludeSymlinks)
                        continue;

                    keptChildren++;
                    entries.Add(new ScannedEntry(
                        relative, EntryKind.Symlink, 0,
                        new DateTimeOffset(info.LastWriteTimeUtc),
                        ReadPermissions(info.FullName),
                        Target: info.LinkTarget));
                    tracker?.Advance(0); // 扫描只读元数据，不读内容，故字节为 0
                    continue;
                }

                keptChildren++;
                var file = (FileInfo)info;
                entries.Add(new ScannedEntry(
                    relative, EntryKind.File, file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc),
                    ReadPermissions(file.FullName)));
                tracker?.Advance(0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(new UnreadablePath(relative, IsDirectory: false, ex.Message));
            }
        }

        // 空文件夹：应用忽略后既无保留文件也无保留子目录（根自身不记录）。
        if (keptChildren == 0 && !string.IsNullOrEmpty(RelativePath(root, dir)))
            emptyDirs.Add(RelativePath(root, dir));
    }

    private static string RelativePath(string root, string full) =>
        Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

    private static string ReadPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            return "0000";

        var mode = (int)File.GetUnixFileMode(path);
        return Convert.ToString(mode, 8).PadLeft(4, '0');
    }
}
