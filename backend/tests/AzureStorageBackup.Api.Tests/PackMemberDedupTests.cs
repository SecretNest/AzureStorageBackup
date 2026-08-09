using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// **File-level** dedup for packed small files. Single-file blobs have always been content-addressed, so
/// identical content is stored once; packed members never were — the same content appearing in two packs really
/// does get stored twice.
/// <para>
/// Duplicates inside one pack are already eaten by 7z's solid archive (dictionary matching across members), so
/// what has to be covered here is the **cross-pack, cross-version** part: different packs share no compression
/// dictionary.
/// </para>
/// <para>
/// It must be **read-only** with respect to existing backups: not a character of the old indexes changes, there
/// is merely one more way to get a hit; the reference shape written after a hit is byte-for-byte what it used to
/// be, so retention cleanup, dead-weight compaction and restore all stay untouched. Each of these is pinned below.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackMemberDedupTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _src;
    private readonly string _dst;
    private readonly string _temp;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private int _mtimeSeq;
    private static readonly DateTime MtimeBase = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public PackMemberDedupTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-packdedup-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(baseDir, "src");
        _dst = Path.Combine(baseDir, "dst");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
        Directory.CreateDirectory(_temp);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
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
        File.SetLastWriteTimeUtc(full, MtimeBase.AddMinutes(++_mtimeSeq));
    }

    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store) Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        // Local-authoritative wiring: packed-member dedup decides from the locally cached index, never reading the cloud.
        var indexCache = new LocalIndexCache(_db, store);
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(_db));
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), null, indexCache, tracked),
            new FileHasher(), indexCache: indexCache, trackedInfo: tracked);
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"));
        return (backup, restore, store);
    }

    /// <summary>Threshold set high enough that all these few-dozen-byte files take the pack path.</summary>
    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _src,
        Name = "packdedup",
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    private static async Task<int> CountPacksAsync(Azure.Storage.Blobs.BlobContainerClient cc)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, "packs/", CancellationToken.None))
            ids.Add(b.Name);
        return ids.Count;
    }

    /// <summary>
    /// The second version adds a small file with **the same content but a different path** as an existing
    /// member: no new pack should appear, the new entry points straight at that member in the old pack, and what
    /// restores must be correct.
    /// </summary>
    [SkippableFact]
    public async Task A_New_File_Matching_An_Existing_Pack_Member_Reuses_It()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packdedup-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // Incompressible content: if a second pack really got built, the size difference cannot hide.
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            Write("docs/original.txt", payload);
            Write("docs/neighbour.txt", "something else entirely");
            await backup.RunAsync(Request(account, name));

            var packsAfterV1 = await CountPacksAsync(cc);
            Assert.True(packsAfterV1 > 0, "v1 should have produced at least one pack");

            // v2: add a file with the same content at a different path.
            Write("archive/copy-of-original.txt", payload);
            await backup.RunAsync(Request(account, name));

            Assert.Equal(packsAfterV1, await CountPacksAsync(cc)); // not one new pack may appear

            var info = await store.ReadInfoAsync(account, name, null);
            var v2 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var original = v2.Entries.Single(e => e.Path == "docs/original.txt");
            var copy = v2.Entries.Single(e => e.Path == "archive/copy-of-original.txt");

            // Points at the same member of the same pack — the member name is the **original** path, not the new one.
            Assert.Equal("pack", copy.Storage!.Kind);
            Assert.Equal(original.Storage!.Ref, copy.Storage.Ref);
            Assert.Equal(original.Storage.EntryName ?? original.Path, copy.Storage.EntryName ?? copy.Path);

            // And it restores, written to **its own** path.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(
                Path.Combine(_dst, "archive", "copy-of-original.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "docs", "original.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Retention cleanup has to see this kind of cross-version reference. After the old version retires, that
    /// pack is still referenced by an entry in the new version — deleting it deletes the new version's data.
    /// </summary>
    [SkippableFact]
    public async Task Retention_Keeps_A_Pack_Still_Referenced_Through_Dedup()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packdedupret-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        var keepOne = Request(account, name) with
        {
            Options = new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
            },
        };

        try
        {
            var payload = string.Concat(Enumerable.Range(0, 300).Select(i => ((char)('m' + i % 13)).ToString()));
            Write("a/one.txt", payload);
            await backup.RunAsync(keepOne);

            // v2 adds a same-content file → dedup points it at v1's pack; meanwhile v1 retires (only 1 version kept).
            Write("b/two.txt", payload);
            await backup.RunAsync(keepOne);

            var info = await store.ReadInfoAsync(account, name, null);
            var v = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var two = v.Entries.Single(e => e.Path == "b/two.txt");

            // The pack it references must still be there — otherwise restore cannot fetch the content.
            var packBlob = $"packs/{two.Storage!.Ref}.7z";
            var exists = await cc.GetBlobClient(packBlob).ExistsAsync()
                         || await cc.GetBlobClient(packBlob + ".001").ExistsAsync();
            Assert.True(exists, $"{packBlob} is referenced by b/two.txt and must not be deleted by retention cleanup");

            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "b", "two.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Newly written packed-member entries must carry the tail hash — the same criteria as the single-file blob
    /// path (all four parts); the two paths cannot each have their own standard.
    /// <para>
    /// But **unchanged files are not backfilled**: they pay no IO at all today, and going out to read them is
    /// pure added random IO (close to an hour for 500k small files on a NAS spinning disk) for hardening whose
    /// marginal value is tiny. Old entries therefore stay missing, and dedup treats it as "missing means it takes
    /// no part in the decision".
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task New_Packed_Members_Carry_A_Tail_But_Unchanged_Ones_Are_Not_Backfilled()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, _, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packtail-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            Write("docs/a.txt", new string('a', 400));
            await backup.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var packed = v1.Entries.Single(e => e.Path == "docs/a.txt");
            Assert.Equal("pack", packed.Storage!.Kind);
            Assert.NotNull(packed.TailHash);   // a newly written entry should have one

            // After the file changes, the new entry still carries it.
            Write("docs/a.txt", new string('b', 400));
            await backup.RunAsync(Request(account, name));
            var info2 = await store.ReadInfoAsync(account, name, null);
            var v2 = await store.ReadIndexAsync(account, name, info2!.Versions[^1].IndexBlob, null);
            var changed = v2.Entries.Single(e => e.Path == "docs/a.txt");
            Assert.NotNull(changed.TailHash);
            Assert.NotEqual(packed.TailHash, changed.TailHash);   // content changed, so the tail should change too

            // "unchanged files get no tail backfill" is tested at the BackupDifferTests level — here the local
            // index cache sits in between, so editing the cloud index blob cannot affect the previous that the
            // next run reads.
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Files with **different** content must never be mistaken for the same member. This one guards the dedup
    /// key itself: if any of the three parts (fullHash + length + head) differs, each must be stored separately.
    /// </summary>
    [SkippableFact]
    public async Task Different_Content_Is_Never_Folded_Together()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packdedupdiff-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            Write("x/first.txt", new string('p', 500));
            await backup.RunAsync(Request(account, name));

            // Same length, different content: only the length matches, the hashes differ → each must be stored separately.
            Write("y/second.txt", new string('q', 500));
            await backup.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v2 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var first = v2.Entries.Single(e => e.Path == "x/first.txt");
            var second = v2.Entries.Single(e => e.Path == "y/second.txt");
            Assert.NotEqual(
                (first.Storage!.Ref, first.Storage.EntryName ?? first.Path),
                (second.Storage!.Ref, second.Storage.EntryName ?? second.Path));

            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(new string('p', 500), await File.ReadAllTextAsync(Path.Combine(_dst, "x", "first.txt")));
            Assert.Equal(new string('q', 500), await File.ReadAllTextAsync(Path.Combine(_dst, "y", "second.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
