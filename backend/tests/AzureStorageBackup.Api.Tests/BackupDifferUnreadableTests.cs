using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 一个文件读不开，不该让其余几万个文件的备份一起作废。
/// diff 阶段就把读失败收敛成 Unreadable，后续阶段不必各自 try/catch。
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

    private static FileChange Change(DiffResult d, string path) => d.Changes.Single(c => c.Path == path);

    /// <summary>指定路径抛给定异常，其余照常算 hash。</summary>
    private sealed class ThrowingHasher(string lockedPath, Exception toThrow) : IFileHasher
    {
        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("head-" + Path.GetFileName(path));

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            Task.FromResult("tail-" + Path.GetFileName(path));

        public Task<string> FullHashAsync(string path, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("full-" + Path.GetFileName(path));
    }

    [Fact]
    public async Task An_Unreadable_New_File_Is_Classified_Unreadable_And_Others_Still_Diff()
    {
        // 期望：locked.mdf 分类为 Unreadable，其余文件照常得到 Added；整个 diff 不抛。
        Write("locked.mdf", "database content");
        Write("plain.txt", "ordinary file");

        var hasher = new ThrowingHasher("locked.mdf",
            new IOException("The process cannot access the file 'locked.mdf' because it is being used by another process."));

        var diff = await new BackupDiffer(hasher).DiffAsync(_root, await ScanAsync(_root), previous: null);

        var locked = Change(diff, "locked.mdf");
        Assert.Equal(ChangeKind.Unreadable, locked.Kind);
        Assert.NotNull(locked.Current);   // 扫描到的条目要保留，供后续沿用上一版本/打标记
        Assert.Null(locked.Previous);     // 新文件，没有上一版本条目
        Assert.Null(locked.HeadHash);
        Assert.Null(locked.FullHash);
        Assert.Null(locked.CarriedStorage);

        // 其余文件不受影响，照常分类
        Assert.Equal(ChangeKind.Added, Change(diff, "plain.txt").Kind);
    }

    [Fact]
    public async Task An_Unreadable_Modified_File_Keeps_Its_Previous_Entry_Reference()
    {
        // 期望：Kind == Unreadable 且 Previous 指向上一版本条目（供索引沿用）。
        var path = Write("locked.mdf", "hello");
        var previous = await SnapshotAsync();

        File.WriteAllText(path, "hello world!"); // 长度变 → 触发重新哈希路径

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
        // 期望：与上一条同样分类为 Unreadable。
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
        // 期望：hasher 抛 OperationCanceledException 时 diff 照常上抛，不被当成 Unreadable。
        // 这条是护栏：捕获写宽成 catch(Exception) 会让取消变成「跳过一个文件」。
        Write("locked.mdf", "database content");
        var scan = await ScanAsync(_root);

        var hasher = new ThrowingHasher("locked.mdf", new OperationCanceledException("cancelled"));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new BackupDiffer(hasher).DiffAsync(_root, scan, previous: null));
    }
}
