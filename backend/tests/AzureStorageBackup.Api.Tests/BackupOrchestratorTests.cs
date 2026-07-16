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

    private (BackupOrchestrator Orchestrator, IBackupInfoStore Store, BlobClientFactory Factory) Build()
    {
        var factory = new BlobClientFactory();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), stagedLimitBytes: 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging);
        return (orchestrator, store, factory);
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
