using System.Net.Sockets;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Records the fixture the SQLite catalog migration's resume test (Task 22) replays: a backup run suspended halfway
/// by the *current* release, together with the index the current release produces when that run is resumed to
/// completion. This is a one-time capture, not a regression test — it never runs in CI (gated on
/// <c>ASB_RECORD_FIXTURES=1</c>) — because its only purpose is to freeze this release's on-disk/on-cloud behavior
/// before the in-memory version index is replaced by a SQLite catalog, at which point the current behavior can no
/// longer be reproduced from the tree.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FixtureRecorderTests
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private const int SmallMinBytes = 1024;
    private const int SmallMaxBytes = 20 * 1024;
    private const int LargeBytes = 6_000_000;
    private static readonly string[] Dirs = ["dir1", "dir2", "dir3"];

    private static Account AzuriteAccount() => new()
    {
        Id = 44,
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

    /// <summary>Deterministic source tree: <paramref name="smallFiles"/> files of 1-20 KB spread round-robin over
    /// three directories (so per-directory packing has something to group), plus <paramref name="largeFiles"/>
    /// files of 6 MB at the root (above <c>SingleFileThresholdBytes</c>, so they land as single-file blobs).</summary>
    private static void WriteTree(string root, int seed, int smallFiles, int largeFiles)
    {
        var rnd = new Random(seed);
        foreach (var d in Dirs)
            Directory.CreateDirectory(Path.Combine(root, d));

        for (var i = 1; i <= smallFiles; i++)
        {
            var dir = Dirs[(i - 1) % Dirs.Length];
            var bytes = new byte[rnd.Next(SmallMinBytes, SmallMaxBytes + 1)];
            rnd.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(root, dir, $"file{i}.bin"), bytes);
        }

        for (var i = 1; i <= largeFiles; i++)
        {
            var bytes = new byte[LargeBytes];
            rnd.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(root, $"large{i}.bin"), bytes);
        }
    }

    /// <summary>Rewrites the content of the first <paramref name="modify"/> small files (same paths WriteTree used,
    /// spread across all three directories so every existing pack changes), and adds <paramref name="add"/> new
    /// files each in its own fresh directory (so each becomes an upload of its own — the second run needs several
    /// distinct upload events for "stop after the third" to actually interrupt something).</summary>
    private static void MutateTree(string root, int seed, int modify, int add)
    {
        var rnd = new Random(seed);
        for (var i = 1; i <= modify; i++)
        {
            var dir = Dirs[(i - 1) % Dirs.Length];
            var bytes = new byte[rnd.Next(SmallMinBytes, SmallMaxBytes + 1)];
            rnd.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(root, dir, $"file{i}.bin"), bytes);
        }

        for (var i = 1; i <= add; i++)
        {
            var dir = Path.Combine(root, $"extra{i}");
            Directory.CreateDirectory(dir);
            var bytes = new byte[rnd.Next(SmallMinBytes, SmallMaxBytes + 1)];
            rnd.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(dir, "new.bin"), bytes);
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        if (!Directory.Exists(source))
            return;
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
    }

    /// <summary>Every blob currently in the container, name-encoded with <c>/</c> → <c>__</c> so a multi-segment
    /// blob name (packs and volumes live under nested "directories") survives as a single flat file.</summary>
    private static async Task DownloadAllBlobsAsync(
        BlobClientFactory factory, Account account, string container, string dest)
    {
        Directory.CreateDirectory(dest);
        var client = factory.CreateServiceClient(account).GetBlobContainerClient(container);
        await foreach (var item in client.GetBlobsAsync())
        {
            var path = Path.Combine(dest, item.Name.Replace("/", "__"));
            await client.GetBlobClient(item.Name).DownloadToAsync(path);
        }
    }

    /// <summary>The locally authoritative info-file bytes (design §3.3): what <see cref="TrackedInfoStore"/> has
    /// cached for this (account, container) right now, exactly as <see cref="IndexSerializer.SerializeInfoFile"/>
    /// produced them.</summary>
    private static async Task<byte[]> LocalInfoBytesAsync(ILocalBackupStateStore state, int accountId, string container)
    {
        var local = await state.TryGetAsync(accountId, container);
        Assert.NotNull(local);
        return local.Value.InfoBytes;
    }

    /// <summary>Stops the run after the Nth successful upload — the same device as <c>BackupResumeTests.CountingUploader</c>,
    /// with the stop wired straight to a <see cref="BackupRunControl"/> instead of a generic callback. Both
    /// <c>UploadIfMissingAsync</c> overloads have to be taken over: the main backup path always goes through the one
    /// with progress, which has a default interface implementation, so taking over only the other one would
    /// intercept nothing and this would never fire.</summary>
    private sealed class SuspendAfterUploads(IBlobUploader inner, int stopAt) : IBlobUploader
    {
        private int _count;

        public BackupRunControl? Control { get; set; }

        private async Task<T> RunAsync<T>(Func<Task<T>> call)
        {
            var result = await call();
            if (Interlocked.Increment(ref _count) == stopAt)
                Control!.RequestStop(StopKind.Suspend);
            return result;
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync(() => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct,
            IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => RunAsync(() => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync<bool>(async () =>
            {
                await inner.UploadOverwriteAsync(
                    account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    /// <summary>
    /// Builds one orchestrator over a fresh in-memory local-authority database (dedup/index-cache/local-state all
    /// backfill from the journal/cloud on first read, so a fresh db per round is fine — see
    /// <see cref="TrackedInfoStore"/> and <see cref="LocalIndexCache"/>), but a <paramref name="journals"/> store and
    /// <paramref name="indexRoot"/> that the caller controls and can snapshot, unlike the throwaway ones
    /// <c>TestLocalAuthority</c>/<c>TestIndexFiles</c> hide from the test. <paramref name="journals"/> is also wired
    /// into <see cref="RetentionCleaner"/> (production does the same via DI — see <c>Program.cs</c>), so the closing
    /// cleanup of a resumed run correctly treats another active journal's blocks as claimed rather than orphaned.
    /// </summary>
    private static (BackupOrchestrator Orchestrator, BackupInfoStore Store, BlobClientFactory Factory, ILocalBackupStateStore LocalState) Build(
        IBlobUploader? uploader, BackupJournalStore journals, string indexRoot, string tempRoot)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(tempRoot, "compress"), Path.Combine(tempRoot, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(tempRoot, "compact"),
            staging);

        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        var indexCache = new LocalIndexCache(db, store, new VersionIndexFileStore(indexRoot));
        var localState = new LocalBackupStateStore(db);
        var tracked = new TrackedInfoStore(store, localState);

        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: indexCache, trackedInfo: tracked, journals: journals),
            new FileHasher(), indexCache, tracked,
            workFactory: TestWorkDbs.New());
        return (orchestrator, store, factory, localState);
    }

    private static BackupRequest Request(Account account, string container, string localRoot) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = localRoot,
        Name = "fixture",
        Options = new BackupEngineOptions
        {
            UploadConcurrency = 1,
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    [SkippableFact]
    public async Task Record_resume_fixture()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("ASB_RECORD_FIXTURES") == "1", "recording only");
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures/resume-2026.9.7"));
        if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
        Directory.CreateDirectory(fixture);

        // Working state lives outside the fixture directory: journal-root/index-cache-root are scratch that the
        // orchestrator writes to as it runs, and only their post-suspend snapshot (copied below into
        // fixture/journal and fixture/index-cache) is what gets committed.
        var scratch = Path.Combine(Path.GetTempPath(), "asb-fixture-record-" + Guid.NewGuid().ToString("N"));
        var journalRoot = Path.Combine(scratch, "journal-root");
        var indexRoot = Path.Combine(scratch, "index-cache-root");
        var journals = new BackupJournalStore(journalRoot);

        var account = AzuriteAccount();
        var container = RandomName("fixture");
        var rootFactory = new BlobClientFactory(TestSecrets.Reader);
        var containerClient = rootFactory.CreateServiceClient(account).GetBlobContainerClient(container);
        try
        {
            // 1. source tree (deterministic content so the fixture is reproducible)
            var source = Path.Combine(fixture, "source");
            WriteTree(source, seed: 7, smallFiles: 38, largeFiles: 2);

            // 2. version 1: a full backup, no journal needed since it runs to completion.
            var (o1, _, _, _) = Build(uploader: null, journals, indexRoot, Path.Combine(scratch, "temp1"));
            var v1 = await o1.RunAsync(Request(account, container, source));
            Assert.Equal(1, v1.Version);

            // 3. mutate, then suspend the second run after three uploads.
            MutateTree(source, seed: 8, modify: 10, add: 5);
            var realUploader = new BlobUploader(rootFactory);
            var stopAfter = new SuspendAfterUploads(realUploader, stopAt: 3);
            var (o2, _, factory2, localState2) = Build(stopAfter, journals, indexRoot, Path.Combine(scratch, "temp2"));

            const string suspendedRunId = "fixture-run";
            await using (var control = new BackupRunControl(journals, configId: 1, runId: suspendedRunId))
            {
                stopAfter.Control = control;
                await Assert.ThrowsAsync<BackupSuspendedException>(
                    () => o2.RunAsync(Request(account, container, source), control: control));
            }

            // 4. snapshot everything the next release has to read.
            CopyDirectory(journalRoot, Path.Combine(fixture, "journal"));
            CopyDirectory(indexRoot, Path.Combine(fixture, "index-cache"));
            await File.WriteAllBytesAsync(
                Path.Combine(fixture, "info.bin"), await LocalInfoBytesAsync(localState2, account.Id, container));
            await DownloadAllBlobsAsync(factory2, account, container, Path.Combine(fixture, "blobs"));
            await File.WriteAllTextAsync(Path.Combine(fixture, "container.txt"), container);
            await File.WriteAllTextAsync(
                Path.Combine(fixture, "config.json"),
                JsonSerializer.Serialize(
                    new { configId = 1, runId = suspendedRunId, localRoot = source },
                    new JsonSerializerOptions { WriteIndented = true }));

            // 5. what the current release produces when it finishes the run.
            var (o3, store3, _, _) = Build(uploader: null, journals, indexRoot, Path.Combine(scratch, "temp3"));
            BackupRunResult result;
            await using (var control3 = new BackupRunControl(journals, configId: 1, runId: "fixture-run-2"))
                result = await o3.RunAsync(Request(account, container, source), control: control3);
            Assert.Equal(2, result.Version);

            var info = await store3.ReadInfoAsync(account, container, null);
            Assert.NotNull(info);
            var v2 = info!.Versions.Single(v => v.Version == 2);
            var index = await store3.ReadIndexAsync(account, container, v2.IndexBlob, null, v2.IndexVolumes);

            Directory.CreateDirectory(Path.Combine(fixture, "expected"));
            await File.WriteAllBytesAsync(
                Path.Combine(fixture, "expected", "v2.idx"), IndexSerializer.SerializeIndex(index));
            await File.WriteAllBytesAsync(
                Path.Combine(fixture, "expected", "info.bin"), IndexSerializer.SerializeInfoFile(info));
        }
        finally
        {
            try { await containerClient.DeleteIfExistsAsync(); } catch { /* best effort */ }
            try { Directory.Delete(scratch, recursive: true); } catch { /* best effort; it is temp space */ }
        }
    }
}
