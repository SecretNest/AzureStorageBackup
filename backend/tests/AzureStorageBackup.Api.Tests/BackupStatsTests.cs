using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The numbers in the summary come out of a real run; they are not the differ parroting its own diff back.
/// These tests run actual backups to verify three things a pure-function test cannot: each ChangeKind is
/// counted correctly, uploaded bytes count only what was really pushed (a dedup hit must not add one byte),
/// and retention cleanup can report how much it deleted.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupStatsTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public BackupStatsTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-stats-" + Guid.NewGuid().ToString("N"));
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

    private static readonly DateTime MtimeBase = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private int _mtimeSeq;

    /// <summary>Write a file and advance its mtime — a same-length rewrite that leaves mtime alone is judged unchanged by the differ.</summary>
    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, MtimeBase.AddMinutes(++_mtimeSeq));
    }

    private void Delete(string rel) =>
        File.Delete(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));

    private BackupOrchestrator Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
    }

    /// <summary>
    /// Threshold squeezed down to 20 KB (the default is 5 MB) so that files of a few tens of KB already exercise
    /// both storage paths: above the threshold goes to a content-addressed single-file data blob (which **does**
    /// dedup), below it gets grouped into a pack (a fresh pack every run, no cross-pack dedup).
    /// Without squeezing it we would have to write files over 5 MB to reach the blob path, making the test far
    /// slower for nothing.
    /// </summary>
    private const long SingleFileThreshold = 20_000;

    private BackupRequest Request(Account account, string container, int maxVersions = 0) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "stats",
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = SingleFileThreshold },
            Retention = maxVersions > 0
                ? new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = maxVersions }
                : new RetentionPolicy(),
        },
    };

    [SkippableFact]
    public async Task Counts_New_Modified_And_Deleted_Separately()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var orchestrator = Build();
        var account = AzuriteAccount();
        var name = RandomName("stats-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            Write("keep.txt", "unchanged");
            Write("edit.txt", "before");
            Write("gone.txt", "doomed-but-weighed");
            var v1 = await orchestrator.RunAsync(Request(account, name));

            // First run: all three files are new, nothing modified and nothing deleted.
            Assert.Equal(3, v1.NewFiles);
            Assert.Equal(0, v1.ModifiedFiles);
            Assert.Equal(0, v1.DeletedFiles);
            Assert.Equal(0, v1.DeletedBytes);

            Write("edit.txt", "after-and-longer");   // modify one
            Write("added.txt", "brand new");         // add one
            Delete("gone.txt");                      // delete one
            var v2 = await orchestrator.RunAsync(Request(account, name));

            Assert.Equal(1, v2.NewFiles);
            Assert.Equal(1, v2.ModifiedFiles);
            Assert.Equal(1, v2.DeletedFiles);
            // The size the deleted file had at the source, read off the previous version's index entry — the file
            // itself is gone by now, so the index is the only place left that knows how big it was.
            Assert.Equal("doomed-but-weighed".Length, v2.DeletedBytes);
            // Unchanged keep.txt counts as neither new nor modified — that is precisely the part incremental backup saves.
            Assert.Equal(v2.ChangedFiles, v2.NewFiles + v2.ModifiedFiles);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Uploaded bytes may only count what actually went up to the cloud. Back the same content up again under a
    /// different path: dedup hits, so on the source side this run "changed" an entire file while uploaded bytes
    /// must be 0 — reporting the two figures separately exists for exactly this case.
    /// <para>The content must be **above** the single-file threshold: dedup is a property of content-addressed data blobs; packs get a fresh pack every run and never dedup across packs.</para>
    /// </summary>
    [SkippableFact]
    public async Task Uploaded_Bytes_Exclude_Deduplicated_Content()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var orchestrator = Build();
        var account = AzuriteAccount();
        var name = RandomName("statsdedup-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var payload = new string('x', 50_000);
            Write("one.txt", payload);
            var v1 = await orchestrator.RunAsync(Request(account, name));
            Assert.True(v1.UploadedBytes > 0, "the first run must really have uploaded something");

            // Same content, different path: a new file on the source side, but not one extra byte in the cloud.
            Write("copy.txt", payload);
            var v2 = await orchestrator.RunAsync(Request(account, name));

            Assert.Equal(1, v2.NewFiles);
            Assert.True(v2.ChangedBytes >= 50_000, "the source side really did change an entire file");
            Assert.Equal(0, v2.UploadedBytes);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// When retention retires an old version, whatever it deleted has to be countable — otherwise that line in the log stays 0 forever.
    /// Both the pack path and the data blob path must be covered: they are counted separately, so testing only one hides that the other is not counting at all.
    /// </summary>
    [SkippableFact]
    public async Task Reports_What_Retention_Deleted()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var orchestrator = Build();
        var account = AzuriteAccount();
        var name = RandomName("statsret-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Keep only 1 version: the moment the second run finishes, v1 and the data only it references should be swept away.
            Write("big.bin", new string('a', 60_000));    // > threshold → single-file data blob
            Write("small.txt", new string('a', 5_000));   // < threshold → grouped into a pack
            var v1 = await orchestrator.RunAsync(Request(account, name, maxVersions: 1));
            Assert.True(v1.Cleanup.IsEmpty, "nothing to retire when there is only one version");

            Write("big.bin", new string('b', 60_000));
            Write("small.txt", new string('b', 5_000));
            var v2 = await orchestrator.RunAsync(Request(account, name, maxVersions: 1));

            Assert.False(v2.Cleanup.IsEmpty);
            Assert.Equal(1, v2.Cleanup.RetiredVersions);
            // Neither of v1's two contents is referenced any more; the objects on both storage paths should be deleted and counted as freed.
            Assert.True(v2.Cleanup.DeletedBlobs > 0, "v1's exclusive data blob should be deleted");
            Assert.True(v2.Cleanup.DeletedPacks > 0, "v1's exclusive pack should be deleted");
            Assert.True(v2.Cleanup.FreedBytes > 0, "freed bytes should be accumulated");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
