using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 同一轮备份内、跨箱的打包成员去重。跨版本那一路由 <see cref="PackMemberDedupTests"/> 覆盖；
/// 这里要钉的是**同一轮**：本轮新封的箱之间，同内容只该装一次。
/// <para>
/// 装箱用 MaxPackMembers = 1 逼成一箱一个成员，跨箱因此是确定的，不必猜装箱结果。
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

    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store) Build(
        IFileCompressor? compressor = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var indexCache = new LocalIndexCache(_db, store);
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(_db));
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            compressor ?? new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), null, indexCache, tracked),
            new FileHasher(), indexCache: indexCache, trackedInfo: tracked);
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"));
        return (backup, restore, store);
    }

    /// <summary>阈值给足让所有文件走 pack 路径；一箱只装一个成员，跨箱因此是确定的。</summary>
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

    /// <summary>
    /// T1 + T2：**同一轮**里三个小文件、其中两个同内容，一箱只装一个成员。
    /// 去重生效时只该有两个包（不是三个），第二条条目指向第一条那个成员，而且两条都还原得回来。
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
            // 不可压缩的内容：万一真的装了两箱，体积差异藏不住。
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            Write("a/first.txt", payload);              // leader（ordinal 路径序最先）
            Write("b/other.txt", "something else entirely");
            Write("c/second.txt", payload);             // 别名

            var run = await backup.RunAsync(Request(account, name));

            // 三个文件、一箱一个成员：没有去重就是 3 个包，有去重是 2 个。
            Assert.Equal(2, await CountPacksAsync(cc));

            // T8：别名仍然是一个**变更文件**，只是不占一个包。记账口径不能因为去重而漏掉它——
            // 它在索引里实实在在有一条条目，用户也确实新加了这个文件。
            Assert.Equal(3, run.ChangedFiles);

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var first = v1.Entries.Single(e => e.Path == "a/first.txt");
            var second = v1.Entries.Single(e => e.Path == "c/second.txt");

            // 引用形状必须与 RecordPack 从前写的逐字节相同：Kind=pack + 同一个 Ref + leader 的 EntryName。
            Assert.Equal("pack", second.Storage!.Kind);
            Assert.Equal(first.Storage!.Ref, second.Storage.Ref);
            Assert.Equal("a/first.txt", second.Storage.EntryName);

            // 两条都要还原到**自己**的路径上。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 内容不同就绝不能合并——哪怕长度一样。这一条是去重判据的反向保险：
    /// 判错的后果是索引指向别人的内容、还原出来是错数据。
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
            Write("c/second.txt", new string('y', 300));   // 同长度、不同内容

            await backup.RunAsync(Request(account, name));

            Assert.Equal(2, await CountPacksAsync(cc));   // 各装各的

            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(new string('x', 300), await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(new string('y', 300), await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
