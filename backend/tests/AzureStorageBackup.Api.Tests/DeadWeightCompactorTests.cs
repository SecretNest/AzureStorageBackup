using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class DeadWeightCompactorTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _packSrc; // where the pack's original members come from (used to build the pack blob)
    private readonly string _local;   // local source root for dead-weight compaction
    private readonly string _temp;

    public DeadWeightCompactorTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-dwc-" + Guid.NewGuid().ToString("N"));
        _packSrc = Path.Combine(_base, "packsrc");
        _local = Path.Combine(_base, "local");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_packSrc);
        Directory.CreateDirectory(_local);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private static void Write(string dir, string rel, string content)
    {
        var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // Builds pack blob packs/p0001.7z (members a/b/c) + info + liveByPack (b/c live, a dead weight).
    private async Task<(BackupInfoFile Info, Dictionary<string, Dictionary<string, LivePackMember>> Live,
        Azure.Storage.Blobs.BlobContainerClient Container, Account Account)> SetupAsync(string name)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        Write(_packSrc, "a.txt", new string('a', 2000));
        Write(_packSrc, "b.txt", new string('b', 2000));
        Write(_packSrc, "c.txt", new string('c', 2000));

        var hasher = new FileHasher();
        var hashA = await hasher.FullHashAsync(Path.Combine(_packSrc, "a.txt"));
        var hashB = await hasher.FullHashAsync(Path.Combine(_packSrc, "b.txt"));
        var hashC = await hasher.FullHashAsync(Path.Combine(_packSrc, "c.txt"));

        var compressor = new SevenZipCompressor();
        var output = Path.Combine(_temp, "p0001.7z");
        var result = await compressor.CompressAsync(
            new CompressionRequest(_packSrc, ["a.txt", "b.txt", "c.txt"], output, null));
        await new BlobUploader(factory).UploadIfMissingAsync(
            account, name, "packs/p0001.7z", result.VolumeFiles[0], AccessTier.Hot);

        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow },
            Packs =
            {
                ["p0001"] = new PackInfo
                {
                    Blob = "packs/p0001.7z",
                    Members = [hashA, hashB, hashC],
                    OriginalBytes = 6000,
                },
            },
        };
        // b and c are still referenced by a live version; a is dead weight (1/3 > 30%). liveByPack is grouped by entryName.
        var live = new Dictionary<string, Dictionary<string, LivePackMember>>
        {
            ["p0001"] = new(StringComparer.Ordinal)
            {
                ["b.txt"] = new LivePackMember("b.txt", 2000, hashB),
                ["c.txt"] = new LivePackMember("c.txt", 2000, hashC),
            },
        };
        return (info, live, container, account);
    }

    private DeadWeightCompactor Compactor() =>
        new(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), new SevenZipCompressor(), new FileHasher(),
            Path.Combine(_temp, "compact"), Staging());

    /// <summary>Compaction's compression output now goes through the staging area (global compression lock + budget), so every construction site has to supply one.</summary>
    private StagingArea Staging() =>
        new(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);

    private async Task<List<string>> PackEntriesAsync(Azure.Storage.Blobs.BlobContainerClient container)
    {
        var work = Path.Combine(_temp, "verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var first = await VolumeBlobIO.DownloadAsync(container, "packs/p0001.7z", work, CancellationToken.None);
        var ex = Path.Combine(work, "x");
        await new SevenZipCompressor().ExtractAsync(first, ex, null, CancellationToken.None);
        return Directory.EnumerateFiles(ex, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f)).OrderBy(x => x).ToList();
    }

    [SkippableFact]
    public async Task Compacts_From_Local_Without_Download_Even_When_Download_Disabled()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var name = RandomName("dwc-local-");
        var (info, live, container, account) = await SetupAsync(name);
        try
        {
            // b and c exist locally and match the pack → compaction works from local files even with downloads forbidden (the Archive scenario).
            Write(_local, "b.txt", new string('b', 2000));
            Write(_local, "c.txt", new string('c', 2000));

            // allowDownload:false → members can only come from local files; that compaction succeeds proves local files were used (the Archive scenario likewise).
            await Compactor().CompactAsync(account, container, null, info, live,
                AccessTier.Hot, null, threshold: 0.30, _local, allowDownload: false, CancellationToken.None);

            Assert.Equal(2, info.Packs["p0001"].Members.Count);
            Assert.Equal(0, info.Packs["p0001"].DeadBytes);
            Assert.Equal(["b.txt", "c.txt"], await PackEntriesAsync(container)); // a has been dropped
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Abandons_When_Member_Missing_Locally_And_Download_Disabled()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var name = RandomName("dwc-abandon-");
        var (info, live, container, account) = await SetupAsync(name);
        try
        {
            // Only b is local, c is missing, and downloads are forbidden → give up on repacking, members unchanged, dead weight recorded.
            Write(_local, "b.txt", new string('b', 2000));

            await Compactor().CompactAsync(account, container, null, info, live,
                AccessTier.Archive, null, threshold: 0.30, _local, allowDownload: false, CancellationToken.None);

            Assert.Equal(3, info.Packs["p0001"].Members.Count); // not compacted
            Assert.Equal(2000, info.Packs["p0001"].DeadBytes);  // dead weight recorded
            Assert.Equal(["a.txt", "b.txt", "c.txt"], await PackEntriesAsync(container)); // pack untouched
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Keeps_Both_Members_That_Share_Identical_Content()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("dwc-dup-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            // The pack holds a (dead weight) + b and d, two members with **identical content** (the same fullHash after dedup, but still two independent members).
            Write(_packSrc, "a.txt", new string('a', 2000));
            Write(_packSrc, "b.txt", new string('s', 2000));
            Write(_packSrc, "d.txt", new string('s', 2000)); // same content as b

            var hasher = new FileHasher();
            var hashA = await hasher.FullHashAsync(Path.Combine(_packSrc, "a.txt"));
            var hashDup = await hasher.FullHashAsync(Path.Combine(_packSrc, "b.txt"));
            Assert.Equal(hashDup, await hasher.FullHashAsync(Path.Combine(_packSrc, "d.txt")));

            var output = Path.Combine(_temp, "p0001.7z");
            var result = await new SevenZipCompressor().CompressAsync(
                new CompressionRequest(_packSrc, ["a.txt", "b.txt", "d.txt"], output, null));
            await new BlobUploader(factory).UploadIfMissingAsync(
                account, name, "packs/p0001.7z", result.VolumeFiles[0], AccessTier.Hot);

            var info = new BackupInfoFile
            {
                Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow },
                Packs = { ["p0001"] = new PackInfo { Blob = "packs/p0001.7z", Members = [hashA, hashDup, hashDup], OriginalBytes = 6000 } },
            };
            // b and d are both live (same hash, different entryName); a is dead weight. Grouping by hash would fold b and d into one → data loss.
            var live = new Dictionary<string, Dictionary<string, LivePackMember>>
            {
                ["p0001"] = new(StringComparer.Ordinal)
                {
                    ["b.txt"] = new LivePackMember("b.txt", 2000, hashDup),
                    ["d.txt"] = new LivePackMember("d.txt", 2000, hashDup),
                },
            };
            Write(_local, "b.txt", new string('s', 2000));
            Write(_local, "d.txt", new string('s', 2000));

            await Compactor().CompactAsync(account, container, null, info, live,
                AccessTier.Hot, null, threshold: 0.30, _local, allowDownload: false, CancellationToken.None);

            // The crux: both members with identical content must be kept (each with its own entryName), or the index still references something that is already gone → data loss.
            Assert.Equal(["b.txt", "d.txt"], await PackEntriesAsync(container));
            Assert.Equal(2, info.Packs["p0001"].Members.Count);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The real line of defence for T5: <see cref="RetentionCleaner"/> scans the retained versions' indexes and groups
    /// each pack's live members by EntryName (liveByPack), while DeadWeightCompactor merely consumes that table to decide which members survive the recompression.
    /// <see cref="Keeps_Both_Members_That_Share_Identical_Content"/> above only verifies DeadWeightCompactor's
    /// behaviour once it has been handed a "correct" liveByPack — that case's liveByPack is assembled by hand in the test and fed straight to CompactAsync,
    /// so the grouping code inside RetentionCleaner never actually runs and the case would notice nothing even if that grouping code were itself wrong.
    /// If the grouping were changed to key on fullHash (the comment in RetentionCleaner.cs says explicitly "hash cannot be the key",
    /// which is precisely a sign it has been / could be changed wrongly), two live members with different EntryNames and the same fullHash would merge into one row in that table,
    /// the recompressed archive would be one member short while the version index still claims it is there — silent data loss, with no test
    /// on this chain able to catch it before. Here CleanupAsync really runs end to end (a real DeadWeightCompactor, not null), so what is pinned down is
    /// the grouping code itself, rather than a way around it.
    /// </summary>
    [SkippableFact]
    public async Task Retention_Cleanup_Preserves_Two_Live_Members_That_Share_A_Hash()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("dwc-cleaner-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            // The pack holds a (which turns into dead weight when v1 retires) + b and d, two members with identical content (same fullHash) but different EntryNames —
            // a historical pack shape that already existed before this feature shipped (same content at different paths; 7z does not share a dictionary across members).
            Write(_packSrc, "a.txt", new string('a', 2000));
            Write(_packSrc, "b.txt", new string('s', 2000));
            Write(_packSrc, "d.txt", new string('s', 2000));

            var hasher = new FileHasher();
            var hashA = await hasher.FullHashAsync(Path.Combine(_packSrc, "a.txt"));
            var hashDup = await hasher.FullHashAsync(Path.Combine(_packSrc, "b.txt"));
            Assert.Equal(hashDup, await hasher.FullHashAsync(Path.Combine(_packSrc, "d.txt")));

            var output = Path.Combine(_temp, "p0001.7z");
            var result = await new SevenZipCompressor().CompressAsync(
                new CompressionRequest(_packSrc, ["a.txt", "b.txt", "d.txt"], output, null));
            await new BlobUploader(factory).UploadIfMissingAsync(
                account, name, "packs/p0001.7z", result.VolumeFiles[0], AccessTier.Hot);

            var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());

            // v1: its only purpose is to retire and thereby free up "dead weight" — its index content is irrelevant to this case's criterion, so leaving it empty is fine.
            var (v1Blob, _) = await store.WriteIndexAsync(account, name, 1, new VersionIndex { Version = 1 }, null);

            // v2 (the retained version): two entries pointing at the same pack, same fullHash, different EntryNames.
            var v2Index = new VersionIndex
            {
                Version = 2,
                Entries =
                [
                    new IndexEntry
                    {
                        Path = "x/b.txt", Kind = "file", Length = 2000, Mtime = DateTimeOffset.UtcNow,
                        Permissions = "644", FullHash = hashDup,
                        Storage = new StorageRef { Kind = "pack", Ref = "p0001", EntryName = "b.txt" },
                    },
                    new IndexEntry
                    {
                        Path = "y/d.txt", Kind = "file", Length = 2000, Mtime = DateTimeOffset.UtcNow,
                        Permissions = "644", FullHash = hashDup,
                        Storage = new StorageRef { Kind = "pack", Ref = "p0001", EntryName = "d.txt" },
                    },
                ],
            };
            var (v2Blob, _) = await store.WriteIndexAsync(account, name, 2, v2Index, null);

            var info = new BackupInfoFile
            {
                Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow },
                Versions =
                {
                    new BackupVersion
                    {
                        Version = 1, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10), IndexBlob = v1Blob,
                        Stats = new VersionStats(1, 2000, 1, 2000),
                    },
                    new BackupVersion
                    {
                        Version = 2, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = v2Blob,
                        Stats = new VersionStats(2, 4000, 2, 4000),
                    },
                },
                Packs = { ["p0001"] = new PackInfo { Blob = "packs/p0001.7z", Members = [hashA, hashDup, hashDup], OriginalBytes = 6000 } },
            };

            Write(_local, "b.txt", new string('s', 2000));
            Write(_local, "d.txt", new string('s', 2000));

            // A real compactor (not null): CleanupAsync really runs the grouping code internally, instead of
            // hand-assembling liveByPack and feeding it straight to CompactAsync the way the case above does.
            var cleaner = new RetentionCleaner(factory, store, new RetentionEvaluator(), Compactor());
            var options = new CleanupOptions
            {
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
                DataTier = AccessTier.Hot,
                DeadWeightThreshold = 0.30,
                LocalRoot = _local,
                AllowRepackDownload = false,
            };

            var report = await cleaner.CleanupAsync(account, name, null, options, info, CancellationToken.None);

            Assert.Equal(1, report.RetiredVersions); // v1 retires, which is what creates the dead weight (otherwise the 30% threshold never triggers a recompression)
            // The crux: both live members with the same fullHash and different EntryNames must stay in the archive;
            // grouping by hash merges them into one row, the recompressed archive keeps only one, and the other entry in the v2 index still claims it is there — data loss with no trace.
            Assert.Equal(["b.txt", "d.txt"], await PackEntriesAsync(container));
            Assert.Equal(2, info.Packs["p0001"].Members.Count);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>A decorating uploader: every upload (if-missing and overwrite alike) throws, simulating a crash during the "upload the new one" phase.</summary>
    private sealed class FailingUploader : IBlobUploader
    {
        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new IOException("injected upload failure");

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new IOException("injected upload failure");
    }

    /// <summary>Records the order of overwrite uploads and really delegates the upload to inner (so ReplaceAsync's residual deletion takes effect).</summary>
    private sealed class RecordingUploader(IBlobUploader inner) : IBlobUploader
    {
        public List<string> OverwriteOrder { get; } = [];

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            OverwriteOrder.Add(blobName);
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    // A crash during the upload-the-new-one phase: the old pack's volumes are still fully present (the earlier delete-first order emptied this → the whole blob was lost).
    [SkippableFact]
    public async Task Recompact_Failure_During_Upload_Leaves_Old_Volumes_Intact()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var name = RandomName("dwc-failupload-");
        var (info, live, container, account) = await SetupAsync(name);
        try
        {
            Write(_local, "b.txt", new string('b', 2000));
            Write(_local, "c.txt", new string('c', 2000));

            // Use the decorating "throw on upload" uploader; CompactAsync swallows the exception per pack, and the point is that the old data must still be there after the failure.
            var compactor = new DeadWeightCompactor(
                new FailingUploader(),
                new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact-fail"), Staging());

            await compactor.CompactAsync(account, container, null, info, live,
                AccessTier.Hot, null, threshold: 0.30, _local, allowDownload: false, CancellationToken.None);

            var remaining = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, "packs/p0001.7z", CancellationToken.None))
                remaining.Add(b.Name);
            Assert.NotEmpty(remaining); // delete-first would leave this empty

            // The old pack's content is undamaged, still the original 3 members.
            Assert.Equal(["a.txt", "b.txt", "c.txt"], await PackEntriesAsync(container));
            // Dead weight recorded, members unchanged (this round of compaction gave up).
            Assert.Equal(3, info.Packs["p0001"].Members.Count);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Locks one member in the compose directory at the very moment of compression: 7z cannot read it, silently drops it, and still produces a valid archive.</summary>
    private sealed class LockMemberDuringCompressCompressor(IFileCompressor inner, string entryName) : IFileCompressor
    {
        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var full = Path.Combine(request.SourceDirectory, entryName);
            var lockIt = File.Exists(full);
            if (lockIt)
                File.SetUnixFileMode(full, UnixFileMode.None);
            try
            {
                return await inner.CompressAsync(request, ct);
            }
            finally
            {
                if (lockIt)
                    File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
            => inner.CompressStreamAsync(request, writeSource, ct);
    }

    /// <summary>Repacking is **overwrite-based** (ReplaceAsync rewrites packs/p0001.7z directly), so 7z dropping a
    /// member on this path is far worse than during a backup: that member is still referenced by a live version, and once
    /// the old pack is overwritten by a new one that lacks it the data is gone for good, while the index goes on claiming
    /// it is in there. Now that the compressor verifies the archive's contents, this round of compaction fails and is
    /// caught by the per-pack fallback — better to forgo one space optimization than to overwrite with a pack missing a member.</summary>
    [SkippableFact]
    public async Task Recompact_Never_Overwrites_A_Good_Pack_With_One_Missing_A_Member()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var name = RandomName("dwc-dropped-");
        var (info, live, container, account) = await SetupAsync(name);
        try
        {
            Write(_local, "b.txt", new string('b', 2000));
            Write(_local, "c.txt", new string('c', 2000));

            var compactor = new DeadWeightCompactor(
                new BlobUploader(new BlobClientFactory(TestSecrets.Reader)),
                new LockMemberDuringCompressCompressor(new SevenZipCompressor(), "b.txt"),
                new FileHasher(), Path.Combine(_temp, "compact-dropped"), Staging());

            await compactor.CompactAsync(account, container, null, info, live,
                AccessTier.Hot, null, threshold: 0.30, _local, allowDownload: false, CancellationToken.None);

            // The old pack is untouched: before the fix this came back with only c.txt — b.txt still referenced yet gone from the cloud.
            Assert.Equal(["a.txt", "b.txt", "c.txt"], await PackEntriesAsync(container));
            Assert.Equal(3, info.Packs["p0001"].Members.Count); // this round of compaction gave up, member list unchanged
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    // ReplaceAsync: overwrite-upload the new volumes + delete the residual old ones (the tail left when there are fewer new volumes than old).
    [SkippableFact]
    public async Task ReplaceAsync_Overwrites_New_And_Deletes_Residual_Volumes()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("vbio-replace-");
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();
        try
        {
            const string baseRef = "data/x.7z";
            // Old: 3 volumes.
            await cc.GetBlobClient(baseRef + ".001").UploadAsync(BinaryData.FromString("OLD1"), overwrite: true);
            await cc.GetBlobClient(baseRef + ".002").UploadAsync(BinaryData.FromString("OLD2"), overwrite: true);
            await cc.GetBlobClient(baseRef + ".003").UploadAsync(BinaryData.FromString("OLD3"), overwrite: true);

            // New: 2 volumes.
            var f1 = Path.Combine(_temp, "n1"); File.WriteAllText(f1, "NEW1");
            var f2 = Path.Combine(_temp, "n2"); File.WriteAllText(f2, "NEW2");

            var rec = new RecordingUploader(new BlobUploader(factory));
            await VolumeBlobIO.ReplaceAsync(rec, account, cc, baseRef, [f1, f2], AccessTier.Hot, retry: null, CancellationToken.None);

            // Every volume was written. The order no longer matters — the first volume used to be the commit marker
            // for "the whole family is complete", and that semantics was deleted along with cloud-existence dedup.
            Assert.Equal([baseRef + ".001", baseRef + ".002"], rec.OverwriteOrder);
            // The new content overwrites the old volumes.
            Assert.Equal("NEW1", (await cc.GetBlobClient(baseRef + ".001").DownloadContentAsync()).Value.Content.ToString());
            Assert.Equal("NEW2", (await cc.GetBlobClient(baseRef + ".002").DownloadContentAsync()).Value.Content.ToString());
            // The residual old volume .003 has been deleted.
            Assert.False((await cc.GetBlobClient(baseRef + ".003").ExistsAsync()).Value);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Downloads_To_Fill_Missing_Members_When_Download_Enabled()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var name = RandomName("dwc-dl-");
        var (info, live, container, account) = await SetupAsync(name);
        try
        {
            // Nothing is available locally, but downloads are allowed → download the old pack, extract it to fill in b and c, then compact.
            await Compactor().CompactAsync(account, container, null, info, live,
                AccessTier.Hot, null, threshold: 0.30, _local, allowDownload: true, CancellationToken.None);

            Assert.Equal(2, info.Packs["p0001"].Members.Count);
            Assert.Equal(["b.txt", "c.txt"], await PackEntriesAsync(container));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
