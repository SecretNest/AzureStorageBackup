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
        CancellationToken ct = default)
    {
        options ??= new ScanOptions();
        var root = Path.GetFullPath(rootPath);

        var entries = new List<ScannedEntry>();
        var emptyDirs = new List<string>();
        var unreadable = new List<UnreadablePath>();

        ScanDirectory(root, root, ignore, options, entries, emptyDirs, unreadable, ct);

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
        CancellationToken ct)
    {
        var keptChildren = 0;

        // 枚举是惰性的（异常在 MoveNext 时才抛），所以必须在 try 内强制求值，否则 foreach
        // 一旦开始迭代，异常就落到 try 外面去了。
        List<FileSystemInfo> children;
        try
        {
            children = [.. new DirectoryInfo(dir).EnumerateFileSystemInfos()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 列不出内容的目录：整棵子树本轮不可知。**绝不能落进 emptyDirs**——那会让还原
            // 重建出一个空目录，其下的文件全部消失得无声无息；也不能什么都不记，
            // 否则 diff 会因为"没扫到"把这些既有条目一律判成删除。
            var relativeDir = RelativePath(root, dir);
            unreadable.Add(new UnreadablePath(relativeDir, IsDirectory: true, ex.Message));
            return;
        }

        foreach (var info in children)
        {
            ct.ThrowIfCancellationRequested();

            var relative = RelativePath(root, info.FullName);
            var isSymlink = info.LinkTarget is not null;
            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;

            if (ignore.IsIgnored(relative, isDirectory))
                continue;

            if (isDirectory && !isSymlink)
            {
                keptChildren++;
                ScanDirectory(info.FullName, root, ignore, options, entries, emptyDirs, unreadable, ct);
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
                    continue;
                }

                keptChildren++;
                var file = (FileInfo)info;
                entries.Add(new ScannedEntry(
                    relative, EntryKind.File, file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc),
                    ReadPermissions(file.FullName)));
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
