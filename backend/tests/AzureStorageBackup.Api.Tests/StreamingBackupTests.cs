using System.Net.Sockets;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 单文件 blob 改为一遍读（边读边算 hash 边压）之后的端到端验收（第 2 期）。
/// 承重点：云端躺着的字节必须与源文件逐字节相同——加密、分卷、store-only、raw 直传四种组合都要对；
/// 已经存在的内容仍然靠预筛整个跳过，不因为"压完才知道名字"而白压一遍。
/// </summary>
[Trait("Category", "Integration")]
public sealed class StreamingBackupTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _root;
    private readonly string _temp;

    public StreamingBackupTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-sbk-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_root);
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

    private async Task<string> WriteSourceAsync(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(full, bytes);
        return full;
    }

    /// <summary>把某个索引条目的 blob 拉回本地、解出内容，与源文件逐字节比对。
    /// 不比对 hash：hash 与索引来自同一遍读，拿它自证是循环论证。</summary>
    private async Task AssertBlobMatchesSourceAsync(
        BlobContainerClient container, IndexEntry entry, string? password)
    {
        var dir = Path.Combine(_temp, "check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var firstVolume = await VolumeBlobIO.DownloadAsync(container, entry.Storage!.Ref, dir, CancellationToken.None);

        var restored = Path.Combine(dir, "restored.bin");
        if (entry.Storage.Raw)
        {
            File.Copy(firstVolume, restored, overwrite: true);
        }
        else
        {
            await using var output = File.Create(restored);
            await new SevenZipCompressor().ExtractToStreamAsync(firstVolume, entry.Path, password, output);
        }

        var source = Path.Combine(_root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(restored));
        Assert.Equal(new FileInfo(source).Length, entry.Length);
    }

    [SkippableTheory]
    [InlineData(null, null, false, false)] // 压缩，单卷
    [InlineData("pw", null, false, false)] // 加密 + 头加密
    [InlineData("pw", 64 * 1024L, false, false)] // 加密 + 分卷
    [InlineData(null, null, true, true)]   // store-only + 无密码 + 不分卷 → raw 直传
    [InlineData("pw", null, true, false)]  // store-only 但加了密 → 仍走 7z 封装
    public async Task Stored_Bytes_Match_The_Source_File(
        string? password, long? volumeBytes, bool dontCompress, bool expectRaw)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher());

        var account = AzuriteAccount();
        var name = RandomName("sbk-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await WriteSourceAsync("media/clip.bin", 250_000);

            await orchestrator.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _root, Name = "stream", Password = password,
                Options = new BackupEngineOptions
                {
                    VolumeBytes = volumeBytes,
                    DontCompress = dontCompress ? new IgnoreRuleSet(["*.bin"]) : null,
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                },
            });

            var info = await store.ReadInfoAsync(account, name, password);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, password);
            var entry = Assert.Single(idx.Entries);

            Assert.Equal("blob", entry.Storage!.Kind);
            Assert.Equal(expectRaw, entry.Storage.Raw);
            await AssertBlobMatchesSourceAsync(container, entry, password);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>数一数流式压缩被调了几次。压完才知道 blob 名，所以"已经存在的内容"要靠预筛
    /// 提前拦下——拦不住的话，一个被改名的 4 GB 文件每次备份都要白压一遍。</summary>
    private sealed class CountingStreamCompressor(IFileCompressor inner) : IFileCompressor
    {
        private int _streamCompressions;
        public int StreamCompressions => Volatile.Read(ref _streamCompressions);

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _streamCompressions);
            return inner.CompressStreamAsync(request, writeSource, ct);
        }

        public Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
            => inner.CompressAsync(request, ct);

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

    [SkippableFact]
    public async Task Content_Already_In_The_Backup_Is_Never_Recompressed()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        conn.Open();
        using var db = new AzureStorageBackup.Api.Data.AppDbContext(
            new DbContextOptionsBuilder<AzureStorageBackup.Api.Data.AppDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();

        var compressor = new CountingStreamCompressor(new SevenZipCompressor());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            compressor, new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
            indexCache: new LocalIndexCache(db, store),
            trackedInfo: new TrackedInfoStore(store, new LocalBackupStateStore(db)));

        var account = AzuriteAccount();
        var name = RandomName("sbkd-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        BackupRequest Request() => new()
        {
            Account = account, Container = name, LocalRoot = _root, Name = "dedup",
            Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
        };

        try
        {
            await WriteSourceAsync("big.bin", 300_000);
            await orchestrator.RunAsync(Request());
            Assert.Equal(1, compressor.StreamCompressions);

            // 改个名：内容一个字节都没变，diff 却把它看成新增。预筛必须在压之前就认出这份内容。
            File.Move(Path.Combine(_root, "big.bin"), Path.Combine(_root, "renamed.bin"));
            await orchestrator.RunAsync(Request());

            Assert.Equal(1, compressor.StreamCompressions); // 一次都没有再压
            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var entry = Assert.Single(idx.Entries, e => e.Path == "renamed.bin");
            Assert.True(await VolumeBlobIO.ExistsAsync(container, entry.Storage!.Ref, CancellationToken.None));

            // 去重的两条记录必须指向同一个 blob，而不是各存一份。
            var dataBlobs = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, "data/", CancellationToken.None))
                dataBlobs.Add(b.Name);
            Assert.Single(dataBlobs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
