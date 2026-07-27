using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class DeadWeightCompactorTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _packSrc; // pack 原始成员来源（用于建 pack blob）
    private readonly string _local;   // 死重压实的本地源根
    private readonly string _temp;

    public DeadWeightCompactorTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-dwc-" + Guid.NewGuid().ToString("N"));
        _packSrc = Path.Combine(_base, "packsrc");
        _local = Path.Combine(_base, "local");
        _temp = Path.Combine(_base, "temp");
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

    private static void Write(string dir, string rel, string content)
    {
        var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // 建 pack blob packs/p0001.7z（成员 a/b/c）+ info + liveByPack（b/c 有效，a 死重）。
    private async Task<(BackupInfoFile Info, Dictionary<string, Dictionary<string, LivePackMember>> Live,
        Azure.Storage.Blobs.BlobContainerClient Container, Account Account)> SetupAsync(string name)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        Write(_packSrc, "a.txt", new string('a', 2000));
        Write(_packSrc, "b.txt", new string('b', 2000));
        Write(_packSrc, "c.txt", new string('c', 2000));

        var hasher = new FileHasher();
        var hashA = await hasher.FullHashAsync(Path.Combine(_packSrc, "a.txt"));
        var hashB = await hasher.FullHashAsync(Path.Combine(_packSrc, "b.txt"));
        var hashC = await hasher.FullHashAsync(Path.Combine(_packSrc, "c.txt"));

        var compressor = new SevenZipCompressor();
        var output = Path.Combine(_temp, "p0001.7z");
        var result = await compressor.CompressAsync(
            new CompressionRequest(_packSrc, ["a.txt", "b.txt", "c.txt"], output, null));
        await new BlobUploader(factory).UploadIfMissingAsync(
            account, name, "packs/p0001.7z", result.VolumeFiles[0], AccessTier.Hot);

        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow },
            Packs =
            {
                ["p0001"] = new PackInfo
                {
                    Blob = "packs/p0001.7z",
                    Members = [hashA, hashB, hashC],
                    OriginalBytes = 6000,
                },
            },
        };
        // b、c 仍被有效版本引用；a 死重（1/3 > 30%）。liveByPack 按 entryName 归组。
        var live = new Dictionary<string, Dictionary<string, LivePackMember>>
        {
            ["p0001"] = new(StringComparer.Ordinal)
            {
                ["b.txt"] = new LivePackMember("b.txt", 2000, hashB),
                ["c.txt"] = new LivePackMember("c.txt", 2000, hashC),
            },
        };
        return (info, live, container, account);
    }

    private DeadWeightCompactor Compactor() =>
        new(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), new SevenZipCompressor(), new FileHasher(),
            Path.Combine(_temp, "compact"));

    private async Task<List<string>> PackEntriesAsync(Azure.Storage.Blobs.BlobContainerClient container)
    {
        var work = Path.Combine(_temp, "verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var first = await VolumeBlobIO.DownloadAsync(container, "packs/p0001.7z", work, CancellationToken.None);
        var ex = Path.Combine(work, "x");
        await new SevenZipCompressor().ExtractAsync(first, ex, null, CancellationToken.None);
        return Directory.EnumerateFiles(ex, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f)).OrderBy(x => x).ToList();
    }

    [SkippableFact]
    public async Task Compacts_From_Local_Without_Download_Even_When_Download_Disabled()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var name = RandomName("dwc-local-");
        var (info, live, container, account) = await SetupAsync(name);
        try
        {
            // 本地有与 pack 一致的 b、c → 即便禁止下载也能从本地压实（Archive 场景）。
            Write(_local, "b.txt", new string('b', 2000));
            Write(_local, "c.txt", new string('c', 2000));

            // allowDownload:false → 只能从本地取成员；能压实即证明用了本地文件（Archive 场景亦然）。
            await Compactor().CompactAsync(account, container, null, info, live,
                AccessTier.Hot, null, threshold: 0.30, _local, allowDownload: false, CancellationToken.None);

            Assert.Equal(2, info.Packs["p0001"].Members.Count);
            Assert.Equal(0, info.Packs["p0001"].DeadBytes);
            Assert.Equal(["b.txt", "c.txt"], await PackEntriesAsync(container)); // a 已丢弃
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Abandons_When_Member_Missing_Locally_And_Download_Disabled()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var name = RandomName("dwc-abandon-");
        var (info, live, container, account) = await SetupAsync(name);
        try
        {
            // 本地只有 b，缺 c，且禁止下载 → 放弃重打包，成员不变、记录死重。
            Write(_local, "b.txt", new string('b', 2000));

            await Compactor().CompactAsync(account, container, null, info, live,
                AccessTier.Archive, null, threshold: 0.30, _local, allowDownload: false, CancellationToken.None);

            Assert.Equal(3, info.Packs["p0001"].Members.Count); // 未压实
            Assert.Equal(2000, info.Packs["p0001"].DeadBytes);  // 死重被记录
            Assert.Equal(["a.txt", "b.txt", "c.txt"], await PackEntriesAsync(container)); // pack 原样
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Keeps_Both_Members_That_Share_Identical_Content()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("dwc-dup-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            // pack 含 a(死重) + b、d 两个**内容相同**的成员（去重后同 fullHash，但仍是两条独立成员）。
            Write(_packSrc, "a.txt", new string('a', 2000));
            Write(_packSrc, "b.txt", new string('s', 2000));
            Write(_packSrc, "d.txt", new string('s', 2000)); // 与 b 内容一致

            var hasher = new FileHasher();
            var hashA = await hasher.FullHashAsync(Path.Combine(_packSrc, "a.txt"));
            var hashDup = await hasher.FullHashAsync(Path.Combine(_packSrc, "b.txt"));
            Assert.Equal(hashDup, await hasher.FullHashAsync(Path.Combine(_packSrc, "d.txt")));

            var output = Path.Combine(_temp, "p0001.7z");
            var result = await new SevenZipCompressor().CompressAsync(
                new CompressionRequest(_packSrc, ["a.txt", "b.txt", "d.txt"], output, null));
            await new BlobUploader(factory).UploadIfMissingAsync(
                account, name, "packs/p0001.7z", result.VolumeFiles[0], AccessTier.Hot);

            var info = new BackupInfoFile
            {
                Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow },
                Packs = { ["p0001"] = new PackInfo { Blob = "packs/p0001.7z", Members = [hashA, hashDup, hashDup], OriginalBytes = 6000 } },
            };
            // b、d 都有效（同 hash 但不同 entryName）；a 死重。若按 hash 归组会把 b、d 折叠成一个 → 丢数据。
            var live = new Dictionary<string, Dictionary<string, LivePackMember>>
            {
                ["p0001"] = new(StringComparer.Ordinal)
                {
                    ["b.txt"] = new LivePackMember("b.txt", 2000, hashDup),
                    ["d.txt"] = new LivePackMember("d.txt", 2000, hashDup),
                },
            };
            Write(_local, "b.txt", new string('s', 2000));
            Write(_local, "d.txt", new string('s', 2000));

            await Compactor().CompactAsync(account, container, null, info, live,
                AccessTier.Hot, null, threshold: 0.30, _local, allowDownload: false, CancellationToken.None);

            // 关键：两个同内容成员都必须保留（含各自 entryName），否则索引仍引用却已丢失 → 数据丢失。
            Assert.Equal(["b.txt", "d.txt"], await PackEntriesAsync(container));
            Assert.Equal(2, info.Packs["p0001"].Members.Count);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>装饰 uploader：上传（if-missing 与 overwrite 皆然）一律抛异常，模拟"传新阶段"崩溃。</summary>
    private sealed class FailingUploader : IBlobUploader
    {
        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new IOException("injected upload failure");

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => throw new IOException("injected upload failure");
    }

    /// <summary>记录 overwrite 上传顺序、并真正委托 inner 上传（供 ReplaceAsync 残留删除生效）。</summary>
    private sealed class RecordingUploader(IBlobUploader inner) : IBlobUploader
    {
        public List<string> OverwriteOrder { get; } = [];

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            OverwriteOrder.Add(blobName);
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    // 传新阶段崩溃：旧 pack 分卷仍完整存在（此前 delete-first 会导致这里被删空 → 整 blob 丢失）。
    [SkippableFact]
    public async Task Recompact_Failure_During_Upload_Leaves_Old_Volumes_Intact()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var name = RandomName("dwc-failupload-");
        var (info, live, container, account) = await SetupAsync(name);
        try
        {
            Write(_local, "b.txt", new string('b', 2000));
            Write(_local, "c.txt", new string('c', 2000));

            // 用「上传即抛」的装饰 uploader；CompactAsync 逐 pack 吞掉异常，关键是旧数据须在失败后仍在。
            var compactor = new DeadWeightCompactor(
                new FailingUploader(),
                new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact-fail"));

            await compactor.CompactAsync(account, container, null, info, live,
                AccessTier.Hot, null, threshold: 0.30, _local, allowDownload: false, CancellationToken.None);

            var remaining = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, "packs/p0001.7z", CancellationToken.None))
                remaining.Add(b.Name);
            Assert.NotEmpty(remaining); // delete-first 会让这里为空

            // 旧 pack 内容未被破坏，仍是原 3 成员。
            Assert.Equal(["a.txt", "b.txt", "c.txt"], await PackEntriesAsync(container));
            // 死重被记录、成员未变（本次压实放弃）。
            Assert.Equal(3, info.Packs["p0001"].Members.Count);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>压缩那一瞬锁住 compose 目录里的某个成员：7z 读不到它、静默丢掉、仍产出有效归档。</summary>
    private sealed class LockMemberDuringCompressCompressor(IFileCompressor inner, string entryName) : IFileCompressor
    {
        public async Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var full = Path.Combine(request.SourceDirectory, entryName);
            var lockIt = File.Exists(full);
            if (lockIt)
                File.SetUnixFileMode(full, UnixFileMode.None);
            try
            {
                return await inner.CompressAsync(request, ct);
            }
            finally
            {
                if (lockIt)
                    File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);
    }

    /// <summary>重打包是**覆盖式**的（ReplaceAsync 直接改写 packs/p0001.7z），所以 7z 在这条路径上
    /// 丢掉一个成员的后果比备份时严重得多：那个成员仍被有效版本引用，旧 pack 一旦被缺它的新 pack
    /// 覆盖，数据就永久没了，而索引照旧声称它在里面。压缩器验收归档内容之后，这一轮压实会失败并
    /// 被逐 pack 的兜底接住——宁可放弃一次空间优化，也不能覆盖出一个缺成员的包。</summary>
    [SkippableFact]
    public async Task Recompact_Never_Overwrites_A_Good_Pack_With_One_Missing_A_Member()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var name = RandomName("dwc-dropped-");
        var (info, live, container, account) = await SetupAsync(name);
        try
        {
            Write(_local, "b.txt", new string('b', 2000));
            Write(_local, "c.txt", new string('c', 2000));

            var compactor = new DeadWeightCompactor(
                new BlobUploader(new BlobClientFactory(TestSecrets.Reader)),
                new LockMemberDuringCompressCompressor(new SevenZipCompressor(), "b.txt"),
                new FileHasher(), Path.Combine(_temp, "compact-dropped"));

            await compactor.CompactAsync(account, container, null, info, live,
                AccessTier.Hot, null, threshold: 0.30, _local, allowDownload: false, CancellationToken.None);

            // 旧 pack 原封不动：修复前这里会变成只剩 c.txt——b.txt 仍被引用却已从云端消失。
            Assert.Equal(["a.txt", "b.txt", "c.txt"], await PackEntriesAsync(container));
            Assert.Equal(3, info.Packs["p0001"].Members.Count); // 本次压实放弃，成员表不变
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    // ReplaceAsync：覆盖上传新卷（倒序，.001 最后作提交标记）+ 删除残留旧卷（新卷数 < 旧卷数的尾部）。
    [SkippableFact]
    public async Task ReplaceAsync_Overwrites_New_Deletes_Residual_And_Uploads_First_Volume_Last()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var account = AzuriteAccount();
        var name = RandomName("vbio-replace-");
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();
        try
        {
            const string baseRef = "data/x.7z";
            // 旧：3 卷。
            await cc.GetBlobClient(baseRef + ".001").UploadAsync(BinaryData.FromString("OLD1"), overwrite: true);
            await cc.GetBlobClient(baseRef + ".002").UploadAsync(BinaryData.FromString("OLD2"), overwrite: true);
            await cc.GetBlobClient(baseRef + ".003").UploadAsync(BinaryData.FromString("OLD3"), overwrite: true);

            // 新：2 卷。
            var f1 = Path.Combine(_temp, "n1"); File.WriteAllText(f1, "NEW1");
            var f2 = Path.Combine(_temp, "n2"); File.WriteAllText(f2, "NEW2");

            var rec = new RecordingUploader(new BlobUploader(factory));
            await VolumeBlobIO.ReplaceAsync(rec, account, cc, baseRef, [f1, f2], AccessTier.Hot, retry: null, CancellationToken.None);

            // 倒序上传：.002 先、.001 最后（提交标记）。
            Assert.Equal([baseRef + ".002", baseRef + ".001"], rec.OverwriteOrder);
            // 新内容覆盖旧卷。
            Assert.Equal("NEW1", (await cc.GetBlobClient(baseRef + ".001").DownloadContentAsync()).Value.Content.ToString());
            Assert.Equal("NEW2", (await cc.GetBlobClient(baseRef + ".002").DownloadContentAsync()).Value.Content.ToString());
            // 残留旧卷 .003 已删。
            Assert.False((await cc.GetBlobClient(baseRef + ".003").ExistsAsync()).Value);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Downloads_To_Fill_Missing_Members_When_Download_Enabled()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var name = RandomName("dwc-dl-");
        var (info, live, container, account) = await SetupAsync(name);
        try
        {
            // 本地全缺，但允许下载 → 下载旧 pack 解压补齐 b、c 后压实。
            await Compactor().CompactAsync(account, container, null, info, live,
                AccessTier.Hot, null, threshold: 0.30, _local, allowDownload: true, CancellationToken.None);

            Assert.Equal(2, info.Packs["p0001"].Members.Count);
            Assert.Equal(["b.txt", "c.txt"], await PackEntriesAsync(container));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
