using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 摘要里的数字是真跑出来的，不是转述 diff 的自述。这里跑真实的备份，验证三件在纯函数测试里
/// 验不到的事：各 ChangeKind 数得对、实传字节只算真正推上去的（去重命中一个字节都不能算）、
/// 保留清理删了多少东西能被数出来。
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupStatsTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public BackupStatsTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-stats-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
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

    private static readonly DateTime MtimeBase = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private int _mtimeSeq;

    /// <summary>写文件并推进 mtime——等长改写不动 mtime 会被差异检测判为未变。</summary>
    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, MtimeBase.AddMinutes(++_mtimeSeq));
    }

    private void Delete(string rel) =>
        File.Delete(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));

    private BackupOrchestrator Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
    }

    /// <summary>
    /// 阈值压到 20 KB（默认是 5 MB），好用几十 KB 的小文件就把两条存储路径都走到：超过阈值的走
    /// 内容寻址的单文件 data blob（**会**去重），低于阈值的成组进 pack（每轮新建一个包，不跨包去重）。
    /// 不压的话得写 5 MB 以上的文件才碰得到 blob 那条路，测试白慢一大截。
    /// </summary>
    private const long SingleFileThreshold = 20_000;

    private BackupRequest Request(Account account, string container, int maxVersions = 0) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "stats",
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = SingleFileThreshold },
            Retention = maxVersions > 0
                ? new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = maxVersions }
                : new RetentionPolicy(),
        },
    };

    [SkippableFact]
    public async Task Counts_New_Modified_And_Deleted_Separately()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var orchestrator = Build();
        var account = AzuriteAccount();
        var name = RandomName("stats-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            Write("keep.txt", "unchanged");
            Write("edit.txt", "before");
            Write("gone.txt", "doomed");
            var v1 = await orchestrator.RunAsync(Request(account, name));

            // 首轮：三个文件全是新增，没有变更也没有删除。
            Assert.Equal(3, v1.NewFiles);
            Assert.Equal(0, v1.ModifiedFiles);
            Assert.Equal(0, v1.DeletedFiles);

            Write("edit.txt", "after-and-longer");   // 改一个
            Write("added.txt", "brand new");         // 加一个
            Delete("gone.txt");                      // 删一个
            var v2 = await orchestrator.RunAsync(Request(account, name));

            Assert.Equal(1, v2.NewFiles);
            Assert.Equal(1, v2.ModifiedFiles);
            Assert.Equal(1, v2.DeletedFiles);
            // 未变的 keep.txt 既不算新增也不算变更——这正是增量备份要省下的那部分。
            Assert.Equal(v2.ChangedFiles, v2.NewFiles + v2.ModifiedFiles);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 实传字节只能算真正推上云的那些。把同一份内容换个路径再备份一次：去重会命中，
    /// 于是这一轮源侧"变更"了整整一个文件，实传却必须是 0——两个口径分开报的全部意义就在这里。
    /// <para>内容必须**超过**单文件阈值：去重是内容寻址 data blob 的性质，pack 每轮都新建一个包，不跨包去重。</para>
    /// </summary>
    [SkippableFact]
    public async Task Uploaded_Bytes_Exclude_Deduplicated_Content()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var orchestrator = Build();
        var account = AzuriteAccount();
        var name = RandomName("statsdedup-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var payload = new string('x', 50_000);
            Write("one.txt", payload);
            var v1 = await orchestrator.RunAsync(Request(account, name));
            Assert.True(v1.UploadedBytes > 0, "首轮必须真的传了东西");

            // 同一份内容，另一个路径：源侧是一个新增文件，云端一个字节都不该涨。
            Write("copy.txt", payload);
            var v2 = await orchestrator.RunAsync(Request(account, name));

            Assert.Equal(1, v2.NewFiles);
            Assert.True(v2.ChangedBytes >= 50_000, "源侧确实变更了一整个文件");
            Assert.Equal(0, v2.UploadedBytes);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 保留策略退役旧版本时，删掉的东西要能被数出来——否则日志里那行永远是 0。
    /// pack 与 data blob 两条路都要覆盖：它们分开计数，只测一条就发现不了另一条压根没在数。
    /// </summary>
    [SkippableFact]
    public async Task Reports_What_Retention_Deleted()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var orchestrator = Build();
        var account = AzuriteAccount();
        var name = RandomName("statsret-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 只留 1 个版本：第二轮一跑完，第一版及其独占数据就该被清掉。
            Write("big.bin", new string('a', 60_000));    // > 阈值 → 单文件 data blob
            Write("small.txt", new string('a', 5_000));   // < 阈值 → 成组进 pack
            var v1 = await orchestrator.RunAsync(Request(account, name, maxVersions: 1));
            Assert.True(v1.Cleanup.IsEmpty, "只有一个版本时无可退役");

            Write("big.bin", new string('b', 60_000));
            Write("small.txt", new string('b', 5_000));
            var v2 = await orchestrator.RunAsync(Request(account, name, maxVersions: 1));

            Assert.False(v2.Cleanup.IsEmpty);
            Assert.Equal(1, v2.Cleanup.RetiredVersions);
            // v1 的两份内容都再没人引用了，两条存储路径各自的对象都该被删掉并计入释放量。
            Assert.True(v2.Cleanup.DeletedBlobs > 0, "v1 的独占 data blob 应被删除");
            Assert.True(v2.Cleanup.DeletedPacks > 0, "v1 的独占 pack 应被删除");
            Assert.True(v2.Cleanup.FreedBytes > 0, "释放的字节应被累加");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
