using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Regression: a repair (§3.2) must write the info file through the local-authoritative state machine
/// (TrackedInfoStore), otherwise the locally cached ETag falls out of step with the cloud and the next backup's
/// conditional write hits a 412 once — and it must record its verdicts in the container catalog the next backup
/// reads its dedup facts out of, so that a damaged address is excluded and healed in passing.
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

    /// <param name="repairIndexVolumeBytes">Lowers the split threshold of the store the REPAIR writes through, so
    /// a rewritten index comes back as several volumes where the backup wrote one — the only way to exercise "the
    /// volume count the rewrite actually took is what gets recorded" without a hundreds-of-MB index.</param>
    private (BackupOrchestrator Backup, BackupChecker Checker, BackupRepairer Repairer, TrackedInfoStore Tracked, VersionCatalogs Catalogs, BlobClientFactory Factory) Build(
        IOperationLog? opLog = null, IFileHasher? repairHasher = null, IBlobUploader? repairUploader = null,
        int? repairIndexVolumeBytes = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var state = new LocalBackupStateStore(_db);
        var tracked = new TrackedInfoStore(store, state);
        // ONE catalog store for the backup and the repair alike, as production has it: the marks a repair records
        // are what the next backup's dedup reads to exclude a damaged address and heal it in passing, and two
        // catalogs over the same container would leave each blind to the other's half of the story.
        var catalogs = TestCatalogs.New(_db, store);
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
            catalogs: catalogs, trackedInfo: tracked,
            workFactory: TestWorkDbs.New());
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"),
            trackedInfo: tracked, journals: _journals);
        var repairStore = repairIndexVolumeBytes is { } volumeBytes
            ? new BackupInfoStore(factory, new SevenZipArchiveCodec()) { IndexVolumeBytes = volumeBytes }
            : store;
        var repairer = new BackupRepairer(
            factory, repairStore, new SevenZipCompressor(), repairHasher ?? new FileHasher(), repairUploader ?? new BlobUploader(factory),
            Path.Combine(_temp, "repair"), staging, catalogs,
            opLog: opLog, checker: checker, trackedInfo: tracked, journals: _journals);
        return (backup, checker, repairer, tracked, catalogs, factory);
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

        var (backup, _, repairer, tracked, catalogs, factory) = Build();
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
            Assert.NotNull(info);
            // In the catalog too, not only in the cloud: the catalog is what the next backup's dedup reads.
            await using var catalog = await catalogs.OpenAsync(account.Id, name, readOnly: true);
            Assert.Contains("two.txt", await catalog.UnrecoverableAsync(1, CancellationToken.None));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Two truthfulness defects reported from the field, one fixture: (1) the pre-check's Local
    /// bookkeeping pass (pinned to None, instant) published its entry count under the Assessing token, whose
    /// UI unit is volumes — the operator saw "4 of 4 volumes" flash at the end of an assessment that probes
    /// per volume; (2) the Repairing stage never told the tracker an object was in hand, so the screen read
    /// "4 objects queued" while one of the four was visibly being hashed. Both are progress-shape contracts:
    /// every Assessing total must be the probe workload (the family's recorded volume count), and a
    /// single-object repair must never report its only object as queued.</summary>
    [SkippableFact]
    public async Task Progress_Reports_Probe_Totals_And_In_Hand_Objects_Truthfully()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, _, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("repp-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var bytes = new byte[2_500_000];
            new Random(7).NextBytes(bytes); // incompressible, so the volume split survives compression
            await File.WriteAllBytesAsync(Path.Combine(_src, "big.bin"), bytes);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    VolumeBytes = 1_000_000,
                },
            });

            var volumes = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                volumes.Add(b.Name);
            Assert.True(volumes.Count >= 3, $"fixture needs a multi-volume family, got {volumes.Count}");
            await container.GetBlobClient(volumes.Order(StringComparer.Ordinal).ElementAt(1)).DeleteIfExistsAsync();

            var assessTotals = new List<int>();
            var repairingQueued = new List<int>();
            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot,
                1_000_000, dontCompress: null, onlyPaths: ["big.bin"], alsoMarkPaths: null,
                onProgress: d =>
                {
                    lock (assessTotals)
                    {
                        if (d.Stage == "Assessing" && d.Total > 0) assessTotals.Add(d.Total);
                        if (d.Stage == "Repairing") repairingQueued.Add(d.Queued);
                    }
                });

            Assert.Equal(["big.bin"], report.Repaired);
            lock (assessTotals)
            {
                Assert.NotEmpty(assessTotals);
                Assert.All(assessTotals, t => Assert.Equal(volumes.Count, t));
                Assert.All(repairingQueued, q => Assert.Equal(0, q));
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>A big store-only file's repair used to read the source twice: once for the hash gate, once
    /// to produce the volumes. The user's call on seeing it live: "这种大文件也都是不用压缩的,读两遍而已,不如合并" —
    /// so the single-file blob route now verifies **during** the volume production (the same
    /// hash-rides-the-compression-read trick the backup path uses), and the separate full read is gone.
    /// The hasher counts the proof: repairing this blob must not call FullHashAsync on its source at all.</summary>
    [SkippableFact]
    public async Task Blob_Repair_Verifies_During_Volume_Production_Without_A_Separate_Read()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var counting = new CountingHasher(new FileHasher());
        var (backup, _, repairer, _, _, factory) = Build(repairHasher: counting);
        var account = AzuriteAccount();
        var name = RandomName("repm-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var content = new byte[2_500_000];
            new Random(11).NextBytes(content);
            await File.WriteAllBytesAsync(Path.Combine(_src, "big.bin"), content);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    VolumeBytes = 1_000_000,
                },
            });

            var volumes = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                volumes.Add(b.Name);
            var damaged = volumes.Order(StringComparer.Ordinal).ElementAt(1);
            await container.GetBlobClient(damaged).DeleteIfExistsAsync();

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot,
                1_000_000, dontCompress: null, onlyPaths: ["big.bin"]);

            Assert.Equal(["big.bin"], report.Repaired);
            // The verdict came from the production read itself — no separate hash pass over the source.
            Assert.Equal(0, counting.FullCalls("big.bin"));
            // And the family is whole again: the volume that was deleted exists once more.
            Assert.True((await container.GetBlobClient(damaged).ExistsAsync()).Value,
                $"the repaired family is missing {damaged}");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The merged verification must keep the hash gate's guarantee: a local file that changed since
    /// the backup — same length, different bytes, the one change stat cannot see — must never be uploaded
    /// under the recorded content's address. The verdict now falls out of the production read; a mismatch
    /// discards the produced volumes, marks the path, and leaves the damaged family exactly as found.</summary>
    [SkippableFact]
    public async Task A_Locally_Changed_Same_Length_Source_Never_Overwrites_The_Cloud()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, _, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("repn-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var content = new byte[2_500_000];
            new Random(13).NextBytes(content);
            var local = Path.Combine(_src, "big.bin");
            await File.WriteAllBytesAsync(local, content);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    VolumeBytes = 1_000_000,
                },
            });

            var volumes = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                volumes.Add(b.Name);
            var damaged = volumes.Order(StringComparer.Ordinal).ElementAt(1);
            await container.GetBlobClient(damaged).DeleteIfExistsAsync();

            content[1_234_567] ^= 0xFF; // bit rot / an in-place edit: same length, different content
            await File.WriteAllBytesAsync(local, content);

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot,
                1_000_000, dontCompress: null, onlyPaths: ["big.bin"]);

            Assert.Empty(report.Repaired);
            Assert.Equal(["big.bin"], report.Unrecoverable);
            // Nothing was written: the damaged family is exactly as the repair found it.
            Assert.False((await container.GetBlobClient(damaged).ExistsAsync()).Value,
                "a mismatched source must not resurrect the family");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The field incident of 2026-08-29 (v9, Archive-tier backup): the repair produced its volumes,
    /// then died on the first upload with 409 BlobArchived — Put Blob is documented to fail when overwriting
    /// an archived blob ("Overwriting an archive blob fails", Put Blob § Remarks), while Delete Blob is
    /// permitted on one. So an overwrite whose target sits in the archive tier must delete first and upload
    /// fresh; every other tier keeps the plain overwrite (smaller crash window). The family here is condemned
    /// and its content is reproduced locally, so delete-then-write risks nothing the damage has not already
    /// taken.</summary>
    [SkippableFact]
    public async Task Repair_Replaces_Volumes_Whose_Old_Copies_Sit_In_The_Archive_Tier()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, _, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("repa-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var content = new byte[2_500_000];
            new Random(17).NextBytes(content);
            await File.WriteAllBytesAsync(Path.Combine(_src, "big.bin"), content);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    VolumeBytes = 1_000_000,
                },
            });

            var volumes = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                volumes.Add(b.Name);
            var ordered = volumes.Order(StringComparer.Ordinal).ToList();
            await container.GetBlobClient(ordered[1]).DeleteIfExistsAsync();
            // The surviving volumes go to the archive tier — the exact state of a damaged Archive-tier backup.
            foreach (var v in ordered.Where(v => v != ordered[1]))
                await container.GetBlobClient(v).SetAccessTierAsync(Azure.Storage.Blobs.Models.AccessTier.Archive);

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot,
                1_000_000, dontCompress: null, onlyPaths: ["big.bin"]);

            Assert.Equal(["big.bin"], report.Repaired);
            foreach (var v in ordered)
                Assert.True((await container.GetBlobClient(v).ExistsAsync()).Value, $"family incomplete: {v} missing");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The design's "marks land first" (volume-identity.md), never implemented until now: at repair
    /// start, every problem path is marked unrecoverable and the marks are PERSISTED before the first object is
    /// touched. This is what lets a backup running beside a suspended repair see the truth — dedup exclusion
    /// and restore substitution read the marks, and a suspend that persisted nothing left them blind. The pause
    /// gate stands in for the suspension: it fires before the first object and kills the run.</summary>
    [SkippableFact]
    public async Task Marks_Land_And_Persist_Before_The_First_Object_Is_Repaired()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, tracked, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("repk-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "one.txt"), "first content here");
            await File.WriteAllTextAsync(Path.Combine(_src, "two.txt"), "second content here");
            await backup.RunAsync(Req(account, name));
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot,
                null, dontCompress: null, onlyPaths: ["one.txt", "two.txt"],
                pauseGate: _ => throw new OperationCanceledException()));

            var info = await tracked.LoadAsync(account, name, null);
            var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
            var v1 = info!.Versions.Single();
            var index = await store.ReadIndexAsync(account, name, v1.IndexBlob, null, v1.IndexVolumes);
            Assert.Contains("one.txt", index.UnrecoverablePaths);
            Assert.Contains("two.txt", index.UnrecoverablePaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>A pack whose every member is unrecoverable used to be removed from info.Packs while the index
    /// entries still referenced it — and from then on every reference-set build for the container threw, which
    /// silently and permanently disabled orphan reclamation AND masked the corruption (an existence check saw
    /// the still-present pack blob and reported Ok). The pack entry stays; the marks tell the story; the
    /// orphan scan keeps working.</summary>
    [SkippableFact]
    public async Task A_Pack_With_No_Recoverable_Members_Leaves_The_Reference_Set_Buildable()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, repairer, _, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("repq-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "m1.txt"), "member one");
            await File.WriteAllTextAsync(Path.Combine(_src, "m2.txt"), "member two");
            // No SingleFileThresholdBytes override: small files group into a pack.
            await backup.RunAsync(Req(account, name) with { Options = new BackupEngineOptions() });

            // Damage the pack and remove the local sources: nothing is recoverable.
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "packs/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();
            File.Delete(Path.Combine(_src, "m1.txt"));
            File.Delete(Path.Combine(_src, "m2.txt"));

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot,
                null, dontCompress: null, onlyPaths: ["m1.txt", "m2.txt"]);
            Assert.Equal(2, report.Unrecoverable.Distinct().Count());

            // The container's safety net must survive: plant an orphan and let a full check's scan judge it.
            await container.GetBlobClient("data/orphan").UploadAsync(new BinaryData("stray"), overwrite: true);
            var check = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { ListOrphans = true }, _src, null, CancellationToken.None);
            Assert.Null(check.OrphanScanIssue);
            Assert.Contains("data/orphan", check.OrphanBlobs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>A deferred (unticked) path whose blob the repair's own pre-check proves healthy must shed its
    /// mark — the pre-check genuinely re-examined it (the scope is onlyPaths ∪ deferPaths), and discarding the
    /// Ok verdict left the path marked forever: restore kept substituting, dedup kept excluding, and every
    /// later cycle reproduced the same dead end deterministically.</summary>
    [SkippableFact]
    public async Task A_Deferred_Path_Proven_Healthy_Sheds_Its_Mark()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, tracked, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("repd-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "b.txt"), "content of b");
            await backup.RunAsync(Req(account, name));
            // Save the blob, damage it, defer-mark it, then put the blob back: the mark now outlives the damage.
            string blobName = "";
            BinaryData? saved = null;
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
            {
                blobName = b.Name;
                saved = (await container.GetBlobClient(b.Name).DownloadContentAsync()).Value.Content;
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();
            }
            await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot,
                null, dontCompress: null, onlyPaths: [], alsoMarkPaths: ["b.txt"]);
            await container.GetBlobClient(blobName).UploadAsync(saved!, overwrite: true);

            // The user defers it again; the pre-check finds it healthy; the verdict must overturn the mark.
            await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot,
                null, dontCompress: null, onlyPaths: [], alsoMarkPaths: ["b.txt"]);

            var info = await tracked.LoadAsync(account, name, null);
            var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
            var v1 = info!.Versions.Single();
            var index = await store.ReadIndexAsync(account, name, v1.IndexBlob, null, v1.IndexVolumes);
            Assert.DoesNotContain("b.txt", index.UnrecoverablePaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>One object's upload failure must not discard the index updates of objects already repaired in
    /// the same run: their replacement volumes are long since in the cloud, and losing the bookkeeping meant a
    /// 10-hour run could end with nothing recorded. The loop now backstops per object — the failed one keeps
    /// its start-of-run mark, the successes persist, and the run still surfaces the failure.</summary>
    [SkippableFact]
    public async Task A_Mid_Run_Failure_Keeps_The_Objects_Already_Repaired()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var failing = new SecondFamilyFailsUploader(null!);
        var (backup, _, repairer, tracked, _, factory) = Build(repairUploader: failing);
        failing.Inner = new BlobUploader(factory);
        var account = AzuriteAccount();
        var name = RandomName("repf-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "one.txt"), "first content here");
            await File.WriteAllTextAsync(Path.Combine(_src, "two.txt"), "second content here");
            await backup.RunAsync(Req(account, name));
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();

            await Assert.ThrowsAnyAsync<Exception>(() => repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot,
                null, dontCompress: null, onlyPaths: ["one.txt", "two.txt"]));

            var info = await tracked.LoadAsync(account, name, null);
            var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
            var v1 = info!.Versions.Single();
            var index = await store.ReadIndexAsync(account, name, v1.IndexBlob, null, v1.IndexVolumes);
            // Exactly one object failed; the other's success must be on the record.
            Assert.Single(index.UnrecoverablePaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Passes everything through, but the SECOND distinct volume family it is asked to upload fails
    /// permanently — the shape of a network fault arriving mid-run.</summary>
    private sealed class SecondFamilyFailsUploader(IBlobUploader inner) : IBlobUploader
    {
        public IBlobUploader Inner = inner;
        private string? _first;

        private bool Fails(string blobName)
        {
            var family = blobName.Split('.')[0];
            var first = Interlocked.CompareExchange(ref _first, family, null) ?? family;
            return family != first;
        }

        public Task<bool> UploadIfMissingAsync(Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null) =>
            Fails(blobName) ? throw new IOException("injected fault") : Inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);

        public Task<bool> UploadIfMissingAsync(Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry, CancellationToken ct,
            IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress) =>
            Fails(blobName) ? throw new IOException("injected fault") : Inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);

        public Task UploadOverwriteAsync(Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null) =>
            Fails(blobName) ? throw new IOException("injected fault") : Inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);

        public Task UploadOverwriteAsync(Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry, CancellationToken ct,
            IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress) =>
            Fails(blobName) ? throw new IOException("injected fault") : Inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);

        public Task DeleteIfExistsAsync(Account account, string container, string blobName, CancellationToken ct = default) =>
            Inner.DeleteIfExistsAsync(account, container, blobName, ct);
    }

    /// <summary>The "118% of original" field report: repair's per-volume completions were booked straight
    /// into transferredBytes, so the object still in flight inflated "uploaded" past the per-object workDone
    /// it is displayed against. The backup's ledger discipline applies now: an unfinished family's landed
    /// volumes ride UnfinishedItemBytes ("+X on the cloud"), and transferred moves only at the object's own
    /// write-off — so while the FIRST object is mid-upload, transferred stays zero.</summary>
    [SkippableFact]
    public async Task Uploaded_Bytes_Wait_For_The_Object_To_Finish()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, _, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("repl-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var content = new byte[30_000_000]; // 30 volumes: the upload phase spans many publishes
            new Random(29).NextBytes(content);
            await File.WriteAllBytesAsync(Path.Combine(_src, "big.bin"), content);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    VolumeBytes = 1_000_000,
                },
            });
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();

            var badSnapshots = 0;
            var sawUnfinished = false;
            long finalTransferred = -1;
            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot,
                1_000_000, dontCompress: null, onlyPaths: ["big.bin"],
                onProgress: d =>
                {
                    if (d.Stage != "Repairing") return;
                    // The single object has not been written off: nothing may claim to be "uploaded" yet.
                    if (d.Processed == 0 && d.TransferredBytes > 0) Interlocked.Increment(ref badSnapshots);
                    if (d.UnfinishedItemBytes > 0) sawUnfinished = true;
                    Interlocked.Exchange(ref finalTransferred, d.TransferredBytes);
                });

            Assert.Equal(["big.bin"], report.Repaired);
            Assert.Equal(0, badSnapshots);
            Assert.True(sawUnfinished, "landed volumes of the in-flight object should ride the unfinished ledger");
            Assert.True(finalTransferred > 0, "the write-off must fold the family into uploaded");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Counts full-hash reads per path while behaving exactly like the real hasher.</summary>
    private sealed class CountingHasher(IFileHasher inner) : IFileHasher
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _full = new(StringComparer.Ordinal);

        public int FullCalls(string pathSuffix) =>
            _full.Where(kv => kv.Key.EndsWith(pathSuffix, StringComparison.Ordinal)).Sum(kv => kv.Value);

        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            inner.HeadHashAsync(path, headBytes, ct);

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            inner.TailHashAsync(path, tailBytes, ct);

        public Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null)
        {
            _full.AddOrUpdate(path, 1, (_, n) => n + 1);
            return inner.FullHashAsync(path, ct, onRead);
        }

        public Task<ContentIdentity> ContentIdentityAsync(
            string path, int segmentBytes, CancellationToken ct = default, IProgress<long>? onRead = null)
        {
            _full.AddOrUpdate(path, 1, (_, n) => n + 1);
            return inner.ContentIdentityAsync(path, segmentBytes, ct, onRead);
        }
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

        var (backup, checker, repairer, tracked, catalogs, factory) = Build();
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
            await using var catalog = await catalogs.OpenAsync(account.Id, name, readOnly: true);
            foreach (var v in info!.Versions)
                Assert.DoesNotContain("a.txt", await catalog.UnrecoverableAsync(v.Version, CancellationToken.None));
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

    /// <summary>Repair deletes nothing container-wide any more ("repair只清自己repair的那几个文件"): even with
    /// cleanup requested, a true orphan AND a suspended run's journalled uploads all survive a repair — full
    /// garbage collection belongs to the post-backup cleanup, which honours journals and runs only once no
    /// check report is pending. The check's own orphan LISTING still honours journals (report-only).</summary>
    [SkippableFact]
    public async Task Repair_Deletes_Nothing_Container_Wide_Even_With_Cleanup_Requested()
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

            Assert.Empty(report.DeletedOrphans);
            Assert.True((await container.GetBlobClient("data/trueorphan").ExistsAsync()).Value);
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

        var (backup, _, repairer, tracked, catalogs, factory) = Build();
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
            Assert.NotNull(info);
            await using var catalog = await catalogs.OpenAsync(account.Id, name, readOnly: true);
            Assert.DoesNotContain("a.txt", await catalog.UnrecoverableAsync(1, CancellationToken.None));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Repair_Updates_Local_Authoritative_State_So_Next_Write_Does_Not_Conflict()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, tracked, catalogs, factory) = Build();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
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

            // The repair must go through the local-authoritative state machine: version 1 is in the catalog under
            // this info file's identity, carrying the repaired entry's new volume sizes.
            var info = await tracked.LoadAsync(account, name, null);
            Assert.NotNull(info);
            var identity = info!.Backup.CreatedAt.UtcTicks;
            await using var catalog = await catalogs.OpenAsync(account.Id, name, readOnly: true);
            var row = await catalog.GetVersionAsync(1, CancellationToken.None);
            Assert.NotNull(row);
            Assert.Equal(identity, row!.Identity);
            var patched = await catalog.GetEntryAsync(1, "a.txt", CancellationToken.None);
            Assert.NotNull(patched);
            Assert.Equal(
                (await store.ReadIndexAsync(account, name, info.Versions[0].IndexBlob, null)).Entries.Single(e => e.Path == "a.txt").Storage!.VolumeSizes,
                patched!.Storage!.VolumeSizes);

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
    /// A1: refs spans every referencing version, and the repair must take its collision metadata from whichever
    /// entry still HAS head/tail, not from whichever one happens to come first. Hence a [Theory] covering both
    /// directions: once stripping v1's head/tail (v2 intact), once stripping v2's (v1 intact). One of the two is
    /// bound to land the "entry missing head/tail" first in refs — and if the production code falls back to
    /// entry0, that direction fails for certain. Written this way when the order came out of dictionary
    /// enumeration and could not be reasoned about; it is now the catalog's (oldest version first), and the theory
    /// is kept because covering both directions is what makes the assertion independent of that order at all.
    /// <para>Before the fix (using refs[0]): in at least one direction refs[0] was the stripped entry, the
    /// metadata written out carried only len, and that direction's head/tail assertions failed.</para>
    /// </summary>
    [SkippableTheory]
    [InlineData(1)] // strip v1 (v2 intact) — covers the direction where the bad entry comes first in enumeration
    [InlineData(2)] // strip v2 (v1 intact) — covers the opposite direction, so the assertions no longer depend on the exact order
    public async Task Repair_Prefers_A_Reference_That_Still_Has_Head_And_Tail_Hashes(int stripVersion)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, _, catalogs, factory) = Build();
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

            // The same entry in the other version is degraded into a "legacy index entry". Written straight to the
            // cloud behind the catalog's back, so the catalog's copy of that version has to be dropped for the
            // doctored index to be the one the repair reads (a version is re-imported on demand, from the cloud).
            var blobRef = await StripHashesAsync(store, account, name, stripVersion, stripTarget.IndexBlob, "a.txt");
            await catalogs.RemoveVersionAsync(account.Id, name, stripVersion);
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
        var (backup, _, repairer, _, catalogs, factory) = Build(opLog);
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
            // Straight to the cloud behind the catalog's back, so the catalog's copy is dropped and re-imported
            // from the doctored index (see the sibling test above).
            var blobRef = await StripHashesAsync(store, account, name, 1, v1.IndexBlob, "a.txt");
            await catalogs.RemoveVersionAsync(account.Id, name, 1);
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

    /// <summary>
    /// The pre-mark pass says WHICH CONTENT is broken (volume-identity.md) — it must scope by the damaged
    /// object, not by path. A path exists in many versions, each referencing its own object: when only the
    /// newest version's object is damaged, an older version's same-named entry references a different,
    /// intact object — and a path-wide pre-mark voids it anyway. If the repair then fails (the local file
    /// has drifted), that false mark is never cleared: restores of the intact old version silently soft-skip
    /// the file, and /file-versions plus the substitution guard both refuse the one healthy copy that could
    /// recover it. Caught live by the damage-repair chaos storm (ChaosMatrixTests).
    /// </summary>
    [SkippableFact]
    public async Task A_Failed_Repair_Never_Marks_A_Bystander_Versions_Healthy_Copy()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, _, _, factory) = Build();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("bystander-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            // v1: doc.txt with content A → object R_A.
            await File.WriteAllTextAsync(Path.Combine(_src, "doc.txt"), "version one body");
            await backup.RunAsync(Req(account, name));
            var afterV1 = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", CancellationToken.None))
                afterV1.Add(b.Name);

            // v2: same path, content B → object R_B. Only R_B gets damaged.
            await File.WriteAllTextAsync(Path.Combine(_src, "doc.txt"), "version two body, different");
            await backup.RunAsync(Req(account, name));
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", CancellationToken.None))
                if (!afterV1.Contains(b.Name))
                    await container.GetBlobClient(b.Name).DeleteIfExistsAsync();

            // The local file drifts to content C: the repair of R_B has no source and must fail —
            // which is exactly when a start-of-run mark, if over-broad, is never cleared again.
            await File.WriteAllTextAsync(Path.Combine(_src, "doc.txt"), "version three, drifted well away");

            var report = await repairer.RepairAsync(
                account, name, null, _src, 2, new CheckOptions(), AccessTier.Hot, null,
                dontCompress: null, onlyPaths: ["doc.txt"]);
            Assert.Equal(["doc.txt"], report.Unrecoverable);

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions.Single(x => x.Version == 1);
            var v2 = info.Versions.Single(x => x.Version == 2);
            var idx1 = await store.ReadIndexAsync(account, name, v1.IndexBlob, null, v1.IndexVolumes);
            var idx2 = await store.ReadIndexAsync(account, name, v2.IndexBlob, null, v2.IndexVolumes);

            // v2's copy really is broken and unhealed: the mark is the truth and stays.
            Assert.Contains("doc.txt", idx2.UnrecoverablePaths);
            // v1's copy references R_A, which nobody touched: no verdict may land on it — this healthy
            // copy is precisely what version substitution needs to recover the file.
            Assert.Empty(idx1.UnrecoverablePaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The rewrite contract of a repair, now that a version is serialized out of the catalog rather than out of a
    /// whole index held in memory: a version the repair did not change must not be rewritten **at all** — its index
    /// blob is the very same bytes afterwards, not the same content re-encoded — and a version it did change must
    /// come back out of the catalog as it went in, entry for entry in the same order, with nothing but the mark
    /// added.
    /// <para>
    /// Both halves guard the same class of failure: serializing from a row store makes it easy to lose the source
    /// order (the <c>seq</c> column is the only thing preserving it) or to persist a version merely because a patch
    /// was aimed at it, and either one silently rewrites history a restore reads back.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Repair_rewrites_only_versions_it_changed_and_keeps_entry_order()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, _, _, factory) = Build();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("rep-order-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            // Three entries, so there is an order to lose — and not written in sorted order either.
            await File.WriteAllTextAsync(Path.Combine(_src, "gamma.txt"), "gamma, the same in both versions");
            await File.WriteAllTextAsync(Path.Combine(_src, "alpha.txt"), "alpha, the same in both versions");
            await File.WriteAllTextAsync(Path.Combine(_src, "beta.txt"), "beta, as version one wrote it");
            await backup.RunAsync(Req(account, name));
            var afterV1 = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", CancellationToken.None))
                afterV1.Add(b.Name);

            // Only beta.txt changes, so the object v2 is about to lose is referenced by v2 alone — v1 is a pure
            // bystander, and the repair has no business rewriting its index.
            await File.WriteAllTextAsync(Path.Combine(_src, "beta.txt"), "beta, rewritten for version two");
            await backup.RunAsync(Req(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions.Single(x => x.Version == 1);
            var v2 = info.Versions.Single(x => x.Version == 2);
            Assert.Equal(1, v1.IndexVolumes); // the byte comparison below reads the single-blob layout
            var beforeV1Bytes = (await container.GetBlobClient(v1.IndexBlob).DownloadContentAsync()).Value.Content.ToArray();
            var beforeV2 = await store.ReadIndexAsync(account, name, v2.IndexBlob, null, v2.IndexVolumes);

            // v2's copy of beta.txt is gone from the cloud, and gone locally too: the repair can only mark it.
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", CancellationToken.None))
                if (!afterV1.Contains(b.Name))
                    await container.GetBlobClient(b.Name).DeleteIfExistsAsync();
            File.Delete(Path.Combine(_src, "beta.txt"));

            var report = await repairer.RepairAsync(
                account, name, null, _src, 2, new CheckOptions(), AccessTier.Hot, null,
                dontCompress: null, onlyPaths: ["beta.txt"]);
            Assert.Equal(["beta.txt"], report.Unrecoverable);

            // The bystander's index blob is untouched — byte for byte, which says more than "reads back the same":
            // a rewrite re-encodes it and changes these bytes even when the content is identical.
            var afterV1Bytes = (await container.GetBlobClient(v1.IndexBlob).DownloadContentAsync()).Value.Content.ToArray();
            Assert.Equal(beforeV1Bytes, afterV1Bytes);

            // The version that did change is itself plus exactly one mark: same entries, same order, same fields.
            var afterV2 = await store.ReadIndexAsync(account, name, v2.IndexBlob, null, v2.IndexVolumes);
            Assert.Contains("beta.txt", afterV2.UnrecoverablePaths);
            beforeV2.UnrecoverablePaths.Add("beta.txt");
            Assert.Equal(IndexSerializer.SerializeIndex(beforeV2), IndexSerializer.SerializeIndex(afterV2));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// A rewritten index is not the same length as the one it replaces — a mark makes it longer, a repaired entry's
    /// new volume sizes longer still — so it can cross the split threshold and land as several volumes where the
    /// info file still records one. Every reader takes <c>versions[].indexVolumes</c> as authoritative (restore, the
    /// checker, the lazy catalog migration, retention's volume deletion), so a stale 1 has them read the first
    /// volume and call it the whole index. The count the write returns is what the info file must carry.
    /// </summary>
    [SkippableFact]
    public async Task Repair_records_the_volume_count_of_the_index_it_rewrote()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        // A threshold only the repair's store is held to: the backup writes its index as one blob, exactly as a
        // real one would, and only the rewrite splits.
        var (backup, _, repairer, tracked, _, factory) = Build(repairIndexVolumeBytes: 1024);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("rep-vol-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            // Enough entries (each with its own random content hashes) that the encoded index is comfortably past
            // 1 KiB even after 7z has had a go at it.
            const int files = 40;
            for (var i = 0; i < files; i++)
                await File.WriteAllTextAsync(Path.Combine(_src, $"file{i:D3}.txt"), $"content number {i} of {files}, unique per file");
            await backup.RunAsync(Req(account, name));

            var before = await store.ReadInfoAsync(account, name, null);
            Assert.Equal(1, before!.Versions.Single().IndexVolumes); // the backup wrote one blob

            // One file's blob is gone from the cloud and gone locally: the repair can only mark it, and rewrites
            // the index to say so.
            var idx = await store.ReadIndexAsync(account, name, before.Versions[0].IndexBlob, null);
            var victim = idx.Entries.Single(e => e.Path == "file007.txt");
            await container.GetBlobClient(victim.Storage!.Ref).DeleteIfExistsAsync();
            File.Delete(Path.Combine(_src, "file007.txt"));

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), AccessTier.Hot, null,
                dontCompress: null, onlyPaths: ["file007.txt"]);
            Assert.Equal(["file007.txt"], report.Unrecoverable);

            var after = await tracked.LoadAsync(account, name, null);
            var v1 = after!.Versions.Single();
            Assert.True(v1.IndexVolumes > 1, $"expected the rewritten index to split, got {v1.IndexVolumes} volume(s)");
            foreach (var volume in VolumeBlobIO.VolumeNames(v1.IndexBlob, v1.IndexVolumes))
                Assert.True((await container.GetBlobClient(volume).ExistsAsync()).Value, $"{volume} is missing");

            // And the recorded count is the one that reads the whole index back — every entry, plus the mark that
            // caused the rewrite in the first place.
            var dest = Path.Combine(_base, "readback.idx");
            await store.ReadIndexToFileAsync(account, name, v1.IndexBlob, null, v1.IndexVolumes, dest);
            await using var readback = File.OpenRead(dest);
            using var reader = new IndexStreamReader(readback);
            Assert.Equal(1, reader.Version);
            Assert.Equal(files, reader.EntryCount);
            Assert.Equal(files, reader.Entries().Count());
            reader.ReadEmptyDirs();
            Assert.Equal(["file007.txt"], reader.ReadUnrecoverable());
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The catalog can hold a version the info file no longer lists — a retired one whose removal never reached it,
    /// or one imported against an older info file. The repair's reads are keyed by storage REF, not by version, so
    /// such a version's entries come back from the very same query as the live ones; marking them would write an
    /// index blob into the cloud for a version nothing claims and report paths no retained version has. The old
    /// whole-index dictionary was bounded by the info file, and the catalog reads must be too.
    /// </summary>
    [SkippableFact]
    public async Task Repair_leaves_a_version_the_info_file_no_longer_lists_alone()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, _, catalogs, factory) = Build();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("rep-stale-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "the one file the info file knows about");
            await backup.RunAsync(Req(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions.Single();
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null, v1.IndexVolumes);

            // A version 99 nothing lists, referencing the SAME damaged object under a path of its own — so that
            // "did the repair touch it" is directly visible in the report rather than inferred.
            var stale = idx with
            {
                Version = 99,
                Entries = [idx.Entries.Single(e => e.Path == "a.txt") with { Path = "stale.txt" }],
            };
            using (var writeLock = await catalogs.LockForWriteAsync(account.Id, name))
            {
                await using var writable = await catalogs.OpenAsync(account.Id, name, readOnly: false);
                using var import = new IndexStreamReader(new MemoryStream(IndexSerializer.SerializeIndex(stale)));
                await writable.ImportVersionAsync(99, info.Backup.CreatedAt.UtcTicks, import, CancellationToken.None);
            }

            // The object is gone from the cloud and gone locally: a.txt is unrepairable and gets marked.
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();
            File.Delete(Path.Combine(_src, "a.txt"));

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), AccessTier.Hot, null, dontCompress: null);
            Assert.Contains("a.txt", report.Unrecoverable);
            Assert.DoesNotContain("stale.txt", report.Unrecoverable);

            await using var catalog = await catalogs.OpenAsync(account.Id, name, readOnly: true);
            Assert.Empty(await catalog.UnrecoverableAsync(99, CancellationToken.None));

            var indexBlobs = new List<string>();
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "indexes/", CancellationToken.None))
                indexBlobs.Add(b.Name);
            Assert.DoesNotContain(indexBlobs, n => n.Contains("v99", StringComparison.Ordinal));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
