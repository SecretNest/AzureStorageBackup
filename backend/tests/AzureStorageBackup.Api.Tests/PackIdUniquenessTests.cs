using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Pack ids must be unique **across runs**.
/// <para>
/// Unlike data blobs they are not content-addressed — the name carries no trace of the content. Ids used to be
/// handed out continuing from the largest one in the info file, so a failed previous run produced a collision:
/// that run had already uploaded <c>packs/p0001.7z</c> but never managed to write the info file, and the next
/// run starts from p0001 again — with that same-numbered pack holding **a different set of members**. Uploads
/// go through if-missing, so a name clash is silently skipped, while the index claims the pack contains this
/// run's members — restore finds nothing there and a batch of files goes silently missing.
/// </para>
/// <para>
/// This is a real state of the user's container: a failed backup left data/ and packs/ behind, with no info file.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackIdUniquenessTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _src;
    private readonly string _dst;
    private readonly string _temp;

    public PackIdUniquenessTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-packid-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(baseDir, "src");
        _dst = Path.Combine(baseDir, "dst");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_src)!, recursive: true); } catch { /* best effort */ }
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

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store) Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"));
        return (backup, restore, store);
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _src,
        Name = "packid",
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    /// <summary>
    /// Simulates "the previous run failed and never wrote the info file": back up a batch of files, then delete
    /// the index and the info file, leaving only data/ and packs/ — exactly the shape of the user's container.
    /// Then run another backup **with a different batch of files**; the new packs must not collide with any of
    /// the leftover pack names, and what restores must be this run's content.
    /// </summary>
    [SkippableFact]
    public async Task A_Rerun_After_A_Failed_Run_Never_Reuses_A_Leftover_Pack_Name()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packid-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // Round one: leaves data/ and packs/ behind.
            Write("first/a.txt", new string('a', 400));
            Write("first/b.txt", new string('b', 400));
            await backup.RunAsync(Request(account, name));

            var leftoverPacks = new List<string>();
            await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, "packs/", CancellationToken.None))
                leftoverPacks.Add(b.Name);
            Assert.NotEmpty(leftoverPacks);

            // Wiping the index and the info file = "that run never finished". data/ and packs/ stay where they are.
            await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, "indexes/", CancellationToken.None))
                await cc.GetBlobClient(b.Name).DeleteIfExistsAsync();
            await cc.GetBlobClient(BackupDiscovery.IndexBlobName).DeleteIfExistsAsync();

            // Round two uses a fresh orchestrator, because "that run never finished" leaves nothing behind
            // locally either: writing local state is part of the finishing sequence, and a run that dies halfway
            // never gets there. Reusing the previous orchestrator would leave local state still remembering round
            // one's info file and its ETag — that is not the shape of "the run failed" but of "someone else
            // touched the cloud", and the write-back would hit a 412, clear local state and demand a rerun
            // (see TrackedInfoStore.WriteAsync).
            var (backup2, _, _) = Build();

            // **A different batch** of files. Neither local nor cloud has an info file, so this is a "brand new" backup.
            Directory.Delete(Path.Combine(_src, "first"), recursive: true);
            Write("second/c.txt", new string('c', 400));
            Write("second/d.txt", new string('d', 400));
            await backup2.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var index = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var refs = index.Entries.Where(e => e.Storage?.Kind == "pack")
                .Select(e => $"packs/{e.Storage!.Ref}.7z").Distinct(StringComparer.Ordinal).ToList();
            Assert.NotEmpty(refs);

            // The crux: not one pack name from this run may be among the leftovers. A collision means the index
            // points at a pack holding someone else's members, while if-missing quietly skips the upload.
            Assert.Empty(refs.Intersect(leftoverPacks, StringComparer.Ordinal));

            // And what restores must be **this run's** content.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(new string('c', 400), await File.ReadAllTextAsync(Path.Combine(_dst, "second", "c.txt")));
            Assert.Equal(new string('d', 400), await File.ReadAllTextAsync(Path.Combine(_dst, "second", "d.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>Ids handed out within one run must all differ — the bare minimum half of "unique".</summary>
    [SkippableFact]
    public async Task Packs_Within_One_Run_Get_Distinct_Names()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, _, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packidmulti-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // Each directory becomes its own pack (packing is per-directory by default), so this run hands out several ids.
            for (var i = 0; i < 5; i++)
                Write($"dir{i}/f.txt", new string((char)('a' + i), 300));
            await backup.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var index = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var packIds = index.Entries.Where(e => e.Storage?.Kind == "pack")
                .Select(e => e.Storage!.Ref).ToList();

            var distinct = packIds.Distinct(StringComparer.Ordinal).ToList();
            Assert.True(distinct.Count > 1, "this run should have produced multiple packs");
            // Every pack name really exists in the container (the ids were handed out right, nothing points at a missing object).
            foreach (var id in distinct)
            {
                var single = await cc.GetBlobClient($"packs/{id}.7z").ExistsAsync();
                var first = await cc.GetBlobClient($"packs/{id}.7z.001").ExistsAsync();
                Assert.True(single.Value || first.Value, $"packs/{id}.7z should be in the container");
            }
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
