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

/// <summary>扫描结果：条目 + 空文件夹（还原时需重建）。</summary>
public sealed record ScanResult(
    IReadOnlyList<ScannedEntry> Entries,
    IReadOnlyList<string> EmptyDirs);

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

        ScanDirectory(root, root, ignore, options, entries, emptyDirs, ct);

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        emptyDirs.Sort(StringComparer.Ordinal);
        return await Task.FromResult(new ScanResult(entries, emptyDirs));
    }

    private void ScanDirectory(
        string dir,
        string root,
        IgnoreRuleSet ignore,
        ScanOptions options,
        List<ScannedEntry> entries,
        List<string> emptyDirs,
        CancellationToken ct)
    {
        var keptChildren = 0;

        foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();

            var relative = RelativePath(root, info.FullName);
            var isSymlink = info.LinkTarget is not null;
            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;

            if (ignore.IsIgnored(relative, isDirectory))
                continue;

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

            if (isDirectory)
            {
                keptChildren++;
                ScanDirectory(info.FullName, root, ignore, options, entries, emptyDirs, ct);
                continue;
            }

            keptChildren++;
            var file = (FileInfo)info;
            entries.Add(new ScannedEntry(
                relative, EntryKind.File, file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc),
                ReadPermissions(file.FullName)));
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
