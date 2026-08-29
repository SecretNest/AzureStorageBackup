using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Regression: a repair (§3.2) must write the info file / version indexes through the local-authoritative state
/// machine (TrackedInfoStore + ILocalIndexCache), otherwise the locally cached ETag falls out of step with the cloud
/// and the next backup's conditional write hits a 412 once.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupRepairerTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _src;
    private readonly string _temp;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    private readonly BackupJournalStore _journals;

    public BackupRepairerTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-repair-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_src);
        _journals = new BackupJournalStore(Path.Combine(_base, "journal"));

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 1,
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

    /// <summary>An IOperationLog spy that only records what was written (used to assert the audit trail the repairer leaves behind).</summary>
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

    private (BackupOrchestrator Backup, BackupChecker Checker, BackupRepairer Repairer, TrackedInfoStore Tracked, ILocalIndexCache IndexCache, BlobClientFactory Factory) Build(
        IOperationLog? opLog = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var state = new LocalBackupStateStore(_db);
        var tracked = new TrackedInfoStore(store, state);
        var indexCache = new LocalIndexCache(_db, store);
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
            indexCache: indexCache, trackedInfo: tracked);
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"),
            trackedInfo: tracked, journals: _journals);
        var repairer = new BackupRepairer(
            factory, store, new SevenZipCompressor(), new FileHasher(), new BlobUploader(factory),
            Path.Combine(_temp, "repair"), staging,
            opLog: opLog, checker: checker, trackedInfo: tracked, indexCache: indexCache, journals: _journals);
        return (backup, checker, repairer, tracked, indexCache, factory);
    }

    private BackupRequest Req(Account a, string c, IgnoreRuleSet? dontCompress = null, string? password = null) => new()
    {
        Account = a, Container = c, LocalRoot = _src, Name = "photos", Password = password,
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
            DontCompress = dontCompress,
        },
    };

    /// <summary>The plan's selection semantics, as the user defined them: ticked = repair now (re-upload);
    /// unticked = mark damaged and leave it to the next backup version — no probing, no hashing, no upload for
    /// it, just the mark that the heal-on-next-backup path acts on. Deselection is fast by construction.</summary>
    [SkippableFact]
    public async Task Repair_Reuploads_The_Selected_And_Marks_The_Rest_For_The_Next_Version()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, tracked, indexCache, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("reps-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "one.txt"), "content of the first");
            await File.WriteAllTextAsync(Path.Combine(_src, "two.txt"), "content of the second");
            await backup.RunAsync(Req(account, name));
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();

            // A whole-container distractor: many volumes that have nothing to do with the selection. The
            // assessment must not probe them — in the field it probed 194,630 volumes for a 4-file repair and
            // priced a ~32-minute wait before any repairing began.
            await File.WriteAllBytesAsync(Path.Combine(_src, "bystander.bin"), new byte[3_000_000]);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    VolumeBytes = 1_000_000,
                },
            });

            var stages = new HashSet<string>(StringComparer.Ordinal);
            var assessTotals = new List<int>();
            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null, onlyPaths: ["one.txt"], alsoMarkPaths: ["two.txt"],
                onProgress: d =>
                {
                    lock (stages)
                    {
                        stages.Add(d.Stage);
                        if (d.Stage == "Assessing" && d.Total > 0) assessTotals.Add(d.Total);
                    }
                });

            // The pre-check's stages surface under the repair's own name: a user watching "Cloud: N volumes"
            // concluded a check had started instead of their repair (field report). The work is the same; the
            // label must say whose work it is.
            lock (stages)
            {
                Assert.Contains("Assessing", stages);
                Assert.Contains("Repairing", stages);
                Assert.DoesNotContain("Cloud", stages);
                // Scoped assessment: only the selected and to-be-marked families are probed — the bystander's
                // several volumes must not enter the total (in the field, the unscoped version probed the whole
                // container: 194,630 volumes for a 4-file selection).
                Assert.All(assessTotals, t => Assert.True(t <= 2, $"assessment probed {t} volumes — the bystander leaked in"));
            }

            Assert.Equal(["one.txt"], report.Repaired);
            Assert.Equal(["two.txt"], report.Unrecoverable);

            var info = await tracked.LoadAsync(account, name, null);
            var v1 = info!.Versions.Single(x => x.Version == 1);
            var index = await indexCache.ReadAsync(account, name, 1, info.Backup.CreatedAt.UtcTicks, v1.IndexBlob, null);
            Assert.Contains("two.txt", index.UnrecoverablePaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The retirement-interplay kernel (volume-identity.md § retirement needs no coordination): a
    /// resumed repair replays its selection against a fresh pre-check, and a selected path that no longer exists
    /// in any retained version — its only referencing version retired while the repair sat suspended — simply
    /// falls out of the intersection: not repaired, not marked, not an error. Retention decided that content's
    /// fate; repair does not resurrect it.</summary>
    [SkippableFact]
    public async Task A_Selected_Path_Absent_From_The_Retained_Versions_Falls_Out_Silently()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, _, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("repg-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alive");
            await backup.RunAsync(Req(account, name));

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null, onlyPaths: ["retired-away.bin"]);

            Assert.Empty(report.Repaired);
            Assert.Empty(report.Unrecoverable);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The whole self-healing loop, end to end (volume-identity.md § damage is a first-class fact):
    /// a file's blob is damaged and marked (deferred); a NEW file with identical content joins the next backup.
    /// Dedup must not hand it the broken ref — excluded, it re-uploads to the same content address and heals the
    /// family in passing, resurrecting the old version's file. The deferred repair that follows the backup then
    /// finds the blob healthy and lifts the marks. Nothing in this loop was told to "repair" anything.</summary>
    [SkippableFact]
    public async Task A_Same_Content_Twin_Heals_The_Damaged_Blob_And_The_Marks_Come_Off()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, repairer, tracked, indexCache, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("heal-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "twin content, worth healing");
            await backup.RunAsync(Req(account, name));

            // Damage + defer: the blob's volumes vanish, and a repair with an empty selection marks everything
            // for the next backup version ("mark all", the plan's defer-everything choice).
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();
            // The plan's defer-everything choice under the two-list contract: nothing ticked, the problem
            // listed for marking. (Scoping means an empty union assesses nothing — "mark all" is always an
            // explicit list now, which is what the plan UI sends.)
            var deferAll = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null, onlyPaths: Array.Empty<string>(), alsoMarkPaths: ["a.txt"]);
            Assert.Contains("a.txt", deferAll.Unrecoverable);
            Assert.Empty(deferAll.Repaired);

            // The next backup brings a twin: same bytes, different path. Dedup must not chain it to the corpse.
            await File.WriteAllTextAsync(Path.Combine(_src, "b.txt"), "twin content, worth healing");
            await backup.RunAsync(Req(account, name));

            // The healing upload has already happened as a side effect: v1's file is restorable again.
            var deep = await checker.CheckAsync(
                account, name, null, 1, new CheckOptions { Cloud = CloudCheckLevel.Content, Local = LocalCheckLevel.None });
            Assert.True(deep.Ok);

            // The deferred repair (what DeferredRepairs hands off after the backup) finds the blob healthy and
            // lifts the marks — the loop converges instead of marking forever.
            var deferred = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null, onlyPaths: ["a.txt"]);
            Assert.Contains("a.txt", deferred.Repaired);

            var info = await tracked.LoadAsync(account, name, null);
            foreach (var v in info!.Versions)
            {
                var idx = await indexCache.ReadAsync(account, name, v.Version, info.Backup.CreatedAt.UtcTicks, v.IndexBlob, null, v.IndexVolumes);
                Assert.DoesNotContain("a.txt", idx.UnrecoverablePaths);
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Clicking repair used to push "Check started" — the repairer's internal pre-check notified as if
    /// it were a user-initiated check, and the user who had just clicked Repair reasonably wondered what was
    /// running. The pre-check is an implementation detail and stays silent; the repair announces itself.</summary>
    [SkippableFact]
    public async Task Repair_Announces_Itself_And_Its_Internal_Check_Stays_Silent()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var opLog = new RecordingOperationLog();
        var (backup, _, repairer, _, _, factory) = Build(opLog);
        var account = AzuriteAccount();
        var name = RandomName("repn-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            await backup.RunAsync(Req(account, name));

            await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null);

            List<(OperationLogLevel Level, string Source, string Message)> entries;
            lock (opLog.Entries) entries = [.. opLog.Entries];
            Assert.Contains(entries, e => e.Message.StartsWith("Repair started", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, e => e.Message.StartsWith("Check started", StringComparison.Ordinal));
            Assert.DoesNotContain(entries, e => e.Message.StartsWith("Check passed", StringComparison.Ordinal));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The retention cleaner has honoured active journals all along; the repair-side orphan sweep did
    /// not, and it runs off the same "referenced by a retained version" set. With a backup suspended mid-run —
    /// the exact state in which a user repairs damage before resuming — its journalled uploads are in the cloud
    /// but in no version index, and a ticked "clean up orphans" would have deleted them, making the eventual
    /// resume re-upload everything it had already sent.</summary>
    [SkippableFact]
    public async Task Orphan_Cleanup_Spares_A_Suspended_Runs_Journalled_Blobs()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, repairer, _, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("repj-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            await backup.RunAsync(Req(account, name));

            // A suspended run's traces: blobs in the cloud that no version references, held only by the journal —
            // plus one true orphan with no journal to its name.
            await container.GetBlobClient("data/journaled").UploadAsync(BinaryData.FromString("in flight"), overwrite: true);
            await container.GetBlobClient("packs/pjournal.7z").UploadAsync(BinaryData.FromString("in-flight pack"), overwrite: true);
            await container.GetBlobClient("data/trueorphan").UploadAsync(BinaryData.FromString("junk"), overwrite: true);
            await using (var j = await _journals.CreateAsync(account.Id, name, "run-suspended", new JournalHeader
            {
                RunId = "run-suspended", ConfigId = 1, StartedAt = DateTimeOffset.UnixEpoch,
                BaselineVersion = 1, LocalRoot = _src, EncryptionIdentity = "plain",
            }, default))
            {
                await j.AppendAsync(new JournalRecord { Kind = "blob", Ref = "data/journaled", Path = "b.bin", FullHash = "h", Volumes = 1 }, default);
                await j.AppendAsync(new JournalRecord { Kind = "pack", Ref = "pjournal", VolumeSizes = [14] }, default);
            }

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions { ListOrphans = true },
                Azure.Storage.Blobs.Models.AccessTier.Hot, null, dontCompress: null);

            Assert.Contains("data/trueorphan", report.DeletedOrphans);
            Assert.DoesNotContain("data/journaled", report.DeletedOrphans);
            Assert.DoesNotContain("packs/pjournal.7z", report.DeletedOrphans);
            Assert.True((await container.GetBlobClient("data/journaled").ExistsAsync()).Value);
            Assert.True((await container.GetBlobClient("packs/pjournal.7z").ExistsAsync()).Value);

            // The check's own orphan listing must agree: a journalled blob is not reported as reclaimable either.
            var check = await checker.CheckAsync(
                account, name, null, null,
                new CheckOptions { Local = LocalCheckLevel.None, ListOrphans = true }, _src);
            Assert.DoesNotContain("data/journaled", check.OrphanBlobs);
            Assert.DoesNotContain("packs/pjournal.7z", check.OrphanBlobs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The mark is a verdict, and a verdict overturned must come off the record: a repair that heals a
    /// blob whose path an earlier run ruled unrecoverable used to leave the mark in place forever — restore then
    /// kept routing the healed file through version substitution as if it were still lost.</summary>
    [SkippableFact]
    public async Task A_Successful_Repair_Clears_The_Unrecoverable_Mark()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, tracked, indexCache, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("repu-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            await backup.RunAsync(Req(account, name));

            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();

            // First repair with the local content rewritten (not appended): rightly unrecoverable, mark written.
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "OMEGA");
            var first = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null);
            Assert.Contains("a.txt", first.Unrecoverable);

            // The original content comes back (the user restored it from elsewhere): the second repair heals the
            // blob, and the verdict must come off with it.
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            var second = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null);
            Assert.Contains("a.txt", second.Repaired);

            var info = await tracked.LoadAsync(account, name, null);
            var v1 = info!.Versions.Single(x => x.Version == 1);
            var index = await indexCache.ReadAsync(account, name, 1, info.Backup.CreatedAt.UtcTicks, v1.IndexBlob, null);
            Assert.DoesNotContain("a.txt", index.UnrecoverablePaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Repair_Updates_Local_Authoritative_State_So_Next_Write_Does_Not_Conflict()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, tracked, indexCache, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rep2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // v1: one data blob (through the local-authoritative state machine — backfilling the local ETag / index cache).
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "repair me please, local-authoritative");
            await backup.RunAsync(Req(account, name));

            // That blob is gone from the cloud; the local file is still there (repairable from local).
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null);
            Assert.Contains("a.txt", report.Repaired);
            Assert.Empty(report.Unrecoverable);

            // The repair must go through the local-authoritative state machine: version 1 in the index cache should
            // have been refreshed (its identity matching this info file).
            var info = await tracked.LoadAsync(account, name, null);
            Assert.NotNull(info);
            var v1 = info!.Versions.Single(x => x.Version == 1);
            var identity = info.Backup.CreatedAt.UtcTicks;
            var cachedIndex = await indexCache.ReadAsync(account, name, 1, identity, v1.IndexBlob, null);
            Assert.NotNull(cachedIndex);

            // The next backup's finalize info write (a tracked ETag conditional write) must not hit a 412 because the repair bypassed the local cache.
            var ex = await Record.ExceptionAsync(() =>
                tracked.WriteAsync(account, name, info, null, Azure.Storage.Blobs.Models.AccessTier.Hot));
            Assert.Null(ex);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// F7: when a repair recompresses a single-file blob, StoreOnly must be derived exactly as a fresh backup derives
    /// it for the same path (from the configured DontCompress rules), not hardcoded to false. Only then is the
    /// repaired archive the same kind of thing a fresh backup writes.
    /// <para>
    /// Both directions are verified together so that "store everything" cannot slip through: logs/big.log matches the
    /// rules (should be stored → archive roughly the size of the original file), data/big.bin does not (should be
    /// compressed → archive far smaller than the original). Both files hold highly compressible content, so the two
    /// modes differ in archive size by more than an order of magnitude and the assertions do not rest on a hair.
    /// </para>
    /// <para>Before the fix (hardcoded StoreOnly: false): logs/big.log was recompressed into a small -mx9 archive and its size assertion failed.</para>
    /// </summary>
    [SkippableFact]
    public async Task Repair_Derives_StoreOnly_From_The_DontCompress_Rules_Like_A_Fresh_Backup_Does()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        // It has to be an **encrypted** backup: an unencrypted store-only file takes the raw upload path
        // (CopyRawAsync) and never goes through 7z at all, so the StoreOnly parameter has no effect on it. When
        // encrypted, store-only still goes through 7z (-mx0 + password), which is exactly the path under test.
        const string password = "repair-store-only-pw";
        var rules = new IgnoreRuleSet(["*.log"]);
        var (backup, _, repairer, _, _, factory) = Build();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("rep-store-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Highly compressible content: the archive sizes for -mx0 and -mx9 differ by an order of magnitude, so
            // the assertions do not sit on a boundary. The two files must differ in content — content addressing
            // would dedup identical content into one blob, leaving only one path to verify.
            Directory.CreateDirectory(Path.Combine(_src, "logs"));
            Directory.CreateDirectory(Path.Combine(_src, "data"));
            await File.WriteAllTextAsync(Path.Combine(_src, "logs", "big.log"), new string('a', 200_000));
            await File.WriteAllTextAsync(Path.Combine(_src, "data", "big.bin"), new string('b', 200_000));
            await backup.RunAsync(Req(account, name, rules, password));

            var info = await store.ReadInfoAsync(account, name, password);
            var v1 = info!.Versions.Single();
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, password);
            var logRef = idx.Entries.Single(e => e.Path == "logs/big.log").Storage!.Ref;
            var binRef = idx.Entries.Single(e => e.Path == "data/big.bin").Storage!.Ref;
            Assert.NotEqual(logRef, binRef); // different content → two independent blobs, each path running its own derivation

            async Task<long> SizeOf(string blobRef) =>
                (await container.GetBlobClient(blobRef).GetPropertiesAsync()).Value.ContentLength;

            var freshLog = await SizeOf(logRef);
            var freshBin = await SizeOf(binRef);
            // First confirm the fresh backup itself really did split by the rules: the stored one is far larger than the compressed one.
            Assert.True(freshLog > freshBin * 10, $"fresh backup did not honour the rules: log={freshLog} bin={freshBin}");

            await container.GetBlobClient(logRef).DeleteIfExistsAsync();
            await container.GetBlobClient(binRef).DeleteIfExistsAsync();

            var report = await repairer.RepairAsync(
                account, name, password, _src, null, new CheckOptions(), AccessTier.Hot, null, dontCompress: rules);
            Assert.Contains("logs/big.log", report.Repaired);
            Assert.Contains("data/big.bin", report.Repaired);

            // The repaired archive's size matches what a fresh backup wrote (same content + same StoreOnly → the same 7z command).
            var repairedLog = await SizeOf(logRef);
            var repairedBin = await SizeOf(binRef);
            Assert.InRange(repairedLog, (long)(freshLog * 0.9), (long)(freshLog * 1.1));
            Assert.InRange(repairedBin, (long)(freshBin * 0.9), (long)(freshBin * 1.1));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Turn the entry for a given path in a given version index into a "legacy index entry" (no head/tail)
    /// and write it back to the cloud. Returns the name of the data blob that entry references.</summary>
    private static async Task<string> StripHashesAsync(
        BackupInfoStore store, Account account, string container, int version, string indexBlob, string path)
    {
        var idx = await store.ReadIndexAsync(account, container, indexBlob, null);
        var i = idx.Entries.FindIndex(e => e.Path == path);
        Assert.True(i >= 0, $"v{version} index has no entry for {path}");
        var blobRef = idx.Entries[i].Storage!.Ref;
        idx.Entries[i] = idx.Entries[i] with { HeadHash = null, TailHash = null };
        await store.WriteIndexAsync(account, container, version, idx, null);
        return blobRef;
    }

    /// <summary>
    /// A1: refs spans every referencing version, and their order depends on dictionary enumeration order — an
    /// undocumented BCL implementation detail, and precisely the property the production code (see the comment in
    /// BackupRepairer.cs) itself calls out as "unreliable", so a test must not turn around and depend on it.
    /// Hence a [Theory] covering both directions: once stripping v1's head/tail (v2 intact), once stripping v2's
    /// (v1 intact). The dictionary insertion order is the same across both runs, only "which version is intact"
    /// swaps, so whether the actual enumeration order is [v1,v2] or [v2,v1], one of the two directions is bound to
    /// land the "entry missing head/tail" at refs[0] — and if the production code falls back to entry0, that
    /// direction fails for certain, with no guessing about enumeration order.
    /// <para>Before the fix (using refs[0]): in at least one direction refs[0] happened to be the stripped entry, the
    /// metadata written out carried only len, and that direction's head/tail assertions failed.</para>
    /// </summary>
    [SkippableTheory]
    [InlineData(1)] // strip v1 (v2 intact) — covers the direction where the bad entry comes first in enumeration
    [InlineData(2)] // strip v2 (v1 intact) — covers the opposite direction, so the assertions no longer depend on the exact order
    public async Task Repair_Prefers_A_Reference_That_Still_Has_Head_And_Tail_Hashes(int stripVersion)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, _, _, factory) = Build();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("rep-meta-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Both v1 and v2 reference the same data blob (a.txt's content did not change).
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "two versions reference me");
            await backup.RunAsync(Req(account, name));
            await File.WriteAllTextAsync(Path.Combine(_src, "b.txt"), "just to create a second version");
            await backup.RunAsync(Req(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            Assert.NotNull(info);
            Assert.Equal(2, info!.Versions.Count);
            var v1 = info.Versions.Single(v => v.Version == 1);
            var v2 = info.Versions.Single(v => v.Version == 2);
            var stripTarget = stripVersion == 1 ? v1 : v2;
            var goodVersion = stripVersion == 1 ? v2 : v1;

            // The entry in the version left alone (the one not stripped) has both fields — that is the one the repair should take its metadata from.
            var goodIndex = await store.ReadIndexAsync(account, name, goodVersion.IndexBlob, null);
            var goodEntry = goodIndex.Entries.Single(e => e.Path == "a.txt");
            Assert.NotNull(goodEntry.HeadHash);
            Assert.NotNull(goodEntry.TailHash);

            // The same entry in the other version is degraded into a "legacy index entry".
            var blobRef = await StripHashesAsync(store, account, name, stripVersion, stripTarget.IndexBlob, "a.txt");
            Assert.Equal(goodEntry.Storage!.Ref, blobRef);

            await container.GetBlobClient(blobRef).DeleteIfExistsAsync();

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), AccessTier.Hot, null, dontCompress: null);
            Assert.Contains("a.txt", report.Repaired);

            var meta = (await container.GetBlobClient(blobRef).GetPropertiesAsync()).Value.Metadata;
            Assert.Equal(goodEntry.HeadHash, meta["head"]);
            Assert.Equal(goodEntry.TailHash, meta["tail"]);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// A2: when not a single reference can supply head/tail, omitting the metadata is the correct handling (writing
    /// empty strings would be worse), but it means this object's collision protection is weakened (in keyed mode,
    /// gone entirely), and leaving no trace makes the degradation invisible. An auditable log entry is mandatory.
    /// <para>Before the fix: there was no log at all, and this test's Single assertion failed.</para>
    /// </summary>
    [SkippableFact]
    public async Task Repair_Records_A_Warning_When_Collision_Metadata_Must_Be_Omitted()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var opLog = new RecordingOperationLog();
        var (backup, _, repairer, _, _, factory) = Build(opLog);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("rep-degr-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "the only reference is a legacy entry");
            await backup.RunAsync(Req(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions.Single();
            var blobRef = await StripHashesAsync(store, account, name, 1, v1.IndexBlob, "a.txt");
            await container.GetBlobClient(blobRef).DeleteIfExistsAsync();

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), AccessTier.Hot, null, dontCompress: null);
            Assert.Contains("a.txt", report.Repaired);

            // The degradation really did happen: the object written out carries no head/tail.
            var meta = (await container.GetBlobClient(blobRef).GetPropertiesAsync()).Value.Metadata;
            Assert.False(meta.ContainsKey("head"));
            Assert.False(meta.ContainsKey("tail"));

            // And it left exactly one auditable trace (not noisy: one per affected object).
            var degraded = Assert.Single(opLog.Entries, e => e.Message.Contains("Collision guard degraded"));
            Assert.Equal(OperationLogLevel.Warning, degraded.Level);
            Assert.Contains(blobRef, degraded.Message);
            Assert.Contains("head and tail", degraded.Message);
            Assert.Equal($"repair:{account.Id}/{name}", degraded.Source);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// A repair has to find a local file with matching content to use as the repair source, and that read previously
    /// had no protection at all. The outer per-blob loop has no backstop either, so one unreadable local file failed
    /// the **whole repair operation** midway — the already-repaired blobs had long since been uploaded, but their
    /// index changes are all written back only after the loop, so that part of the work was lost along with it.
    /// <para>
    /// The trigger is not rare in the slightest: a repair runs precisely after a check reported problems. The checker
    /// now reports an unreadable local file as Missing and runs the whole way through (fixed in the previous round),
    /// the user goes straight from reading the report to clicking repair — and then the repair falls over on that
    /// very same file.
    /// </para>
    /// <para>This test corrupts the cloud blobs of two files, one of which has an unreadable local copy: the other
    /// must still be repaired as usual, and the unreadable one takes the existing "not obtainable from local → mark
    /// unrecoverable" path rather than making the whole repair throw.</para>
    /// </summary>
    [SkippableFact]
    public async Task An_Unreadable_Local_File_Does_Not_Abort_The_Whole_Repair()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var (backup, _, repairer, _, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rep-unread-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var locked = Path.Combine(_src, "locked.txt");

        try
        {
            await File.WriteAllTextAsync(locked, "readable at backup time, locked before the repair");
            await File.WriteAllTextAsync(Path.Combine(_src, "fine.txt"), "stays readable throughout");
            await backup.RunAsync(Req(account, name)); // a threshold of 1 → each becomes its own data blob

            // Both cloud copies are gone; the repair has to rely on local.
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();

            File.SetUnixFileMode(locked, UnixFileMode.None); // becomes unreadable after the backup and before the repair

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), AccessTier.Hot, null, dontCompress: null);

            // The readable one is repaired as usual — before the fix, the whole run threw on locked.txt and this line was never reached.
            Assert.Contains("fine.txt", report.Repaired);

            var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions.Single().IndexBlob, null);
            var fineRef = idx.Entries.Single(e => e.Path == "fine.txt").Storage!.Ref;
            Assert.True(await container.GetBlobClient(fineRef).ExistsAsync()); // the data really is back in the cloud

            // The unreadable one takes the existing handling: local cannot produce a usable copy → mark it
            // unrecoverable, rather than using it to overwrite the cloud.
            Assert.Contains("locked.txt", report.Unrecoverable);
            Assert.DoesNotContain("locked.txt", report.Repaired);
        }
        finally
        {
            try { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// Repairing a pack **rewrites in place** the archive of the same packId, so the compression mode has to be
    /// fetched back out of <see cref="PackInfo.StoreOnly"/>.
    /// <para>
    /// Deliberately different from the single-file path: there the don't-compress rules are re-run per path (the
    /// <c>dontCompress</c> parameter), whereas a pack's compression mode was fixed at packing time and recorded on
    /// the pack — here the rules are not even passed in (<c>dontCompress: null</c>) and the repaired pack must still
    /// come out store-only.
    /// </para>
    /// <para>Before the fix (hardcoded <c>StoreOnly: false</c>): the repaired pack was recompressed into a small -mx9 archive and the size assertion failed.</para>
    /// </summary>
    [SkippableFact]
    public async Task Repair_Keeps_A_Store_Only_Pack_Store_Only()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        // Highly compressible: store-only is ≈ 400,000 bytes, -mx9 leaves one or two KB, so the size assertions do not sit on a boundary.
        const int filler = 200_000;
        var (backup, _, repairer, _, _, factory) = Build();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("rep-pack-store-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // The two members differ in content: identical content would be deduplicated into a single member, leaving this pack with only one.
            Directory.CreateDirectory(Path.Combine(_src, "logs"));
            await File.WriteAllTextAsync(Path.Combine(_src, "logs", "one.log"), new string('a', filler));
            await File.WriteAllTextAsync(Path.Combine(_src, "logs", "two.log"), new string('b', filler));

            await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "photos",
                Options = new BackupEngineOptions
                {
                    // Raise the threshold so these two files go through grouped packing rather than single-file blobs.
                    Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                    DontCompress = new IgnoreRuleSet(["*.log"]),
                },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            var pack = Assert.Single(info!.Packs);
            Assert.True(pack.Value.StoreOnly, "the fresh pack should have been recorded as store-only");

            async Task<long> SizeOfPackAsync() =>
                (await container.GetBlobClient(pack.Value.Blob).GetPropertiesAsync()).Value.ContentLength;

            var fresh = await SizeOfPackAsync();
            Assert.True(fresh > filler * 1.8, $"the fresh pack should be uncompressed, was {fresh}");

            // Wipe the whole pack = cloud-side corruption. Both members are still present locally, so the repair should rebuild it from local.
            await container.GetBlobClient(pack.Value.Blob).DeleteIfExistsAsync();

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), AccessTier.Hot, null, dontCompress: null);
            Assert.Contains("logs/one.log", report.Repaired);

            var repaired = await SizeOfPackAsync();
            Assert.True(repaired > filler * 1.8,
                $"the repaired pack must still be store-only, was {repaired} (compressed would be about 1 KB)");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
