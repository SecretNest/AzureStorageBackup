using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Cross-pack dedup of packed members within one backup run. The cross-version path is covered by
/// <see cref="PackMemberDedupTests"/>; what gets pinned here is the **within-run** case: among the packs sealed
/// in this run, identical content must be packed only once.
/// <para>
/// Packing uses MaxPackMembers = 1 to force one member per pack, which makes "across packs" deterministic and
/// removes any guessing about the packing result.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackAliasDedupTests : IDisposable
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

    public PackAliasDedupTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-packalias-" + Guid.NewGuid().ToString("N"));
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

    /// <param name="deadWeightCompaction">
    /// Wires the real <see cref="DeadWeightCompactor"/> into retention cleanup (which is how Program.cs wires it
    /// in production, sharing the same <see cref="StagingArea"/> as the backup). Off by default: most cases only
    /// care about packing and restore, and wiring it in means every backup ends with an extra download/repack of the pack.
    /// </param>
    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store) Build(
        IFileCompressor? compressor = null, bool deadWeightCompaction = false)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var indexCache = new LocalIndexCache(_db, store);
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(_db));
        var compactor = deadWeightCompaction
            ? new DeadWeightCompactor(
                new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(),
                Path.Combine(_temp, "compact"), staging)
            : null;
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            compressor ?? new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor, indexCache, tracked),
            new FileHasher(), indexCache: indexCache, trackedInfo: tracked);
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"));
        return (backup, restore, store);
    }

    /// <summary>Threshold set high enough that every file takes the pack path; one member per pack makes "across packs" deterministic.</summary>
    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _src,
        Name = "packalias",
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000, MaxPackMembers = 1 },
        },
    };

    private static async Task<int> CountPacksAsync(Azure.Storage.Blobs.BlobContainerClient cc)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, "packs/", CancellationToken.None))
            ids.Add(b.Name);
        return ids.Count;
    }

    /// <summary>Unpacks a pack and lists which members the archive **actually** holds (directory segments
    /// included, in ordinal order).
    /// What the index says is one thing, how many members the archive really contains is another — for dedup and
    /// compaction only the latter counts.</summary>
    private async Task<List<string>> PackEntryNamesAsync(Azure.Storage.Blobs.BlobContainerClient cc, string packId)
    {
        var work = Path.Combine(_temp, "peek-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var first = await VolumeBlobIO.DownloadAsync(cc, $"packs/{packId}.7z", work, CancellationToken.None);
        var extracted = Path.Combine(work, "x");
        await new SevenZipCompressor().ExtractAsync(first, extracted, null, CancellationToken.None);
        return [.. Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(extracted, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(x => x, StringComparer.Ordinal)];
    }

    /// <summary>Total bytes a pack occupies in the container (all volumes included). Compare before and after
    /// compaction to tell whether the pack really got rewritten.</summary>
    private static async Task<long> PackBytesAsync(Azure.Storage.Blobs.BlobContainerClient cc, string packId)
    {
        long total = 0;
        await foreach (var b in cc.GetBlobsAsync(
            BlobTraits.None, BlobStates.None, $"packs/{packId}.7z", CancellationToken.None))
            total += b.Properties.ContentLength ?? 0;
        return total;
    }

    /// <summary>A deterministic pseudo-random letter string: barely compressible, so size differences cannot hide.
    /// The same seed yields the same string, different seeds necessarily yield different content.</summary>
    private static string Noise(int seed, int length)
    {
        var rnd = new Random(seed);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)('a' + rnd.Next(26));
        return new string(chars);
    }

    /// <summary>
    /// T1 + T2: three small files in **one run**, two of them with identical content, one member per pack.
    /// With dedup working there should be only two packs (not three), the second entry points at the first one's
    /// member, and both restore.
    /// <para>
    /// The second half of T2 gets pinned as well: the two entries share one archive member, but mtime and
    /// permissions are **their own**. Only the leader's bytes lie in the archive, yet the metadata must come from
    /// each entry itself (<c>RestoreOrchestrator</c>'s <c>ApplyMetadata(dest, entry)</c>). The shape of getting it
    /// wrong is "the alias restores carrying the leader's timestamp/permissions" — right content, wrong metadata,
    /// a restore that looks fine, but the next backup re-backs the whole file because its mtime changed, while
    /// wrong permissions are an outright security problem (a 0600 file restored as 0644).
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Same_Content_In_Different_Packs_Is_Stored_Once_Within_One_Run()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packalias-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // Incompressible content: if two packs really got built, the size difference cannot hide.
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            Write("a/first.txt", payload);              // leader (first in ordinal path order)
            Write("b/other.txt", "something else entirely");
            Write("c/second.txt", payload);             // alias

            // The metadata of the two paths must **differ**, otherwise "each correct" and "both took the leader's"
            // give the same result and the test measures nothing.
            // mtime is bumped by Write (leader = base+1min, alias = base+3min); permissions are pulled further apart here.
            var leaderSrc = Path.Combine(_src, "a", "first.txt");
            var aliasSrc = Path.Combine(_src, "c", "second.txt");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(leaderSrc, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // 0600
                File.SetUnixFileMode(
                    aliasSrc,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);                            // 0644
            }
            var leaderMtime = File.GetLastWriteTimeUtc(leaderSrc);
            var aliasMtime = File.GetLastWriteTimeUtc(aliasSrc);
            Assert.NotEqual(leaderMtime, aliasMtime);

            var run = await backup.RunAsync(Request(account, name));

            // Three files, one member per pack: without dedup that is 3 packs, with dedup 2.
            Assert.Equal(2, await CountPacksAsync(cc));

            // T8: an alias is still a **changed file**, it just does not occupy a pack. The accounting must not
            // drop it because of dedup — it really does have an entry in the index, and the user really did add
            // that file.
            Assert.Equal(3, run.ChangedFiles);

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var first = v1.Entries.Single(e => e.Path == "a/first.txt");
            var second = v1.Entries.Single(e => e.Path == "c/second.txt");

            // The reference shape must be byte-for-byte what RecordPack used to write: Kind=pack + the same Ref + the leader's EntryName.
            Assert.Equal("pack", second.Storage!.Kind);
            Assert.Equal(first.Storage!.Ref, second.Storage.Ref);
            Assert.Equal("a/first.txt", second.Storage.EntryName);

            // Both must restore to **their own** paths.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            var leaderDst = Path.Combine(_dst, "a", "first.txt");
            var aliasDst = Path.Combine(_dst, "c", "second.txt");
            Assert.Equal(payload, await File.ReadAllTextAsync(leaderDst));
            Assert.Equal(payload, await File.ReadAllTextAsync(aliasDst));

            // Metadata belongs to each of them: the alias must never end up with the leader's.
            Assert.Equal(leaderMtime, File.GetLastWriteTimeUtc(leaderDst));
            Assert.Equal(aliasMtime, File.GetLastWriteTimeUtc(aliasDst));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(leaderDst));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    File.GetUnixFileMode(aliasDst));
            }
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Different content must never be merged — not even at the same length. This one is the reverse safety net
    /// on the dedup criteria: getting it wrong means the index points at someone else's content and restore hands
    /// back wrong data.
    /// </summary>
    [SkippableFact]
    public async Task Different_Content_Of_The_Same_Length_Is_Never_Merged()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packaliasdiff-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            Write("a/first.txt", new string('x', 300));
            Write("c/second.txt", new string('y', 300));   // same length, different content

            await backup.RunAsync(Request(account, name));

            Assert.Equal(2, await CountPacksAsync(cc));   // each packed separately

            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(new string('x', 300), await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(new string('y', 300), await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// T6: the leader is rewritten inside the compression window → it gets kicked out of that pack and
    /// reprocessed under a new hash, so the content it finally stores **no longer equals** the alias's content.
    /// At that point the alias must never point at it (that would make the index point at someone else's content
    /// and restore hand back wrong data); it has to be backed up again on its own.
    /// <para>
    /// Both files restoring to the content each should have is this red line holding.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task An_Alias_Is_Rebuilt_When_Its_Leader_Changes_During_Compression()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var account = AzuriteAccount();
        var name = RandomName("packaliasorphan-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            const string mutated = "leader got rewritten while it was being compressed";
            Write("a/first.txt", payload);       // leader
            Write("c/second.txt", payload);      // alias

            // Swap the leader's content after compression: revalidation notices it changed, kicks it out of that
            // pack and reprocesses it under a new hash.
            var (backup, restore, store) = Build(
                new MutatingAfterCompressCompressor(new SevenZipCompressor(), _src, "a/first.txt", mutated));

            await backup.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var first = v1.Entries.Single(e => e.Path == "a/first.txt");
            var second = v1.Entries.Single(e => e.Path == "c/second.txt");

            // The content identities of the two entries must have parted ways — the alias must not still hang off the leader's new content.
            Assert.NotEqual(first.FullHash, second.FullHash);

            // The decisive one: what restores must be each file's own content.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(mutated, await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>Rewrites the target member's content **after** compression, simulating "the file changed while it
    /// was being processed" (§9, PRD special note D).
    /// The grouped path hashes first and compresses second, so this hooks in after CompressAsync, and that is how
    /// revalidation discovers the content changed.
    /// Same technique as BackupOrchestratorTests.MutatingCompressor, covering only the half needed here.</summary>
    private sealed class MutatingAfterCompressCompressor(
        IFileCompressor inner, string rootPath, string relPath, string newContent) : IFileCompressor
    {
        private int _fired;

        public async Task<CompressionResult> CompressAsync(
            CompressionRequest request, CancellationToken ct = default)
        {
            var result = await inner.CompressAsync(request, ct);
            if (request.Entries.Contains(relPath) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                var full = Path.Combine(rootPath, relPath.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllText(full, newContent);
                File.SetLastWriteTimeUtc(full, File.GetLastWriteTimeUtc(full).AddSeconds(7));
            }
            return result;
        }

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
            => inner.CompressStreamAsync(request, writeSource, ct);

        public Task ExtractAsync(
            string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
    }

    /// <summary>
    /// T3 — the most important case of this feature. After the file at the leader's **path** is deleted, the
    /// alias must still restore.
    /// <para>
    /// At that point the entryName in liveByPack is supplied by the alias entry alone (RetentionCleaner groups by
    /// EntryName, not by fullHash), so the pack is not deleted, the member does not die, and it is still there in
    /// the extraction directory. Every link in that chain has to hold for the alias to survive — and it is very
    /// easy for some future "let's just group by hash while we're here" refactor to quietly snap it.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task An_Alias_Survives_After_Its_Leader_Path_Is_Deleted()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packaliasdel-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        // Keep only the newest version: v1 retires, so the pack can only be pinned by the alias entry in v2.
        var keepOne = Request(account, name) with
        {
            Options = new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000, MaxPackMembers = 1 },
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
            },
        };

        try
        {
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            Write("a/first.txt", payload);       // leader
            Write("c/second.txt", payload);      // alias
            await backup.RunAsync(keepOne);

            var packsAfterV1 = await CountPacksAsync(cc);
            Assert.Equal(1, packsAfterV1);       // identical content was packed only once

            // v2: delete the leader's path. From then on that member in the pack is referenced only by the alias entry.
            File.Delete(Path.Combine(_src, "a", "first.txt"));
            await backup.RunAsync(keepOne);

            // Not one pack may go missing — deleting it deletes c/second.txt's data.
            Assert.Equal(packsAfterV1, await CountPacksAsync(cc));

            var info = await store.ReadInfoAsync(account, name, null);
            // This test's premise is that v1 has already retired (MaxVersions = 1), which is what makes the alias
            // entry the pack's only pin. Without asserting it, if v1 somehow did not retire, "the pack was not
            // deleted" would pass on the strength of the leader's own old entry in v1 — the test becomes an
            // illusion that measures nothing and never verifies that an alias pins a pack at all.
            Assert.Single(info!.Versions);
            var v2 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            Assert.DoesNotContain(v2.Entries, e => e.Path == "a/first.txt");
            var second = v2.Entries.Single(e => e.Path == "c/second.txt");
            // The member name is still the **original** path, which no longer exists — restore fetches from the archive by it.
            Assert.Equal("a/first.txt", second.Storage!.EntryName);

            // The decisive one: the content is still there and it restores.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "a", "first.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// T7: two entries point at the same pack member, and check must report both healthy.
    /// <para>
    /// BackupChecker looks up actual[entryName] entry by entry; both look up the same item, so of course the
    /// content matches. And the premise "the member count the archive yields == the member count enumerated" is
    /// unaffected too — aliases never enter the archive.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Check_Reports_Both_Entries_Healthy()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, _, _) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packaliaschk-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            Write("a/first.txt", payload);
            Write("c/second.txt", payload);
            await backup.RunAsync(Request(account, name));

            var checker = new BackupChecker(
                factory, new BackupInfoStore(factory, new SevenZipArchiveCodec()),
                new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"));
            var report = await checker.CheckAsync(account, name, null, null, new CheckOptions());

            // Not one corruption may appear, and both entries must show up in the findings.
            Assert.True(report.Ok);
            Assert.Empty(report.CorruptedPaths);
            Assert.Contains(report.Findings, f => f.Path == "a/first.txt");
            Assert.Contains(report.Findings, f => f.Path == "c/second.txt");
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The shape seen most often in production: two identical files sitting in **the same directory**
    /// (downloaded twice, or copied and renamed).
    /// <para>
    /// Every alias case above crosses directories, and the same-directory case really did change behaviour: the
    /// two files used to be two members of one solid archive (7z's dictionary matching across members already
    /// cost almost no extra bytes), whereas now the second becomes an alias and the archive holds only one
    /// member. Logically equivalent — two index entries, one copy of the content — but "equivalent" was reasoned,
    /// never run.
    /// </para>
    /// <para>
    /// One member per pack (MaxPackMembers = 1), so without dedup that is two packs; with dedup it is one pack
    /// holding exactly one member. Both numbers are asserted: on pack count alone this would still pass if the
    /// behaviour ever became "two members in one pack".
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Same_Content_In_The_Same_Directory_Is_Stored_Once()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packaliassamedir-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            var payload = Noise(20260807, 4000);
            Write("d/one.txt", payload);        // leader (first in ordinal path order within the directory)
            Write("d/two.txt", payload);        // alias, same directory

            await backup.RunAsync(Request(account, name));

            Assert.Equal(1, await CountPacksAsync(cc));

            var info = await store.ReadInfoAsync(account, name, null);
            var packId = Assert.Single(info!.Packs.Keys);
            // The archive really holds only one member — that is the direct evidence of "stored only once".
            Assert.Equal(["d/one.txt"], await PackEntryNamesAsync(cc, packId));

            var v1 = await store.ReadIndexAsync(account, name, info.Versions[^1].IndexBlob, null);
            var one = v1.Entries.Single(e => e.Path == "d/one.txt");
            var two = v1.Entries.Single(e => e.Path == "d/two.txt");
            Assert.Equal("pack", two.Storage!.Kind);
            Assert.Equal(one.Storage!.Ref, two.Storage.Ref);
            Assert.Equal("d/one.txt", two.Storage.EntryName);

            // Both must restore to their own paths.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "d", "one.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "d", "two.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The end-to-end version of T5, and the nastiest chain in this feature: **an alias produced by this
    /// feature** → triggering dead-weight compaction that **rewrites** that pack → the alias still restoring the
    /// correct content afterwards.
    /// <para>
    /// How this differs from the cases in <c>DeadWeightCompactorTests</c>: there the pack and liveByPack are
    /// hand-built historical shapes, and no restore was ever run after compaction. Here it is real from end to
    /// end — the pack is sealed by this run's backup, the alias is produced by this feature, liveByPack is
    /// grouped by <c>RetentionCleaner</c> scanning the index itself, compaction is really triggered by the
    /// threshold, and a real restore runs at the end.
    /// </para>
    /// <para>
    /// The combination chosen is the nastiest one, T3 stacked on compaction: v2 deletes the leader's **path**
    /// together with three pieces of dead weight, so that
    /// <list type="bullet">
    /// <item>the pack's only surviving member <c>a/leader.txt</c> is pinned by the alias entry
    /// <c>c/alias.txt</c> alone (liveByPack groups by EntryName);</item>
    /// <item><c>hasAbsentLocal</c> is true (a/leader.txt is gone locally), so compaction takes the **download and
    /// reassemble** path: download the old pack → extract → put only the surviving members into the compose directory → recompress over the same packId.</item>
    /// </list>
    /// Every link in this chain works in theory, but it had never actually been run. Snap any one of them
    /// (grouping switched to fullHash, say, or giving up on the whole pack when local probing fails) and the
    /// alias's data silently disappears during one automatic cleanup.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task An_Alias_Survives_Dead_Weight_Compaction_That_Rewrites_Its_Pack()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        // Wire in the real dead-weight compactor (which is how production DI wires it).
        var (backup, restore, store) = Build(deadWeightCompaction: true);
        var account = AzuriteAccount();
        var name = RandomName("packaliascompact-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        // Default MaxPackMembers (not 1): the whole a/ directory goes into one pack, which is the only way the
        // leader can share a pack with the dead weight — with one member per pack, dead weight and surviving
        // members sit in separate packs and compaction can never happen.
        // MaxVersions = 1: v1 retires, which is what makes dead weight appear (it only grows when a version retires).
        // DeadWeightThreshold stays at the default 0.30 and AllowRepackDownload at the default true (the
        // download-and-reassemble path needs it).
        var request = Request(account, name) with
        {
            Options = new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
            },
        };

        try
        {
            var payload = Noise(1, 400);
            Write("a/leader.txt", payload);       // leader: after v2 it is the pack's only surviving member
            Write("a/dead1.txt", Noise(2, 20_000));   // three future pieces of dead weight, all different (otherwise they would dedup against each other)
            Write("a/dead2.txt", Noise(3, 20_000));
            Write("a/dead3.txt", Noise(4, 20_000));
            Write("c/alias.txt", payload);        // alias, another directory, not packed this run

            await backup.RunAsync(request);

            var infoV1 = await store.ReadInfoAsync(account, name, null);
            var packId = Assert.Single(infoV1!.Packs.Keys);
            // Premise check: five files, one pack, four members in it (the alias is not packed).
            Assert.Equal(1, await CountPacksAsync(cc));
            Assert.Equal(4, infoV1.Packs[packId].Members.Count);
            Assert.Equal(
                ["a/dead1.txt", "a/dead2.txt", "a/dead3.txt", "a/leader.txt"],
                await PackEntryNamesAsync(cc, packId));
            var v1 = await store.ReadIndexAsync(account, name, infoV1.Versions[^1].IndexBlob, null);
            // Premise check: c/alias.txt really is **an alias produced by this feature**, not two copies that happened to be stored separately.
            Assert.Equal("a/leader.txt", v1.Entries.Single(e => e.Path == "c/alias.txt").Storage!.EntryName);
            var bytesBefore = await PackBytesAsync(cc, packId);

            // v2: delete the whole a/ directory. The leader's path disappears along with the three pieces of dead
            // weight, leaving a/leader.txt as the pack's only still-referenced member — and the only thing
            // referencing it is the alias entry.
            Directory.Delete(Path.Combine(_src, "a"), recursive: true);
            var run2 = await backup.RunAsync(request);

            // Did compaction really trigger? Pinned item by item, not by "it ran without erroring":
            // ① v1 must really retire, or there is no dead weight (no retirement → the three dead pieces stay referenced by v1 → ratio = 0 → no trigger).
            Assert.Equal(1, run2.Cleanup.RetiredVersions);
            var infoV2 = await store.ReadInfoAsync(account, name, null);
            Assert.Single(infoV2!.Versions);
            // ② The pack is still there (the alias pins it), and it is the **same packId** — compaction rewrites in place, it does not create a new one.
            Assert.Contains(packId, infoV2.Packs.Keys);
            // ③ The member table shrinks from 4 to 1 and dead weight goes to zero: only RecompactAsync completing
            //    the newSizes.Count > 0 branch can write that (the give-up branch only updates DeadBytes and
            //    leaves the member table untouched).
            Assert.Single(infoV2.Packs[packId].Members);
            Assert.Equal(0, infoV2.Packs[packId].DeadBytes);
            Assert.Equal(payload.Length, infoV2.Packs[packId].OriginalBytes);
            // ④ The cloud archive itself was rewritten: only the surviving member is left and the size shrank
            //    sharply (three 20,000-byte pseudo-random texts are gone).
            Assert.Equal(["a/leader.txt"], await PackEntryNamesAsync(cc, packId));
            var bytesAfter = await PackBytesAsync(cc, packId);
            Assert.True(bytesAfter < bytesBefore / 2,
                $"pack should have been rewritten much smaller, was {bytesBefore} → {bytesAfter}");
            // ⑤ It really took the **download and reassemble** path: at compaction time there was no local
            //    a/leader.txt to use as a repair source, so the only possible source was the copy extracted from
            //    the downloaded old pack.
            Assert.False(Directory.Exists(Path.Combine(_src, "a")));

            // The decisive one: in a pack that compaction rewrote, the alias still restores the correct content.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "alias.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "a", "leader.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The orphan rerun splits into two groups by compression mode (<c>orphanAliases.ToLookup(… DontCompress …)</c>).
    /// In every case so far <c>DontCompress</c> was null, <c>ToLookup</c> always produced exactly one group, and
    /// half of that <c>foreach</c> had never executed.
    /// <para>
    /// Here the orphaned aliases **straddle both compression modes**: the leader is rewritten inside the
    /// compression window → the two aliases hanging off it are orphaned together, one matching the
    /// do-not-compress rule and one not. Both groups have to be reached, each with the right mode — a pack can
    /// only have one mode, and mixing them would make the rule effectively nonexistent for packed files.
    /// </para>
    /// <para>
    /// How we confirm **both** groups were reached, and separately: the two aliases land in **different** packs,
    /// those two packs have <c>PackInfo.StoreOnly</c> one true and one false, and their cloud sizes are one large
    /// and one small (the store-only pack is about the original file size, the compressed one two to three orders
    /// of magnitude smaller). If the split were removed and both aliases went through a single ProcessPackAsync,
    /// their compression mode would be identical and these three assertions would all go red together.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Orphan_Aliases_Are_Rerun_On_Both_Sides_Of_The_Dont_Compress_Rule()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var account = AzuriteAccount();
        var name = RandomName("packaliasstoreonly-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // Highly compressible and big enough: the size gap between the store-only pack and the compressed one
            // is therefore visible to the naked eye, and whether the mode reached 7z needs no reasoning.
            const int filler = 200_000;
            var payload = new string('q', filler);
            const string mutated = "leader got rewritten while it was being compressed";
            Write("a/first.txt", payload);        // leader (first in ordinal path order)
            Write("c/second.txt", payload);       // alias one: does not match the rule → compressed pack
            Write("n/third.log", payload);        // alias two: matches *.log → store-only pack

            // Rewrite the leader after compression: revalidation notices the change → overrides records the new hash → both aliases are orphaned.
            var (backup, restore, store) = Build(
                new MutatingAfterCompressCompressor(new SevenZipCompressor(), _src, "a/first.txt", mutated));

            await backup.RunAsync(Request(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000, MaxPackMembers = 1 },
                    DontCompress = new IgnoreRuleSet(["*.log"]),
                },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            var index = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var leader = index.Entries.Single(e => e.Path == "a/first.txt");
            var second = index.Entries.Single(e => e.Path == "c/second.txt");
            var third = index.Entries.Single(e => e.Path == "n/third.log");

            // Premise check: both aliases really were orphaned (neither points at the leader's new content) and each stored its own copy.
            Assert.NotEqual(leader.FullHash, second.FullHash);
            Assert.Equal(second.FullHash, third.FullHash);
            Assert.Equal("pack", second.Storage!.Kind);
            Assert.Equal("pack", third.Storage!.Kind);
            // Orphaned aliases no longer dedup against each other (a trade-off stated in the design), so they necessarily sit in separate packs.
            Assert.NotEqual(second.Storage.Ref, third.Storage.Ref);

            // Both groups were reached, each with its own compression mode.
            Assert.False(info.Packs[second.Storage.Ref].StoreOnly);
            Assert.True(info.Packs[third.Storage.Ref].StoreOnly);

            // The mode really reached 7z: the store-only pack is about the original file size, the compressed one far smaller.
            var compressedBytes = await PackBytesAsync(cc, second.Storage.Ref);
            var storedBytes = await PackBytesAsync(cc, third.Storage.Ref);
            Assert.True(storedBytes > filler * 0.9,
                $"store-only pack should be about the original size, was {storedBytes}");
            Assert.True(compressedBytes < filler / 10,
                $"compressed pack should be far smaller than the original, was {compressedBytes}");

            // The decisive one: all three paths restore the content each should have.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(mutated, await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "n", "third.log")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The case for the ordering argument at the packing decision point: "if the leader hits an existing pack,
    /// later files with the same content hit that same first tier through the same _packMembers table and the same
    /// four-part criteria, and never reach the alias table at all." — which has so far held on reasoning alone.
    /// <para>
    /// Two backup runs: v1 stores some content; v2 adds **two** new files with that same content. Both should hit
    /// cross-version dedup and point at the member in v1's pack, producing no new pack and never forming the shape
    /// "alias pointing at this run's leader".
    /// </para>
    /// <para>
    /// The criterion is <c>EntryName</c>: on a cross-version dedup hit it is v1's path (<c>a/first.txt</c>);
    /// if the two tiers were swapped in order, or the alias table got in first, the second and third entries would
    /// point at this run's <c>c/second.txt</c> and there would be one extra pack. Neither difference is harmless —
    /// references piling onto this run's new pack means the old pack has to be rewritten the moment it retires,
    /// and <c>LocalDedupResolver</c> deliberately piles references onto the old pack precisely to avoid that.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Later_Duplicates_Hit_Cross_Version_Dedup_Instead_Of_The_Alias_Table()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packaliascrossver-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            var payload = Noise(777, 4000);
            Write("a/first.txt", payload);
            await backup.RunAsync(Request(account, name));

            var packsAfterV1 = await CountPacksAsync(cc);
            Assert.Equal(1, packsAfterV1);
            var infoV1 = await store.ReadInfoAsync(account, name, null);
            var packId = Assert.Single(infoV1!.Packs.Keys);

            // v2: two **new** files with the same content. Both should point at the member in v1's pack.
            Write("c/second.txt", payload);
            Write("d/third.txt", payload);
            await backup.RunAsync(Request(account, name));

            // Not one new pack may appear — both are caught at the cross-version tier and never reach packing.
            Assert.Equal(packsAfterV1, await CountPacksAsync(cc));

            var infoV2 = await store.ReadInfoAsync(account, name, null);
            Assert.Equal([packId], infoV2!.Packs.Keys);
            var v2 = await store.ReadIndexAsync(account, name, infoV2.Versions[^1].IndexBlob, null);
            foreach (var path in new[] { "a/first.txt", "c/second.txt", "d/third.txt" })
            {
                var storage = v2.Entries.Single(e => e.Path == path).Storage!;
                Assert.Equal("pack", storage.Kind);
                Assert.Equal(packId, storage.Ref);
                // The key: the member name is **v1's** path. If the alias table got in first, the last two would point at c/second.txt.
                Assert.Equal("a/first.txt", storage.EntryName);
            }

            // All three restore.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "d", "third.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
