using System.Text;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class BackupDifferTests : IDisposable
{
    private readonly string _root;

    public BackupDifferTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-diff-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>用 differ 自身（previous=null 全部 Added）产出一份"上一版本索引"快照。</summary>
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

    private sealed class CountingHasher(IFileHasher inner) : IFileHasher
    {
        public int HeadCalls;
        public int FullCalls;

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default)
        {
            Interlocked.Increment(ref HeadCalls);
            return inner.HeadHashAsync(path, headBytes, ct);
        }

        public Task<string> FullHashAsync(string path, CancellationToken ct = default)
        {
            Interlocked.Increment(ref FullCalls);
            return inner.FullHashAsync(path, ct);
        }
    }

    private static FileChange Change(DiffResult d, string path) => d.Changes.Single(c => c.Path == path);

    [Fact]
    public async Task First_Backup_Marks_Everything_Added()
    {
        Write("a.txt", "aaa");
        Write("sub/b.txt", "bbbbb");

        var diff = await new BackupDiffer(new FileHasher()).DiffAsync(_root, await ScanAsync(_root), previous: null);

        Assert.All(diff.Changes, c => Assert.Equal(ChangeKind.Added, c.Kind));
        Assert.Equal(2, diff.ChangedFiles);
        Assert.Equal(8, diff.ChangedBytes);
        Assert.NotNull(Change(diff, "a.txt").FullHash);
    }

    [Fact]
    public async Task Unchanged_Files_Are_Not_Hashed()
    {
        Write("a.txt", "aaa");
        Write("b.txt", "bbb");
        var previous = await SnapshotAsync();

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(_root, await ScanAsync(_root), previous);

        Assert.All(diff.Changes, c => Assert.Equal(ChangeKind.Unchanged, c.Kind));
        Assert.Equal(0, diff.ChangedFiles);
        Assert.Equal(0, counter.HeadCalls); // length+mtime+perms 相同 → 完全跳过哈希
        Assert.Equal(0, counter.FullCalls);
        // 未变条目沿用上一版本的哈希与存储
        Assert.Equal(previous.Entries.Single(e => e.Path == "a.txt").FullHash, Change(diff, "a.txt").FullHash);
        Assert.NotNull(Change(diff, "a.txt").CarriedStorage);
    }

    [Fact]
    public async Task Content_Change_Same_Length_Is_Modified()
    {
        var path = Write("a.txt", "hello");
        var previous = await SnapshotAsync();

        File.WriteAllText(path, "world"); // 同长度不同内容
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));

        var diff = await new BackupDiffer(new FileHasher()).DiffAsync(_root, await ScanAsync(_root), previous);

        var c = Change(diff, "a.txt");
        Assert.Equal(ChangeKind.Modified, c.Kind);
        Assert.NotEqual(previous.Entries.Single(e => e.Path == "a.txt").FullHash, c.FullHash);
        Assert.Equal(1, diff.ChangedFiles);
    }

    [Fact]
    public async Task Length_Change_Is_Modified_Without_Head_Precheck()
    {
        var path = Write("a.txt", "hello");
        var previous = await SnapshotAsync();

        File.WriteAllText(path, "hello world!"); // 长度变

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(_root, await ScanAsync(_root), previous);

        Assert.Equal(ChangeKind.Modified, Change(diff, "a.txt").Kind);
        Assert.Equal(0, counter.HeadCalls);   // 长度已不同，无需 head 预筛
        Assert.Equal(1, counter.FullCalls);    // 仍需 fullHash 作去重键
    }

    [Fact]
    public async Task Metadata_Only_Change_Reuses_Content()
    {
        var path = Write("a.txt", "same content");
        var previous = await SnapshotAsync();

        // 内容不变，仅改 mtime（触发两级哈希，但都相同）
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(30));

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(_root, await ScanAsync(_root), previous);

        var c = Change(diff, "a.txt");
        Assert.Equal(ChangeKind.MetadataOnly, c.Kind);
        Assert.Equal(1, counter.HeadCalls);
        Assert.Equal(1, counter.FullCalls);
        Assert.Equal(previous.Entries.Single(e => e.Path == "a.txt").FullHash, c.FullHash);
        Assert.NotNull(c.CarriedStorage);            // 复用旧存储，不重传
        Assert.Equal(0, diff.ChangedFiles);          // 仅元数据不计入变更
    }

    [Fact]
    public async Task Removed_File_Is_Deleted()
    {
        Write("keep.txt", "k");
        Write("gone.txt", "g");
        var previous = await SnapshotAsync();

        File.Delete(Path.Combine(_root, "gone.txt"));

        var diff = await new BackupDiffer(new FileHasher()).DiffAsync(_root, await ScanAsync(_root), previous);

        var gone = Change(diff, "gone.txt");
        Assert.Equal(ChangeKind.Deleted, gone.Kind);
        Assert.Null(gone.Current);
        Assert.Equal(ChangeKind.Unchanged, Change(diff, "keep.txt").Kind);
    }
}
