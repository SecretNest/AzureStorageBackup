using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Steps 7/8/9 commit the version; step 10 (retention cleanup and dead-weight compaction) is maintenance that
/// happens to be tacked onto the end of the run. A multi-day 3 TB backup came back as
/// "Backup failed: Public — Retry failed after 6 tries. (…exceeded the configured timeout of 0:01:40.) ×6",
/// and the reason it always struck at the end is that cleanup is the one stretch whose work grows with the size of
/// the backup while having no volume splitting and no per-item retry to absorb a bad patch of network.
/// <para>
/// The orchestrator already spells this out for the cancel case — "the version was committed long ago, so this is
/// still a successful backup, only the cleanup did not finish" — but that reasoning only ever covered
/// OperationCanceledException. A network failure walked straight past it into the catch-all and condemned a backup
/// whose data was already safely in the cloud.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CleanupFailureTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public CleanupFailureTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-cleanfail-" + Guid.NewGuid().ToString("N"));
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
        Id = 71,
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

    private void Write(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
    }

    /// <summary>
    /// Hands out working clients until <see cref="Armed"/>, then throws the exact shape the incident produced: an
    /// AggregateException wrapping six network timeouts, which is what Azure's own retry policy raises once it has
    /// exhausted its attempts. Only the cleaner gets this factory, so the backup itself uploads normally — that is
    /// the whole point, since the bug is about what happens *after* the version is committed.
    /// </summary>
    private sealed class FailsWhenArmed(BlobClientFactory inner) : IBlobClientFactory
    {
        public bool Armed { get; set; }

        public BlobServiceClient CreateServiceClient(Account account)
        {
            if (Armed)
                throw new AggregateException(
                    "Retry failed after 6 tries.",
                    Enumerable.Range(0, 6).Select(_ => (Exception)new TaskCanceledException(
                        "The operation was cancelled because it exceeded the configured timeout of 0:01:40.")));
            return inner.CreateServiceClient(account);
        }

        public Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default)
            => inner.TestConnectionAsync(account, ct);
    }

    private sealed class RecordingOperationLog : IOperationLog
    {
        public List<(OperationLogLevel Level, string Source, string Message)> Entries { get; } = [];

        public Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default, bool? durable = null)
        {
            lock (Entries) Entries.Add((level, source, message));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LogEntry>> QueryAsync(
            OperationLogLevel? minLevel, string? source, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LogEntry>>([]);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;
        public Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Fails every upload with an error that is *not* transient, so it is neither retried nor parked on the pause
    /// gate — the run has to die where it stands, which is what makes the stage in the failure record meaningful.
    /// </summary>
    private sealed class AlwaysFailsUploader : IBlobUploader
    {
        private static Exception Boom() => new InvalidOperationException("upload refused by the test double");

        // Only the two required members; the progress-carrying overload has a default implementation that routes here.
        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => throw Boom();

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => throw Boom();
    }

    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory, FailsWhenArmed Cleaner) Build(
        IOperationLog opLog, IBlobUploader? uploader = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);

        // The cleaner alone is wired to the sabotaged factory; everything the backup proper touches keeps the real one.
        var cleanerFactory = new FailsWhenArmed(factory);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"), staging);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(cleanerFactory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked,
            notifier: null, opLog: opLog);
        return (orchestrator, factory, cleanerFactory);
    }

    /// <summary>
    /// MaxVersions = 1 is what gives cleanup something to do on the second run. Without a version to retire it
    /// returns before touching the cloud at all (deliberately — an orphan sweep lists data/ and packs/ in full), so
    /// a sabotaged factory would never be called and the bug would not reproduce.
    /// </summary>
    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "Public",
        Password = null,
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
            Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
        },
    };

    /// <summary>
    /// The version is committed before cleanup runs, so a cleanup that cannot reach the cloud leaves a complete,
    /// restorable backup behind. It must therefore be reported as a success — with the unfinished maintenance said
    /// out loud — instead of a failure that sends the operator hunting for data that is in fact already safe.
    /// </summary>
    [SkippableFact]
    public async Task Cleanup_failure_leaves_the_committed_backup_successful()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("cleanfail");
        var log = new RecordingOperationLog();
        var (orchestrator, factory, cleaner) = Build(log);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            Write("a/one.bin", 1_000_000);
            Write("a/two.bin", 6_000_000);
            await container.CreateIfNotExistsAsync();

            // First run: cleanup has nothing to retire, so it never reaches the cloud and the sabotage stays idle.
            await orchestrator.RunAsync(Request(account, name));

            // Second run: version 1 now falls outside MaxVersions = 1, so cleanup has real work — and the cloud is
            // gone the moment it reaches for it, which is the incident being reproduced.
            Write("a/three.bin", 1_500_000);
            cleaner.Armed = true;
            var result = await orchestrator.RunAsync(Request(account, name));

            // The run reports the version it committed, exactly as a clean run would.
            Assert.Equal(2, result.Version);

            // And the cloud agrees: the info file carries the version, so a restore would find it.
            var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
            var info = await store.ReadInfoAsync(account, name, password: null);
            Assert.NotNull(info);
            Assert.Contains(info!.Versions, v => v.Version == 2);

            // Silence would be worse than the old failure: the operator has to learn that the retention cleanup and
            // compaction did not run, so that a container growing past its retention policy is not a mystery.
            Assert.Contains(log.Entries, e =>
                e.Level == OperationLogLevel.Warning &&
                e.Message.Contains("cleanup", StringComparison.OrdinalIgnoreCase));

            // Nothing may be recorded as an outright failure — that is the regression being locked down.
            Assert.DoesNotContain(log.Entries, e =>
                e.Level == OperationLogLevel.Error &&
                e.Message.Contains("Backup failed", StringComparison.OrdinalIgnoreCase));

            // The summary has to distinguish "nothing needed cleaning up" from "cleanup could not run" — an empty
            // cleanup line reads identically for both, and only one of them wants the operator's attention.
            Assert.Contains("Cleanup: skipped", BackupSummary.Format(result), StringComparison.Ordinal);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// A failure that really is a failure must name the stage it died in. The 3 TB incident was traced to cleanup
    /// only because it always struck at the end — the record itself carried nothing but the Azure message, which
    /// names no stage at all.
    /// </summary>
    [SkippableFact]
    public async Task A_genuine_failure_records_the_stage_it_died_in()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stagefail");
        var log = new RecordingOperationLog();
        var (orchestrator, factory, _) = Build(log, new AlwaysFailsUploader());
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            Write("a/one.bin", 1_000_000);
            await container.CreateIfNotExistsAsync();
            // Every upload is refused, so the run dies in the upload stage and the record has to name it.
            await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.RunAsync(Request(account, name)));

            var failure = Assert.Single(log.Entries, e =>
                e.Level == OperationLogLevel.Error &&
                e.Message.Contains("Backup failed", StringComparison.Ordinal));
            Assert.Contains("Failed during ", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }
}
