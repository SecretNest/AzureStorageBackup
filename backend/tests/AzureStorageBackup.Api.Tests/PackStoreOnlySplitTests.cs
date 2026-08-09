using System.Net.Sockets;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The "do not compress" rule has to apply to **packed** small files too.
/// <para>
/// <c>CompressPackAsync</c> used to hard-code storeOnly to false, so the rule only affected single-file blobs:
/// jpg/mp4/already-compressed archives under the threshold in a directory still got chewed through by
/// <c>-mx9</c>, pure wasted CPU. The planner now splits one directory into two packs by compressibility, and
/// the compression mode rides along with the pack all the way down to 7z.
/// </para>
/// <para>
/// Asserting on size rather than on arguments: the content is a highly compressible repeat of one character, so
/// the archives from <c>-mx0</c> and <c>-mx9</c> differ by three orders of magnitude and never sit near a
/// boundary. A pack always goes through 7z (multiple members), so unlike the single-file path this needs no
/// encryption to be observable — an unencrypted store-only **single file** takes the raw passthrough
/// (CopyRawAsync) and never touches 7z.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackStoreOnlySplitTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <summary>Highly compressible: -mx9 leaves a few hundred bytes, -mx0 keeps all 200,000 as they are.</summary>
    private const int Filler = 200_000;

    private readonly string _base;
    private readonly string _src;
    private readonly string _packSrc;
    private readonly string _local;
    private readonly string _temp;

    public PackStoreOnlySplitTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-packstore-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_base, "src");
        _packSrc = Path.Combine(_base, "packsrc");
        _local = Path.Combine(_base, "local");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_packSrc);
        Directory.CreateDirectory(_local);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
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

    private StagingArea Staging() =>
        new(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);

    private static void Write(string root, string rel, string content)
    {
        var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static async Task<long> SizeOfAsync(BlobContainerClient container, string blobRef) =>
        (await container.GetBlobClient(blobRef).GetPropertiesAsync()).Value.ContentLength;

    // ---- Case 1: a brand new backup ----

    [SkippableFact]
    public async Task A_Mixed_Directory_Is_Split_Into_A_Compressed_Pack_And_A_Store_Only_Pack()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var authority = new TestLocalAuthority(store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, Staging(),
            new RetentionCleaner(
                factory, store, new RetentionEvaluator(),
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);

        var account = AzuriteAccount();
        var name = RandomName("packsplit-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // One directory, two fates. Different content: identical content would be folded into a single copy
            // by content addressing / member dedup, leaving only one path left to verify.
            Write(_src, "d/keep.log", new string('a', Filler));
            Write(_src, "d/comp.txt", new string('b', Filler));

            await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "packsplit",
                Options = new BackupEngineOptions
                {
                    // Threshold raised so both 200,000-byte files take the grouped-pack path instead of single-file blobs.
                    Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                    DontCompress = new IgnoreRuleSet(["*.log"]),
                },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            Assert.Equal(2, info!.Packs.Count);

            var stored = Assert.Single(info.Packs.Values, p => p.StoreOnly);
            var compressed = Assert.Single(info.Packs.Values, p => !p.StoreOnly);

            // The compression mode really reached 7z: the store-only pack is almost exactly the original file
            // size, the compressed one three orders of magnitude smaller.
            var storedSize = await SizeOfAsync(container, stored.Blob);
            var compressedSize = await SizeOfAsync(container, compressed.Blob);
            Assert.True(storedSize > Filler * 0.9,
                $"store-only pack should be about the original size, was {storedSize}");
            Assert.True(compressedSize < Filler / 10,
                $"compressed pack should be far smaller than the original, was {compressedSize}");

            // The two files really landed in two different packs, each in the right one.
            var idx = await store.ReadIndexAsync(account, name, info.Versions.Single().IndexBlob, null);
            var logRef = idx.Entries.Single(e => e.Path == "d/keep.log").Storage!;
            var txtRef = idx.Entries.Single(e => e.Path == "d/comp.txt").Storage!;
            Assert.Equal("pack", logRef.Kind);
            Assert.Equal("pack", txtRef.Kind);
            Assert.NotEqual(logRef.Ref, txtRef.Ref);
            Assert.Equal($"packs/{logRef.Ref}.7z", stored.Blob);
            Assert.Equal($"packs/{txtRef.Ref}.7z", compressed.Blob);

            // The store-only pack still extracts, byte for byte — store-only does not mean "stored badly".
            var work = Path.Combine(_temp, "x" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var first = await VolumeBlobIO.DownloadAsync(container, stored.Blob, work, CancellationToken.None);
            var outDir = Path.Combine(work, "out");
            await new SevenZipCompressor().ExtractAsync(first, outDir, null, CancellationToken.None);
            Assert.Equal(
                new string('a', Filler),
                await File.ReadAllTextAsync(Path.Combine(outDir, "d", "keep.log")));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    // ---- Case 2: dead-weight compaction after a version retires ----

    /// <summary>
    /// Compaction **rewrites in place** the archive under the same packId, and all it holds is the surviving
    /// members and a pack id — not the rule set from back then. The compression mode must be read back from
    /// <see cref="PackInfo.StoreOnly"/>, otherwise a store-only pack that survives one version retirement gets
    /// recompressed with the default mode — and compaction runs automatically after retention cleanup, so nobody
    /// ever sees the change happen.
    /// </summary>
    [SkippableFact]
    public async Task Compaction_Keeps_A_Store_Only_Pack_Store_Only()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("packstore-dwc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Three members packed into one store-only pack; afterwards only b and c stay referenced
            // (a becomes dead weight, 1/3 > 30% triggers compaction).
            Write(_packSrc, "a.log", new string('a', Filler));
            Write(_packSrc, "b.log", new string('b', Filler));
            Write(_packSrc, "c.log", new string('c', Filler));

            var hasher = new FileHasher();
            var hashB = await hasher.FullHashAsync(Path.Combine(_packSrc, "b.log"));
            var hashC = await hasher.FullHashAsync(Path.Combine(_packSrc, "c.log"));

            var output = Path.Combine(_temp, "p0001.7z");
            var result = await new SevenZipCompressor().CompressAsync(
                new CompressionRequest(_packSrc, ["a.log", "b.log", "c.log"], output, null, StoreOnly: true));
            await new BlobUploader(factory).UploadIfMissingAsync(
                account, name, "packs/p0001.7z", result.VolumeFiles[0], AccessTier.Hot);

            var before = await SizeOfAsync(container, "packs/p0001.7z");
            Assert.True(before > Filler * 2, $"the seeded pack should be uncompressed, was {before}");

            var info = new BackupInfoFile
            {
                Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow },
                Packs =
                {
                    ["p0001"] = new PackInfo
                    {
                        Blob = "packs/p0001.7z",
                        Members = [hashB, hashC],
                        OriginalBytes = Filler * 3,
                        StoreOnly = true,
                    },
                },
            };
            var live = new Dictionary<string, Dictionary<string, LivePackMember>>
            {
                ["p0001"] = new(StringComparer.Ordinal)
                {
                    ["b.log"] = new LivePackMember("b.log", Filler, hashB),
                    ["c.log"] = new LivePackMember("c.log", Filler, hashC),
                },
            };

            // The surviving members are available locally → compaction needs no download.
            Write(_local, "b.log", new string('b', Filler));
            Write(_local, "c.log", new string('c', Filler));

            var compactor = new DeadWeightCompactor(
                new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(),
                Path.Combine(_temp, "compact"), Staging());
            await compactor.CompactAsync(
                account, container, null, info, live, AccessTier.Hot, null, threshold: 0.30,
                _local, allowDownload: false, CancellationToken.None);

            // It really compacted (a is gone), and it is **still store-only**.
            Assert.Equal(2, info.Packs["p0001"].Members.Count);
            Assert.True(info.Packs["p0001"].StoreOnly, "compaction must not clear the store-only flag");
            var after = await SizeOfAsync(container, "packs/p0001.7z");
            Assert.True(after > Filler * 1.8,
                $"compacted pack should still be uncompressed (about two members), was {after}");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
