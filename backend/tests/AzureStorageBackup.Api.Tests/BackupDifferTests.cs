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

    /// <summary>Same as SnapshotAsync but also records the tail hash in each entry — the tail early exit needs it as its comparison baseline.</summary>
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
    /// A completely unchanged file **pays no IO at all**, and a missing tail is not backfilled. There used to be a backfill here so old backups
    /// would self-heal; it was removed: an unchanged file never touches the disk, so reading it for this one component is a random read conjured
    /// out of nothing (close to an hour for 500k small files on a NAS spinning disk), while the hardening it buys has vanishingly small marginal value.
    /// This assertion is **deliberate**: whoever wants the self-healing back has to explain here first why that IO is worth it.
    /// </summary>
    [Fact]
    public async Task An_Unchanged_File_Costs_No_IO_Even_If_Its_Tail_Is_Missing()
    {
        Write("a.txt", "unchanged");
        var previous = await SnapshotAsync();   // SnapshotAsync records no tail — exactly what an old index looks like
        Assert.Null(previous.Entries.Single(e => e.Path == "a.txt").TailHash);

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(_root, await ScanAsync(_root), previous);

        var c = Change(diff, "a.txt");
        Assert.Equal(ChangeKind.Unchanged, c.Kind);
        Assert.Null(c.TailHash);
        Assert.Equal(0, counter.TailCalls);
        Assert.Equal(0, counter.HeadCalls);
        Assert.Equal(0, counter.FullCalls);
        Assert.Equal(0, counter.IdentityCalls);
    }

    /// <summary>
    /// A large file with unchanged length but changed mtime (full hash deferrable): a mismatching tail should settle it on the spot, **without reading the whole file**.
    /// This is the most expensive pass — a 100 GB file means 100 GB of reads — and "the content changed" is already established by the time
    /// the 4KB tail has been read. Database files, virtual disks and overwritten logs are all typical cases of the length staying put while the tail moves first.
    /// </summary>
    [Fact]
    public async Task A_Differing_Tail_Settles_It_Without_Reading_The_Whole_File()
    {
        var path = Write("big.bin", new string('a', 8192) + "TAIL-ONE");
        var previous = await SnapshotWithTailAsync();

        // Rewrite at the same length, touching only the tail. mtime has to advance too, otherwise we take the "completely unchanged" path.
        File.WriteAllText(path, new string('a', 8192) + "TAIL-TWO");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(
            _root, await ScanAsync(_root), previous, fullHashDeferred: _ => true);

        var c = Change(diff, "big.bin");
        Assert.Equal(ChangeKind.Modified, c.Kind);
        Assert.Equal(0, counter.FullCalls);      // the crux: the full-file pass was never paid for
        Assert.Equal(0, counter.IdentityCalls);
        Assert.Equal(1, counter.HeadCalls);
        Assert.Equal(1, counter.TailCalls);
    }

    /// <summary>
    /// When head and tail both match the whole file must still be read — that is the only basis for telling "the content really changed" from "it just got touched".
    /// Skipping it means treating everything as changed, i.e. every touch re-uploads the file.
    /// </summary>
    [Fact]
    public async Task Matching_Head_And_Tail_Still_Costs_The_Full_Read()
    {
        var path = Write("big.bin", new string('a', 8192) + "SAME-TAIL");
        var previous = await SnapshotWithTailAsync();

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)); // touch only the mtime

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(
            _root, await ScanAsync(_root), previous, fullHashDeferred: _ => true);

        Assert.Equal(ChangeKind.MetadataOnly, Change(diff, "big.bin").Kind);
        // One read — since the whole file is being read anyway, take all three segments, with the tail picked up along the way.
        Assert.Equal(1, counter.IdentityCalls);
        Assert.Equal(0, counter.FullCalls);
    }

    /// <summary>
    /// Pack members do not get the tail early exit: they are small, and once classified Modified the fullHash still has to be computed and written into the index —
    /// exiting early saves nothing and just wastes one open + seek.
    /// </summary>
    [Fact]
    public async Task A_Packed_Member_Skips_The_Tail_Probe()
    {
        var path = Write("small.txt", "0123456789");
        var previous = await SnapshotWithTailAsync();

        File.WriteAllText(path, "0123456ABC"); // same length, different tail
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(
            _root, await ScanAsync(_root), previous, fullHashDeferred: _ => false);

        Assert.Equal(ChangeKind.Modified, Change(diff, "small.txt").Kind);
        Assert.Equal(0, counter.TailCalls);     // that extra probe was never made
        Assert.Equal(1, counter.IdentityCalls); // one read, all three segments
    }

    /// <summary>
    /// A file known to have changed is read **once**. The three hash segments used to be computed by opening the file three separate times, even though
    /// the full-file pass already goes past the head and the tail — on a first backup of a few hundred thousand small files, that is a few hundred thousand redundant open + seek pairs.
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
    /// When the full hash is deferred (single-file blob) only the leading 4KB is read. **The tail costs not a single pass** — all three segments on that path
    /// fall out of the compression pass for free and overwrite these values, so computing it here is a wasted read.
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
        Assert.Equal(0, counter.HeadCalls); // length+mtime+perms all match → skip hashing entirely
        Assert.Equal(0, counter.FullCalls);
        // Unchanged entries carry the previous version's hashes and storage forward
        Assert.Equal(previous.Entries.Single(e => e.Path == "a.txt").FullHash, Change(diff, "a.txt").FullHash);
        Assert.NotNull(Change(diff, "a.txt").CarriedStorage);
    }

    [Fact]
    public async Task Content_Change_Same_Length_Is_Modified()
    {
        var path = Write("a.txt", "hello");
        var previous = await SnapshotAsync();

        File.WriteAllText(path, "world"); // same length, different content
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

        File.WriteAllText(path, "hello world!"); // length changed

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(_root, await ScanAsync(_root), previous);

        var c = Change(diff, "a.txt");
        Assert.Equal(ChangeKind.Modified, c.Kind);
        // An index entry must carry the complete hashes: both headHash and fullHash are recorded
        Assert.NotNull(c.HeadHash);
        Assert.NotNull(c.FullHash);
        // Both hashes are there, but the file was read **once**: they come from the same pass along with the tail, no longer one file open each.
        Assert.Equal(1, counter.IdentityCalls);
        Assert.Equal(0, counter.HeadCalls);
        Assert.Equal(0, counter.FullCalls);
    }

    [Fact]
    public async Task Metadata_Only_Change_Reuses_Content()
    {
        var path = Write("a.txt", "same content");
        var previous = await SnapshotAsync();

        // Content unchanged, only mtime touched (triggers the two-level hashing, but both levels match)
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(30));

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(_root, await ScanAsync(_root), previous);

        var c = Change(diff, "a.txt");
        Assert.Equal(ChangeKind.MetadataOnly, c.Kind);
        Assert.Equal(1, counter.HeadCalls);   // ask the head first; only go further when it matches
        // Once the head matches the whole file has to be read to tell "really changed" from "just touched" — one read, all three segments.
        Assert.Equal(1, counter.IdentityCalls);
        Assert.Equal(0, counter.FullCalls);
        Assert.Equal(previous.Entries.Single(e => e.Path == "a.txt").FullHash, c.FullHash);
        Assert.NotNull(c.CarriedStorage);            // reuse the old storage, no re-upload
        Assert.Equal(0, diff.ChangedFiles);          // metadata-only does not count as a change
    }

    /// <summary>
    /// A single-file blob's full hash falls out of the compression read for free and then overwrites whatever the diff recorded — so having the diff
    /// read it too means reading every large file end to end twice. Measured by the user: for a file close to 100 GB, the diff stage reads a full
    /// 100 GB purely to compute a hash nobody uses, and during all that time not one byte is going over the network.
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
        Assert.Null(big.FullHash);      // deferred: the compression pass computes it and writes it into the index
        Assert.NotNull(big.HeadHash);   // the 4KB head is still read — which also settles "can it be opened right now"

        // The packed ones are unaffected: their hash has to be written into the pack member at boxing time, with no second chance to backfill it.
        Assert.NotNull(Change(diff, "small.txt").FullHash);
        // The deferred one paid only a single 4KB head read — no tail either (the compression pass hands back all three segments together).
        Assert.Equal(1, counter.HeadCalls);
        Assert.Equal(0, counter.FullCalls);
        // The one that needs the full hash takes one read and gets all three segments.
        Assert.Equal(1, counter.IdentityCalls);

        // The change statistics look only at length and are unaffected — the "N changed" in the UI must not lose a few entries to this optimization.
        Assert.Equal(2, diff.ChangedFiles);
    }

    [Fact]
    public async Task Deferred_Paths_Are_Not_Read_Whole_When_Their_Length_Changed()
    {
        var path = Write("big.bin", "hello");
        var previous = await SnapshotAsync();

        File.WriteAllText(path, "hello world!"); // length changed → the content is already known to have changed, so the hash has only one use left: generating the address

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(
            _root, await ScanAsync(_root), previous, fullHashDeferred: _ => true);

        var c = Change(diff, "big.bin");
        Assert.Equal(ChangeKind.Modified, c.Kind);
        Assert.Null(c.FullHash);
        Assert.Equal(0, counter.FullCalls);
    }

    /// <summary>
    /// This case is the boundary of the whole optimization, and the place where skipping the wrong read silently burns money: when the length has not changed
    /// and only mtime or permissions were touched, fullHash is the **only** basis for distinguishing "it just got touched" (MetadataOnly, no re-upload) from
    /// "the content really changed" (Modified). Skipping it on this path too means treating everything as changed — every touch re-uploads the file.
    /// </summary>
    [Fact]
    public async Task A_Touched_File_Is_Still_Hashed_In_Full_Even_When_Deferral_Is_On()
    {
        var path = Write("big.bin", "same content");
        var previous = await SnapshotAsync();

        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(30)); // content untouched

        var counter = new CountingHasher(new FileHasher());
        var diff = await new BackupDiffer(counter).DiffAsync(
            _root, await ScanAsync(_root), previous, fullHashDeferred: _ => true);

        var c = Change(diff, "big.bin");
        Assert.Equal(ChangeKind.MetadataOnly, c.Kind);
        // Deferral only spares the full read on the "already known to have changed" branch; this branch is deciding whether it changed at all, so the full read is mandatory.
        Assert.Equal(1, counter.IdentityCalls);
        Assert.Equal(0, counter.FullCalls);
        Assert.NotNull(c.FullHash);
        Assert.NotNull(c.CarriedStorage);   // carrying the old storage forward = not one byte re-uploaded
        Assert.Equal(0, diff.ChangedFiles);
    }

    /// <summary>
    /// A read that was skipped has to be skipped in the progress too. Counted as a whole file, a 100 GB deferred entry would be recorded as 100 GB read in an instant,
    /// the diff's throughput reading would spike to tens of GB/s, and the remaining time would turn into a joke.
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

        Assert.Equal(100, seen[^1].Bytes); // only the one that really was read in full counts
        Assert.Equal(2, seen[^1].Processed); // the entry count advances as usual; the progress bar is unaffected
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
