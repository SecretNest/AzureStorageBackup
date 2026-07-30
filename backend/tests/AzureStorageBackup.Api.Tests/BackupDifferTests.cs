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

    /// <summary>同 SnapshotAsync，但把尾部 hash 也记进条目——尾部早退要拿它当比对基准。</summary>
    private async Task<VersionIndex> SnapshotWithTailAsync()
    {
        var snapshot = await SnapshotAsync();
        var hasher = new FileHasher();
        var withTail = new List<IndexEntry>(snapshot.Entries.Count);
        foreach (var e in snapshot.Entries)
            withTail.Add(e with
            {
                TailHash = await hasher.TailHashAsync(Path.Combine(_root, e.Path), 4096),
            });
        return snapshot with { Entries = withTail };
    }

    private sealed class CountingHasher(IFileHasher inner) : IFileHasher
    {
        public int HeadCalls;
        public int FullCalls;
        public int IdentityCalls;

        public Task<ContentIdentity> ContentIdentityAsync(
            string path, int segmentBytes, CancellationToken ct = default)
        {
            Interlocked.Increment(ref IdentityCalls);
            return inner.ContentIdentityAsync(path, segmentBytes, ct);
        }

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default)
        {
            Interlocked.Increment(ref HeadCalls);
            return inner.HeadHashAsync(path, headBytes, ct);
        }

        public int TailCalls;

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default)
        {
            Interlocked.Increment(ref TailCalls);
            return inner.TailHashAsync(path, tailBytes, ct);
        }

        public Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null)
        {
            Interlocked.Increment(ref FullCalls);
            return inner.FullHashAsync(path, ct);
        }
    }

    private static FileChange Change(DiffResult d, string path) => d.Changes.Single(c => c.Path == path);

    /// <summary>
    /// 长度没变、mtime 变了的大文件（全文可延后）：尾部对不上就该当场定案，**不读全文**。
    /// 这是最贵的一趟——100 GB 的文件就是 100 GB 的读——而"内容变了"在读完 4KB 尾巴时
    /// 已经成立。数据库文件、虚拟磁盘、被覆写的日志都是长度不变而尾部先动的典型。
    /// </summary>
    [Fact]
    public async Task A_Differing_Tail_Settles_It_Without_Reading_The_Whole_File()
    {
        var path = Write("big.bin", new string('a', 8192) + "TAIL-ONE");
        var previous = await SnapshotWithTailAsync();

        // 等长改写，只动尾巴。mtime 也要推进，否则走的是"完全未变"那条路。
        File.WriteAllText(path, new string('a', 8192) + "TAIL-TWO");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(
            _root, await ScanAsync(_root), previous, fullHashDeferred: _ => true);

        var c = Change(diff, "big.bin");
        Assert.Equal(ChangeKind.Modified, c.Kind);
        Assert.Equal(0, counter.FullCalls);      // 要害：全文那一趟没付
        Assert.Equal(0, counter.IdentityCalls);
        Assert.Equal(1, counter.HeadCalls);
        Assert.Equal(1, counter.TailCalls);
    }

    /// <summary>
    /// 头尾都一样时仍必须读全文——那是分清"内容真变了"和"只是被 touch 了一下"的唯一依据。
    /// 省掉它就只能一律当作变更，等于每次 touch 都把文件重传一遍。
    /// </summary>
    [Fact]
    public async Task Matching_Head_And_Tail_Still_Costs_The_Full_Read()
    {
        var path = Write("big.bin", new string('a', 8192) + "SAME-TAIL");
        var previous = await SnapshotWithTailAsync();

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)); // 只碰 mtime

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(
            _root, await ScanAsync(_root), previous, fullHashDeferred: _ => true);

        Assert.Equal(ChangeKind.MetadataOnly, Change(diff, "big.bin").Kind);
        Assert.Equal(1, counter.FullCalls);
    }

    /// <summary>
    /// 打包成员不做尾部早退：它们小，而且判成 Modified 之后 fullHash 仍要算了写进索引——
    /// 早退一步省不下什么，白付一次 open + seek。
    /// </summary>
    [Fact]
    public async Task A_Packed_Member_Skips_The_Tail_Probe()
    {
        var path = Write("small.txt", "0123456789");
        var previous = await SnapshotWithTailAsync();

        File.WriteAllText(path, "0123456ABC"); // 等长、尾部不同
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(
            _root, await ScanAsync(_root), previous, fullHashDeferred: _ => false);

        Assert.Equal(ChangeKind.Modified, Change(diff, "small.txt").Kind);
        Assert.Equal(0, counter.TailCalls);     // 没有多问那一次
        Assert.Equal(1, counter.IdentityCalls); // 一遍读拿全三段
    }

    /// <summary>
    /// 一个确定变更的文件只读**一遍**。三段 hash 从前是分三次各开一次文件算的，而全文那一趟
    /// 本来就路过头和尾——首次备份几十万个小文件，那就是几十万次多余的 open + seek。
    /// </summary>
    [Fact]
    public async Task A_Changed_File_Is_Read_Once_Not_Three_Times()
    {
        Write("a.txt", "aaa");
        var hasher = new CountingHasher(new FileHasher());

        var diff = await new BackupDiffer(hasher).DiffAsync(_root, await ScanAsync(_root), previous: null);

        Assert.Equal(1, hasher.IdentityCalls);
        Assert.Equal(0, hasher.HeadCalls);
        Assert.Equal(0, hasher.FullCalls);
        var c = Change(diff, "a.txt");
        Assert.NotNull(c.HeadHash);
        Assert.NotNull(c.FullHash);
        Assert.NotNull(c.TailHash);
    }

    /// <summary>
    /// 全文 hash 被延后时（单文件 blob）只读头 4KB。**尾部一趟都不必付**——那条路的三段
    /// 都由压缩那一遍顺手算出并覆盖，在这里算等于白读一次。
    /// </summary>
    [Fact]
    public async Task A_Deferred_Full_Hash_Costs_Only_The_Head_Read()
    {
        Write("big.bin", new string('x', 8192));
        var hasher = new CountingHasher(new FileHasher());

        var diff = await new BackupDiffer(hasher).DiffAsync(
            _root, await ScanAsync(_root), previous: null, fullHashDeferred: _ => true);

        Assert.Equal(1, hasher.HeadCalls);
        Assert.Equal(0, hasher.FullCalls);
        Assert.Equal(0, hasher.IdentityCalls);
        var c = Change(diff, "big.bin");
        Assert.Null(c.FullHash);
        Assert.Null(c.TailHash);
    }

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
    public async Task Length_Change_Is_Modified_And_Records_Both_Hashes()
    {
        var path = Write("a.txt", "hello");
        var previous = await SnapshotAsync();

        File.WriteAllText(path, "hello world!"); // 长度变

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(_root, await ScanAsync(_root), previous);

        var c = Change(diff, "a.txt");
        Assert.Equal(ChangeKind.Modified, c.Kind);
        // 索引条目须含完整哈希：headHash + fullHash 都记录
        Assert.NotNull(c.HeadHash);
        Assert.NotNull(c.FullHash);
        // 两个 hash 都在，但只读了**一遍**：它们连同尾部一起来自同一趟读，不再各开一次文件。
        Assert.Equal(1, counter.IdentityCalls);
        Assert.Equal(0, counter.HeadCalls);
        Assert.Equal(0, counter.FullCalls);
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

    /// <summary>
    /// 单文件 blob 的全文 hash 由压缩那一遍读顺手算出，还会覆盖 diff 记的值——diff 再读一遍
    /// 就是把每个大文件从头到尾读了两遍。用户实测：一个接近 100 GB 的文件，diff 阶段光是为了
    /// 算这个用不上的 hash 就要读满 100 GB，而那段时间网络上一个字节都没在传。
    /// </summary>
    [Fact]
    public async Task Deferred_Paths_Are_Not_Read_Whole_When_They_Are_New()
    {
        Write("big.bin", "pretend this is 100 GB");
        Write("small.txt", "packed with others");

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(
            _root, await ScanAsync(_root), previous: null, fullHashDeferred: p => p == "big.bin");

        var big = Change(diff, "big.bin");
        Assert.Equal(ChangeKind.Added, big.Kind);
        Assert.Null(big.FullHash);      // 延后：压缩那一遍会算出来并写进索引
        Assert.NotNull(big.HeadHash);   // 4KB 的头照读——顺带把"此刻打得开吗"问清楚了

        // 打包的那些不受影响：它们的 hash 是装箱时就要写进 pack 成员的，没有第二次机会补算。
        Assert.NotNull(Change(diff, "small.txt").FullHash);
        // 延后的那个只付了一趟 4KB 的头读——尾部也不算（压缩那一遍会连三段一起给出来）。
        Assert.Equal(1, counter.HeadCalls);
        Assert.Equal(0, counter.FullCalls);
        // 要算全文的那个走一遍读，三段一起拿到。
        Assert.Equal(1, counter.IdentityCalls);

        // 变更统计只看长度，不受影响——界面上的 "N changed" 不能因为这个优化少数几个。
        Assert.Equal(2, diff.ChangedFiles);
    }

    [Fact]
    public async Task Deferred_Paths_Are_Not_Read_Whole_When_Their_Length_Changed()
    {
        var path = Write("big.bin", "hello");
        var previous = await SnapshotAsync();

        File.WriteAllText(path, "hello world!"); // 长度变 → 已经确定内容变了，hash 只剩生成地址一个用途

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(
            _root, await ScanAsync(_root), previous, fullHashDeferred: _ => true);

        var c = Change(diff, "big.bin");
        Assert.Equal(ChangeKind.Modified, c.Kind);
        Assert.Null(c.FullHash);
        Assert.Equal(0, counter.FullCalls);
    }

    /// <summary>
    /// 这一条是整个优化的边界，也是省错了就会静默烧钱的地方：length 没变、只有 mtime 或权限被碰过时，
    /// fullHash 是区分「只是 touch 了一下」（MetadataOnly，不重传）与「内容真的变了」（Modified）
    /// 的**唯一**依据。在这条路上也省掉它，就只能一律当成变更——每次 touch 都把文件重传一遍。
    /// </summary>
    [Fact]
    public async Task A_Touched_File_Is_Still_Hashed_In_Full_Even_When_Deferral_Is_On()
    {
        var path = Write("big.bin", "same content");
        var previous = await SnapshotAsync();

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(30)); // 内容没动

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(
            _root, await ScanAsync(_root), previous, fullHashDeferred: _ => true);

        var c = Change(diff, "big.bin");
        Assert.Equal(ChangeKind.MetadataOnly, c.Kind);
        Assert.Equal(1, counter.FullCalls);
        Assert.NotNull(c.FullHash);
        Assert.NotNull(c.CarriedStorage);   // 沿用旧存储 = 一个字节都不重传
        Assert.Equal(0, diff.ChangedFiles);
    }

    /// <summary>
    /// 省掉的读也要从进度里省掉。按整份文件计，一个 100 GB 的延后条目会在一瞬间被记成 100 GB 已读，
    /// diff 的速度读数冲到几十 GB/s，剩余时间跟着变成一句笑话。
    /// </summary>
    [Fact]
    public async Task Deferred_Files_Do_Not_Inflate_The_Read_Byte_Count()
    {
        Write("big.bin", new string('x', 4096));
        Write("small.txt", new string('y', 100));

        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 2, seen.Add);
        await new BackupDiffer(new FileHasher()).DiffAsync(
            _root, await ScanAsync(_root), previous: null, tracker: tracker,
            fullHashDeferred: p => p == "big.bin");
        tracker.Complete();

        Assert.Equal(100, seen[^1].Bytes); // 只有真读全了的那个算数
        Assert.Equal(2, seen[^1].Processed); // 条目数照常推进，进度条不受影响
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
