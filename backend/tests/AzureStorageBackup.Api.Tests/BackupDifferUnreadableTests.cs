using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// One unreadable file must not void the backup of the other tens of thousands of files along with it.
/// The diff stage funnels read failures into Unreadable so that later stages do not each need their own try/catch.
/// </summary>
public sealed class BackupDifferUnreadableTests : IDisposable
{
    private readonly string _root;

    public BackupDifferUnreadableTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-diff-unreadable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static Task<ScanResult> ScanAsync(string root) =>
        new LocalFileScanner().ScanAsync(root, new IgnoreRuleSet([]));

    /// <summary>Produce a "previous version index" snapshot using the differ itself (previous=null, so everything comes out Added).</summary>
    private async Task<VersionIndex> SnapshotAsync()
    {
        var scan = await ScanAsync(_root);
        var diff = await new BackupDiffer(new FileHasher()).DiffAsync(_root, scan, previous: null);

        var entries = diff.Changes
            .Where(c => c.Current is not null)
            .Select(c => new IndexEntry
            {
                Path = c.Path,
                Kind = c.Current!.Kind == EntryKind.File ? "file" : "symlink",
                Length = c.Current.Length,
                Mtime = c.Current.ModifiedAt,
                Permissions = c.Current.Permissions,
                HeadHash = c.HeadHash,
                FullHash = c.FullHash,
                Target = c.Current.Target,
                Storage = new StorageRef { Kind = "blob", Ref = "data/" + c.FullHash },
            })
            .ToList();

        return new VersionIndex { Version = 1, Entries = entries, EmptyDirs = scan.EmptyDirs.ToList() };
    }

    private static FileChange Change(DiffResult d, string path) => d.Changes.Single(c => c.Path == path);

    /// <summary>The given path throws the given exception; everything else is hashed as usual.</summary>
    private sealed class ThrowingHasher(string lockedPath, Exception toThrow) : IFileHasher
    {
        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("head-" + Path.GetFileName(path));

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            Task.FromResult("tail-" + Path.GetFileName(path));

        public Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("full-" + Path.GetFileName(path));
    }

    [Fact]
    public async Task An_Unreadable_New_File_Is_Classified_Unreadable_And_Others_Still_Diff()
    {
        // Expected: locked.mdf is classified Unreadable, the other files still come out Added, and the diff as a whole does not throw.
        Write("locked.mdf", "database content");
        Write("plain.txt", "ordinary file");

        var hasher = new ThrowingHasher("locked.mdf",
            new IOException("The process cannot access the file 'locked.mdf' because it is being used by another process."));

        var diff = await new BackupDiffer(hasher).DiffAsync(_root, await ScanAsync(_root), previous: null);

        var locked = Change(diff, "locked.mdf");
        Assert.Equal(ChangeKind.Unreadable, locked.Kind);
        Assert.NotNull(locked.Current);   // keep the scanned entry so later stages can carry the previous version forward / stamp the marker
        Assert.Null(locked.Previous);     // a new file, so there is no previous-version entry
        Assert.Null(locked.HeadHash);
        Assert.Null(locked.FullHash);
        Assert.Null(locked.CarriedStorage);

        // The other files are unaffected and classified as usual
        Assert.Equal(ChangeKind.Added, Change(diff, "plain.txt").Kind);
    }

    [Fact]
    public async Task An_Unreadable_Modified_File_Keeps_Its_Previous_Entry_Reference()
    {
        // Expected: Kind == Unreadable, and Previous points at the previous-version entry (for the index to carry forward).
        var path = Write("locked.mdf", "hello");
        var previous = await SnapshotAsync();

        File.WriteAllText(path, "hello world!"); // length changed → triggers the re-hashing path

        var hasher = new ThrowingHasher("locked.mdf",
            new IOException("The process cannot access the file because it is being used by another process."));

        var diff = await new BackupDiffer(hasher).DiffAsync(_root, await ScanAsync(_root), previous);

        var c = Change(diff, "locked.mdf");
        Assert.Equal(ChangeKind.Unreadable, c.Kind);
        Assert.Same(previous.Entries.Single(e => e.Path == "locked.mdf"), c.Previous);
        Assert.Null(c.HeadHash);
        Assert.Null(c.FullHash);
        Assert.Null(c.CarriedStorage);
    }

    [Fact]
    public async Task An_Unreadable_File_With_Unchanged_Length_But_Changed_Mtime_Is_Classified_Unreadable()
    {
        // Expected: unchanged length but changed mtime → takes the "two-level hashing" branch (headHash first, then fullHash if warranted).
        // That branch used to be covered only on the happy path; no test ever actually reached it with a read failure.
        var path = Write("locked.mdf", "hello");
        var previous = await SnapshotAsync();

        // Leave the content alone (length unchanged) and change only the mtime, to make sure we land in the same-length, different-mtime branch.
        File.SetLastWriteTimeUtc(path, previous.Entries.Single(e => e.Path == "locked.mdf").Mtime.UtcDateTime.AddHours(1));

        var hasher = new ThrowingHasher("locked.mdf",
            new IOException("The process cannot access the file because it is being used by another process."));

        var diff = await new BackupDiffer(hasher).DiffAsync(_root, await ScanAsync(_root), previous);

        var c = Change(diff, "locked.mdf");
        Assert.Equal(ChangeKind.Unreadable, c.Kind);
        Assert.Same(previous.Entries.Single(e => e.Path == "locked.mdf"), c.Previous);
        Assert.Null(c.HeadHash);
        Assert.Null(c.FullHash);
        Assert.Null(c.CarriedStorage);
    }

    [Fact]
    public async Task UnauthorizedAccess_Is_Treated_The_Same_As_IOException()
    {
        // Expected: classified Unreadable just like the previous case.
        Write("locked.mdf", "database content");

        var hasher = new ThrowingHasher("locked.mdf", new UnauthorizedAccessException("Access to the path is denied."));

        var diff = await new BackupDiffer(hasher).DiffAsync(_root, await ScanAsync(_root), previous: null);

        var c = Change(diff, "locked.mdf");
        Assert.Equal(ChangeKind.Unreadable, c.Kind);
        Assert.Null(c.HeadHash);
        Assert.Null(c.FullHash);
        Assert.Null(c.CarriedStorage);
    }

    [Fact]
    public async Task Cancellation_Still_Aborts_The_Diff()
    {
        // Expected: when the hasher throws OperationCanceledException the diff rethrows it as usual and does not treat it as Unreadable.
        // This is a guardrail: widening the catch to catch(Exception) would turn a cancellation into "skipped one file".
        Write("locked.mdf", "database content");
        var scan = await ScanAsync(_root);

        var hasher = new ThrowingHasher("locked.mdf", new OperationCanceledException("cancelled"));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new BackupDiffer(hasher).DiffAsync(_root, scan, previous: null));
    }
}
