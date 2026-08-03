using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 索引里的 <c>raw</c> 标志说的是"这个 blob 里躺着的是原始字节，还是一个 7z 归档"。它一旦与
/// blob 的实际内容对不上，还原就会把归档本身当成文件内容写出来——一次看起来完全成功的还原，
/// 产出的却是坏文件。
/// <para>
/// 这类损坏曾经真实存在（同批同内容的两个文件被指派成不同存储形态、各自上传，见
/// <see cref="EmptyFileRoundTripTests"/>），已经从产生端修掉。但**已经写下去的**备份修不回来，
/// 所以这里要回答一个运维问题：手上一份来历不明的备份，能不能靠现成的检查功能查出它有没有这个毛病？
/// 答案必须是确定的——否则用户只能靠"全部重做一遍"来求心安。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class RawFlagMismatchDetectionTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _src;
    private readonly string _temp;

    public RawFlagMismatchDetectionTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-rawflag-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
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

    /// <summary>
    /// 备份一个走 7z 单文件 blob 的文件（raw=false），然后把索引里那条的 raw 翻成 true——
    /// 得到的正是那类损坏备份的形状：blob 里是归档，索引却声称它是原始字节。
    /// 随后跑一次 Content 级检查，看它认不认得出来。
    /// </summary>
    [SkippableFact]
    public async Task Content_Level_Check_Catches_A_Raw_Flag_That_Lies()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"));

        var account = AzuriteAccount();
        var name = RandomName("rawflag-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 不可压缩的内容：归档字节与原始字节明显不同，翻标志之后的错配是实打实的。
            var payload = new byte[50_000];
            new Random(4242).NextBytes(payload);
            Directory.CreateDirectory(Path.Combine(_src, "solo"));
            await File.WriteAllBytesAsync(Path.Combine(_src, "solo", "a.bin"), payload);

            await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "rawflag",
                // DontGroup 强制走单文件 blob；不加 DontCompress，所以它是 7z 归档（raw=false）。
                Options = new BackupEngineOptions { DontGroup = new IgnoreRuleSet(["solo/**"]) },
            });

            // 健康的备份先得是绿的，否则下面的断言证明不了任何事。
            var healthy = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });
            Assert.True(healthy.Ok, "健康备份的 Content 级检查本该通过");

            // 把 raw 标志翻掉：blob 里仍是归档，索引却开始声称它是原始字节。
            var info = await store.ReadInfoAsync(account, name, null);
            var version = info!.Versions[^1];
            var index = await store.ReadIndexAsync(account, name, version.IndexBlob, null);
            var target = index.Entries.Single(e => e.Path == "solo/a.bin");
            Assert.NotNull(target.Storage);
            Assert.False(target.Storage!.Raw, "前提：这一条本该是 7z 归档");

            var tampered = new VersionIndex
            {
                Version = index.Version,
                EmptyDirs = index.EmptyDirs,
                Entries = [.. index.Entries.Select(e => e.Path == "solo/a.bin"
                    ? e with { Storage = e.Storage! with { Raw = true } }
                    : e)],
            };
            await store.WriteIndexAsync(account, name, version.Version, tampered, null);

            var report = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });

            // 这是本文件存在的全部意义：这类损坏必须能被现成的检查功能查出来，
            // 而且要精确指到出问题的那个文件上，不能只给一句笼统的"有问题"。
            Assert.False(report.Ok, "raw 标志与 blob 实际内容不符，Content 级检查必须报错");
            Assert.Contains("solo/a.bin", report.CorruptedPaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 反方向同样要认得出来：blob 里是原始字节，索引却说它是归档。
    /// 两个方向都覆盖，才敢对用户说"跑一次 Content 级检查就能知道"。
    /// </summary>
    [SkippableFact]
    public async Task Content_Level_Check_Catches_The_Mismatch_In_The_Other_Direction()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress2"), Path.Combine(_temp, "staged2"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check2"));

        var account = AzuriteAccount();
        var name = RandomName("rawflag2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var payload = new byte[50_000];
            new Random(777).NextBytes(payload);
            Directory.CreateDirectory(Path.Combine(_src, "raw"));
            await File.WriteAllBytesAsync(Path.Combine(_src, "raw", "b.bin"), payload);

            await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "rawflag2",
                // DontGroup + DontCompress + 无密码 → raw 直传（blob 里就是原始字节）。
                Options = new BackupEngineOptions
                {
                    DontGroup = new IgnoreRuleSet(["raw/**"]),
                    DontCompress = new IgnoreRuleSet(["raw/**"]),
                },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            var version = info!.Versions[^1];
            var index = await store.ReadIndexAsync(account, name, version.IndexBlob, null);
            var target = index.Entries.Single(e => e.Path == "raw/b.bin");
            Assert.True(target.Storage!.Raw, "前提：这一条本该是 raw 直传");

            var tampered = new VersionIndex
            {
                Version = index.Version,
                EmptyDirs = index.EmptyDirs,
                Entries = [.. index.Entries.Select(e => e.Path == "raw/b.bin"
                    ? e with { Storage = e.Storage! with { Raw = false } }
                    : e)],
            };
            await store.WriteIndexAsync(account, name, version.Version, tampered, null);

            var report = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });

            Assert.False(report.Ok, "索引声称是归档、blob 里却是原始字节，Content 级检查必须报错");
            Assert.Contains("raw/b.bin", report.CorruptedPaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
