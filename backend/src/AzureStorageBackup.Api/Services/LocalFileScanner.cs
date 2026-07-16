using System.Security.Cryptography;

namespace AzureStorageBackup.Api.Services;

/// <summary>扫描条目类型。symlink 仅当用户选择包含时出现（默认跳过）。</summary>
public enum EntryKind
{
    File,
    Symlink,
}

/// <summary>扫描出的单个条目（PRD 特别说明 A：path/kind/length/mtime/权限/两级 hash）。</summary>
public sealed record ScannedEntry(
    string Path,
    EntryKind Kind,
    long Length,
    DateTimeOffset ModifiedAt,
    string Permissions,
    string HeadHash,
    string FullHash,
    string? Target = null);

/// <summary>扫描结果：条目 + 空文件夹（还原时需重建）。</summary>
public sealed record ScanResult(
    IReadOnlyList<ScannedEntry> Entries,
    IReadOnlyList<string> EmptyDirs);

/// <summary>扫描选项。</summary>
public sealed record ScanOptions
{
    /// <summary>headHash 覆盖的文件头部字节数（默认 4KB，M4 决策 §13.3）。</summary>
    public int HeadHashBytes { get; init; } = 4096;

    /// <summary>是否包含符号链接（默认跳过，M4 决策）。</summary>
    public bool IncludeSymlinks { get; init; } = false;
}

/// <summary>
/// 本地文件扫描器：遍历本地根，应用 gitignore 忽略规则，产出条目 + 空文件夹。
/// symlink 默认跳过。两级 hash（headHash 头部预筛 + fullHash 整文件）见 M4 设计 §4。
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

        await ScanDirectoryAsync(root, root, ignore, options, entries, emptyDirs, ct);

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        emptyDirs.Sort(StringComparer.Ordinal);
        return new ScanResult(entries, emptyDirs);
    }

    private async Task ScanDirectoryAsync(
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
                    HeadHash: "", FullHash: "",
                    Target: info.LinkTarget));
                continue;
            }

            if (isDirectory)
            {
                keptChildren++;
                await ScanDirectoryAsync(info.FullName, root, ignore, options, entries, emptyDirs, ct);
                continue;
            }

            keptChildren++;
            entries.Add(await ScanFileAsync((FileInfo)info, relative, options, ct));
        }

        // 空文件夹：应用忽略后既无保留文件也无保留子目录（根自身不记录）。
        if (keptChildren == 0 && !string.IsNullOrEmpty(RelativePath(root, dir)))
            emptyDirs.Add(RelativePath(root, dir));
    }

    private static async Task<ScannedEntry> ScanFileAsync(
        FileInfo file,
        string relative,
        ScanOptions options,
        CancellationToken ct)
    {
        var (headHash, fullHash) = await ComputeHashesAsync(file.FullName, options.HeadHashBytes, ct);

        return new ScannedEntry(
            relative,
            EntryKind.File,
            file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc),
            ReadPermissions(file.FullName),
            headHash,
            fullHash);
    }

    private static async Task<(string HeadHash, string FullHash)> ComputeHashesAsync(
        string path, int headBytes, CancellationToken ct)
    {
        using var full = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var head = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var buffer = new byte[81920];
        long headRemaining = headBytes;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            full.AppendData(buffer, 0, read);
            if (headRemaining > 0)
            {
                var take = (int)Math.Min(headRemaining, read);
                head.AppendData(buffer, 0, take);
                headRemaining -= take;
            }
        }

        return (Format(head.GetHashAndReset()), Format(full.GetHashAndReset()));
    }

    private static string Format(byte[] hash) => "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();

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
