namespace AzureStorageBackup.Api.Services;

/// <summary>Scanned entry kind. symlink only shows up when the user opts to include them (skipped by default).</summary>
public enum EntryKind
{
    File,
    Symlink,
}

/// <summary>
/// A single scanned entry (metadata only, PRD special note A: path/kind/length/mtime/permissions).
/// Hashes are not computed here — the diff engine computes them lazily on demand (M4 design §4.2), so a backup does not re-read every file every time.
/// </summary>
public sealed record ScannedEntry(
    string Path,
    EntryKind Kind,
    long Length,
    DateTimeOffset ModifiedAt,
    string Permissions,
    string? Target = null);

/// <summary>
/// A path that could not be read during the scan. Directories carry far more weight than files: a directory whose
/// contents cannot be listed means the **entire subtree** is unknown this round, and it must never be treated as deleted
/// just because it "wasn't scanned" — that would wipe a whole subtree out of the index, and you would only discover the files were gone at restore time.
/// </summary>
public sealed record UnreadablePath(string Path, bool IsDirectory, string Reason);

/// <summary>Scan result: entries + empty directories (which restore has to recreate) + unreadable paths.</summary>
public sealed record ScanResult(
    IReadOnlyList<ScannedEntry> Entries,
    IReadOnlyList<string> EmptyDirs,
    IReadOnlyList<UnreadablePath> Unreadable);

/// <summary>Scan options.</summary>
public sealed record ScanOptions
{
    /// <summary>Whether to include symlinks (skipped by default, M4 decision).</summary>
    public bool IncludeSymlinks { get; init; } = false;

    /// <summary>Backup scope (design docs/backup-scope-selection-design.md). Includes everything by default.</summary>
    public ScopeRuleSet Scope { get; init; } = ScopeRuleSet.All;
}

/// <summary>
/// Local file scanner: walk the local root, apply the gitignore ignore rules, produce entries (metadata) + empty directories.
/// symlinks are skipped by default. Hashes are computed lazily by the diff stage.
/// </summary>
public sealed class LocalFileScanner
{
    public async Task<ScanResult> ScanAsync(
        string rootPath,
        IgnoreRuleSet ignore,
        ScanOptions? options = null,
        CancellationToken ct = default,
        // Scanning a large directory tree takes minutes on its own, and the UI shows nothing at all for that whole time.
        // There is no "total" to speak of here — the total is exactly what the scan is computing — so we report only the number of entries scanned so far and the current directory.
        StageTracker? tracker = null)
    {
        options ??= new ScanOptions();
        var root = Path.GetFullPath(rootPath);

        var entries = new List<ScannedEntry>();
        var emptyDirs = new List<string>();
        var unreadable = new List<UnreadablePath>();

        _ = ScanDirectory(root, root, ignore, options, entries, emptyDirs, unreadable, ct, tracker);

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        emptyDirs.Sort(StringComparer.Ordinal);
        unreadable.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return await Task.FromResult(new ScanResult(entries, emptyDirs, unreadable));
    }

    /// <returns>Whether this subtree really left anything behind (entries / empty directories / unreadable paths).
    /// The parent directory uses this to decide whether to count itself as having "kept children" — a directory that was
    /// only passed through on the way down to some re-included directory deeper in has left nothing of its own behind, and must never enter EmptyDirs.</returns>
    private bool ScanDirectory(
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

        // Reading a directory has **two** failure points; both must be caught, and neither may enclose the loop body
        // (that turns into "the catch spans the whole unit of work" and misreports a failure while handling an entry as
        // "the directory can't be listed"):
        //   1) EnumerateFileSystemInfos() itself — it opens the directory handle during construction, so a directory
        //      with no read/execute permission throws right here (not later at MoveNext);
        //   2) MoveNext during iteration — the directory deleted mid-scan, media read errors, and so on.
        // Nor may we take the easy way out and materialize the whole directory into a list: hundreds of thousands of files
        // in a single directory (logs/caches/asset libraries) is common, and that keeps every FileSystemInfo resident in
        // memory — enough to OOM inside a container. So we drive the iterator by hand.
        IEnumerator<FileSystemInfo> found;
        try
        {
            found = new DirectoryInfo(dir).EnumerateFileSystemInfos().GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory whose contents cannot be listed: the entire subtree is unknown this round. **It must never land
            // in emptyDirs** — that would have restore recreate an empty directory with every file beneath it silently
            // gone; and recording nothing is no good either, or diff would judge all those existing entries deleted because they "weren't scanned".
            unreadable.Add(new UnreadablePath(RelativePath(root, dir), IsDirectory: true, ex.Message));
            return true;
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
                // Failure **partway** through iteration: the entries already scanned stay in entries as usual — they were
                // genuinely read, which beats carrying over old ones. The diff side registers by path, so entries already scanned are not overwritten a second time by this directory's marker.
                unreadable.Add(new UnreadablePath(RelativePath(root, dir), IsDirectory: true, ex.Message));
                return true;
            }


            var relative = RelativePath(root, info.FullName);
            var isSymlink = info.LinkTarget is not null;
            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;

            if (ignore.IsIgnored(relative, isDirectory))
                continue;

            if (isDirectory && !isSymlink)
            {
                // Directory excluded, and no re-including rule anywhere in the subtree → prune the whole thing, don't descend.
                // Judging on IsInScope alone is not enough: an excluded directory may still have + rules beneath it (design §2).
                if (!options.Scope.MayContainIncluded(relative))
                    continue;

                // keptChildren only increments when the subtree **actually** left something behind. Directories merely passed
                // through don't count — otherwise `- docs` + `+ docs/2026` would record docs as an empty directory and restore would conjure it back out of nowhere.
                if (ScanDirectory(info.FullName, root, ignore, options, entries, emptyDirs, unreadable, ct, tracker))
                    keptChildren++;
                continue;
            }

            if (!options.Scope.IsInScope(relative))
                continue;

            // A single entry's metadata can be unreadable too (deleted after enumeration, permissions revoked). Silently
            // skipping is just as unacceptable: skipping is the same as telling diff it was deleted. Record one, and let diff carry over the previous version's entry.
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
                    tracker?.Advance(0); // Scanning reads metadata only, never content, so zero bytes
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

        // Empty directory: after applying ignore and scope, no kept files and no kept subdirectories (the root itself is not recorded).
        var self = RelativePath(root, dir);
        if (keptChildren == 0 && !string.IsNullOrEmpty(self))
        {
            // A directory not itself in scope (merely passed through) is neither an empty directory nor "left something behind".
            if (!options.Scope.IsInScope(self))
                return false;
            emptyDirs.Add(self);
        }

        return true;
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
