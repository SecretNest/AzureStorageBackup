using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// pack 号必须**跨运行**唯一。
/// <para>
/// 它不像 data blob 那样内容寻址——名字里没有内容的影子。号码从前接着信息文件里的最大号往下发，
/// 于是上一次运行失败时会重号：那一次已经把 <c>packs/p0001.7z</c> 传上去了、却没能写成信息文件，
/// 下一次又从 p0001 发起，而这个同号包装的是**另一批成员**。上传走 if-missing，撞上同名就跳过，
/// 索引却声称它含这一次的成员——还原时从那个包里根本取不到，静默地少一批文件。
/// </para>
/// <para>
/// 这是用户容器的真实状态：一次失败的备份留下了 data/ 和 packs/，信息文件没写成。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackIdUniquenessTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _src;
    private readonly string _dst;
    private readonly string _temp;

    public PackIdUniquenessTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-packid-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(baseDir, "src");
        _dst = Path.Combine(baseDir, "dst");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
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

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store) Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"));
        return (backup, restore, store);
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _src,
        Name = "packid",
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    /// <summary>
    /// 模拟"上一次运行失败、信息文件没写成"：备份一批文件，然后把索引与信息文件删掉，
    /// 只留下 data/ 与 packs/——正是用户容器的形状。再跑一次**换成另一批文件**的备份，
    /// 新包绝不能撞上那些遗留的包名，还原出来的内容必须是这一次的。
    /// </summary>
    [SkippableFact]
    public async Task A_Rerun_After_A_Failed_Run_Never_Reuses_A_Leftover_Pack_Name()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packid-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // 第一轮：留下 data/ 与 packs/。
            Write("first/a.txt", new string('a', 400));
            Write("first/b.txt", new string('b', 400));
            await backup.RunAsync(Request(account, name));

            var leftoverPacks = new List<string>();
            await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, "packs/", CancellationToken.None))
                leftoverPacks.Add(b.Name);
            Assert.NotEmpty(leftoverPacks);

            // 把索引与信息文件抹掉 = "那一轮没能收尾"。data/ 与 packs/ 留在原地。
            await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, "indexes/", CancellationToken.None))
                await cc.GetBlobClient(b.Name).DeleteIfExistsAsync();
            await cc.GetBlobClient(BackupDiscovery.IndexBlobName).DeleteIfExistsAsync();

            // 第二轮换一个编排器，因为"那一轮没能收尾"在本地也留不下任何东西：写本地状态是
            // 收尾动作的一部分，运行倒在半路时它压根没执行。沿用上一个编排器的话，本地状态里
            // 还记着第一轮写成的信息文件和它的 ETag——那不是"运行失败"的形状，而是"云端被
            // 别人动过"，写回时会撞 412 并清本地状态要求重跑（见 TrackedInfoStore.WriteAsync）。
            var (backup2, _, _) = Build();

            // **另一批**文件。本地与云端都没有信息文件，所以这是一次"全新"备份。
            Directory.Delete(Path.Combine(_src, "first"), recursive: true);
            Write("second/c.txt", new string('c', 400));
            Write("second/d.txt", new string('d', 400));
            await backup2.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var index = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var refs = index.Entries.Where(e => e.Storage?.Kind == "pack")
                .Select(e => $"packs/{e.Storage!.Ref}.7z").Distinct(StringComparer.Ordinal).ToList();
            Assert.NotEmpty(refs);

            // 要害：这一轮的包名一个都不能是上一轮遗留的那些。撞上就等于索引指着一个装着
            // 别人成员的包，而 if-missing 会安静地跳过上传。
            Assert.Empty(refs.Intersect(leftoverPacks, StringComparer.Ordinal));

            // 而且还原出来的必须是**这一轮**的内容。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(new string('c', 400), await File.ReadAllTextAsync(Path.Combine(_dst, "second", "c.txt")));
            Assert.Equal(new string('d', 400), await File.ReadAllTextAsync(Path.Combine(_dst, "second", "d.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>同一轮里发出的号必须互不相同——这是"唯一"最起码的那一半。</summary>
    [SkippableFact]
    public async Task Packs_Within_One_Run_Get_Distinct_Names()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, _, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packidmulti-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // 每个目录各自成箱（默认按目录打包），于是这一轮会发出好几个号。
            for (var i = 0; i < 5; i++)
                Write($"dir{i}/f.txt", new string((char)('a' + i), 300));
            await backup.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var index = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var packIds = index.Entries.Where(e => e.Storage?.Kind == "pack")
                .Select(e => e.Storage!.Ref).ToList();

            var distinct = packIds.Distinct(StringComparer.Ordinal).ToList();
            Assert.True(distinct.Count > 1, "这一轮本该产生多个包");
            // 每个包名在容器里都确实存在（号发对了，没有指向不存在的对象）。
            foreach (var id in distinct)
            {
                var single = await cc.GetBlobClient($"packs/{id}.7z").ExistsAsync();
                var first = await cc.GetBlobClient($"packs/{id}.7z.001").ExistsAsync();
                Assert.True(single.Value || first.Value, $"packs/{id}.7z 应该在容器里");
            }
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
