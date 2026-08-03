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

    /// <summary>把目标文件篡改一次，模拟「文件在处理中变化」（§9、PRD 特别说明 D）。
    /// 两条路径的时机不同：分组路径先 hash 后压，所以挂在 <c>CompressAsync</c> 之**后**（重校验据此
    /// 发现内容变了）；单文件路径边读边压，改写必须发生在 7z 开始读之**前**，才谈得上
    /// "压进去的和 diff 时看到的不是同一份"。</summary>
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
                // 让上传明显长于"压缩 + 校验归档内容"，并发窗口才稳定可观测。压缩是全局串行的，
                // 所以两次上传能不能重叠，取决于一次上传是否比下一次压缩更久——这个延迟太接近
                // 压缩耗时，测的就成了压缩快慢而不是并发上限。
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
            // 包名从索引里读，不写死：pack 号带每轮随机前缀（跨运行唯一，见 RunState.NextPackId）。
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
            await orchestrator.RunAsync(Req());        // v1: 一个包装着 {a,b,c}
            // 包名从索引里读，不写死（pack 号带每轮随机前缀，见 RunState.NextPackId）。
            var v1Pack = await OnlyPackIdAsync(store, account, name);
            WriteText("d/a.txt", new string('A', 2000)); // 改 a（等长不同内容）
            await orchestrator.RunAsync(Req());        // v2: a 进新包；退役 v1 → 老包里 a_old 死重(1/3>30%)→压实

            var info = await store.ReadInfoAsync(account, name, null);
            var p1 = info!.Packs[v1Pack];
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

    /// <summary>从最新版本索引里读出唯一那个 pack 的号。pack 号带每轮随机前缀（跨运行唯一，
    /// 见 <c>RunState.NextPackId</c>），所以测试不能写死 "p0001"。</summary>
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

    /// <summary>
    /// 单文件 blob 边读边压之后，hash 算的**就是压进归档的那些字节**——"内容在处理中变化"
    /// 不再是一个需要重校验去追平的竞态：压进去的是什么，索引记的就是什么。
    /// 本用例在 7z 开始读之前把文件换掉，然后把 blob 拉回来解出内容重算，断言索引条目、
    /// blob 名与归档实际内容三者严丝合缝。
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

            var expected = await new FileHasher().FullHashAsync(Path.Combine(_root, "a.txt")); // 换上去的新内容
            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var entry = Assert.Single(idx.Entries);

            Assert.Equal(expected, entry.FullHash);                 // 索引 fullHash = 实际压进去的内容
            Assert.Equal("data/" + expected, entry.Storage!.Ref);   // blob 名同样由它决定
            Assert.Equal("changed-content!!".Length, entry.Length); // 长度一并来自那一遍读
            await AssertReferencedBlobsExist(container, idx);

            // 承重断言：归档里躺着的字节确实就是索引描述的那份。
            var (length, hash) = await HashStoredBlobAsync(container, entry.Storage.Ref, password: null);
            Assert.Equal(entry.Length, length);
            Assert.Equal(entry.FullHash, hash);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>把单文件 blob 拉回本地、流式解出内容并重算长度与 hash。</summary>
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

    /// <summary>
    /// 上一次运行传了几卷就倒了，留下一批既不在索引、也不在本地状态里的卷。重跑同一个加密文件时
    /// 这些残卷必须先被抹掉，不能靠 if-missing 跳过它们。
    /// <para>
    /// 加密下 AES 每次换随机 salt/IV，同一个文件两次压出来的密文不同，而 blob 名是从**明文**内容
    /// hash 派生的——两次跑落在同一个地址上。跳过残卷的话，.001 是上次的密文、后面几卷是这次的，
    /// 拼起来解不开，那个文件就此还原不了。明文没有这个问题（压缩输出逐字节确定），所以清理只
    /// 针对加密的多卷归档，见 BackupOrchestrator.ClearLeftoverVolumesAsync。
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
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },  // 强制走单文件 blob 路径
                    VolumeBytes = 64 * 1024,
                },
            };

            // v1：先立住信息文件。加密备份的 blob 地址是用 password + 信息文件里的 KdfSalt 派生的
            // HMAC，盐一换地址就全变——所以"中断后重跑撞上自己的残卷"这件事只在信息文件还在时
            // 才谈得上，而那正是真实的形状：写索引、写信息文件都是最后才做的收尾动作。
            WriteText("small.txt", "seed");
            await orchestrator.RunAsync(request);

            // v2：一个压不动的大文件（随机字节），按 64 KB 切成好几卷。传到第 4 卷时把它打断。
            var payload = new byte[400_000];
            Random.Shared.NextBytes(payload);
            await File.WriteAllBytesAsync(Path.Combine(_root, "big.bin"), payload);
            breaker.Arm();
            await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.RunAsync(request));

            var leftovers = await ListAsync(container, "data/");
            Assert.True(leftovers.Count > 1, $"这一轮该留下好几卷才对，实际 {leftovers.Count} 个 data blob");
            var leftoverBytes = new Dictionary<string, byte[]>();
            foreach (var b in leftovers)
                leftoverBytes[b] = await ReadAllAsync(container, b);

            // v2 重跑：同一个编排器（本地状态仍停在 v1——那一轮没能收尾，什么都没记下）。
            breaker.Disarm();
            await orchestrator.RunAsync(request);

            var info = await store.ReadInfoAsync(account, name, password);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, password);
            var big = idx.Entries.Single(e => e.Path == "big.bin").Storage!;
            Assert.True(big.Volumes > 1, $"这个用例需要多卷，实际只有 {big.Volumes} 卷");

            // 要害：那些残卷没有一个被原样留下来。留下任何一个都意味着这一族里混着上一轮的密文
            // （AES 每次换随机 salt/IV，同一个文件两次压出来的字节必然不同），整族就解不开了。
            foreach (var name2 in VolumeBlobIO.VolumeNames(big.Ref, big.Volumes))
            {
                if (leftoverBytes.TryGetValue(name2, out var old))
                    Assert.NotEqual(old, await ReadAllAsync(container, name2));
            }

            await AssertReferencedBlobsExist(container, idx);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>传满 N 卷之后就开始抛——模拟一次传到半路倒掉的运行。Arm 之前原样转发。</summary>
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
                // 不可重试的错误，好让这一轮当场倒掉而不是退避重试到成功。
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

    /// <summary>
    /// 版本要同时记下开始与结束时刻，且结果里报出来的是**版本记录里的那两个**——不是运行器
    /// 自己的时钟。收尾清理在版本提交之后还要跑一阵，各取各的时钟就会出现完成提示与还原
    /// 下拉对同一次备份写出两个不同时间。
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
                // 全部排除，一个文件都不剩。
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
            // 没配范围时的空根是正常情况（比如刚建好还没往里放东西），不该被这条兜底拦下。
            // _root 此刻是空的——这条用例刻意什么都不写进去。
            var result = await orchestrator.RunAsync(Request(account, name));

            Assert.Equal(1, result.Version);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    // 复现终审 Important：范围是 "-" + "+ photos"，而 photos 是个掉线的 SMB/NFS 挂载点——
    // 这在出货的 NAS 上是常态，不是误操作。ScanDirectory 把它记进 Unreadable 并返回
    // true，于是 Entries 和 EmptyDirs 都是空的，但那不是"范围选空了"，是"这棵子树本轮读不到"。
    // 之前的守卫只看 Entries/EmptyDirs，会把这个误诊成范围配错，抛异常拦下整次备份；
    // 更糟的是，若这不是首次备份，diff 引擎本该按"读不开 ≠ 删除"沿用上一版本的条目，
    // 守卫却抢在 diff 之前就把整次运行连本地这条正确行为一起拦掉了。
    [SkippableFact]
    public async Task An_Unreadable_Mount_Does_Not_Trigger_The_Empty_Scope_Guard()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var photosDir = Path.Combine(_root, "photos");
        Directory.CreateDirectory(photosDir);
        WriteText("photos/a.jpg", "x"); // 挂载点掉线前留在里面的东西——目录本身不是空的

        // 收走读权限（保留 execute），模拟掉线的挂载点：opendir/readdir 拿不到内容。
        File.SetUnixFileMode(photosDir, UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // root（以及任何有 CAP_DAC_OVERRIDE 的用户）不受目录权限位约束，chmod 在那种环境下
        // 不是屏障——枚举照样成功，Unreadable 永远不会非空，断言会为错误的原因通过。
        // 与其赌运行环境，不如实测一下这道 chmod 是否真的挡住了枚举，挡不住就如实 Skip。
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
                // 排除一切，只重新包含 photos——与描述里的场景一致。
                Options = new BackupEngineOptions
                {
                    Scan = new ScanOptions { Scope = ScopeRuleSet.Parse("-\n+ photos") },
                },
            };

            // 不该抛"检查范围选择"的异常：范围本身没问题，是 photos 这棵子树读不到。
            var result = await orchestrator.RunAsync(request);

            Assert.Equal(1, result.Version);
        }
        finally
        {
            // 恢复权限，否则 Dispose() 里对 _root 的递归删除会在这个目录上失败。
            File.SetUnixFileMode(photosDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await container.DeleteIfExistsAsync();
        }
    }
}
