using System.Net.Sockets;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 「不压缩」规则对**被打包的**小文件也要生效。
/// <para>
/// 从前 <c>CompressPackAsync</c> 把 storeOnly 硬编码成 false，规则只对单文件 blob 起作用：
/// 一个目录里小于阈值的 jpg/mp4/已压缩归档照样被 <c>-mx9</c> 啃一遍，纯浪费 CPU。现在规划器
/// 按可压缩性把同一目录切成两箱，压法随箱走到 7z。
/// </para>
/// <para>
/// 尺寸断言而非参数断言：内容是高度可压的同一字符重复，<c>-mx0</c> 与 <c>-mx9</c> 的归档差
/// 三个数量级，不会卡在边界上。pack 一定要过 7z（多成员），所以不必像单文件那条路那样必须加密
/// 才测得到——未加密的 store-only **单文件**走原始直传（CopyRawAsync），根本不过 7z。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackStoreOnlySplitTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <summary>高度可压：-mx9 压完只剩几百字节，-mx0 原样保留 20 万。</summary>
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

    // ---- 用例 1：全新备份 ----

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
            // 同一个目录，两种命运。内容不同：同内容会被内容寻址/成员去重合成一份，那样就只剩
            // 一条路径可验了。
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
                    // 阈值抬高，让这两个 20 万字节的文件都走分组打包而不是单文件 blob。
                    Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                    DontCompress = new IgnoreRuleSet(["*.log"]),
                },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            Assert.Equal(2, info!.Packs.Count);

            var stored = Assert.Single(info.Packs.Values, p => p.StoreOnly);
            var compressed = Assert.Single(info.Packs.Values, p => !p.StoreOnly);

            // 压法真的落到了 7z 上：只存的那一箱几乎就是原文件大小，压缩的那一箱小三个数量级。
            var storedSize = await SizeOfAsync(container, stored.Blob);
            var compressedSize = await SizeOfAsync(container, compressed.Blob);
            Assert.True(storedSize > Filler * 0.9,
                $"store-only pack should be about the original size, was {storedSize}");
            Assert.True(compressedSize < Filler / 10,
                $"compressed pack should be far smaller than the original, was {compressedSize}");

            // 两个文件确实分属两箱，且各自进了对的那一箱。
            var idx = await store.ReadIndexAsync(account, name, info.Versions.Single().IndexBlob, null);
            var logRef = idx.Entries.Single(e => e.Path == "d/keep.log").Storage!;
            var txtRef = idx.Entries.Single(e => e.Path == "d/comp.txt").Storage!;
            Assert.Equal("pack", logRef.Kind);
            Assert.Equal("pack", txtRef.Kind);
            Assert.NotEqual(logRef.Ref, txtRef.Ref);
            Assert.Equal($"packs/{logRef.Ref}.7z", stored.Blob);
            Assert.Equal($"packs/{txtRef.Ref}.7z", compressed.Blob);

            // 只存的那一箱照样解得开、内容一字不差——store-only 不是"没存好"。
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

    // ---- 用例 2：版本退役后的死重压实 ----

    /// <summary>
    /// 压实是**原地重写**同一个 packId 的归档，而它手上只有存活成员和一个包号、没有当初那份规则。
    /// 压法必须从 <see cref="PackInfo.StoreOnly"/> 取回来，否则一个只存不压的包挨过一次版本退役
    /// 就被重压成默认压法了——而压实是保留清理之后自动跑的，没有任何人会看见这次改变。
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
            // 三个成员打成一个 store-only 包；随后只有 b、c 仍被引用（a 成死重，1/3 > 30% 触发压实）。
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

            // 存活成员在本地取得到 → 不必下载即可压实。
            Write(_local, "b.log", new string('b', Filler));
            Write(_local, "c.log", new string('c', Filler));

            var compactor = new DeadWeightCompactor(
                new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(),
                Path.Combine(_temp, "compact"), Staging());
            await compactor.CompactAsync(
                account, container, null, info, live, AccessTier.Hot, null, threshold: 0.30,
                _local, allowDownload: false, CancellationToken.None);

            // 确实压实了（丢掉了 a），而且**仍然是只存不压**。
            Assert.Equal(2, info.Packs["p0001"].Members.Count);
            Assert.True(info.Packs["p0001"].StoreOnly, "compaction must not clear the store-only flag");
            var after = await SizeOfAsync(container, "packs/p0001.7z");
            Assert.True(after > Filler * 1.8,
                $"compacted pack should still be uncompressed (about two members), was {after}");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
