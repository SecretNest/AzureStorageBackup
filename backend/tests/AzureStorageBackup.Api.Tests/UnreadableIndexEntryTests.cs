using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Decision 5: when a file cannot be read this run (in use / no permission), the index must not treat it as deleted, and must not fabricate a broken
/// entry whose content reference is empty — it should carry the file's previous-version entry forward and stamp UnreadableAt on it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnreadableIndexEntryTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public UnreadableIndexEntryTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-unread-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Reads of the given path always throw the given exception; every other file is hashed as usual (same approach as BackupDifferUnreadableTests).</summary>
    private sealed class ThrowingHasher(string lockedPath, Exception toThrow) : IFileHasher
    {
        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("head-" + Path.GetFileName(path));

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("tail-" + Path.GetFileName(path));

        public Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("full-" + Path.GetFileName(path));
    }

    /// <summary>Builds a runnable orchestrator; with no differ it uses the real hasher, and passing a custom differ lets you simulate a file being unreadable.</summary>
    private (BackupOrchestrator Orchestrator, IBackupInfoStore Store, BlobClientFactory Factory) Build(BackupDiffer? differ = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var tag = Guid.NewGuid().ToString("N");
        var staging = new StagingArea(
            Path.Combine(_temp, "compress-" + tag), Path.Combine(_temp, "staged-" + tag), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), differ ?? new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
        return (orchestrator, store, factory);
    }

    private BackupRequest Request(Account account, string container, RetentionPolicy? retention = null) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions
        {
            // Single-file threshold dropped to 1: every file becomes its own data/{hash} blob, which makes it easy to assert by hash whether a blob got reclaimed.
            Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
            Retention = retention ?? new RetentionPolicy(),
        },
    };

    [SkippableFact]
    public async Task A_Previously_Backed_Up_File_Carries_Its_Old_Entry_Forward()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator1, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("unreadfw-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "hello");
            await orchestrator1.RunAsync(Request(account, name)); // v1: a normal backup

            var info1 = await store.ReadInfoAsync(account, name, null);
            var idx1 = await store.ReadIndexAsync(account, name, info1!.Versions[0].IndexBlob, null);
            var prevEntry = Assert.Single(idx1.Entries);

            // v2: inject a hasher that throws for a.txt to simulate it being unreadable this run; also change the file's length so that length+mtime
            // cannot classify it Unchanged and skip reading it altogether — Unreadable is only triggered by actually reaching the hashing stage.
            var (orchestrator2, _, _) = Build(new BackupDiffer(new ThrowingHasher("a.txt",
                new IOException("The process cannot access the file 'a.txt' because it is being used by another process."))));
            File.WriteAllText(Path.Combine(_root, "a.txt"), "hello world!");
            await orchestrator2.RunAsync(Request(account, name)); // v2: unreadable

            var info2 = await store.ReadInfoAsync(account, name, null);
            var idx2 = await store.ReadIndexAsync(account, name, info2!.Versions[^1].IndexBlob, null);
            var entry = Assert.Single(idx2.Entries);

            Assert.NotNull(entry.UnreadableAt);
            // The new entry points at exactly the same already-uploaded content as the old one — carrying the old values forward, not making something up out of this run's newly scanned length/content.
            Assert.Equal(prevEntry.Storage!.Ref, entry.Storage!.Ref);
            Assert.Equal(prevEntry.FullHash, entry.FullHash);
            Assert.Equal(prevEntry.Length, entry.Length); // 5 ("hello"), not this run's 12 ("hello world!")
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The guardrail for decision 5. If unreadable were treated as deleted, a few rounds of retention would
    /// wipe a file that is merely in use long-term out of every version — while each run's warning looks like nothing more than "skipped one file".</summary>
    [SkippableFact]
    public async Task An_Unreadable_File_Is_Never_Recorded_As_Deleted()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 2 };
        var (orchestrator1, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("unreaddel-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "hello");
            await orchestrator1.RunAsync(Request(account, name, retention)); // v1: a normal backup, uploads data/{hash}

            var info1 = await store.ReadInfoAsync(account, name, null);
            var idx1 = await store.ReadIndexAsync(account, name, info1!.Versions[0].IndexBlob, null);
            var hash = Assert.Single(idx1.Entries).FullHash!;

            // Three runs in a row where a.txt is unreadable. MaxVersions=2 retires v1 somewhere in those runs — and if a.txt were treated as deleted,
            // no later version would reference data/{hash} any more, so retiring v1 would reclaim it as "exclusively owned data" and the file would be lost forever.
            var (orchestrator2, _, _) = Build(new BackupDiffer(new ThrowingHasher("a.txt",
                new IOException("locked by another process"))));
            for (var i = 0; i < 3; i++)
                await orchestrator2.RunAsync(Request(account, name, retention));

            var infoFinal = await store.ReadInfoAsync(account, name, null);
            var idxFinal = await store.ReadIndexAsync(account, name, infoFinal!.Versions[^1].IndexBlob, null);

            // Still present in the newest version's index — it did not vanish from the entries as if it had been deleted.
            Assert.Contains(idxFinal.Entries, e => e.Path == "a.txt");
            // And the data blob it references still exists: proof that every run kept it referenced by a version that is being retained,
            // rather than leaving it exclusively owned by a retired version and reclaimed along with it (which is exactly what happens when it is treated as deleted).
            Assert.True(await container.GetBlobClient("data/" + hash).ExistsAsync(),
                "unreadable file's data blob was reclaimed by retention as if the file had been deleted");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task A_Brand_New_Unreadable_File_Is_Absent_From_The_Version()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, store, factory) = Build(new BackupDiffer(new ThrowingHasher("new.txt",
            new IOException("locked by another process"))));
        var account = AzuriteAccount();
        var name = RandomName("unreadnew-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("new.txt", "content nobody has ever successfully read");
            await orchestrator.RunAsync(Request(account, name)); // v1: never once read successfully, so there is no old entry to carry forward

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            Assert.DoesNotContain(idx.Entries, e => e.Path == "new.txt"); // no content to point at, and fabricating an entry would be a lie
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
