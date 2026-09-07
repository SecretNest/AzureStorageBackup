using System.Net.Sockets;
using System.Text;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class RetentionCleanerJournalTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "asb-cleanj-" + Guid.NewGuid().ToString("N"));
    private readonly BackupJournalStore _journals;

    public RetentionCleanerJournalTests()
    {
        Directory.CreateDirectory(_temp);
        _journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 45,
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

    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private static async Task PutAsync(BlobContainerClient container, string name, string body)
        => await container.GetBlobClient(name).UploadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(body)), overwrite: true);

    private static async Task<List<string>> NamesAsync(BlobContainerClient container, string prefix)
    {
        var names = new List<string>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, default))
            names.Add(b.Name);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>The catalog is thrown away with the test: every container here is created fresh, so the versions
    /// the cleanup asks about are migrated into it from the cloud on the way through, exactly as they would be on
    /// a container that had never been cleaned since the upgrade.</summary>
    private RetentionCleaner Cleaner(
        IBlobClientFactory factory, DeadWeightCompactor? compactor = null, IVersionCatalogs? catalogs = null)
    {
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        return new(factory, store, new RetentionEvaluator(), compactor,
            catalogs ?? new TestLocalAuthority(store).Catalogs, journals: _journals);
    }

    /// <summary>Really not a single cloud request was sent — the sentence "no sweep asked for means no LIST" is only nailed down once it is actually counted.</summary>
    private sealed class CountingFactory(BlobClientFactory inner) : IBlobClientFactory
    {
        private int _requests;
        private int _lists;

        /// <summary>Total number of requests sent (including LIST, HEAD, DELETE and PUT).</summary>
        public int Requests => Volatile.Read(ref _requests);

        /// <summary>Of those, the listing requests (<c>comp=list</c>). This is where the cost of an orphan sweep lands.</summary>
        public int Lists => Volatile.Read(ref _lists);

        public BlobServiceClient CreateServiceClient(Account account)
        {
            var uri = new Uri(account.BlobEndpoint);
            var credential = new StorageSharedKeyCredential(
                BlobClientFactory.ParseAccountName(uri), TestSecrets.Reader.RevealAccountKey(account));
            var options = new BlobClientOptions();
            options.AddPolicy(new CountingPolicy(this), HttpPipelinePosition.PerCall);
            return new BlobServiceClient(uri, credential, options);
        }

        public Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default)
            => inner.TestConnectionAsync(account, ct);

        private sealed class CountingPolicy(CountingFactory owner) : HttpPipelineSynchronousPolicy
        {
            public override void OnSendingRequest(HttpMessage message)
            {
                Interlocked.Increment(ref owner._requests);
                if (message.Request.Uri.Query.Contains("comp=list", StringComparison.Ordinal))
                    Interlocked.Increment(ref owner._lists);
            }
        }
    }

    private async Task WriteJournalAsync(int accountId, string container, string runId, params JournalRecord[] records)
    {
        await using var j = await _journals.CreateAsync(accountId, container, runId, new JournalHeader
        {
            RunId = runId, ConfigId = 1, StartedAt = DateTimeOffset.UnixEpoch, BaselineVersion = 0,
            LocalRoot = "/data/src", EncryptionIdentity = "plain",
        }, default);
        foreach (var r in records)
            await j.AppendAsync(r, default);
    }

    private static CleanupOptions Options(string? localRoot = null) => new()
    {
        Retention = new RetentionPolicy { MaxVersions = 50, MaxAgeDays = 365, Mode = RetentionMode.EitherTriggers },
        LocalRoot = localRoot,
    };

    [SkippableFact]
    public async Task Journalled_blocks_survive_the_orphan_sweep()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "data/keep", "kept");
            await PutAsync(container, "data/keep.001", "kept volume");
            await PutAsync(container, "data/gone", "orphan");
            // Packs need volumes too. "The base name survived, the volumes got swept" is the easiest way for this
            // criterion to go wrong, and the names on the pack side are trickier than on the data side: PackIdOf cuts at ".7z", not at the three-digit suffix.
            await PutAsync(container, "packs/pkeep.7z", "kept pack");
            await PutAsync(container, "packs/pkeep.7z.001", "kept pack volume");
            await PutAsync(container, "packs/pgone.7z", "orphan pack");
            await PutAsync(container, "packs/pgone.7z.001", "orphan pack volume");
            await WriteJournalAsync(account.Id, name, "run-x",
                new JournalRecord { Kind = "blob", Ref = "data/keep", Path = "a.bin", FullHash = "keep", Volumes = 2 },
                new JournalRecord { Kind = "pack", Ref = "pkeep", VolumeSizes = [5] });

            // Not a single version retired, yet we still sweep: the blocks left behind by a cancellation are exactly this situation.
            var report = await Cleaner(factory).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch } },
                default, sweepOrphans: true);

            Assert.Equal(["data/keep", "data/keep.001"], await NamesAsync(container, "data/"));
            Assert.Equal(["packs/pkeep.7z", "packs/pkeep.7z.001"], await NamesAsync(container, "packs/"));
            Assert.Equal(1, report.DeletedBlobs);
            Assert.Equal(1, report.DeletedPacks);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task A_volume_past_the_999th_survives_the_sweep()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            // The uploader names volumes {index:D3} — three digits of padding, four digits and up past .999.
            // Only the family's edges matter to the normalizer, so the middle 997 volumes are not uploaded.
            await PutAsync(container, "data/keep.001", "first volume");
            await PutAsync(container, "data/keep.999", "the last 3-digit volume");
            await PutAsync(container, "data/keep.1000", "the first 4-digit volume");
            await PutAsync(container, "data/keep.1001", "holds the 7z end header");
            await WriteJournalAsync(account.Id, name, "run-x",
                new JournalRecord { Kind = "blob", Ref = "data/keep", Path = "a.bin", FullHash = "keep", Volumes = 1001 });

            var report = await Cleaner(factory).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch } },
                default, sweepOrphans: true);

            Assert.Equal(["data/keep.001", "data/keep.1000", "data/keep.1001", "data/keep.999"],
                await NamesAsync(container, "data/"));
            Assert.Equal(0, report.DeletedBlobs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Once_the_journal_is_gone_the_blocks_are_swept()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "data/keep", "kept");
            await WriteJournalAsync(account.Id, name, "run-x",
                new JournalRecord { Kind = "blob", Ref = "data/keep", Path = "a.bin", FullHash = "keep" });
            _journals.DeleteAll(account.Id, name);   // this is exactly what the delete-config fallback does

            await Cleaner(factory).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch } },
                default, sweepOrphans: true);

            Assert.Empty(await NamesAsync(container, "data/"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Without_the_sweep_flag_a_no_op_cleanup_touches_nothing()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "data/gone", "orphan");

            // No version retired and no sweep requested → not one cloud request should be sent. On a container with
            // hundreds of thousands of objects those two LIST passes are not free work — and "the orphan is still
            // there" on its own cannot tell "listed once and left alone" apart from "never listed at all",
            // so what is counted here is the requests actually sent (the counter sits on the HTTP pipeline).
            var counting = new CountingFactory(factory);
            var report = await Cleaner(counting).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch } },
                default);

            Assert.Equal(0, counting.Requests);
            Assert.True(report.IsEmpty);
            Assert.Equal(["data/gone"], await NamesAsync(container, "data/"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The journal criterion also governs the row in <c>info.Packs</c>. Delete that row and the crate itself is still
    /// safe and sound in the cloud (the case above guards that), but the info file has no record of it: when the next
    /// round reuses this crate from the journal, <c>RecordPackAsync</c> writes it back exactly as it was, while every check/restore in between believes this crate does not exist.
    /// </summary>
    [SkippableFact]
    public async Task A_journalled_pack_keeps_its_row_in_the_info_file()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "packs/pkeep.7z", "kept pack");
            await PutAsync(container, "packs/pgone.7z", "orphan pack");
            await WriteJournalAsync(account.Id, name, "run-x",
                new JournalRecord { Kind = "pack", Ref = "pkeep", VolumeSizes = [5] });

            var info = new BackupInfoFile
            {
                Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch },
                Packs =
                {
                    ["pkeep"] = new PackInfo { Blob = "packs/pkeep.7z", OriginalBytes = 5 },
                    ["pgone"] = new PackInfo { Blob = "packs/pgone.7z", OriginalBytes = 5 },
                },
            };

            await Cleaner(factory).CleanupAsync(account, name, null, Options(), info, default, sweepOrphans: true);

            // There is not a single retained version and neither crate is referenced by any index — the only thing telling them apart is the journal.
            Assert.Equal(["pkeep"], info.Packs.Keys.ToList());
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// A pure orphan sweep (with no version retired at all) must not drag dead-weight compaction and an info-file rewrite along with it.
    /// <para>
    /// Dead weight only grows when a version retires, and when compaction fails or gives up
    /// <see cref="DeadWeightCompactor"/> just writes the same DeadBytes back unchanged — so the next round's
    /// judgement comes out identical. Hung off a nightly scheduled cleanup, that means the same packs get downloaded,
    /// recompressed and re-uploaded every night, forever. Same for the info file: rewriting it when nothing changed is paying for a conditional write with If-Match for nothing.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_sweep_with_no_retirement_neither_compacts_nor_rewrites_the_info_file()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "packs/p1.7z", "a pack that is 99.9% dead weight on paper");
            await PutAsync(container, "data/gone", "orphan");

            // The live member really is put on disk locally and its hash really is computed: if compaction is ever
            // invoked it takes the **success** branch (take material locally, recompress, overwrite-upload) rather
            // than the "give up / throw" ones. That is the key to whether this test's criterion can tell "it never
            // ran" apart — give-up and failure write DeadBytes as 999, visible at a glance;
            // only the success branch writes DeadBytes back to 0, which looks exactly like "it never ran at all".
            var localRoot = Path.Combine(_temp, "src");
            Directory.CreateDirectory(localRoot);
            await File.WriteAllTextAsync(Path.Combine(localRoot, "a.bin"), "x");
            var liveHash = await new FileHasher().FullHashAsync(Path.Combine(localRoot, "a.bin"), default);

            // Version 1 references only a single 1-byte member inside p1, while the crate records 1000 bytes of
            // original size → 99.9% dead weight, far past the default 30% threshold. If compaction is ever invoked, it will certainly touch this crate.
            var (indexBlob, _) = await store.WriteIndexAsync(account, name, 1, new VersionIndex
            {
                Version = 1,
                Entries =
                [
                    new IndexEntry
                    {
                        Path = "a.bin", Kind = "file", Length = 1, Permissions = "644", FullHash = liveHash,
                        Storage = new StorageRef { Kind = "pack", Ref = "p1", EntryName = "a.bin" },
                    },
                ],
            }, password: null);

            var info = new BackupInfoFile
            {
                Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch },
                Versions =
                {
                    new BackupVersion
                    {
                        Version = 1, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = indexBlob,
                        Stats = new VersionStats(1, 1, 1, 1),
                    },
                },
                Packs = { ["p1"] = new PackInfo { Blob = "packs/p1.7z", OriginalBytes = 1000, Members = [liveHash] } },
            };

            var staging = new StagingArea(
                Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
            var compactor = new DeadWeightCompactor(
                new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(),
                Path.Combine(_temp, "compact"), staging);

            // Keep 50 versions → the one version there is does not retire, yet an orphan sweep is still requested.
            var report = await Cleaner(factory, compactor).CleanupAsync(
                account, name, null, Options(localRoot), info, default, sweepOrphans: true);

            Assert.Equal(1, report.DeletedBlobs);                       // the sweep really did happen
            Assert.Equal(0, report.RetiredVersions);                    // and no version retired
            Assert.Empty(await NamesAsync(container, "data/"));
            // The criterion rests on the two fields **only compaction writes**. Not DeadBytes: a successful compaction
            // writes it as 0, exactly like never having run, which would leave this test unable to tell whether it
            // took effect at all. OriginalBytes, by contrast, would certainly drop from 1000 to 1
            // (only the live member left), and the member list would be swapped out wholesale.
            Assert.Equal(1000, info.Packs["p1"].OriginalBytes);
            Assert.Equal([liveHash], info.Packs["p1"].Members);
            Assert.Empty(info.Packs["p1"].VolumeSizes);
            // The give-up and failure branches write DeadBytes=999, which this blocks along the way.
            Assert.Equal(0, info.Packs["p1"].DeadBytes);
            // Not one byte of the info file was written either: this container never had an info-file blob at all.
            Assert.False((await container.GetBlobClient(BackupDiscovery.IndexBlobName).ExistsAsync()).Value);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// A version that is retiring anyway must not be able to block the cleanup by having an unreadable index.
    /// <para>
    /// The criterion is now read out of the catalog, which means every version in the info file gets migrated into
    /// it on the way through — and a migration goes to the cloud when nothing local has the index. For a retained
    /// version a failure there has to stop the round (its refs are what protects live content), but for a retiring
    /// one it must not: its only contribution is naming what may go, and what it alone held is collected by the
    /// orphan half of the criterion regardless — exactly as it was by the old code, which never read a retired
    /// index at all. Otherwise one lost index blob would leave the container unable to free a byte, for good.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_retiring_version_whose_index_is_gone_does_not_block_the_cleanup()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanmiss");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "data/v1only", "only version 1 has this");
            await PutAsync(container, "data/shared", "both versions have this");

            var (idx1, _) = await store.WriteIndexAsync(account, name, 1, new VersionIndex
            {
                Version = 1,
                Entries =
                [
                    Entry("a.bin", "data/v1only"),
                    Entry("b.bin", "data/shared"),
                ],
            }, null);
            var (idx2, _) = await store.WriteIndexAsync(account, name, 2, new VersionIndex
            {
                Version = 2,
                Entries = [Entry("b.bin", "data/shared")],
            }, null);

            // The lost index: version 1 is still in the info file, but its manifest is no longer in the cloud and
            // no local copy of it has ever existed (the catalog under this cleaner is brand new).
            await container.GetBlobClient(idx1).DeleteAsync();

            var info = new BackupInfoFile
            {
                Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch },
                Versions =
                {
                    new BackupVersion { Version = 1, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), IndexBlob = idx1, Stats = new VersionStats(2, 2, 2, 2) },
                    new BackupVersion { Version = 2, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = idx2, Stats = new VersionStats(1, 1, 1, 1) },
                },
            };

            var report = await Cleaner(factory).CleanupAsync(account, name, null, new CleanupOptions
            {
                Retention = new RetentionPolicy { MaxVersions = 1, Mode = RetentionMode.VersionOnly },
            }, info);

            Assert.Equal(1, report.RetiredVersions);
            // Version 2 was migrated, so what it still references is known and survives; version 1's own block is
            // referenced by nobody left and goes, as an orphan rather than as a named candidate.
            Assert.Equal(["data/shared"], await NamesAsync(container, "data/"));
            Assert.Equal(1, report.DeletedBlobs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The catalog is a cache of what the info file says exists, and the info file is the authority: a version in
    /// the catalog that the info file does not list must be dropped before anything is judged against it.
    /// <para>
    /// This is the state an earlier round leaves behind when it dies between its cloud deletes and the removal at
    /// the end — the retirement commit went out first, so the info file has already forgotten the version while its
    /// rows are still in the catalog. Nothing later retires it: the next round computes no retirement at all, so it
    /// never reaches that removal, and those rows would go on answering "referenced" for exactly the objects the
    /// failed round did not manage to delete — protecting them permanently. The old code could not get into this
    /// state, because it rebuilt the referenced set from the info file's own version list every time.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_catalog_version_the_info_file_no_longer_lists_is_dropped_and_stops_protecting_its_blobs()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanrec");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "data/v1only", "the block the failed round did not delete");
            await PutAsync(container, "data/shared", "still referenced by version 2");

            var (idx1, _) = await store.WriteIndexAsync(account, name, 1, new VersionIndex
            {
                Version = 1,
                Entries = [Entry("a.bin", "data/v1only"), Entry("b.bin", "data/shared")],
            }, null);
            var (idx2, _) = await store.WriteIndexAsync(account, name, 2, new VersionIndex
            {
                Version = 2,
                Entries = [Entry("b.bin", "data/shared")],
            }, null);

            var created = DateTimeOffset.UnixEpoch;
            var v1 = new BackupVersion { Version = 1, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), IndexBlob = idx1, Stats = new VersionStats(2, 2, 2, 2) };
            var v2 = new BackupVersion { Version = 2, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = idx2, Stats = new VersionStats(1, 1, 1, 1) };

            // The leftover state: both versions in the catalog, only version 2 in the info file.
            var catalogs = new TestLocalAuthority(store).Catalogs;
            await catalogs.EnsureVersionAsync(account, name, v1, created.UtcTicks, null, default);
            await catalogs.EnsureVersionAsync(account, name, v2, created.UtcTicks, null, default);

            var info = new BackupInfoFile
            {
                Backup = new BackupMeta { Name = name, CreatedAt = created },
                Versions = { v2 },
            };

            // Nothing retires this round (50 versions kept, one version present) — precisely the round that would
            // never reach the removal at the end of the cleanup.
            var report = await Cleaner(factory, catalogs: catalogs).CleanupAsync(
                account, name, null, Options(), info, default, sweepOrphans: true);

            Assert.Equal(0, report.RetiredVersions);
            Assert.Equal(1, report.DeletedBlobs);
            Assert.Equal(["data/shared"], await NamesAsync(container, "data/"));

            await using var catalog = await catalogs.OpenAsync(account.Id, name, readOnly: true, default);
            Assert.Equal([2], (await catalog.ListVersionsAsync(default)).Select(v => v.Version));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    private static IndexEntry Entry(string path, string reference) => new()
    {
        Path = path, Kind = "file", Length = 4, Permissions = "0644", FullHash = "xxh128:" + new string('e', 32),
        Storage = new StorageRef { Kind = "blob", Ref = reference },
    };

    /// <summary>Retirement must commit before it deletes — the one place in the codebase that had it backwards.
    /// The old order deleted a retired version's index volumes from the cloud FIRST and wrote the info file (the
    /// only record of which versions exist) LAST, so a cancellation or crash in between left the info file
    /// pointing at manifests that no longer exist: restore and check of that version fail with a missing blob,
    /// discovered only when someone needs it. And the window sits on every backup's tail — cleanup runs
    /// automatically after each run, with a user-facing Stop able to cancel it mid-way (the orchestrator
    /// swallows that cancellation as a harmless skipped cleanup). Committed-first, a failure merely leaves the
    /// retired index behind as an unreferenced blob for a later sweep; the info file never lies.
    /// The lease stands in for the crash: it makes the index delete fail after the commit point.</summary>
    [SkippableFact]
    public async Task Retirement_Commits_The_Info_File_Before_Deleting_Index_Volumes()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanord");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var (idx1, vols1) = await store.WriteIndexAsync(account, name, 1, new VersionIndex { Version = 1 }, null);
            var (idx2, vols2) = await store.WriteIndexAsync(account, name, 2, new VersionIndex { Version = 2 }, null);
            var info = new BackupInfoFile
            {
                Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch },
                Versions =
                [
                    new BackupVersion { Version = 1, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), IndexBlob = idx1, IndexVolumes = vols1, Stats = new VersionStats(0, 0, 0, 0) },
                    new BackupVersion { Version = 2, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = idx2, IndexVolumes = vols2, Stats = new VersionStats(0, 0, 0, 0) },
                ],
            };
            await store.WriteInfoAsync(account, name, info, null);

            // The stand-in for a crash/stop mid-deletion: the retired version's index cannot be deleted.
            var lease = container.GetBlobClient(idx1).GetBlobLeaseClient();
            await lease.AcquireAsync(BlobLeaseClient.InfiniteLeaseDuration);
            try
            {
                try
                {
                    await Cleaner(factory).CleanupAsync(account, name, null, new CleanupOptions
                    {
                        Retention = new RetentionPolicy { MaxVersions = 1, Mode = RetentionMode.VersionOnly },
                    }, info);
                }
                catch
                {
                    // A failed delete may still fail the cleanup; what must NOT happen is the commit going missing.
                }

                var after = await store.ReadInfoAsync(account, name, null);
                Assert.NotNull(after);
                Assert.Equal([2], after!.Versions.Select(v => v.Version));
            }
            finally
            {
                await lease.ReleaseAsync();
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

}
