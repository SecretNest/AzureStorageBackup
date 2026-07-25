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
        IBlobUploader? uploader = null, ProcessingVerifier? verifier = null, IFileCompressor? compressor = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"));
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            compressor ?? new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor), new FileHasher(), verifier: verifier);
        return (orchestrator, store, factory);
    }

    /// <summary>压缩含目标文件后，篡改其源文件一次，模拟「文件在处理中变化」（§9、PRD 特别说明 D）。</summary>
    private sealed class MutatingCompressor(IFileCompressor inner, string relPath, string newContent) : IFileCompressor
    {
        private int _fired;
        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var result = await inner.CompressAsync(request, ct);
            if (request.Entries.Contains(relPath) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                var full = Path.Combine(request.SourceDirectory, relPath.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllText(full, newContent);
                File.SetLastWriteTimeUtc(full, File.GetLastWriteTimeUtc(full).AddSeconds(7));
            }
            return result;
        }
        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);
    }

    /// <summary>统计 ReadIndexAsync 调用次数的 store 装饰器（验证本地缓存命中）。</summary>
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
        var cache = new LocalIndexCache(db, counting);
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, counting, staging,
            new RetentionCleaner(factory, counting, new RetentionEvaluator()), new FileHasher(), indexCache: cache);

        var account = AzuriteAccount();
        var name = RandomName("orchlc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "alpha");
            await orchestrator.RunAsync(Request(account, name)); // v1：无上一版本，写完缓存 v1
            await orchestrator.RunAsync(Request(account, name)); // v2：上一版本索引应命中本地缓存

            Assert.Equal(0, counting.IndexReads); // 从未下载云端第二级索引
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
            await orchestrator.RunAsync(Request(account, name)); // v1：本地无 → 读云端一次(返回空→新建)
            var readsAfterFirst = counting.InfoReads;
            await orchestrator.RunAsync(Request(account, name)); // v2：本地权威 → 不应再读云端信息文件

            Assert.Equal(readsAfterFirst, counting.InfoReads); // 第二次备份零信息文件读
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>记录同时在飞的上传数，验证上传并发。</summary>
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
                await Task.Delay(250, ct); // 让上传明显长于压缩，使并发窗口稳定可观测
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

    /// <summary>统计 data/ blob 上传次数（验证去重不重复上传）。</summary>
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
            WriteText("dir/y.txt", "identical payload"); // 同内容不同路径

            await orchestrator.RunAsync(Request(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            Assert.Equal(1, counting.DataUploads); // 两个同内容文件只上传一份 data blob
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

            // 删掉云端的 data blob：若备份靠云端 HEAD 判存在，v2 会发现缺失并重传；靠本地索引则仍去重。
            await foreach (var b in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();
            counting.Reset();

            WriteText("b.txt", "shared body"); // 新文件、与 a 同内容
            var v2 = await orchestrator.RunAsync(Request(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            }); // v2

            Assert.Equal(2, v2.Version);
            Assert.Equal(0, counting.DataUploads); // 纯本地去重：未重传（证明未读云端存在性）
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
            await orchestrator.RunAsync(Request(account, name)); // v1：成功，本地记录云端 ETag=E1

            // 模拟外部改动云端信息文件（另一台机器备份 / container 被重建）：绕过 tracked 直接
            // 无条件改写云端，令云端 ETag 前进，而本地权威状态仍停留在旧 ETag（未同步）。
            var cloudInfo = await store.ReadInfoAsync(account, name, null);
            Assert.NotNull(cloudInfo);
            await store.WriteInfoAsync(account, name, cloudInfo!, null);

            WriteText("b.txt", "beta");
            // v2：finalize 阶段 trackedInfo.WriteAsync 用陈旧本地 ETag 做 If-Match → 云端 412 → 包装异常抛出。
            await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.RunAsync(Request(account, name)));

            // 冲突后：本次未提交的版本 2 绝不能出现在本地索引缓存中（否则下次备份会把它当作已提交版本读取，产生幽灵 diff 基线）。
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
            // 用 VolumeBlobIO.ExistsAsync：单卷查基名，多卷查首卷 .001。
            var baseRef = e.Storage!.Kind == "pack" ? $"packs/{e.Storage.Ref}.7z" : e.Storage.Ref;
            Assert.True(await VolumeBlobIO.ExistsAsync(container, baseRef, CancellationToken.None),
                $"missing blob {baseRef} for {e.Path}");
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

            WriteText("f.txt", "v2"); await orchestrator.RunAsync(Req());
            WriteText("f.txt", "v3"); await orchestrator.RunAsync(Req());

            var info = await store.ReadInfoAsync(account, name, null);
            Assert.Equal([2, 3], info!.Versions.Select(v => v.Version)); // v1 retired
            Assert.DoesNotContain("p0001", info.Packs.Keys);              // v1's exclusive pack removed from info
            Assert.False(await container.GetBlobClient(v1IndexBlob).ExistsAsync()); // v1 index blob deleted
            Assert.False(await container.GetBlobClient("packs/p0001.7z").ExistsAsync()); // v1 pack blob deleted
            Assert.True(await container.GetBlobClient("packs/p0002.7z").ExistsAsync());  // still referenced
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

        // 6MB 随机（不可压缩）文件 → 单文件 data blob，且 1MB 分卷 → 多卷 data/{hash}.001/.002...
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
            await orchestrator.RunAsync(Req());        // v2 → cleanup 退役 v1；big.bin 未变仍被 v2 引用

            var hash = await new FileHasher().FullHashAsync(Path.Combine(_root, "big.bin"));
            // 仍被 v2 引用的分卷 data blob 必须保留（修复前会被误删 → 数据丢失）。
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

        // 同目录三个等大小文件 → 合并成一个 pack p0001（3 成员）。
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
            await orchestrator.RunAsync(Req());        // v1: p0001{a,b,c}
            WriteText("d/a.txt", new string('A', 2000)); // 改 a（等长不同内容）
            await orchestrator.RunAsync(Req());        // v2: a→p0002；退役 v1 → p0001 中 a_old 死重(1/3>30%)→压实

            var info = await store.ReadInfoAsync(account, name, null);
            var p1 = info!.Packs["p0001"];
            Assert.Equal(2, p1.Members.Count); // a_old 被丢弃，仅保留 b、c
            Assert.Equal(0, p1.DeadBytes);

            // 压实后 pack 仍可用：v2 索引引用的对象都在，且还原 b/c 成功。
            var idx = await store.ReadIndexAsync(account, name, info.Versions[^1].IndexBlob, null);
            await AssertReferencedBlobsExist(container, idx);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
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
            WriteText("dir/b.txt", "bravo"); // 两个目录 → 2 个 pack

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
            // 4 个 > 阈值的单文件 blob（内容各异，避免去重跳过）。
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

    [SkippableFact]
    public async Task Single_File_Changed_During_Processing_Is_Stored_Under_New_Hash()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var mutating = new MutatingCompressor(new SevenZipCompressor(), "a.txt", "changed-content!!");
        var (orchestrator, store, factory) = Build(
            verifier: new ProcessingVerifier(new FileHasher()), compressor: mutating);
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

            var expected = await new FileHasher().FullHashAsync(Path.Combine(_root, "a.txt")); // 稳定后的新内容 hash
            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var entry = Assert.Single(idx.Entries);

            Assert.Equal(expected, entry.FullHash);                 // 索引 fullHash 用新内容
            Assert.Equal("data/" + expected, entry.Storage!.Ref);   // blob 名用新 hash
            Assert.Equal("changed-content!!".Length, entry.Length); // 元数据一并更新
            Assert.True(await container.GetBlobClient("data/" + expected).ExistsAsync(),
                "referenced blob must exist under the new hash");
            await AssertReferencedBlobsExist(container, idx);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Pack_Member_Changed_During_Compression_Rejoins_Grouping()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var mutating = new MutatingCompressor(new SevenZipCompressor(), "d/y.txt", "yyyy-CHANGED");
        var (orchestrator, store, factory) = Build(
            verifier: new ProcessingVerifier(new FileHasher()), compressor: mutating);
        var account = AzuriteAccount();
        var name = RandomName("orchpk-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("d/x.txt", "xxxx"); // 同目录两小文件 → 增量分组
            WriteText("d/y.txt", "yyyy");

            await orchestrator.RunAsync(Request(account, name)); // 默认 5M 阈值 → 分组

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var x = idx.Entries.Single(e => e.Path == "d/x.txt");
            var y = idx.Entries.Single(e => e.Path == "d/y.txt");

            Assert.Equal("pack", x.Storage!.Kind);                 // 未变成员在 pack
            // 变更成员以新 hash 重新入队 → 进入下一组（仍是 pack），而非单文件。
            Assert.Equal("pack", y.Storage!.Kind);
            Assert.NotEqual(x.Storage.Ref, y.Storage.Ref);         // 落在不同的 pack
            var expectedY = await new FileHasher().FullHashAsync(Path.Combine(_root, "d/y.txt"));
            Assert.Equal(expectedY, y.FullHash);                   // fullHash 用稳定后的新内容
            await AssertReferencedBlobsExist(container, idx);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>检测 AppendAsync 是否被并发调用（模拟共享 DbContext）。</summary>
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

    /// <summary>捕获 AppendAsync 调用（等级/是否长存/消息）。</summary>
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
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(), opLog: log,
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

            // 逐文件日志落到按备份的文本文件（不再进 SQLite），含文件名。
            var vfile = Directory.EnumerateFiles(Path.Combine(vlogRoot, name), "*.log").Single();
            Assert.Contains("dir/note.txt", await File.ReadAllTextAsync(vfile));
            Assert.DoesNotContain(log.Entries, e => e.Level == OperationLogLevel.Debug); // Debug 不再入库
            // 起止事件仍是长存(durable=true)审计日志。
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
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(), opLog: log);

        var account = AzuriteAccount();
        var name = RandomName("orchrec-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 4 个不同内容的单文件 blob，各预置一个元数据不符的 data/{hash} → 各触发一次碰撞 Record。
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
            Assert.Equal(1, log.MaxConcurrent); // Record 串行化，绝不并发访问共享 DbContext
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Hash_Collision_Falls_Back_To_Alternate_Name()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("orchhc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("x.txt", "hello");
            var hash = await new FileHasher().FullHashAsync(Path.Combine(_root, "x.txt"));

            // 预置一个 data/{hash}，其元数据代表「不同内容」——模拟 hash 碰撞。
            await container.GetBlobClient($"data/{hash}").UploadAsync(
                BinaryData.FromString("other content"),
                new BlobUploadOptions
                {
                    Metadata = new Dictionary<string, string> { ["len"] = "999999", ["head"] = "xxh128:00" },
                });

            var request = Request(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            };
            await orchestrator.RunAsync(request);

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var e = Assert.Single(idx.Entries);

            Assert.Equal($"data/{hash}~1", e.Storage!.Ref);   // 避让到备用名，不覆盖既有 blob
            Assert.Equal(hash, e.FullHash);                   // 索引仍记内容 hash
            Assert.True(await container.GetBlobClient($"data/{hash}~1").ExistsAsync());
            await AssertReferencedBlobsExist(container, idx);
        }
        finally { await container.DeleteIfExistsAsync(); }
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
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 }, // 单文件 blob（不分组）
                    DontCompress = new IgnoreRuleSet(["*"]),                 // store-only
                },
            };

            await orchestrator.RunAsync(request);

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var e = Assert.Single(idx.Entries);
            Assert.True(e.Storage!.Raw); // 标记为原始

            // blob 内容就是原始文件字节（不是 7z 归档）。
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
            WriteBytes("big.bin", 6_000_000); // > 5M → 单文件 data blob
            var request = Request(account, name) with { Password = "pw" };

            await orchestrator.RunAsync(request);

            var info = await store.ReadInfoAsync(account, name, "pw");
            Assert.NotNull(info!.Backup.KdfSalt); // 加密备份生成了盐
            var idx = await store.ReadIndexAsync(account, name, info.Versions[0].IndexBlob, "pw");
            var e = Assert.Single(idx.Entries);

            // 存储名是密钥化地址，不含公开 fullHash；明文 data/{fullHash} 不存在（防指纹识别）。
            Assert.DoesNotContain(e.FullHash!, e.Storage!.Ref);
            Assert.False(await container.GetBlobClient($"data/{e.FullHash}").ExistsAsync());
            await AssertReferencedBlobsExist(container, idx); // 密钥化地址处的 blob 存在
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
}
