using System.Net.Sockets;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

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
        AccountKey = AzuriteKey,
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
        IBlobUploader? uploader = null, ProcessingVerifier? verifier = null)
    {
        var factory = new BlobClientFactory();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), stagedLimitBytes: 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), verifier: verifier);
        return (orchestrator, store, factory);
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
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default)
        {
            lock (_l) { _current++; _max = Math.Max(_max, _current); }
            try
            {
                await Task.Delay(250, ct); // 让上传明显长于压缩，使并发窗口稳定可观测
                return await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct);
            }
            finally { lock (_l) _current--; }
        }

        public Task UploadBatchAsync(
            Account account, string container, IReadOnlyList<UploadItem> items,
            int maxConcurrency, RetryOptions? retry = null, CancellationToken ct = default)
            => inner.UploadBatchAsync(account, container, items, maxConcurrency, retry, ct);
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
            var blobName = e.Storage!.Kind == "pack" ? $"packs/{e.Storage.Ref}.7z" : e.Storage.Ref;
            Assert.True(await container.GetBlobClient(blobName).ExistsAsync(), $"missing blob {blobName} for {e.Path}");
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

        var factoryProbe = new BlobClientFactory();
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
