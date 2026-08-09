using System.Net.Sockets;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Zero-byte files and empty directories walk every storage path and then come back restored unchanged. Empty files
/// have several suspicious spots on this pipeline, and each of them is different:
/// <list type="bullet">
/// <item>the pack path treats it as an empty member inside the archive — 7z can store it, but restore has to actually create the file rather than "skip it, there is no content";</item>
/// <item>the single-file blob path has to feed an **empty stdin** to <c>7z -si</c>;</item>
/// <item>raw passthrough bypasses 7z, pushes a 0-byte blob straight up and pulls it back unchanged;</item>
/// <item>an encrypted single file is yet another one, where after header encryption not even the entry names can be listed.</item>
/// </list>
/// An empty file is not "no content", it is "a file whose content has length zero" — after a restore the difference between the two is whether the file exists at all.
/// Same for an empty directory: it travels through the index's separate EmptyDirs list rather than content storage, and must be restored along with everything else.
/// <para>
/// Deduplication always goes through <see cref="LocalDedupResolver"/> (locally authoritative, no cloud HEAD), and
/// identical content within one batch is coordinated by its reservation table. There used to be a fallback wiring of
/// "ask the cloud when there is no local index", and this file once tested both; it has since been deleted along with that path.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class EmptyFileRoundTripTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _src;
    private readonly string _dst;
    private readonly string _temp;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public EmptyFileRoundTripTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-empty-" + Guid.NewGuid().ToString("N"));
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

    private void WriteEmpty(string rel)
    {
        var full = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, []);
    }

    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, TestLocalAuthority Authority) Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var authority = new TestLocalAuthority(_db, store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(),
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"));
        return (backup, restore, authority);
    }

    /// <summary>
    /// Classification looks only at length and rules: <c>DontGroup</c> forces a single-file blob regardless of length,
    /// and stacking <c>DontCompress</c> on top of it with no password lands in raw passthrough; anything matching no rule is grouped into a pack by default. One backup therefore walks all four paths.
    /// </summary>
    private static BackupEngineOptions EngineOptions() => new()
    {
        DontGroup = new IgnoreRuleSet(["solo/**", "raw/**"]),
        DontCompress = new IgnoreRuleSet(["raw/**"]),
    };

    [SkippableTheory]
    [InlineData(null)]       // plaintext (raw passthrough is only reachable without a password)
    [InlineData("pw-123")]   // encrypted
    public async Task Zero_Byte_Files_And_Empty_Dirs_Survive_Every_Storage_Path(string? password)
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, _) = Build();
        var account = AzuriteAccount();
        var name = RandomName("empty-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteEmpty("packed/zero.txt");    // → an empty member inside a pack
            WriteEmpty("solo/zero.bin");      // → single-file blob, empty stdin fed to 7z -si
            WriteEmpty("raw/zero.dat");       // → raw passthrough without a password; falls back to an encrypted single file with one
            // Put a neighbour with real content in the same directory: an empty member must not spoil the whole crate, nor cost the neighbour its content.
            var neighbour = new string('n', 1_000);
            File.WriteAllText(Path.Combine(_src, "packed", "neighbour.txt"), neighbour);
            // An empty directory travels through the index's separate EmptyDirs list, not content storage — it is
            // tested together with the empty files to make sure no special handling of "zero length" swallowed it too.
            Directory.CreateDirectory(Path.Combine(_src, "hollow", "deeper"));

            var result = await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "empty-files",
                Password = password,
                Options = EngineOptions(),
            });

            Assert.Equal(4, result.NewFiles);

            await restore.RunAsync(new RestoreRequest
            {
                Account = account,
                Container = name,
                TargetRoot = _dst,
                Password = password,
            });

            foreach (var rel in new[] { "packed/zero.txt", "solo/zero.bin", "raw/zero.dat" })
            {
                var path = Path.Combine(_dst, rel.Replace('/', Path.DirectorySeparatorChar));
                // Existence is the crux here: if an empty file is taken for "no content" and skipped entirely, the
                // restored tree comes out one file short while every byte-comparing assertion passes happily.
                Assert.True(File.Exists(path), $"{rel} was not restored");
                var bytes = await File.ReadAllBytesAsync(path);
                Assert.True(bytes.Length == 0,
                    $"{rel} came back with {bytes.Length} byte(s) after the restore; first 8: "
                    + Convert.ToHexString(bytes.AsSpan(0, Math.Min(8, bytes.Length))));
            }

            Assert.Equal(neighbour, await File.ReadAllTextAsync(
                Path.Combine(_dst, "packed", "neighbour.txt")));
            Assert.True(Directory.Exists(Path.Combine(_dst, "hollow", "deeper")), "the empty directory was not restored");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// An empty file must not occupy anything in the cloud. It has no content, yet it used to be compressed into a
    /// 7z archive **larger than the original** (0 bytes → 131 bytes), take up a content-addressed address, and go through one upload, one download and one extraction.
    /// <para>
    /// This is also the root of that race: every empty file has the same fullHash, so they all crowd onto the same
    /// data/{hash}, while "compressed into an archive" and "raw passthrough" put completely different bytes at that
    /// address — whoever finishes uploading first decides the raw flag recorded in the later arrival's index, and the
    /// restore that does not match writes the archive itself out as the file content. Do not upload, and this whole class of problem ceases to exist.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Empty_Files_Cost_Nothing_In_The_Cloud()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, _) = Build();
        var account = AzuriteAccount();
        var name = RandomName("emptycost-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteEmpty("a/zero1.txt");
            WriteEmpty("b/zero2.bin");
            WriteEmpty("solo/zero3.dat");
            Directory.CreateDirectory(Path.Combine(_src, "hollow"));

            var result = await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "empty-only",
                Options = EngineOptions(),
            });

            Assert.Equal(3, result.NewFiles);
            Assert.Equal(0, result.UploadedBytes);

            // There must be no data blob or pack in the container — only the indexes and the info file.
            var stored = new List<string>();
            await foreach (var b in container.GetBlobsAsync())
                stored.Add(b.Name);
            Assert.DoesNotContain(stored, n => n.StartsWith("data/", StringComparison.Ordinal));
            Assert.DoesNotContain(stored, n => n.StartsWith("packs/", StringComparison.Ordinal));

            // And it still restores all the same.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            foreach (var rel in new[] { "a/zero1.txt", "b/zero2.bin", "solo/zero3.dat" })
            {
                var path = Path.Combine(_dst, rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"{rel} was not restored");
                Assert.Empty(await File.ReadAllBytesAsync(path));
            }
            Assert.True(Directory.Exists(Path.Combine(_dst, "hollow")), "the empty directory was not restored");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Empty files in old backups carry a storage reference (back then they were compressed and uploaded like
    /// everything else). Those entries must **clean themselves up** on the next backup rather than wait for the user to touch the file.
    /// <para>
    /// Left unfixed they never get better: an empty file that never changes (.gitkeep, __init__.py, lock files…) is
    /// judged Unchanged every round, and Unchanged carries the previous version's Storage into the new index
    /// unchanged (BackupDiffer.Unchanged → CarriedStorage). If that reference recorded the wrong raw flag back then,
    /// it is handed down generation after generation, and the user has no reason whatsoever to touch a file that has never changed.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Inherited_Storage_Refs_On_Empty_Files_Are_Dropped_On_The_Next_Backup()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var (backup, restore, authority) = Build();
        var account = AzuriteAccount();
        var name = RandomName("inherit-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteEmpty("packed/zero.txt");
            File.WriteAllText(Path.Combine(_src, "packed", "other.txt"), new string('o', 2_000));
            await backup.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _src,
                Name = "inherit", Options = EngineOptions(),
            });

            // Turn v1 into "what an old backup looked like": stuff a storage reference into the empty file's entry.
            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var index = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var donor = index.Entries.Single(e => e.Path == "packed/other.txt").Storage;
            Assert.NotNull(donor);
            Assert.Null(index.Entries.Single(e => e.Path == "packed/zero.txt").Storage); // the new code never gives one in the first place

            var tampered = new VersionIndex
            {
                Version = index.Version,
                EmptyDirs = index.EmptyDirs,
                Entries = [.. index.Entries.Select(e => e.Path == "packed/zero.txt"
                    ? e with { Storage = donor with { EntryName = "packed/zero.txt" } }
                    : e)],
            };
            await store.WriteIndexAsync(account, name, v1.Version, tampered, null);
            // The local cache has to be changed too: a backup reading the previous version's index only honours the
            // local copy, so changing the cloud one alone changes nothing.
            // And this is exactly the real shape of an "old backup" — that entry with a storage reference was written into the local cache just like this back then.
            await authority.IndexCache.PutAsync(
                account.Id, name, v1.Version, info.Backup.CreatedAt.UtcTicks, tampered);

            // Not one byte of the source file is touched → diff judges Unchanged, which is exactly the path where inheriting it forever is easiest.
            await backup.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _src,
                Name = "inherit", Options = EngineOptions(),
            });

            var info2 = await store.ReadInfoAsync(account, name, null);
            var v2 = info2!.Versions[^1];
            Assert.NotEqual(v1.Version, v2.Version);
            var index2 = await store.ReadIndexAsync(account, name, v2.IndexBlob, null);
            var healed = index2.Entries.Single(e => e.Path == "packed/zero.txt");
            Assert.Null(healed.Storage);
            Assert.Equal(0, healed.Length);

            // And it still restores after healing itself.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            var dest = Path.Combine(_dst, "packed", "zero.txt");
            Assert.True(File.Exists(dest), "the self-healed empty file was not restored");
            Assert.Empty(await File.ReadAllBytesAsync(dest));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// **Non-empty** files with identical content in the same batch that the rules assign to different storage shapes.
    /// The store-only one goes through raw passthrough (bare bytes), the other is compressed into a 7z archive —
    /// completely different bytes, yet the same fullHash, so both point at the same data/{hash} address. Without
    /// in-batch coordination two concurrent tasks each claim that same empty slot and each upload; the later write is
    /// skipped by UploadIfMissing while the two index entries each record their own raw flag: one of them necessarily
    /// disagrees with the bytes actually lying in the blob, and restore writes the archive itself out as the file content.
    /// <para>
    /// Coordination is done by LocalDedupResolver's reservation table (a later arrival with the same content waits for the first uploader and inherits its ref/raw/volume count).
    /// What this case guards is that the table also works under "same content, different shapes".
    /// </para>
    /// <para>
    /// Honestly: this case **failed** to reproduce the bug before in-batch coordination was added (backing the fix out
    /// and running 6 rounds stayed green). The reason is now clear — raw is a copy and 7z is compression, and with
    /// non-empty content the copy always lands first, so by the time the compressing one goes to resolve it can
    /// already see it, dedup hits, raw=true is inherited, and the two entries naturally agree. In other words the
    /// correctness here has been resting on the timing gap of "copying is faster than compressing" rather than on
    /// design — splitting makes store-only fall back to 7z, and a small file with a high compression ratio makes
    /// compression as fast as copying (the empty file is the extreme case, see the previous test), so the assumption can stop holding at any moment.
    /// So what this one guards is the invariant itself: **whatever shape the same content gets assigned, it must end
    /// up pointing at the same blob, with a consistent raw flag**. It is a regression guardrail, not a reproducer for that race.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Same_Content_In_Different_Storage_Shapes_Agrees_On_One_Blob()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, _) = Build();
        var account = AzuriteAccount();
        var name = RandomName("shapes-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Incompressible content: the byte difference between raw and the archive is therefore obvious, and it is plain at a glance which one displaced the other.
            var payloads = new List<byte[]>();
            for (var i = 0; i < 6; i++)
            {
                var buf = new byte[40_000];
                new Random(1000 + i).NextBytes(buf);
                payloads.Add(buf);
                Directory.CreateDirectory(Path.Combine(_src, "solo"));
                Directory.CreateDirectory(Path.Combine(_src, "raw"));
                // The same content, one copy through the 7z single-file blob path, one through raw passthrough.
                await File.WriteAllBytesAsync(Path.Combine(_src, "solo", $"c{i}.bin"), buf);
                await File.WriteAllBytesAsync(Path.Combine(_src, "raw", $"c{i}.bin"), buf);
            }

            await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "shapes",
                Options = EngineOptions(),
            });

            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });

            for (var i = 0; i < payloads.Count; i++)
            {
                foreach (var dir in new[] { "solo", "raw" })
                {
                    var path = Path.Combine(_dst, dir, $"c{i}.bin");
                    Assert.True(File.Exists(path), $"{dir}/c{i}.bin was not restored");
                    var got = await File.ReadAllBytesAsync(path);
                    Assert.True(payloads[i].SequenceEqual(got),
                        $"{dir}/c{i}.bin came back with bytes that do not match the source (length {got.Length}, expected {payloads[i].Length})");
                }
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
