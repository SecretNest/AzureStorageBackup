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

/// <summary>Scan result: entries + empty directories (which restore has to recreate) + unreadable paths.
/// Kept only for the old list-returning <see cref="LocalFileScanner.ScanAsync(string, IgnoreRuleSet, ScanOptions?, CancellationToken, StageTracker?)"/>
/// overload, which the orchestrator still calls; the sink-based overload returns <see cref="ScanSummary"/> instead.</summary>
public sealed record ScanResult(
    IReadOnlyList<ScannedEntry> Entries,
    IReadOnlyList<string> EmptyDirs,
    IReadOnlyList<UnreadablePath> Unreadable);

/// <summary>Scan options.</summary>
public sealed record ScanOptions
{
    /// <summary>Whether to include symlinks (skipped by default, M4 decision).</summary>
    public bool IncludeSymlinks { get; init; } = false;

    /// <summary>Backup scope (design docs/configuration.md). Includes everything by default.</summary>
    public ScopeRuleSet Scope { get; init; } = ScopeRuleSet.All;
}

/// <summary>
/// Local file scanner: walk the local root, apply the gitignore ignore rules, hand each entry (metadata) to a sink,
/// and record the empty directories and unreadable paths along the way. symlinks are skipped by default. Hashes are
/// computed lazily by the diff stage.
/// </summary>
public sealed class LocalFileScanner
{
    /// <summary>
    /// Walks <paramref name="rootPath"/> and hands every kept entry to <paramref name="sink"/> one at a time, instead
    /// of building a list proportional to file count — a real run's sink (<see cref="WorkDbScanSink"/>) writes
    /// straight into the per-run work database, so the whole tree is never resident in memory here.
    /// </summary>
    public async Task<ScanSummary> ScanAsync(
        string rootPath,
        IgnoreRuleSet ignore,
        IScanSink sink,
        ScanOptions? options = null,
        CancellationToken ct = default,
        // Scanning a large directory tree takes minutes on its own, and the UI shows nothing at all for that whole time.
        // There is no "total" to speak of here — the total is exactly what the scan is computing — so we report only the number of entries scanned so far and the current directory.
        StageTracker? tracker = null)
    {
        options ??= new ScanOptions();
        var root = Path.GetFullPath(rootPath);

        var emptyDirs = new List<string>();
        var unreadable = new List<UnreadablePath>();
        // A single-element array, not a plain local: ScanDirectory is recursive and async, so the count has to be
        // threaded through as a mutable reference (async methods cannot take ref/out parameters) rather than
        // returned and summed by hand at every call site.
        var count = new long[1];

        await ScanDirectory(root, root, ignore, options, sink, count, emptyDirs, unreadable, ct, tracker);

        emptyDirs.Sort(StringComparer.Ordinal);
        unreadable.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new ScanSummary(count[0], emptyDirs, unreadable);
    }

    /// <summary>
    /// The old list-returning shape, kept only so the orchestrator keeps compiling while it is migrated one stage at
    /// a time (Task 13 deletes this). Implemented in terms of the sink-based overload: collect into a
    /// <see cref="ListScanSink"/>, sort the way the walk itself used to before this refactor moved sorting out to
    /// the caller.
    /// </summary>
    public async Task<ScanResult> ScanAsync(
        string rootPath,
        IgnoreRuleSet ignore,
        ScanOptions? options = null,
        CancellationToken ct = default,
        StageTracker? tracker = null)
    {
        var sink = new ListScanSink();
        var summary = await ScanAsync(rootPath, ignore, sink, options, ct, tracker);

        sink.Entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new ScanResult(sink.Entries, summary.EmptyDirs, summary.Unreadable);
    }

    /// <returns>Whether this subtree really left anything behind (entries / empty directories / unreadable paths).
    /// The parent directory uses this to decide whether to count itself as having "kept children" — a directory that was
    /// only passed through on the way down to some re-included directory deeper in has left nothing of its own behind, and must never enter EmptyDirs.</returns>
    private async Task<bool> ScanDirectory(
        string dir,
        string root,
        IgnoreRuleSet ignore,
        ScanOptions options,
        IScanSink sink,
        long[] count,
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
                if (await ScanDirectory(info.FullName, root, ignore, options, sink, count, emptyDirs, unreadable, ct, tracker))
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
                    await sink.AddAsync(new ScannedEntry(
                        relative, EntryKind.Symlink, 0,
                        new DateTimeOffset(info.LastWriteTimeUtc),
                        // NOT ReadPermissions: GetUnixFileMode(string) resolves the link, and on a dangling target it
                    // throws FileNotFoundException — which the unreadable catch swallowed, so the symlink (a
                    // legitimate piece of content whose target string has nothing to do with the target existing)
                    // silently never entered the index at all: the worst-direction failure. The checker and the
                    // compressor both special-case symlinks away from GetUnixFileMode for exactly this reason,
                    // and the diff compares symlinks by Target alone, never by permissions.
                    "0777",
                        Target: info.LinkTarget), ct);
                    count[0]++;
                    tracker?.Advance(0); // Scanning reads metadata only, never content, so zero bytes
                    continue;
                }

                keptChildren++;
                var file = (FileInfo)info;
                await sink.AddAsync(new ScannedEntry(
                    relative, EntryKind.File, file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc),
                    ReadPermissions(file.FullName)), ct);
                count[0]++;
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
