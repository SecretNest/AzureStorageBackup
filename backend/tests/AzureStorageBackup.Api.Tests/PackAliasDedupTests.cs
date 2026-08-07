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

    /// <summary>
    /// T6：leader 在压缩窗口里被改写 → 它被踢出那一箱、以新 hash 重新处理，于是它最终存下去的
    /// 内容**不再等于**别名的内容。这时别名绝不能指过去（那会让索引指向别人的内容、还原出错
    /// 数据），必须自己被重新备份一遍。
    /// <para>
    /// 两个文件都还原成各自应有的内容，就是这条红线守住了。
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
            Write("c/second.txt", payload);      // 别名

            // 压缩之后把 leader 的内容换掉：重校验会发现它变了，把它踢出那一箱、以新 hash 重处理。
            var (backup, restore, store) = Build(
                new MutatingAfterCompressCompressor(new SevenZipCompressor(), _src, "a/first.txt", mutated));

            await backup.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var first = v1.Entries.Single(e => e.Path == "a/first.txt");
            var second = v1.Entries.Single(e => e.Path == "c/second.txt");

            // 两条条目的内容身份必须已经分道扬镳——别名绝不能还挂在 leader 那份新内容上。
            Assert.NotEqual(first.FullHash, second.FullHash);

            // 决定性的一条：还原出来的必须各是各的内容。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(mutated, await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>压缩**之后**改写目标成员的内容，模拟"文件在处理中变化"（§9、PRD 特别说明 D）。
    /// 分组路径先 hash 后压，所以挂在 CompressAsync 之后，重校验据此发现内容变了。
    /// 与 BackupOrchestratorTests.MutatingCompressor 同一套手法，只覆盖这里用得到的那一半。</summary>
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
    /// T3——本特性最要紧的一条。leader 那个**路径**的文件被删掉之后，别名仍然要能还原。
    /// <para>
    /// 那时 liveByPack 里那个 entryName 由别名条目独自提供（RetentionCleaner 按 EntryName 归组，
    /// 不按 fullHash），所以包不删、成员不死、解压目录里照样取得到。这条链每一环都对，别名才
    /// 活得下来——而它极容易被将来某次"顺手改成按 hash 归组"的重构悄悄踩断。
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

        // 只保留最新一个版本：v1 退役，包只能靠 v2 里那条别名条目钉住。
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
            Write("c/second.txt", payload);      // 别名
            await backup.RunAsync(keepOne);

            var packsAfterV1 = await CountPacksAsync(cc);
            Assert.Equal(1, packsAfterV1);       // 同内容只装了一箱

            // v2：把 leader 那个路径删掉。包里那个成员此后只被别名条目引用着。
            File.Delete(Path.Combine(_src, "a", "first.txt"));
            await backup.RunAsync(keepOne);

            // 包一个都不能少——删了就等于把 c/second.txt 的数据删了。
            Assert.Equal(packsAfterV1, await CountPacksAsync(cc));

            var info = await store.ReadInfoAsync(account, name, null);
            var v2 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            Assert.DoesNotContain(v2.Entries, e => e.Path == "a/first.txt");
            var second = v2.Entries.Single(e => e.Path == "c/second.txt");
            // 成员名仍是**最初**那个已经不存在的路径——还原要按它去归档里取。
            Assert.Equal("a/first.txt", second.Storage!.EntryName);

            // 决定性的一条：内容还在，还原得回来。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "a", "first.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
