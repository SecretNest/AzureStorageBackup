using System.Net.Sockets;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The info file is the only authority on which versions a backup has; the catalog is a local cache that can fall
/// out of step with it. Retention deletes a retired version's exclusively-owned blobs from the cloud FIRST and drops
/// its catalog rows afterwards, so a Stop, a shutdown or one 5xx in between leaves a catalog holding a version whose
/// data is already gone — and every dedup, collision-avoidance and prescreen query the catalog answers is unbounded
/// by version. This class pins the one thing that makes that state harmless: a run reconciles the catalog against
/// the info file before it asks it anything.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogReconcileTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "asb-reconcile-" + Guid.NewGuid().ToString("N"));
    private readonly string _src;
    private readonly string _temp;

    public CatalogReconcileTests()
    {
        _src = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_src);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    private static Account AzuriteAccount() => new()
    {
        Id = 1,
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static byte[] Rand(int size, int seed)
    {
        var buf = new byte[size];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    private void Write(string rel, byte[] content) =>
        File.WriteAllBytes(Path.Combine(_src, rel), content);

    /// <summary>The single-file blob a version's entry points at, read out of the catalog the way dedup reads it.</summary>
    private static async Task<string> RefOfAsync(IVersionCatalogs catalogs, Account account, string container, int version, string path)
    {
        await using var catalog = await catalogs.OpenAsync(account.Id, container, readOnly: true, CancellationToken.None);
        var entries = await catalog.EntriesAtAsync(version, [path], CancellationToken.None);
        var storage = Assert.Single(entries).Storage;
        Assert.NotNull(storage);
        Assert.Equal("blob", storage!.Kind);
        return storage.Ref;
    }

    [SkippableFact]
    public async Task A_version_the_info_file_no_longer_lists_cannot_dedup_a_new_file_onto_its_deleted_blob()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        var container = "reconcile-" + Guid.NewGuid().ToString("N")[..8];
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(blobFactory, new SevenZipArchiveCodec());
        var hasher = new FileHasher();
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(db));
        var catalogs = TestCatalogs.New(db, store);
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(hasher), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(blobFactory), blobFactory, store, staging,
            new RetentionCleaner(blobFactory, store, new RetentionEvaluator(), catalogs: catalogs, trackedInfo: tracked),
            hasher, catalogs: catalogs, trackedInfo: tracked, workFactory: TestWorkDbs.New());

        var account = AzuriteAccount();
        var cc = blobFactory.CreateServiceClient(account).GetBlobContainerClient(container);

        BackupRequest Request() => new()
        {
            Account = account,
            Container = container,
            LocalRoot = _src,
            Name = "reconcile-fixture",
            // 20 KB threshold, so `lonely.bin` below is stored as its own data blob rather than folded into a pack:
            // a single-file blob is what the content-addressed dedup lookup hands back, and the whole point here is
            // which blob a new file is pointed at.
            Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 20_000 } },
        };

        try
        {
            // v1 holds a file nothing else does…
            var lonely = Rand(40_000, 11);
            Write("keep.txt", Rand(30_000, 12));
            Write("lonely.bin", lonely);
            Assert.Equal(1, (await orchestrator.RunAsync(Request())).Version);
            var lonelyRef = await RefOfAsync(catalogs, account, container, 1, "lonely.bin");

            // …and v2 does not, so v1 alone references that blob.
            File.Delete(Path.Combine(_src, "lonely.bin"));
            Assert.Equal(2, (await orchestrator.RunAsync(Request())).Version);

            // Now stage exactly what an interrupted cleanup leaves behind: the info file has already committed v1's
            // retirement (retention commits before it deletes), the blob v1 alone owned is gone from the cloud, and
            // the process died before `RemoveVersionAsync` reached the catalog — which still holds v1 and its rows.
            var info = await tracked.LoadAsync(account, container, null, CancellationToken.None);
            Assert.NotNull(info);
            info!.Versions.RemoveAll(v => v.Version == 1);
            await tracked.WriteAsync(account, container, info, null, tier: null, ct: CancellationToken.None);
            Assert.True(await cc.GetBlobClient(lonelyRef).DeleteIfExistsAsync());

            await using (var stale = await catalogs.OpenAsync(account.Id, container, readOnly: true, CancellationToken.None))
                Assert.Equal([1, 2], (await stale.ListVersionsAsync(CancellationToken.None)).Select(v => v.Version));

            // The same content comes back. Before the reconcile, dedup found v1's row, recorded the deleted blob's
            // address as this file's storage and uploaded nothing — a version that says a file is backed up at a
            // name holding nothing at all.
            Write("lonely.bin", lonely);
            Assert.Equal(3, (await orchestrator.RunAsync(Request())).Version);

            // The address is content-derived, so the new version may perfectly well name the same blob — what must
            // not happen is naming it without the bytes being there.
            var freshRef = await RefOfAsync(catalogs, account, container, 3, "lonely.bin");
            Assert.True(await cc.GetBlobClient(freshRef).ExistsAsync(),
                $"version 3 points 'lonely.bin' at {freshRef}, which does not exist in the container");

            // And the stale version is out of the catalog, which is what made the answer above possible.
            await using var after = await catalogs.OpenAsync(account.Id, container, readOnly: true, CancellationToken.None);
            Assert.Equal([2, 3], (await after.ListVersionsAsync(CancellationToken.None)).Select(v => v.Version));
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }
}
