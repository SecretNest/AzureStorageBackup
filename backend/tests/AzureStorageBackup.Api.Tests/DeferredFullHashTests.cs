using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 单文件 blob 的全文 hash 延后到压缩那一遍再算之后，索引里记下的必须仍然是**真正压进归档的那些
/// 字节**的 hash。索引 hash 一旦错了不会当场报错：这一轮照样"成功"，直到下一轮 diff 拿它比对、
/// 或者还原时按 <c>data/{hash}</c> 去取，才会发现指向的 blob 不存在。所以这里把三条压缩路径
/// （正常压缩 / 不压缩直传 / 加密）和几种变更判定都跑一遍真备份，逐条核对索引 hash。
/// </summary>
[Trait("Category", "Integration")]
public sealed class DeferredFullHashTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _root;
    private readonly string _temp;

    public DeferredFullHashTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-defer-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_root);
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

    private string Write(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private BackupOrchestrator Build(BlobClientFactory factory, IBackupInfoStore store)
    {
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
    }

    private BackupRequest Request(Account account, string container, BackupEngineOptions options, string? password = null)
        => new()
        {
            Account = account, Container = container, LocalRoot = _root, Name = "defer",
            Options = options, Password = password,
        };

    /// <summary>索引里每一条的 FullHash 都必须等于源文件此刻真实的 hash。</summary>
    private async Task AssertIndexHashesAreRealAsync(VersionIndex idx)
    {
        var hasher = new FileHasher();
        foreach (var e in idx.Entries.Where(e => e.Kind == "file"))
        {
            var local = Path.Combine(_root, e.Path.Replace('/', Path.DirectorySeparatorChar));
            Assert.NotNull(e.FullHash);
            Assert.Equal(await hasher.FullHashAsync(local), e.FullHash);
            Assert.Equal(new FileInfo(local).Length, e.Length);
        }
    }

    /// <summary>
    /// 三条压缩路径一次跑齐：正常 7z 压缩的单文件、命中不压缩列表因而原样直传（raw）的单文件、
    /// 以及合并进 pack 的小文件。三条路都走 <c>StreamAndStageAsync</c> 那一遍读算 hash，
    /// 但只有前两条属于"延后"的范围——pack 成员的 hash 装箱时就要写进成员表，没有第二次机会补算。
    /// </summary>
    [SkippableFact]
    public async Task Index_Hashes_Are_Real_Across_Compressed_Raw_And_Packed_Paths()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("defer1-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            Write("big.bin", 40_000);              // 单文件 + 正常压缩
            Write("raw/movie.mkv", 40_000);        // 单文件 + 不压缩直传
            for (var i = 0; i < 4; i++)
                Write($"docs/f{i}.txt", 2_000);    // 打包

            var options = new BackupEngineOptions
            {
                DontCompress = new IgnoreRuleSet(["raw/"]),
                Plan = new PlanOptions { SingleFileThresholdBytes = 10_000, GroupCapBytes = 100_000 },
            };
            await Build(factory, store).RunAsync(Request(account, name, options));

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            // 先确认这份数据真的把三条路都走到了，否则下面的断言可能只是在验一条路。
            var single = idx.Entries.Where(e => e.Storage!.Kind == "blob").ToList();
            Assert.Equal(["big.bin", "raw/movie.mkv"], single.Select(e => e.Path).Order(StringComparer.Ordinal));
            Assert.True(single.Single(e => e.Path == "raw/movie.mkv").Storage!.Raw, "raw passthrough expected");
            Assert.False(single.Single(e => e.Path == "big.bin").Storage!.Raw);
            Assert.Equal(4, idx.Entries.Count(e => e.Storage!.Kind == "pack"));

            await AssertIndexHashesAreRealAsync(idx);

            // 未加密时地址就是内容地址：hash 错了这里立刻穿帮，blob 也会指不到。
            foreach (var e in single)
            {
                Assert.Equal("data/" + e.FullHash, e.Storage!.Ref);
                Assert.True(await container.GetBlobClient(e.Storage.Ref).ExistsAsync(), e.Storage.Ref);
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 加密时 blob 名是 <c>HMAC(key, fullHash)</c>（防止拿公开文件的 hash 反推容器里有什么），
    /// 于是"地址对不对"不再能直接看出 hash 对不对——索引里的 FullHash 是唯一的真相来源，
    /// 下一轮 diff 和还原都靠它。这条路正是用户实际在跑的配置。
    /// </summary>
    [SkippableFact]
    public async Task Index_Hashes_Are_Real_When_The_Backup_Is_Encrypted()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("defer2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        const string password = "correct horse battery staple";

        try
        {
            Write("big.bin", 40_000);
            Write("raw/movie.mkv", 40_000); // 加密时"不压缩"仍要过 7z——绝不能把明文直传上去
            for (var i = 0; i < 3; i++)
                Write($"docs/f{i}.txt", 2_000);

            var options = new BackupEngineOptions
            {
                DontCompress = new IgnoreRuleSet(["raw/"]),
                Plan = new PlanOptions { SingleFileThresholdBytes = 10_000, GroupCapBytes = 100_000 },
            };
            await Build(factory, store).RunAsync(Request(account, name, options, password));

            var info = await store.ReadInfoAsync(account, name, password);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, password);

            await AssertIndexHashesAreRealAsync(idx);

            foreach (var e in idx.Entries.Where(e => e.Storage!.Kind == "blob"))
            {
                Assert.False(e.Storage!.Raw, "encrypted backups must never store plaintext raw");
                Assert.NotEqual("data/" + e.FullHash, e.Storage.Ref); // 地址被 HMAC 打散了
                Assert.True(await container.GetBlobClient(e.Storage.Ref).ExistsAsync(), e.Storage.Ref);
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 第二轮：延后算出来的 hash 写进索引之后，必须能被下一轮 diff 当作可信基准用。
    /// 这是索引 hash 出错时最先暴露、也最贵的地方——比对不上就把没变的文件整份重传一遍。
    /// 四种判定各来一个：完全没动、只碰 mtime、同长度改内容、改长度。
    /// </summary>
    [SkippableFact]
    public async Task The_Next_Run_Can_Trust_The_Deferred_Hashes()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("defer3-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var untouched = Write("untouched.bin", 40_000);
            var touched = Write("touched.bin", 40_000);
            var rewritten = Write("rewritten.bin", 40_000);
            var grown = Write("grown.bin", 40_000);

            var options = new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 10_000 },
            };
            var first = await Build(factory, store).RunAsync(Request(account, name, options));
            Assert.Equal(4, first.ChangedFiles);

            // 什么都没动的那个：连打开都不该打开。
            _ = untouched;
            // 只把 mtime 往前推：内容一个字节没变 → 必须判成 MetadataOnly，不重传。
            File.SetLastWriteTimeUtc(touched, File.GetLastWriteTimeUtc(touched).AddMinutes(5));
            // 同长度换内容：只有全文 hash 能发现。
            var sameLength = new byte[40_000];
            Random.Shared.NextBytes(sameLength);
            File.WriteAllBytes(rewritten, sameLength);
            // 长度变了：靠长度就能判，不必读全文。
            File.WriteAllBytes(grown, new byte[50_000]);

            var second = await Build(factory, store).RunAsync(Request(account, name, options));

            // 只有后两个算变更。第一轮的 hash 若有一个不对，untouched/touched 会跟着一起被重传。
            Assert.Equal(2, second.ChangedFiles);

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[1].IndexBlob, null);
            Assert.Equal(4, idx.Entries.Count);
            await AssertIndexHashesAreRealAsync(idx);

            foreach (var e in idx.Entries)
                Assert.True(await container.GetBlobClient(e.Storage!.Ref).ExistsAsync(), $"{e.Path} → {e.Storage.Ref}");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
