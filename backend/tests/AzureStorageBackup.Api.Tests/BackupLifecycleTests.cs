using System.Net.Sockets;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The end-to-end backup lifecycle chain (real Azurite + real 7-Zip, nothing mocked):
/// fresh backup → incremental (dedup measured for real) → dead-weight compaction → read-only check → deliberate cloud damage → repair from local → byte-for-byte restore.
/// One real file tree walks the whole chain in order, each stage asserting an observable business result; at the far end the restored bytes are compared against what was originally written.
/// Encrypted and unencrypted each get a run — after the "ciphertext into the database + decrypt at the throat" rework, the encrypted path carries the most risk.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupLifecycleTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <summary>Baseline mtime for the source files: every write advances it by a minute, so even a "same-length rewrite" is guaranteed to be recognised as a change by the differ.</summary>
    private static readonly DateTime MtimeBase = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _base;
    private readonly string _root;
    private readonly string _temp;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly RecordingUploader _uploader;
    private int _mtimeSeq;

    public BackupLifecycleTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-life-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_root);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _uploader = new RecordingUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)));
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

    /// <summary>Records the blob name of every data/pack object upload — the incremental stage uses it to actually measure that "unchanged files were not re-uploaded".</summary>
    private sealed class RecordingUploader(IBlobUploader inner) : IBlobUploader
    {
        private readonly List<string> _names = [];

        public IReadOnlyList<string> Uploads { get { lock (_names) return [.. _names]; } }

        public void Reset() { lock (_names) _names.Clear(); }

        private void Note(string blobName) { lock (_names) _names.Add(blobName); }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Note(blobName);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Note(blobName);
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    private sealed record Rig(
        BackupOrchestrator Backup,
        BackupChecker Checker,
        BackupRepairer Repairer,
        RestoreOrchestrator Restore,
        IBackupInfoStore Store,
        BlobClientFactory Factory);

    /// <summary>Wires up the whole chain the way production does: the local authoritative state machine (TrackedInfoStore + LocalIndexCache) runs through backup/cleanup/compaction/repair.</summary>
    private Rig Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var hasher = new FileHasher();
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(_db));
        var indexCache = new LocalIndexCache(_db, store);
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            _uploader, new SevenZipCompressor(), hasher, Path.Combine(_temp, "compact"), staging);
        var cleaner = new RetentionCleaner(
            factory, store, new RetentionEvaluator(), compactor, indexCache, tracked);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(hasher), new GroupingPlanner(),
            new SevenZipCompressor(), _uploader, factory, store, staging, cleaner, hasher,
            indexCache: indexCache, trackedInfo: tracked);
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), hasher, Path.Combine(_temp, "check"), trackedInfo: tracked);
        var repairer = new BackupRepairer(
            factory, store, new SevenZipCompressor(), hasher, _uploader, Path.Combine(_temp, "repair"), staging,
            checker: checker, trackedInfo: tracked, indexCache: indexCache);
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), hasher, Path.Combine(_temp, "restore"));
        return new Rig(backup, checker, repairer, restore, store, factory);
    }

    private BackupRequest Request(Account account, string container, string? password, int maxVersions) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "lifecycle",
        Description = "end-to-end lifecycle fixture",
        Password = password,
        Options = new BackupEngineOptions
        {
            // 20K threshold: the small files under docs/ group into a pack while the big ones under media/ take the single-file data blob path — both storage paths covered.
            Plan = new PlanOptions { SingleFileThresholdBytes = 20_000 },
            Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = maxVersions },
        },
    };

    // ───────────────────────── Source tree and snapshots ─────────────────────────

    /// <summary>Writes a file and gives it a unique, increasing mtime. A same-length rewrite that leaves the mtime alone is judged unchanged by the differ, so it has to be advanced explicitly.</summary>
    private void Write(string rel, byte[] content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        File.SetLastWriteTimeUtc(full, MtimeBase.AddMinutes(++_mtimeSeq));
    }

    private void WriteText(string rel, string text) => Write(rel, Encoding.UTF8.GetBytes(text));

    /// <summary>Deterministic incompressible content — it keeps pack/blob size proportional to member count, which is what makes compaction's size reclamation observable at all.</summary>
    private static byte[] Rand(int size, int seed)
    {
        var buf = new byte[size];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    /// <summary>Snapshot of the current source tree: relative path → (content bytes, mtime). The byte-for-byte comparison after a restore goes against this.</summary>
    private Dictionary<string, (byte[] Bytes, DateTime Mtime)> Snapshot()
    {
        var map = new Dictionary<string, (byte[], DateTime)>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            map[Rel(_root, f)] = (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f));
        return map;
    }

    private static string Rel(string root, string full) =>
        Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The restored tree must match the snapshot exactly: the same set of files, every file byte for byte identical, mtime restored from the index metadata.</summary>
    private static void AssertTreeEquals(
        Dictionary<string, (byte[] Bytes, DateTime Mtime)> expected, string target, string label)
    {
        var actual = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Rel(target, f), StringComparer.Ordinal);

        // Directory structure matches: one file too many or one too few and it fails.
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());

        foreach (var (rel, exp) in expected)
        {
            var got = File.ReadAllBytes(actual[rel]);
            Assert.True(exp.Bytes.AsSpan().SequenceEqual(got),
                $"{label}: restored content differs for {rel} ({exp.Bytes.Length} vs {got.Length} bytes)");
            Assert.Equal(exp.Mtime, File.GetLastWriteTimeUtc(actual[rel]));
        }
    }

    /// <summary>Restores a version into a brand-new empty directory and compares everything. FailedFiles must be 0 — a group download/extract failure is only counted, never thrown.</summary>
    private async Task<string> RestoreAndAssertAsync(
        Rig rig, Account account, string container, string? password, int version,
        Dictionary<string, (byte[] Bytes, DateTime Mtime)> expected, string label)
    {
        var target = Path.Combine(_base, "restore", label);
        Directory.CreateDirectory(target);

        var result = await rig.Restore.RunAsync(new RestoreRequest
        {
            Account = account,
            Container = container,
            TargetRoot = target,
            Password = password,
            Version = version,
        });

        Assert.Equal(version, result.Version);
        Assert.Equal(0, result.FailedFiles);   // failures are swallowed into a count, so not asserting is the same as not testing
        Assert.Equal(0, result.SkippedFiles);  // the target is an empty directory, nothing should be skipped
        Assert.Equal(expected.Count, result.RestoredFiles);
        AssertTreeEquals(expected, target, label);
        return target;
    }

    // ───────────────────────── Cloud probes and damage tools ─────────────────────────

    private static StorageRef StorageOf(VersionIndex index, string path) =>
        index.Entries.Single(e => e.Path == path).Storage
        ?? throw new InvalidOperationException($"{path} has no storage ref");

    private static string BlobNameOf(StorageRef s) => s.Kind == "pack" ? $"packs/{s.Ref}.7z" : s.Ref;

    private static async Task AssertReferencedBlobsExistAsync(BlobContainerClient cc, VersionIndex index)
    {
        foreach (var e in index.Entries)
            Assert.True(
                await VolumeBlobIO.ExistsAsync(cc, BlobNameOf(e.Storage!), CancellationToken.None),
                $"missing blob {BlobNameOf(e.Storage!)} for {e.Path}");
    }

    /// <summary>Full snapshot of the container (name → length + ETag): used to assert that "a check without repair is read-only".</summary>
    private static async Task<Dictionary<string, (long Length, string ETag)>> BlobFingerprintAsync(BlobContainerClient cc)
    {
        var map = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
        await foreach (var b in cc.GetBlobsAsync())
            map[b.Name] = (b.Properties.ContentLength ?? -1, b.Properties.ETag?.ToString() ?? "");
        return map;
    }

    /// <summary>Damage tool one: delete every volume of an archive outright (simulating an object wrongly deleted, or swept away by a lifecycle policy).</summary>
    private static async Task DeleteArchiveAsync(BlobContainerClient cc, string baseRef)
    {
        var deleted = 0;
        await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, CancellationToken.None))
        {
            if (!VolumeBlobIO.IsVolumeOf(baseRef, b.Name))
                continue;
            await cc.GetBlobClient(b.Name).DeleteIfExistsAsync();
            deleted++;
        }
        Assert.True(deleted > 0, $"nothing deleted for {baseRef} — the damage step itself is broken");
    }

    /// <summary>Damage tool two: rewrite the content **at the same length** (bit rot). The size does not change, so a HEAD-level check cannot see it; only downloading and recomputing the hash finds it.</summary>
    private static async Task CorruptInPlaceAsync(BlobContainerClient cc, string blobName)
    {
        var blob = cc.GetBlobClient(blobName);
        var props = (await blob.GetPropertiesAsync()).Value;
        var junk = Rand((int)props.ContentLength, 9_001);
        await blob.UploadAsync(
            BinaryData.FromBytes(junk),
            new BlobUploadOptions { Metadata = new Dictionary<string, string>(props.Metadata) });

        var after = (await blob.GetPropertiesAsync()).Value.ContentLength;
        Assert.Equal(props.ContentLength, after); // the damage must not change the size, or this step has no discriminating power
    }

    /// <summary>Asserts an archive really is an encrypted one: it will not open without the password, and does open with it.</summary>
    private async Task AssertArchiveIsEncryptedAsync(BlobContainerClient cc, string baseRef, string password)
    {
        var dir = Path.Combine(_temp, "encprobe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var first = await VolumeBlobIO.DownloadAsync(cc, baseRef, dir, CancellationToken.None);
        var codec = new SevenZipCompressor();

        await Assert.ThrowsAnyAsync<Exception>(
            () => codec.ExtractAsync(first, Path.Combine(dir, "nopw"), null));
        await codec.ExtractAsync(first, Path.Combine(dir, "withpw"), password);
    }

    // ───────────────────────── The lifecycle main chain ─────────────────────────

    [SkippableTheory]
    [InlineData("correct horse battery staple")] // encrypted: the highest-risk path in this round of changes, it has to walk the whole chain
    [InlineData(null)]                           // unencrypted
    public async Task Full_Lifecycle_From_First_Backup_Through_Compaction_Damage_Repair_And_Restore(string? password)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var rig = Build();
        var account = AzuriteAccount();
        var name = RandomName(password is null ? "lifeplain-" : "lifeenc-");
        var cc = rig.Factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // ═══ Stage 1: fresh backup ═══
            Write("docs/a.txt", Rand(4000, 11));   // 5 small files in the same directory → merged into one pack
            Write("docs/b.txt", Rand(4000, 12));
            Write("docs/c.txt", Rand(4000, 13));
            Write("docs/d.txt", Rand(4000, 14));
            Write("docs/e.txt", Rand(4000, 15));
            Write("media/photo.bin", Rand(40_000, 21)); // ≥20K threshold → single-file data blob
            Write("media/clip.bin", Rand(30_000, 22));
            WriteText("notes/deep/readme.txt", "nested note, first revision");
            WriteText("top.txt", "root level file, first revision");
            Directory.CreateDirectory(Path.Combine(_root, "empty")); // an empty directory has to enter the index and be recreated on restore

            var snap1 = Snapshot();
            var r1 = await rig.Backup.RunAsync(Request(account, name, password, maxVersions: 2));

            Assert.Equal(1, r1.Version);
            Assert.Equal(snap1.Count, r1.ChangedFiles); // on a first backup every file counts as changed

            var info1 = await rig.Store.ReadInfoAsync(account, name, password);
            Assert.NotNull(info1);
            Assert.Equal(password is not null, info1!.Backup.Encrypted);
            Assert.Single(info1.Versions);
            // Encrypted and unencrypted use different info-file blob names.
            Assert.True(await cc.GetBlobClient(password is null
                ? BackupDiscovery.IndexBlobName
                : BackupDiscovery.EncryptedIndexBlobName).ExistsAsync());

            var idx1 = await rig.Store.ReadIndexAsync(account, name, info1.Versions[0].IndexBlob, password);
            Assert.Equal(snap1.Count, idx1.Entries.Count);                             // entry count == local file count
            Assert.Equal(snap1.Keys.Order(), idx1.Entries.Select(e => e.Path).Order()); // and they correspond one for one
            Assert.Contains("empty", idx1.EmptyDirs);
            await AssertReferencedBlobsExistAsync(cc, idx1);

            var docsPack = StorageOf(idx1, "docs/a.txt");
            Assert.Equal("pack", docsPack.Kind);
            foreach (var p in new[] { "docs/b.txt", "docs/c.txt", "docs/d.txt", "docs/e.txt" })
                Assert.Equal(docsPack.Ref, StorageOf(idx1, p).Ref); // all 5 members belong to the same pack
            var clip1 = StorageOf(idx1, "media/clip.bin");
            Assert.Equal("blob", clip1.Kind);

            // Baseline of the collision-detection metadata a fresh backup writes (len/head/tail, or the opaque v when encrypted) — after a repair it must match this exactly.
            var clipMetaBaseline = (await cc.GetBlobClient(clip1.Ref).GetPropertiesAsync()).Value.Metadata;
            Assert.NotEmpty(clipMetaBaseline);

            // An encrypted backup's data objects live at keyed addresses; the plaintext data/{fullHash} must not exist (anti-fingerprinting).
            if (password is not null)
            {
                var clipHash = idx1.Entries.Single(e => e.Path == "media/clip.bin").FullHash!;
                Assert.DoesNotContain(clipHash, clip1.Ref);
                Assert.False(await cc.GetBlobClient($"data/{clipHash}").ExistsAsync());
            }

            // ═══ Stage 2: incremental backup (3 modified, 1 added, 1 subtree deleted) ═══
            _uploader.Reset();
            Write("docs/a.txt", Rand(4000, 111)); // same length, different content
            Write("docs/b.txt", Rand(4000, 112));
            Write("docs/c.txt", Rand(4000, 113));
            Write("media/photo.bin", Rand(40_000, 121));
            Write("media/copy.bin", snap1["media/clip.bin"].Bytes); // newly added, content exactly the same as clip.bin
            Directory.Delete(Path.Combine(_root, "notes"), recursive: true);

            var snap2 = Snapshot();
            var r2 = await rig.Backup.RunAsync(Request(account, name, password, maxVersions: 2));

            Assert.Equal(2, r2.Version);
            Assert.Equal(5, r2.ChangedFiles); // a/b/c/photo modified + copy added; deletions do not count as changes

            var info2 = await rig.Store.ReadInfoAsync(account, name, password);
            Assert.Equal([1, 2], info2!.Versions.Select(v => v.Version));
            var idx2 = await rig.Store.ReadIndexAsync(account, name, info2.Versions[^1].IndexBlob, password);
            Assert.Equal(snap2.Keys.Order(), idx2.Entries.Select(e => e.Path).Order()); // the deleted files are gone from the index
            await AssertReferencedBlobsExistAsync(cc, idx2);

            // Dedup measured for real (the core value of an incremental): unchanged files still point at the very same v1 storage object...
            foreach (var p in new[] { "docs/d.txt", "docs/e.txt" })
                Assert.Equal(docsPack.Ref, StorageOf(idx2, p).Ref);
            Assert.Equal(clip1.Ref, StorageOf(idx2, "media/clip.bin").Ref);
            // ...a newly added file with identical content hits the existing object too (cross-version content-addressed dedup)...
            Assert.Equal(clip1.Ref, StorageOf(idx2, "media/copy.bin").Ref);

            // ...and the objects uploaded this round are **exactly and only** the new ones produced by changed content: a single redundant re-upload fails this assertion.
            var docsPack2 = StorageOf(idx2, "docs/a.txt");
            var photo2 = StorageOf(idx2, "media/photo.bin");
            Assert.NotEqual(docsPack.Ref, docsPack2.Ref);
            Assert.Equal(
                new[] { $"packs/{docsPack2.Ref}.7z", photo2.Ref }.Order(),
                _uploader.Uploads.Order());

            // ═══ Stage 6a: restore version 1 and version 2 ═══
            // Version 1 gets retired by the retention policy shortly, so verify here first that it restores byte for byte.
            var v1Dir = await RestoreAndAssertAsync(rig, account, name, password, 1, snap1, "v1");
            Assert.True(Directory.Exists(Path.Combine(v1Dir, "empty")), "empty directory was not recreated");
            await RestoreAndAssertAsync(rig, account, name, password, 2, snap2, "v2");

            // ═══ Stage 3: dead-weight compaction ═══
            // docsPack has 5 members; a/b/c moved to a new pack from v2 on, and d changes again in v3. Once v1 retires,
            // only d (referenced by v2) and e (referenced by v2/v3) are still live in docsPack → dead weight 3/5 = 60% > the 30% threshold → repack in place.
            // Note that d's local file is the v3 content by now, which does not match the v1 content inside the pack → compaction has to download the old pack and extract to fill the gap
            // (this is exactly where an encrypted backup takes the "download + extract with the password" path).
            var packBlob = $"packs/{docsPack.Ref}.7z";
            var packSizeBefore = (await cc.GetBlobClient(packBlob).GetPropertiesAsync()).Value.ContentLength;

            Write("docs/d.txt", Rand(4000, 114));
            WriteText("top.txt", "root level file, second revision");
            var snap3 = Snapshot();
            var r3 = await rig.Backup.RunAsync(Request(account, name, password, maxVersions: 2));
            Assert.Equal(3, r3.Version);

            var info3 = await rig.Store.ReadInfoAsync(account, name, password);
            Assert.Equal([2, 3], info3!.Versions.Select(v => v.Version)); // v1 has retired

            var compacted = info3.Packs[docsPack.Ref];
            Assert.Equal(2, compacted.Members.Count);   // the dead-weight members a/b/c are dropped, only d and e remain
            Assert.Equal(0, compacted.DeadBytes);
            Assert.Equal(8000L, compacted.OriginalBytes);

            var packSizeAfter = (await cc.GetBlobClient(packBlob).GetPropertiesAsync()).Value.ContentLength;
            Assert.True(packSizeAfter < packSizeBefore,
                $"dead weight was not physically reclaimed: {packSizeBefore} → {packSizeAfter} bytes");
            Assert.Equal(packSizeAfter, compacted.VolumeSizes[0]); // the size recorded in the info file matches what is actually in the cloud

            // Compaction's most dangerous failure mode is reclaiming data that is **still referenced**: restore both retained versions in full and compare byte for byte.
            await RestoreAndAssertAsync(rig, account, name, password, 2, snap2, "v2-after-compaction");
            await RestoreAndAssertAsync(rig, account, name, password, 3, snap3, "v3-after-compaction");

            // ═══ Stage 4: check (without repair) ═══
            var deep = new CheckOptions
            {
                Cloud = CloudCheckLevel.Content,
                Local = LocalCheckLevel.Content,
                ListOrphans = true,
            };

            var healthy = await rig.Checker.CheckAsync(account, name, password, null, deep, _root);
            Assert.True(healthy.Ok);
            Assert.Null(healthy.MetadataIssue);
            Assert.Equal(snap3.Count, healthy.Findings.Count);
            Assert.All(healthy.Findings, f => Assert.Equal(CloudState.Ok, f.Cloud));
            Assert.All(healthy.Findings, f => Assert.Equal(LocalState.Ok, f.Local)); // the local tree matches v3 exactly
            Assert.Empty(healthy.OrphanBlobs);                                        // nothing left over after retirement + compaction

            // Damage the cloud on purpose: (1) delete clip's data blob outright; (2) rewrite the pack holding docs/a, b and c at the same length.
            var idx3 = await rig.Store.ReadIndexAsync(account, name, info3.Versions[^1].IndexBlob, password);
            var clip3 = StorageOf(idx3, "media/clip.bin");
            var abcPack = StorageOf(idx3, "docs/a.txt");
            Assert.Equal(docsPack2.Ref, abcPack.Ref); // v3 did not touch a/b/c, so it still uses v2's pack
            Assert.Equal(clip1.Ref, clip3.Ref); // media/clip.bin's content never changed, so its address is stable and the v1 metadata baseline can be compared against

            await DeleteArchiveAsync(cc, clip3.Ref);
            await CorruptInPlaceAsync(cc, $"packs/{abcPack.Ref}.7z");

            var fingerprintBefore = await BlobFingerprintAsync(cc);

            // The "existence + size" level sees only the deleted one: same-length bit rot leaves the size untouched, and this level is designed not to catch it.
            var shallow = await rig.Checker.CheckAsync(account, name, password, null,
                new CheckOptions { Cloud = CloudCheckLevel.ExistenceSize, Local = LocalCheckLevel.Content }, _root);
            Assert.False(shallow.Ok);
            Assert.Equal(
                new[] { "media/clip.bin", "media/copy.bin" }.Order(),
                shallow.CorruptedPaths.Order()); // both paths sharing the same data blob are reported faithfully

            // The "content" level downloads, extracts and recomputes the hash, dragging same-length bit rot out too.
            var damaged = await rig.Checker.CheckAsync(account, name, password, null, deep, _root);
            Assert.False(damaged.Ok);
            Assert.Equal(
                new[] { "docs/a.txt", "docs/b.txt", "docs/c.txt", "media/clip.bin", "media/copy.bin" }.Order(),
                damaged.CorruptedPaths.Order());
            // The local source files are all present and their content matches → every one of them is repairable from local.
            Assert.Equal(damaged.CorruptedPaths.Order(), damaged.RepairablePaths.Order());

            // A check without repair must be **read-only**: after two rounds of checking (downloads and extracts included), every cloud blob's length and ETag is unchanged.
            Assert.Equal(fingerprintBefore, await BlobFingerprintAsync(cc));

            // ═══ Stage 5: check + repair ═══
            var repair = await rig.Repairer.RepairAsync(
                account, name, password, _root, version: null,
                deep with { ListOrphans = true }, AccessTier.Hot, volumeBytes: null, dontCompress: null);

            Assert.Equal(damaged.CorruptedPaths.Order(), repair.Repaired.Order());
            Assert.Empty(repair.Unrecoverable);
            Assert.Empty(repair.DeletedOrphans); // repair only replaces content; it should neither create nor reclaim orphans

            // The collision-detection metadata that repair rebuilds has to equal the fresh backup's key for key — "present is good enough" is not the bar, since differing values make dedup misjudge a collision
            // (defect 2: repair used to drop len/head/tail entirely, silently switching collision protection off).
            var clipMetaAfterRepair = (await cc.GetBlobClient(clip3.Ref).GetPropertiesAsync()).Value.Metadata;
            Assert.Equal(
                clipMetaBaseline.OrderBy(kv => kv.Key, StringComparer.Ordinal),
                clipMetaAfterRepair.OrderBy(kv => kv.Key, StringComparer.Ordinal));

            var afterRepair = await rig.Checker.CheckAsync(account, name, password, null, deep, _root);
            Assert.True(afterRepair.Ok);
            Assert.All(afterRepair.Findings, f => Assert.Equal(CloudState.Ok, f.Cloud));
            Assert.Empty(afterRepair.OrphanBlobs);

            // ═══ Stage 6b: restore (the damaged version, and the older version that shares objects with it) ═══
            // The criterion for "fixed" is not that the file exists, but that its content is byte for byte what was originally written.
            await RestoreAndAssertAsync(rig, account, name, password, 3, snap3, "v3-after-repair");
            await RestoreAndAssertAsync(rig, account, name, password, 2, snap2, "v2-after-repair");
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// An encrypted backup's confidentiality has to survive a repair: after rebuilding a single-file data blob from
    /// local and replacing it, the cloud object must still be an **encrypted** archive.
    /// <para>
    /// Regression background: <c>ReplaceBlobAsync</c> in <see cref="BackupRepairer"/> once hard-coded
    /// <c>CompressionRequest.Password</c> to <c>null</c> (while <c>RepairPackAsync</c> in the same class did pass the
    /// password correctly), so the moment an encrypted backup was repaired, that data blob landed in the cloud as a
    /// plaintext 7z. The defect is **functionally symptomless** — 7z ignores <c>-p</c> on an unencrypted archive, so
    /// check and restore both pass — which means it can only be probed at the storage layer and cannot be judged by
    /// "it restores fine". This test is the guard for exactly that.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Repair_Of_Encrypted_Backup_Keeps_The_Data_Blob_Encrypted()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        const string password = "correct horse battery staple";
        var rig = Build();
        var account = AzuriteAccount();
        var name = RandomName("liferepenc-");
        var cc = rig.Factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            Write("media/clip.bin", Rand(30_000, 22)); // ≥20K → single-file data blob
            await rig.Backup.RunAsync(Request(account, name, password, maxVersions: 5));

            var info = await rig.Store.ReadInfoAsync(account, name, password);
            var idx = await rig.Store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, password);
            var clip = StorageOf(idx, "media/clip.bin");

            // Baseline: the object the backup path writes really is an encrypted archive (which also proves this probe discriminates).
            await AssertArchiveIsEncryptedAsync(cc, clip.Ref, password);

            // The cloud object is gone → repair from the local source file, which is still there.
            await DeleteArchiveAsync(cc, clip.Ref);
            var repair = await rig.Repairer.RepairAsync(
                account, name, password, _root, version: null,
                new CheckOptions { Cloud = CloudCheckLevel.ExistenceSize }, AccessTier.Hot, volumeBytes: null, dontCompress: null);
            Assert.Equal(["media/clip.bin"], repair.Repaired);

            // The object repair writes back must still be encrypted.
            await AssertArchiveIsEncryptedAsync(cc, clip.Ref, password);
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }
}
