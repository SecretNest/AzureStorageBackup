using System.Net.Sockets;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupOrchestratorTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public BackupOrchestratorTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-orch-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
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

    private void WriteText(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void WriteBytes(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
    }

    private (BackupOrchestrator Orchestrator, IBackupInfoStore Store, BlobClientFactory Factory) Build(
        IBlobUploader? uploader = null, IFileCompressor? compressor = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"),
            staging);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            compressor ?? new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor, indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
        return (orchestrator, store, factory);
    }

    /// <summary>Tampers with the target file once, simulating "the file changed while it was being processed" (§9, PRD special note D).
    /// The timing differs between the two paths: the grouping path hashes first and compresses second, so this hooks in **after**
    /// <c>CompressAsync</c> (that is how re-verification notices the content changed); the single-file path compresses as it reads,
    /// so the rewrite has to land **before** 7z starts reading for there to be any sense in which "what got compressed in is not the same copy the diff saw".</summary>
    private sealed class MutatingCompressor(
        IFileCompressor inner, string rootPath, string relPath, string newContent) : IFileCompressor
    {
        private int _fired;
        private int _firedStream;

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
        {
            if (request.EntryName == relPath && Interlocked.Exchange(ref _firedStream, 1) == 0)
                Mutate();
            return inner.CompressStreamAsync(request, writeSource, ct);
        }

        private void Mutate()
        {
            var full = Path.Combine(rootPath, relPath.Replace('/', Path.DirectorySeparatorChar));
            File.WriteAllText(full, newContent);
            File.SetLastWriteTimeUtc(full, File.GetLastWriteTimeUtc(full).AddSeconds(7));
        }
        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var result = await inner.CompressAsync(request, ct);
            if (request.Entries.Contains(relPath) && Interlocked.Exchange(ref _fired, 1) == 0)
                Mutate();
            return result;
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
    }

    /// <summary>Store decorator that counts ReadIndexAsync calls (to verify local cache hits).</summary>
    private sealed class CountingStore(IBackupInfoStore inner) : IBackupInfoStore
    {
        public int IndexReads { get; private set; }
        public int InfoReads { get; private set; }
        public Task<VersionIndex> ReadIndexAsync(Account a, string c, string b, string? p, CancellationToken ct = default)
        {
            IndexReads++;
            return inner.ReadIndexAsync(a, c, b, p, ct);
        }
        public Task<BackupInfoFile?> ReadInfoAsync(Account a, string c, string? p, CancellationToken ct = default) { InfoReads++; return inner.ReadInfoAsync(a, c, p, ct); }
        public Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account a, string c, string? p, CancellationToken ct = default) { InfoReads++; return inner.ReadInfoWithETagAsync(a, c, p, ct); }
        public Task WriteInfoAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t = null, CancellationToken ct = default) => inner.WriteInfoAsync(a, c, i, p, t, ct);
        public Task<string> WriteInfoConditionalAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t, string? e, CancellationToken ct = default) => inner.WriteInfoConditionalAsync(a, c, i, p, t, e, ct);
        public Task<string> WriteIndexAsync(Account a, string c, int v, VersionIndex i, string? p, AccessTier? t = null, CancellationToken ct = default) => inner.WriteIndexAsync(a, c, v, i, p, t, ct);
    }

    [SkippableFact]
    public async Task Second_Backup_Reads_Previous_Index_From_Local_Cache()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var counting = new CountingStore(new BackupInfoStore(factory, new SevenZipArchiveCodec()));
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AzureStorageBackup.Api.Data.AppDbContext>()
            .UseSqlite(conn).Options;
        using var db = new AzureStorageBackup.Api.Data.AppDbContext(opts);
        db.Database.EnsureCreated();
        var authority = new TestLocalAuthority(db, counting);
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, counting, staging,
            new RetentionCleaner(factory, counting, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(),
            authority.IndexCache, authority.Tracked);

        var account = AzuriteAccount();
        var name = RandomName("orchlc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "alpha");
            await orchestrator.RunAsync(Request(account, name)); // v1: no previous version; caches v1 once it is written
            await orchestrator.RunAsync(Request(account, name)); // v2: the previous version's index should hit the local cache

            Assert.Equal(0, counting.IndexReads); // never downloaded the cloud's second-level index
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Second_Backup_Does_Not_Read_Cloud_Info_File()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var counting = new CountingStore(new BackupInfoStore(factory, new SevenZipArchiveCodec()));
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<AzureStorageBackup.Api.Data.AppDbContext>().UseSqlite(conn).Options;
        using var db = new AzureStorageBackup.Api.Data.AppDbContext(opts);
        db.Database.EnsureCreated();
        var tracked = new TrackedInfoStore(counting, new LocalBackupStateStore(db));
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, counting, staging,
            new RetentionCleaner(factory, counting, new RetentionEvaluator()), new FileHasher(),
            indexCache: new LocalIndexCache(db, counting), trackedInfo: tracked);

        var account = AzuriteAccount();
        var name = RandomName("orchti-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "alpha");
            await orchestrator.RunAsync(Request(account, name)); // v1: nothing locally → one cloud read (comes back empty → create new)
            var readsAfterFirst = counting.InfoReads;
            await orchestrator.RunAsync(Request(account, name)); // v2: local is authoritative → must not read the cloud info file again

            Assert.Equal(readsAfterFirst, counting.InfoReads); // zero info-file reads on the second backup
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Records how many uploads are in flight at once, to verify upload concurrency.</summary>
    private sealed class ConcurrencyTrackingUploader(IBlobUploader inner) : IBlobUploader
    {
        private int _current;
        private int _max;
        private readonly Lock _l = new();
        public int MaxConcurrent { get { lock (_l) return _max; } }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            lock (_l) { _current++; _max = Math.Max(_max, _current); }
            try
            {
                // Make an upload clearly longer than "compress + verify archive contents" so the concurrency window
                // is stably observable. Compression is globally serial, so whether two uploads can overlap comes down
                // to whether one upload outlasts the next compression — set this delay too close to the compression
                // time and what gets measured is compression speed, not the concurrency cap.
                await Task.Delay(800, ct);
                return await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
            }
            finally { lock (_l) _current--; }
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
    }

    /// <summary>Counts data/ blob uploads (to verify dedup does not upload twice).</summary>
    private sealed class CountingUploader(IBlobUploader inner) : IBlobUploader
    {
        private int _dataUploads;
        public int DataUploads => Volatile.Read(ref _dataUploads);
        public void Reset() => Volatile.Write(ref _dataUploads, 0);

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (blobName.StartsWith("data/", StringComparison.Ordinal))
                Interlocked.Increment(ref _dataUploads);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (blobName.StartsWith("data/", StringComparison.Ordinal))
                Interlocked.Increment(ref _dataUploads);
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    private (BackupOrchestrator, IBackupInfoStore) BuildTracked(BlobClientFactory factory, IBlobUploader uploader, Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var opts = new DbContextOptionsBuilder<AzureStorageBackup.Api.Data.AppDbContext>().UseSqlite(conn).Options;
        var db = new AzureStorageBackup.Api.Data.AppDbContext(opts);
        db.Database.EnsureCreated();
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
            indexCache: new LocalIndexCache(db, store),
            trackedInfo: new TrackedInfoStore(store, new LocalBackupStateStore(db)));
        return (orchestrator, store);
    }

    [SkippableFact]
    public async Task Local_Dedup_Uploads_Identical_Content_Once_Per_Run()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var counting = new CountingUploader(new BlobUploader(factory));
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        conn.Open();
        var (orchestrator, _) = BuildTracked(factory, counting, conn);

        var account = AzuriteAccount();
        var name = RandomName("orchdd-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            WriteText("x.txt", "identical payload");
            WriteText("dir/y.txt", "identical payload"); // same content, different path

            await orchestrator.RunAsync(Request(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            Assert.Equal(1, counting.DataUploads); // two files with identical content upload only one data blob
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Cross_Version_Dedup_Uses_Local_Index_Without_Reading_Cloud()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var counting = new CountingUploader(new BlobUploader(factory));
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        conn.Open();
        var (orchestrator, _) = BuildTracked(factory, counting, conn);

        var account = AzuriteAccount();
        var name = RandomName("orchxd-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            WriteText("a.txt", "shared body");
            await orchestrator.RunAsync(Request(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            }); // v1

            // Delete the cloud data blob: if backup decided existence by a cloud HEAD, v2 would find it missing and re-upload; going by the local index it still dedups.
            await foreach (var b in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();
            counting.Reset();

            WriteText("b.txt", "shared body"); // new file, same content as a
            var v2 = await orchestrator.RunAsync(Request(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            }); // v2

            Assert.Equal(2, v2.Version);
            Assert.Equal(0, counting.DataUploads); // purely local dedup: nothing re-uploaded (proving cloud existence was never read)
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Info_Write_Conflict_Does_Not_Leave_Ghost_Version_In_Index_Cache()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<AzureStorageBackup.Api.Data.AppDbContext>().UseSqlite(conn).Options;
        using var db = new AzureStorageBackup.Api.Data.AppDbContext(opts);
        db.Database.EnsureCreated();
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(db));
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
            indexCache: new LocalIndexCache(db, store), trackedInfo: tracked);

        var account = AzuriteAccount();
        var name = RandomName("orchconf-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "alpha");
            await orchestrator.RunAsync(Request(account, name)); // v1: succeeds; the local side records the cloud ETag=E1

            // Simulate an external change to the cloud info file (another machine backing up / the container being
            // rebuilt): bypass tracked and overwrite the cloud unconditionally, advancing the cloud ETag while the
            // local authoritative state still sits on the old ETag (not synced).
            var cloudInfo = await store.ReadInfoAsync(account, name, null);
            Assert.NotNull(cloudInfo);
            await store.WriteInfoAsync(account, name, cloudInfo!, null);

            WriteText("b.txt", "beta");
            // v2: at finalize, trackedInfo.WriteAsync does an If-Match with the stale local ETag → cloud 412 → wrapped exception thrown.
            await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.RunAsync(Request(account, name)));

            // After the conflict: the uncommitted version 2 must never show up in the local index cache (otherwise the next backup reads it as a committed version and gets a ghost diff baseline).
            var ghost = await db.CachedVersionIndexes
                .FirstOrDefaultAsync(x => x.AccountId == account.Id && x.Container == name && x.Version == 2);
            Assert.Null(ghost);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 } },
    };

    private static async Task AssertReferencedBlobsExist(BlobContainerClient container, VersionIndex index)
    {
        foreach (var e in index.Entries)
        {
            // Check **volume by volume** against the volume count the index recorded. Checking only the first volume
            // is not enough: volumes are uploaded concurrently with no requirement on which lands first, so the first
            // one being there says nothing about the whole family being complete.
            var baseRef = e.Storage!.Kind == "pack" ? $"packs/{e.Storage.Ref}.7z" : e.Storage.Ref;
            var (present, _) = await VolumeBlobIO.VerifyVolumesAsync(
                container, baseRef, Math.Max(1, e.Storage.Volumes), [], CancellationToken.None);
            Assert.True(present, $"missing blob {baseRef} for {e.Path}");
        }
    }

    [SkippableFact]
    public async Task First_Backup_Then_Incremental_Produces_Versions()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("orch-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "alpha");
            WriteText("dir/b.txt", "bravo");
            WriteBytes("big.bin", 6_000_000); // > 5M -> single data blob

            // v1 — first backup
            var r1 = await orchestrator.RunAsync(Request(account, name));
            Assert.Equal(1, r1.Version);
            Assert.Equal(3, r1.ChangedFiles);

            var info1 = await store.ReadInfoAsync(account, name, null);
            Assert.Single(info1!.Versions);
            var idx1 = await store.ReadIndexAsync(account, name, info1.Versions[0].IndexBlob, null);
            Assert.Equal(3, idx1.Entries.Count);
            await AssertReferencedBlobsExist(container, idx1);

            // v2 — no changes
            var r2 = await orchestrator.RunAsync(Request(account, name));
            Assert.Equal(2, r2.Version);
            Assert.Equal(0, r2.ChangedFiles);

            // v3 — change one file
            WriteText("a.txt", "alpha-CHANGED");
            var r3 = await orchestrator.RunAsync(Request(account, name));
            Assert.Equal(3, r3.Version);
            Assert.True(r3.ChangedFiles >= 1);

            var info3 = await store.ReadInfoAsync(account, name, null);
            Assert.Equal(3, info3!.Versions.Count);
            var idx3 = await store.ReadIndexAsync(account, name, info3.Versions[^1].IndexBlob, null);
            Assert.Equal(3, idx3.Entries.Count);
            await AssertReferencedBlobsExist(container, idx3);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Retention_Deletes_Old_Versions_And_Their_Exclusive_Data()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("orchr-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        BackupRequest Req() => Request(account, name) with
        {
            Options = new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 2 },
            },
        };

        try
        {
            WriteText("f.txt", "v1"); await orchestrator.RunAsync(Req());
            var info1 = await store.ReadInfoAsync(account, name, null);
            var v1IndexBlob = info1!.Versions[0].IndexBlob;
            // Read the pack name from the index instead of hard-coding it: pack ids carry a per-run random prefix (unique across runs, see RunState.NextPackId).
            var v1Pack = await OnlyPackIdAsync(store, account, name);

            WriteText("f.txt", "v2"); await orchestrator.RunAsync(Req());
            WriteText("f.txt", "v3"); await orchestrator.RunAsync(Req());

            var info = await store.ReadInfoAsync(account, name, null);
            Assert.Equal([2, 3], info!.Versions.Select(v => v.Version)); // v1 retired
            var v3Pack = await OnlyPackIdAsync(store, account, name);
            Assert.NotEqual(v1Pack, v3Pack);
            Assert.DoesNotContain(v1Pack, info.Packs.Keys);              // v1's exclusive pack removed from info
            Assert.False(await container.GetBlobClient(v1IndexBlob).ExistsAsync()); // v1 index blob deleted
            Assert.False(await container.GetBlobClient($"packs/{v1Pack}.7z").ExistsAsync()); // v1 pack blob deleted
            Assert.True(await container.GetBlobClient($"packs/{v3Pack}.7z").ExistsAsync());  // still referenced
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Retention_Keeps_Volume_Split_Blobs_Still_Referenced()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("orchvs-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        // A 6MB random (incompressible) file → single-file data blob, and 1MB volumes → multi-volume data/{hash}.001/.002...
        var buf = new byte[6_000_000];
        new Random(42).NextBytes(buf);
        File.WriteAllBytes(Path.Combine(_root, "big.bin"), buf);

        BackupRequest Req() => Request(account, name) with
        {
            Options = new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                VolumeBytes = 1_000_000,
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
            },
        };

        try
        {
            await orchestrator.RunAsync(Req());        // v1
            await orchestrator.RunAsync(Req());        // v2 → cleanup retires v1; big.bin is unchanged and still referenced by v2

            var hash = await new FileHasher().FullHashAsync(Path.Combine(_root, "big.bin"));
            // A volume-split data blob that v2 still references must be kept (before the fix it was wrongly deleted → data loss).
            Assert.True(await container.GetBlobClient($"data/{hash}.001").ExistsAsync(),
                "referenced volume-split data blob was deleted by retention cleanup");

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            await AssertReferencedBlobsExist(container, idx);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task DeadWeight_Compaction_Rewrites_Pack_Dropping_Unreferenced_Members()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("orchdw-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        // Three equally sized files in the same directory → merged into one pack p0001 (3 members).
        WriteText("d/a.txt", new string('a', 2000));
        WriteText("d/b.txt", new string('b', 2000));
        WriteText("d/c.txt", new string('c', 2000));

        BackupRequest Req() => Request(account, name) with
        {
            Options = new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
            },
        };

        try
        {
            await orchestrator.RunAsync(Req());        // v1: one pack holding {a,b,c}
            // Read the pack name from the index instead of hard-coding it (pack ids carry a per-run random prefix, see RunState.NextPackId).
            var v1Pack = await OnlyPackIdAsync(store, account, name);
            WriteText("d/a.txt", new string('A', 2000)); // change a (same length, different content)
            await orchestrator.RunAsync(Req());        // v2: a goes into a new pack; v1 retires → a_old is dead weight in the old pack (1/3>30%) → compaction

            var info = await store.ReadInfoAsync(account, name, null);
            var p1 = info!.Packs[v1Pack];
            Assert.Equal(2, p1.Members.Count); // a_old is dropped, only b and c are kept
            Assert.Equal(0, p1.DeadBytes);

            // The pack is still usable after compaction: everything the v2 index references is present, and b/c restore fine.
            var idx = await store.ReadIndexAsync(account, name, info.Versions[^1].IndexBlob, null);
            await AssertReferencedBlobsExist(container, idx);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>Reads the id of the one and only pack out of the latest version index. Pack ids carry a per-run random
    /// prefix (unique across runs, see <c>RunState.NextPackId</c>), so tests cannot hard-code "p0001".</summary>
    private static async Task<string> OnlyPackIdAsync(IBackupInfoStore store, Account account, string container)
    {
        var info = await store.ReadInfoAsync(account, container, null);
        var index = await store.ReadIndexAsync(account, container, info!.Versions[^1].IndexBlob, null);
        return index.Entries.Where(e => e.Storage?.Kind == "pack")
            .Select(e => e.Storage!.Ref).Distinct(StringComparer.Ordinal).Single();
    }

    private sealed class SyncProgress(List<BackupProgress> sink) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) { lock (sink) sink.Add(value); }
    }

    [SkippableFact]
    public async Task Progress_Is_Reported_Through_Stages()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("orchp-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "alpha");
            WriteText("dir/b.txt", "bravo"); // two directories → 2 packs

            var reports = new List<BackupProgress>();
            await orchestrator.RunAsync(Request(account, name), new SyncProgress(reports));

            Assert.Equal(BackupStage.Completed, reports[^1].Stage);
            Assert.Contains(reports, p => p.Stage == BackupStage.Uploading && p.TotalItems == 2);
            Assert.Contains(reports, p => p.Stage == BackupStage.Uploading && p.UploadedItems == p.TotalItems && p.Percent == 100);
            Assert.Contains(reports, p => p.Stage == BackupStage.Uploading && p.ChangedFiles == 2);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Blobs_Are_Uploaded_Concurrently_Up_To_The_Limit()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factoryProbe = new BlobClientFactory(TestSecrets.Reader);
        var tracker = new ConcurrencyTrackingUploader(new BlobUploader(factoryProbe));
        var (orchestrator, store, factory) = Build(uploader: tracker);
        var account = AzuriteAccount();
        var name = RandomName("orchc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 4 single-file blobs above the threshold (each with different content, so dedup does not skip them).
            for (var i = 0; i < 4; i++)
            {
                var bytes = new byte[6_000_000];
                bytes[0] = (byte)i;
                File.WriteAllBytes(Path.Combine(_root, $"big{i}.bin"), bytes);
            }

            var request = Request(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                    UploadConcurrency = 3,
                },
            };

            var r = await orchestrator.RunAsync(request);

            Assert.Equal(1, r.Version);
            Assert.True(tracker.MaxConcurrent >= 2,
                $"expected concurrent uploads, saw max {tracker.MaxConcurrent}");
            Assert.True(tracker.MaxConcurrent <= 3, $"exceeded concurrency limit: {tracker.MaxConcurrent}");

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            Assert.Equal(4, idx.Entries.Count);
            await AssertReferencedBlobsExist(container, idx);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// Now that a single-file blob is compressed as it is read, the hash is computed over **exactly the bytes that went
    /// into the archive** — "the content changed mid-processing" is no longer a race that re-verification has to chase
    /// down: whatever went in is what the index records.
    /// This test swaps the file out before 7z starts reading, then pulls the blob back, extracts the content and
    /// recomputes, asserting that the index entry, the blob name and the archive's actual content line up exactly.
    /// </summary>
    [SkippableFact]
    public async Task Single_File_Changed_Before_It_Is_Read_Is_Stored_As_What_Was_Actually_Read()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var mutating = new MutatingCompressor(new SevenZipCompressor(), _root, "a.txt", "changed-content!!");
        var (orchestrator, store, factory) = Build(compressor: mutating);
        var account = AzuriteAccount();
        var name = RandomName("orchv-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "original");
            var request = Request(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            };

            await orchestrator.RunAsync(request);

            var expected = await new FileHasher().FullHashAsync(Path.Combine(_root, "a.txt")); // the new content that was swapped in
            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var entry = Assert.Single(idx.Entries);

            Assert.Equal(expected, entry.FullHash);                 // index fullHash = the content actually compressed in
            Assert.Equal("data/" + expected, entry.Storage!.Ref);   // the blob name is decided by it too
            Assert.Equal("changed-content!!".Length, entry.Length); // the length comes from that same read as well
            await AssertReferencedBlobsExist(container, idx);

            // The load-bearing assertion: the bytes lying in the archive really are the ones the index describes.
            var (length, hash) = await HashStoredBlobAsync(container, entry.Storage.Ref, password: null);
            Assert.Equal(entry.Length, length);
            Assert.Equal(entry.FullHash, hash);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>Pulls a single-file blob back locally, streams the content out and recomputes its length and hash.</summary>
    private async Task<(long Length, string Hash)> HashStoredBlobAsync(
        BlobContainerClient container, string blobRef, string? password)
    {
        var dir = Path.Combine(_temp, "verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var firstVolume = await VolumeBlobIO.DownloadAsync(container, blobRef, dir, CancellationToken.None);

        var hasher = new StreamingHasher(0, 0);
        await using var sink = new HashingStream(hasher);
        await new SevenZipCompressor().ExtractToStreamAsync(firstVolume, entryName: null, password, sink);
        return (hasher.Length, hasher.FullHash);
    }

    [SkippableFact]
    public async Task Pack_Member_Changed_During_Compression_Rejoins_Grouping()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var mutating = new MutatingCompressor(new SevenZipCompressor(), _root, "d/y.txt", "yyyy-CHANGED");
        var (orchestrator, store, factory) = Build(compressor: mutating);
        var account = AzuriteAccount();
        var name = RandomName("orchpk-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("d/x.txt", "xxxx"); // two small files in the same directory → incremental grouping
            WriteText("d/y.txt", "yyyy");

            await orchestrator.RunAsync(Request(account, name)); // default 5M threshold → grouping

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var x = idx.Entries.Single(e => e.Path == "d/x.txt");
            var y = idx.Entries.Single(e => e.Path == "d/y.txt");

            Assert.Equal("pack", x.Storage!.Kind);                 // the unchanged member is in a pack
            // The changed member is re-queued under its new hash → it joins the next group (still a pack), not a single file.
            Assert.Equal("pack", y.Storage!.Kind);
            Assert.NotEqual(x.Storage.Ref, y.Storage.Ref);         // they land in different packs
            var expectedY = await new FileHasher().FullHashAsync(Path.Combine(_root, "d/y.txt"));
            Assert.Equal(expectedY, y.FullHash);                   // fullHash uses the new content once it settled
            await AssertReferencedBlobsExist(container, idx);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>Detects whether AppendAsync gets called concurrently (standing in for a shared DbContext).</summary>
    private sealed class ConcurrencyProbeLog : IOperationLog
    {
        private int _active;
        public int MaxConcurrent { get; private set; }
        public async Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default, bool? durable = null)
        {
            var now = Interlocked.Increment(ref _active);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref _active);
        }
        public Task<IReadOnlyList<LogEntry>> QueryAsync(OperationLogLevel? l, string? s, DateTimeOffset? f, DateTimeOffset? t, int n, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LogEntry>>([]);
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;
        public Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Captures AppendAsync calls (level / durable or not / message).</summary>
    private sealed class CapturingLog : IOperationLog
    {
        public List<(OperationLogLevel Level, bool? Durable, string Message)> Entries { get; } = [];
        public Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default, bool? durable = null)
        {
            lock (Entries) Entries.Add((level, durable, message));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<LogEntry>> QueryAsync(OperationLogLevel? l, string? s, DateTimeOffset? f, DateTimeOffset? t, int n, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LogEntry>>([]);
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;
        public Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Verbose_Logging_Writes_Per_File_To_Verbose_Text_Log()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var log = new CapturingLog();
        var vlogRoot = Path.Combine(_temp, "vlog");
        var verboseLog = new VerboseFileLog(vlogRoot);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked, opLog: log,
            verboseLog: verboseLog);

        var account = AzuriteAccount();
        var name = RandomName("orchvb-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("dir/note.txt", "hello");
            var request = Request(account, name) with
            {
                Options = new BackupEngineOptions { VerboseLogging = true },
            };
            await orchestrator.RunAsync(request);

            // The per-file log lands in a per-backup text file (no longer in SQLite), and includes the file name.
            var vfile = Directory.EnumerateFiles(Path.Combine(vlogRoot, name), "*.log").Single();
            Assert.Contains("dir/note.txt", await File.ReadAllTextAsync(vfile));
            Assert.DoesNotContain(log.Entries, e => e.Level == OperationLogLevel.Debug); // Debug no longer goes into the database
            // Start/finish events are still durable (durable=true) audit log entries.
            Assert.Contains(log.Entries, e => e.Durable == true && e.Message.Contains("succeeded"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Event_Recording_Is_Serialized_Under_Concurrent_Uploads()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var log = new ConcurrencyProbeLog();
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked, opLog: log);

        var account = AzuriteAccount();
        var name = RandomName("orchrec-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 4 single-file blobs with different content, each pre-seeded with a data/{hash} whose metadata does not match → each triggers one collision Record.
            var hasher = new FileHasher();
            for (var i = 0; i < 4; i++)
            {
                WriteText($"f{i}.txt", "content-" + i);
                var hash = await hasher.FullHashAsync(Path.Combine(_root, $"f{i}.txt"));
                await container.GetBlobClient($"data/{hash}").UploadAsync(
                    BinaryData.FromString("x"),
                    new BlobUploadOptions { Metadata = new Dictionary<string, string> { ["len"] = "999", ["head"] = "xxh128:00" } });
            }

            var request = Request(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    UploadConcurrency = 4,
                },
            };
            await orchestrator.RunAsync(request);

            Assert.True(log.MaxConcurrent >= 1);
            Assert.Equal(1, log.MaxConcurrent); // Record is serialized; never concurrent access to the shared DbContext
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The previous run fell over a few volumes in, leaving behind a batch of volumes that are in neither the index nor
    /// the local state. When the same encrypted file is run again those leftovers have to be wiped first; they cannot
    /// be skipped by if-missing.
    /// <para>
    /// Under encryption AES draws a fresh random salt/IV every time, so compressing the same file twice yields different
    /// ciphertext — yet the blob name is derived from the hash of the **plaintext** content, so both runs land at the
    /// same address. Skip the leftovers and .001 is the previous run's ciphertext while the later volumes are this
    /// run's; put together they will not decrypt, and that file can never be restored again. Plaintext does not have
    /// this problem (compression output is byte-for-byte deterministic), so the cleanup targets encrypted multi-volume
    /// archives only — see BackupOrchestrator.ClearLeftoverVolumesAsync.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Leftover_Volumes_From_A_Failed_Run_Are_Cleared_Before_Re_Uploading()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        const string password = "pw-leftover";
        var breaker = new FailAfterNVolumesUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), 3);
        var (orchestrator, store, factory) = Build(uploader: breaker);
        var account = AzuriteAccount();
        var name = RandomName("orchleft-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var request = Request(account, name) with
            {
                Password = password,
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },  // force the single-file blob path
                    VolumeBytes = 64 * 1024,
                },
            };

            // v1: establish the info file first. An encrypted backup's blob addresses are an HMAC derived from the
            // password plus the KdfSalt in the info file, and swapping the salt changes every address — so "restart
            // after an interruption and collide with your own leftovers" only makes sense while the info file is still
            // there, and that is exactly the real shape of things: writing the index and writing the info file are both
            // wind-up actions done last.
            WriteText("small.txt", "seed");
            await orchestrator.RunAsync(request);

            // v2: a big incompressible file (random bytes), cut into several 64 KB volumes. Break it on the 4th volume.
            var payload = new byte[400_000];
            Random.Shared.NextBytes(payload);
            await File.WriteAllBytesAsync(Path.Combine(_root, "big.bin"), payload);
            breaker.Arm();
            await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.RunAsync(request));

            var leftovers = await ListAsync(container, "data/");
            Assert.True(leftovers.Count > 1, $"this round should have left several volumes behind, actual {leftovers.Count} data blob(s)");
            var leftoverBytes = new Dictionary<string, byte[]>();
            foreach (var b in leftovers)
                leftoverBytes[b] = await ReadAllAsync(container, b);

            // v2 re-run: the same orchestrator (local state is still sitting at v1 — that round never wound up and recorded nothing).
            breaker.Disarm();
            await orchestrator.RunAsync(request);

            var info = await store.ReadInfoAsync(account, name, password);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, password);
            var big = idx.Entries.Single(e => e.Path == "big.bin").Storage!;
            Assert.True(big.Volumes > 1, $"this test needs multiple volumes, actual only {big.Volumes} volume(s)");

            // The crux: not one of those leftover volumes survived as it was. Leaving any of them means the family has
            // the previous run's ciphertext mixed into it (AES draws a fresh random salt/IV each time, so compressing
            // the same file twice necessarily produces different bytes), and then the whole family cannot be decrypted.
            foreach (var name2 in VolumeBlobIO.VolumeNames(big.Ref, big.Volumes))
            {
                if (leftoverBytes.TryGetValue(name2, out var old))
                    Assert.NotEqual(old, await ReadAllAsync(container, name2));
            }

            await AssertReferencedBlobsExist(container, idx);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Starts throwing once N volumes have gone up — simulating a run that falls over partway through an upload. Before Arm it forwards everything unchanged.</summary>
    private sealed class FailAfterNVolumesUploader(IBlobUploader inner, int allowed) : IBlobUploader
    {
        private int _armed;
        private int _uploaded;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public void Disarm() => Interlocked.Exchange(ref _armed, 0);

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, null);

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry, CancellationToken ct,
            IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        {
            if (Volatile.Read(ref _armed) == 1
                && blobName.StartsWith("data/", StringComparison.Ordinal)
                && Interlocked.Increment(ref _uploaded) > allowed)
            {
                // A non-retryable error, so this round falls over on the spot instead of backing off and retrying until it succeeds.
                throw new IOException("simulated failure partway through a multi-volume upload");
            }
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
    }

    private static async Task<List<string>> ListAsync(BlobContainerClient container, string prefix)
    {
        var names = new List<string>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, CancellationToken.None))
            names.Add(b.Name);
        return names;
    }

    private static async Task<byte[]> ReadAllAsync(BlobContainerClient container, string blobName)
    {
        using var ms = new MemoryStream();
        await container.GetBlobClient(blobName).DownloadToAsync(ms);
        return ms.ToArray();
    }

    [SkippableFact]
    public async Task Store_Only_Unencrypted_Single_File_Is_Stored_Raw()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("orchraw-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "alpha-raw-content");
            var request = Request(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 }, // single-file blob (no grouping)
                    DontCompress = new IgnoreRuleSet(["*"]),                 // store-only
                },
            };

            await orchestrator.RunAsync(request);

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var e = Assert.Single(idx.Entries);
            Assert.True(e.Storage!.Raw); // marked as raw

            // The blob content is the raw file bytes (not a 7z archive).
            var blob = await container.GetBlobClient(e.Storage.Ref).DownloadContentAsync();
            Assert.Equal("alpha-raw-content", blob.Value.Content.ToString());
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Encrypted_Backup_Uses_Keyed_Blob_Addresses()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("orchke-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteBytes("big.bin", 6_000_000); // > 5M → single-file data blob
            var request = Request(account, name) with { Password = "pw" };

            await orchestrator.RunAsync(request);

            var info = await store.ReadInfoAsync(account, name, "pw");
            Assert.NotNull(info!.Backup.KdfSalt); // the encrypted backup generated a salt
            var idx = await store.ReadIndexAsync(account, name, info.Versions[0].IndexBlob, "pw");
            var e = Assert.Single(idx.Entries);

            // The storage name is a keyed address containing no public fullHash; the plaintext data/{fullHash} does not exist (anti-fingerprinting).
            Assert.DoesNotContain(e.FullHash!, e.Storage!.Ref);
            Assert.False(await container.GetBlobClient($"data/{e.FullHash}").ExistsAsync());
            await AssertReferencedBlobsExist(container, idx); // the blob at the keyed address does exist
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Encrypted_Backup_RoundTrips_Through_Info_And_Index()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("orche-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("secret.txt", "classified");
            var request = Request(account, name) with { Password = "pw" };

            var r = await orchestrator.RunAsync(request);

            Assert.Equal(1, r.Version);
            Assert.True(await container.GetBlobClient(BackupDiscovery.EncryptedIndexBlobName).ExistsAsync());
            var info = await store.ReadInfoAsync(account, name, "pw");
            Assert.True(info!.Backup.Encrypted);
            var idx = await store.ReadIndexAsync(account, name, info.Versions[0].IndexBlob, "pw");
            Assert.Equal("secret.txt", Assert.Single(idx.Entries).Path);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// A version has to record both the start and the finish moment, and what the result reports must be **those two
    /// from the version record** — not the runner's own clock. Retention cleanup still runs for a while after the
    /// version is committed, so letting each side take its own clock makes the completion toast and the restore
    /// dropdown write two different times for one and the same backup.
    /// </summary>
    [SkippableFact]
    public async Task Version_Records_Start_And_Finish_And_Result_Reports_The_Same_Pair()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("orchts-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "alpha");
            var before = DateTimeOffset.UtcNow;

            var result = await orchestrator.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v = Assert.Single(info!.Versions);
            Assert.NotNull(v.StartedAt);
            Assert.True(v.StartedAt >= before, $"started {v.StartedAt} should be >= {before}");
            Assert.True(v.StartedAt <= v.CreatedAt, $"started {v.StartedAt} should be <= finished {v.CreatedAt}");
            Assert.Equal(v.StartedAt, result.StartedAt);
            Assert.Equal(v.CreatedAt, result.CompletedAt);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Backup_Fails_Loudly_When_The_Scope_Leaves_Nothing()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("scope-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("photos/a.jpg", "x");

            var request = Request(account, name) with
            {
                // Exclude everything, leaving not a single file.
                Options = new BackupEngineOptions
                {
                    Scan = new ScanOptions { Scope = ScopeRuleSet.Parse("-") },
                },
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => orchestrator.RunAsync(request));

            Assert.Contains("scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task An_Empty_Root_Without_A_Scope_Is_Still_Allowed()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("scope-empty-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // An empty root with no scope configured is a normal situation (just created and nothing put in yet, say),
            // and should not be stopped by this backstop. _root is empty right now — this test deliberately writes nothing into it.
            var result = await orchestrator.RunAsync(Request(account, name));

            Assert.Equal(1, result.Version);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    // Reproduces the final-review Important: the scope is "-" + "+ photos", and photos is a dropped SMB/NFS mount
    // point — which on a shipped NAS is the normal state of affairs, not a misoperation. ScanDirectory records it into
    // Unreadable and returns true, so Entries and EmptyDirs are both empty, but that is not "the scope selected
    // nothing", it is "this subtree could not be read this round".
    // The old guard looked only at Entries/EmptyDirs and misdiagnosed this as a misconfigured scope, throwing and
    // blocking the entire backup; worse, if this is not the first backup, the diff engine ought to be carrying the
    // previous version's entries forward under "unreadable != deleted", yet the guard cut the whole run off — and that
    // correct local behaviour along with it — before diff ever ran.
    [SkippableFact]
    public async Task An_Unreadable_Mount_Does_Not_Trigger_The_Empty_Scope_Guard()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var photosDir = Path.Combine(_root, "photos");
        Directory.CreateDirectory(photosDir);
        WriteText("photos/a.jpg", "x"); // what was left inside before the mount dropped — the directory itself is not empty

        // Take read permission away (keeping execute) to simulate a dropped mount point: opendir/readdir gets nothing.
        File.SetUnixFileMode(photosDir, UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // root (and anyone holding CAP_DAC_OVERRIDE) is not bound by directory permission bits, so chmod is no barrier
        // in that environment — enumeration succeeds anyway, Unreadable is never non-empty, and the assertion passes
        // for the wrong reason. Rather than bet on the runtime environment, measure whether this chmod really does
        // block enumeration, and honestly Skip when it does not.
        var reallyUnreadable = false;
        try { new DirectoryInfo(photosDir).EnumerateFileSystemInfos().GetEnumerator().MoveNext(); }
        catch (UnauthorizedAccessException) { reallyUnreadable = true; }
        Skip.IfNot(reallyUnreadable,
            "running as a user that bypasses directory permission checks (e.g. root); chmod is not a barrier here");

        var (orchestrator, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("scope-unread-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var request = Request(account, name) with
            {
                // Exclude everything and re-include only photos — matching the scenario in the description.
                Options = new BackupEngineOptions
                {
                    Scan = new ScanOptions { Scope = ScopeRuleSet.Parse("-\n+ photos") },
                },
            };

            // It must not throw the "check your scope selection" exception: the scope itself is fine, it is the photos subtree that cannot be read.
            var result = await orchestrator.RunAsync(request);

            Assert.Equal(1, result.Version);
        }
        finally
        {
            // Restore the permissions, or the recursive delete of _root in Dispose() fails on this directory.
            File.SetUnixFileMode(photosDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await container.DeleteIfExistsAsync();
        }
    }
}
