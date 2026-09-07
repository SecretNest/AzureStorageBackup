using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The diff no longer builds a dictionary of the whole previous index and no longer returns a list of every change: it
/// walks the scan and the previous version as two path-ordered cursors and pushes each change out through a callback.
/// That is a rewrite of the one component whose verdict decides what gets uploaded and what silently disappears from a
/// backup, so the safety net here is differential rather than example-based: generate trees with every fate a file can
/// have, run the frozen pre-rewrite implementation (<see cref="LegacyBackupDiffer"/>) and the merge over the same
/// input, and require the two to agree change for change.
/// <para>
/// Deleted changes are compared as a multiset rather than in sequence, on purpose. The merge emits a deletion the
/// moment the previous cursor passes the path over, which is earlier than the old trailing sweep did — and it is
/// allowed to, because a Deleted change produces no index entry (BuildEntries skips it) and only feeds counters, so
/// its position cannot reach the serialized index. Everything else must match position for position.
/// </para>
/// </summary>
public sealed class BackupDifferMergeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(29)]
    public async Task Merge_Diff_Matches_The_Legacy_Diff(int seed)
    {
        var rng = new Random(seed);
        using var tree = new TempTree();
        var hasher = new FileHasher();

        var previous = await GenerateIndexAsync(rng, tree, hasher, entries: 60);
        var (scan, unreadable) = await ScanWithUnreadableAsync(rng, tree);

        var legacy = await new LegacyBackupDiffer(hasher).DiffAsync(
            tree.Root, new ScanResult(scan, [], unreadable), previous,
            null, CancellationToken.None, null, null, Deferred);

        var merged = new List<FileChange>();
        var totals = await new BackupDiffer(hasher).DiffAsync(
            tree.Root, scan.ToAsyncEnumerable(), previous.Entries.ToAsyncEnumerable(), unreadable,
            null, CancellationToken.None, null,
            (c, _) => { merged.Add(c); return Task.CompletedTask; },
            e => Deferred(e.Path));

        // The generator hands every fate to some file, so a change to it that quietly stopped exercising a branch
        // would show up here instead of turning the whole comparison into a tautology over Unchanged entries.
        Assert.All(
            new[]
            {
                ChangeKind.Added, ChangeKind.Modified, ChangeKind.MetadataOnly,
                ChangeKind.Unchanged, ChangeKind.Deleted,
            },
            k => Assert.Contains(legacy.Changes, c => c.Kind == k));

        Assert.Equal(
            legacy.Changes.Where(c => c.Kind != ChangeKind.Deleted).Select(Key),
            merged.Where(c => c.Kind != ChangeKind.Deleted).Select(Key));
        Assert.Equal(
            legacy.Changes.Where(c => c.Kind == ChangeKind.Deleted).Select(c => c.Path).Order(StringComparer.Ordinal),
            merged.Where(c => c.Kind == ChangeKind.Deleted).Select(c => c.Path).Order(StringComparer.Ordinal));

        Assert.Equal(legacy.ChangedFiles, totals.ChangedFiles);
        Assert.Equal(legacy.ChangedBytes, totals.ChangedBytes);
        Assert.Equal(legacy.Changes.Count, totals.Emitted);
        Assert.Equal(merged.Count, totals.Emitted);
    }

    /// <summary>
    /// A cursor that is not in ascending ordinal order silently produces a wrong diff — the merge would pass its own
    /// entries over and call them added or deleted. Two comparisons per entry buy an exception naming the path
    /// instead, which is the difference between "the backup is wrong" and "the backup stopped".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Cursor_Out_Of_Ordinal_Order_Is_Refused_By_Name(bool breakTheScan)
    {
        using var tree = new TempTree();
        tree.Write("a.txt", "a"u8.ToArray());
        tree.Write("b.txt", "b"u8.ToArray());

        var ordered = (await tree.ScanAsync()).Entries.ToList();
        var reversed = Enumerable.Reverse(ordered).ToList();
        var previous = ordered.Select(ToEntry).ToList();
        var previousReversed = Enumerable.Reverse(previous).ToList();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BackupDiffer(new FileHasher()).DiffAsync(
                tree.Root,
                (breakTheScan ? reversed : ordered).ToAsyncEnumerable(),
                (breakTheScan ? previous : previousReversed).ToAsyncEnumerable(),
                [], null, CancellationToken.None, null, (_, _) => Task.CompletedTask, null));

        Assert.Contains("a.txt", ex.Message, StringComparison.Ordinal);
        Assert.Contains(breakTheScan ? "scan" : "previous", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path the scanner reported as an unreadable **file** while also emitting a scanned entry for it must not grow a
    /// second, previous-less Unreadable change on top of the one the scan already produced: that entry has no content
    /// behind it, so BuildEntries would drop the real one's storage on the floor. The scanner does not do this today —
    /// this pins that the merge does not depend on it not doing it.
    /// </summary>
    [Fact]
    public async Task An_Unreadable_File_That_Was_Also_Scanned_Is_Not_Reported_Twice()
    {
        using var tree = new TempTree();
        tree.Write("a.txt", "a"u8.ToArray());

        var scan = await tree.ScanAsync();
        var unreadable = new[] { new UnreadablePath("a.txt", false, "in use") };

        var merged = new List<FileChange>();
        await new BackupDiffer(new FileHasher()).DiffAsync(
            tree.Root, scan.Entries.ToAsyncEnumerable(), null, unreadable,
            null, CancellationToken.None, null,
            (c, _) => { merged.Add(c); return Task.CompletedTask; }, null);

        Assert.Equal(ChangeKind.Added, Assert.Single(merged).Kind);
    }

    /// <summary>Everything that distinguishes two changes, so that "equal" means equal and not merely same-shaped.</summary>
    private static string Key(FileChange c) =>
        $"{c.Path}|{c.Kind}|{c.HeadHash}|{c.FullHash}|{c.TailHash}|{c.CarriedStorage?.Ref}|{c.UnreadableReason}" +
        $"|{c.Current?.Length.ToString() ?? "-"}|{c.Previous?.Path ?? "-"}|{c.Previous?.FullHash ?? "-"}";

    /// <summary>Single-file blobs get their full hash from the compression pass, so the diff defers it for them.</summary>
    private static bool Deferred(string path) => path.Contains("big", StringComparison.Ordinal);

    private static readonly string[] Dirs = ["", "d1", "d1/sub", "d2", "d2/deep/er"];

    /// <summary>
    /// Writes a tree, snapshots it into a previous-version index, then hands each file a different fate, so one run
    /// covers unchanged / touched / rewritten in place / resized / deleted / never-in-the-index at once. The fates are
    /// dealt round-robin from a random offset rather than drawn independently, so every seed exercises every branch.
    /// </summary>
    private static async Task<VersionIndex> GenerateIndexAsync(Random rng, TempTree tree, IFileHasher hasher, int entries)
    {
        var paths = new List<string>();
        for (var i = 0; i < entries; i++)
        {
            var dir = Dirs[i % Dirs.Length];
            // "big" files are the ones the deferral predicate picks up, and they are large enough that head and tail
            // are distinct 4KB windows — the tail early exit only means anything when they are.
            var big = i % 7 == 0;
            var name = $"{(big ? "big" : "f")}{i:D3}.bin";
            paths.Add(dir.Length == 0 ? name : dir + "/" + name);
            tree.Write(paths[^1], Content(rng, big ? 9000 : rng.Next(16, 200)));
        }

        var snapshot = await new LegacyBackupDiffer(hasher).DiffAsync(
            tree.Root, await tree.ScanAsync(), previous: null);
        var index = new List<IndexEntry>();
        foreach (var c in snapshot.Changes)
        {
            var e = ToEntry(c.Current!) with { HeadHash = c.HeadHash, FullHash = c.FullHash };
            // Half the entries carry a tail hash and half do not: an index written before the field existed is still
            // out there, and it is the difference between taking the tail early exit and skipping it.
            index.Add(rng.Next(2) == 0
                ? e with { TailHash = await hasher.TailHashAsync(tree.Full(e.Path), 4096) }
                : e);
        }

        var offset = rng.Next(6);
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            switch ((i + offset) % 6)
            {
                case 0: // untouched → Unchanged
                    break;
                case 1: // only the mtime moves → MetadataOnly
                    File.SetLastWriteTimeUtc(tree.Full(path), DateTime.UtcNow.AddHours(1));
                    break;
                case 2: // same length, different bytes → Modified through the head/tail/full ladder
                    tree.Write(path, Content(rng, (int)new FileInfo(tree.Full(path)).Length));
                    File.SetLastWriteTimeUtc(tree.Full(path), DateTime.UtcNow.AddHours(2));
                    break;
                case 3: // resized → Modified without any pre-screen
                    tree.Write(path, Content(rng, rng.Next(201, 400)));
                    break;
                case 4: // gone from disk → Deleted, or Unreadable when it sits under an unreadable directory
                    File.Delete(tree.Full(path));
                    break;
                default: // on disk but never in the index → Added
                    index.RemoveAll(e => string.Equals(e.Path, path, StringComparison.Ordinal));
                    break;
            }
        }

        return new VersionIndex { Version = 1, Entries = index };
    }

    /// <summary>
    /// Picks 0-3 unreadable paths and removes what they cover from the scan, the way a real scan behaves: a directory
    /// that cannot be listed contributes no entries from its whole subtree, and a file that cannot be opened is not
    /// emitted either. Some picks are names that exist in neither the scan nor the index — a brand-new file that was
    /// unreadable from the start still has to be reported.
    /// </summary>
    private static async Task<(List<ScannedEntry> Scan, IReadOnlyList<UnreadablePath> Unreadable)>
        ScanWithUnreadableAsync(Random rng, TempTree tree)
    {
        var scanned = (await tree.ScanAsync()).Entries;
        var picks = new List<UnreadablePath>();
        var count = rng.Next(0, 4);
        for (var i = 0; i < count; i++)
        {
            if (i % 2 == 0)
                picks.Add(new UnreadablePath(Dirs[rng.Next(1, Dirs.Length)], true, "permission denied"));
            else if (rng.Next(3) == 0 || scanned.Count == 0)
                picks.Add(new UnreadablePath($"zz-never-seen-{i}.bin", false, "in use"));
            else
                picks.Add(new UnreadablePath(scanned[rng.Next(scanned.Count)].Path, false, "in use"));
        }

        var unreadable = picks
            .DistinctBy(p => p.Path, StringComparer.Ordinal)
            .OrderBy(p => p.Path, StringComparer.Ordinal)
            .ToList();

        var scan = scanned
            .Where(e => !unreadable.Any(u => u.IsDirectory ? IsUnder(u.Path, e.Path) : u.Path == e.Path))
            .ToList();
        return (scan, unreadable);
    }

    private static bool IsUnder(string dir, string path) =>
        dir is "" or "." || path.StartsWith(dir + "/", StringComparison.Ordinal);

    private static IndexEntry ToEntry(ScannedEntry e) => new()
    {
        Path = e.Path,
        Kind = e.Kind == EntryKind.File ? "file" : "symlink",
        Length = e.Length,
        Mtime = e.ModifiedAt,
        Permissions = e.Permissions,
        Target = e.Target,
        Storage = new StorageRef { Kind = "blob", Ref = "data/" + e.Path },
    };

    private static byte[] Content(Random rng, int size)
    {
        var bytes = new byte[size];
        rng.NextBytes(bytes);
        return bytes;
    }

    /// <summary>A throwaway directory tree. Paths are relative and slash-separated, exactly as the index stores them.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "asb-merge-" + Guid.NewGuid().ToString("N"));

        public TempTree() => Directory.CreateDirectory(Root);

        public string Full(string relative) =>
            Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string relative, byte[] content)
        {
            var full = Full(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);
        }

        public Task<ScanResult> ScanAsync() => new LocalFileScanner().ScanAsync(Root, new IgnoreRuleSet([]));

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }
}
