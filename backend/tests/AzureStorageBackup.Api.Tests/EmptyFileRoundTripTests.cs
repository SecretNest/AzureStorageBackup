using System.Net.Sockets;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 0 字节文件与空目录走遍每一条存储路径，再原样还原回来。空文件在这条管线上有好几个可疑点，
/// 而且各不相同：
/// <list type="bullet">
/// <item>pack 路径把它当成归档里的一个空成员——7z 存得下，但还原时得真的把文件建出来，而不是"没有内容所以跳过"；</item>
/// <item>单文件 blob 路径要把**空的 stdin** 喂给 <c>7z -si</c>；</item>
/// <item>raw 直传绕开 7z，直接推一个 0 字节的 blob 上去，再原样拉回来；</item>
/// <item>加密单文件又是另一条，头加密之后连条目名都列不出来。</item>
/// </list>
/// 空文件不是"没有内容"，它是"内容长度为零的文件"——两者在还原后的差别是文件存不存在。
/// 空目录同理，它走的是索引里独立的 EmptyDirs 名单而非内容存储，也必须一并还原出来。
/// <para>
/// 两种接线都要测。生产主路径有本地权威索引，去重走 <see cref="LocalDedupResolver"/>，它对
/// 同批同内容有预约协调；导入未同步的备份没有本地索引，退回发云端 HEAD 判存在性，那条路上
/// 没有任何同批协调。两条路对"同内容却存成不同形态"的处置不同，只测一条会漏掉另一条。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class EmptyFileRoundTripTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _src;
    private readonly string _dst;
    private readonly string _temp;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public EmptyFileRoundTripTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-empty-" + Guid.NewGuid().ToString("N"));
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

    private void WriteEmpty(string rel)
    {
        var full = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, []);
    }

    /// <param name="localAuthoritative">
    /// true = 生产主路径（本地权威索引 → LocalDedupResolver）；
    /// false = 导入未同步的备份（无本地索引 → 云端 HEAD 回退）。
    /// </param>
    private (BackupOrchestrator Backup, RestoreOrchestrator Restore) Build(bool localAuthoritative)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var indexCache = localAuthoritative ? new LocalIndexCache(_db, store) : null;
        var tracked = localAuthoritative ? new TrackedInfoStore(store, new LocalBackupStateStore(_db)) : null;
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
            indexCache: indexCache, trackedInfo: tracked);
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"));
        return (backup, restore);
    }

    /// <summary>
    /// 归类只看长度与规则：<c>DontGroup</c> 无视长度强制单文件 blob，再叠 <c>DontCompress</c>
    /// 且无密码就落进 raw 直传；不匹配任何规则的默认成组进 pack。一次备份因此把四条路都走到。
    /// </summary>
    private static BackupEngineOptions EngineOptions() => new()
    {
        DontGroup = new IgnoreRuleSet(["solo/**", "raw/**"]),
        DontCompress = new IgnoreRuleSet(["raw/**"]),
    };

    [SkippableTheory]
    [InlineData(null, true)]       // 生产主路径，明文（raw 直传只有无密码时才走得到）
    [InlineData(null, false)]      // 导入未同步的备份，明文
    [InlineData("pw-123", true)]   // 生产主路径，加密
    [InlineData("pw-123", false)]  // 导入未同步的备份，加密
    public async Task Zero_Byte_Files_And_Empty_Dirs_Survive_Every_Storage_Path(
        string? password, bool localAuthoritative)
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore) = Build(localAuthoritative);
        var account = AzuriteAccount();
        var name = RandomName("empty-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteEmpty("packed/zero.txt");    // → pack 里的空成员
            WriteEmpty("solo/zero.bin");      // → 单文件 blob，空 stdin 喂给 7z -si
            WriteEmpty("raw/zero.dat");       // → 无密码时是 raw 直传；有密码时退回加密单文件
            // 同目录里放个有内容的邻居：空成员不该把整箱带坏，也不该让邻居丢内容。
            var neighbour = new string('n', 1_000);
            File.WriteAllText(Path.Combine(_src, "packed", "neighbour.txt"), neighbour);
            // 空目录走的是索引里独立的 EmptyDirs 名单，不经过内容存储——和空文件一起测，
            // 是为了确保针对"零长度"的任何特殊处理都没有顺手把它一并吞掉。
            Directory.CreateDirectory(Path.Combine(_src, "hollow", "deeper"));

            var result = await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "empty-files",
                Password = password,
                Options = EngineOptions(),
            });

            Assert.Equal(4, result.NewFiles);

            await restore.RunAsync(new RestoreRequest
            {
                Account = account,
                Container = name,
                TargetRoot = _dst,
                Password = password,
            });

            foreach (var rel in new[] { "packed/zero.txt", "solo/zero.bin", "raw/zero.dat" })
            {
                var path = Path.Combine(_dst, rel.Replace('/', Path.DirectorySeparatorChar));
                // 存在性是这里的要害：空文件被当成"没有内容"而整个跳过的话，还原出来的树会
                // 少一个文件，而所有按字节比对的断言都会开开心心地通过。
                Assert.True(File.Exists(path), $"{rel} 没有被还原出来");
                var bytes = await File.ReadAllBytesAsync(path);
                Assert.True(bytes.Length == 0,
                    $"{rel} 还原后有 {bytes.Length} 字节，前 8 个："
                    + Convert.ToHexString(bytes.AsSpan(0, Math.Min(8, bytes.Length))));
            }

            Assert.Equal(neighbour, await File.ReadAllTextAsync(
                Path.Combine(_dst, "packed", "neighbour.txt")));
            Assert.True(Directory.Exists(Path.Combine(_dst, "hollow", "deeper")), "空目录没有被还原出来");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 空文件不该在云端占任何东西。它没有内容，却曾经要被压成一个**比原文件还大**的 7z 归档
    /// （0 字节 → 131 字节）、占一个内容寻址地址、走一次上传一次下载一次解压。
    /// <para>
    /// 这也是那个竞态的根：所有空文件的 fullHash 相同，于是它们全挤在同一个 data/{hash} 上，
    /// 而"压成归档"与"raw 直传"在那个地址上的字节完全不同——谁先传完就决定了后到者索引里的
    /// raw 标志，对不上的那次还原会把归档本身当成文件内容写出来。不上传，这一整类就不存在了。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Empty_Files_Cost_Nothing_In_The_Cloud()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore) = Build(localAuthoritative: true);
        var account = AzuriteAccount();
        var name = RandomName("emptycost-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteEmpty("a/zero1.txt");
            WriteEmpty("b/zero2.bin");
            WriteEmpty("solo/zero3.dat");
            Directory.CreateDirectory(Path.Combine(_src, "hollow"));

            var result = await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "empty-only",
                Options = EngineOptions(),
            });

            Assert.Equal(3, result.NewFiles);
            Assert.Equal(0, result.UploadedBytes);

            // 容器里不该有任何 data blob 或 pack——只有索引与信息文件。
            var stored = new List<string>();
            await foreach (var b in container.GetBlobsAsync())
                stored.Add(b.Name);
            Assert.DoesNotContain(stored, n => n.StartsWith("data/", StringComparison.Ordinal));
            Assert.DoesNotContain(stored, n => n.StartsWith("packs/", StringComparison.Ordinal));

            // 而且照样还原得回来。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            foreach (var rel in new[] { "a/zero1.txt", "b/zero2.bin", "solo/zero3.dat" })
            {
                var path = Path.Combine(_dst, rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"{rel} 没有被还原出来");
                Assert.Empty(await File.ReadAllBytesAsync(path));
            }
            Assert.True(Directory.Exists(Path.Combine(_dst, "hollow")), "空目录没有被还原出来");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 老备份里的空文件带着 storage 引用（那时它们照常被压缩上传）。这些条目必须在下一次备份时
    /// **自己变干净**，而不是等用户去动那个文件。
    /// <para>
    /// 不修的话它们永远好不了：一个从不变化的空文件（.gitkeep、__init__.py、锁文件……）每轮都被
    /// 判成 Unchanged，而 Unchanged 会把上一版本的 Storage 原样带进新索引（BackupDiffer.Unchanged
    /// → CarriedStorage）。要是那条引用当初就记错了 raw 标志，它会一代代传下去，而用户完全没有
    /// 理由去碰一个从没变过的文件。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Inherited_Storage_Refs_On_Empty_Files_Are_Dropped_On_The_Next_Backup()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var (backup, restore) = Build(localAuthoritative: false); // 直接读云端索引，好让篡改生效
        var account = AzuriteAccount();
        var name = RandomName("inherit-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteEmpty("packed/zero.txt");
            File.WriteAllText(Path.Combine(_src, "packed", "other.txt"), new string('o', 2_000));
            await backup.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _src,
                Name = "inherit", Options = EngineOptions(),
            });

            // 把 v1 改成"老备份的样子"：给空文件条目塞一个 storage 引用。
            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var index = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var donor = index.Entries.Single(e => e.Path == "packed/other.txt").Storage;
            Assert.NotNull(donor);
            Assert.Null(index.Entries.Single(e => e.Path == "packed/zero.txt").Storage); // 新代码本就不给

            await store.WriteIndexAsync(account, name, v1.Version, new VersionIndex
            {
                Version = index.Version,
                EmptyDirs = index.EmptyDirs,
                Entries = [.. index.Entries.Select(e => e.Path == "packed/zero.txt"
                    ? e with { Storage = donor with { EntryName = "packed/zero.txt" } }
                    : e)],
            }, null);

            // 源文件一个字节都不动 → diff 判 Unchanged，正是最容易一路沿用下去的那条路。
            await backup.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _src,
                Name = "inherit", Options = EngineOptions(),
            });

            var info2 = await store.ReadInfoAsync(account, name, null);
            var v2 = info2!.Versions[^1];
            Assert.NotEqual(v1.Version, v2.Version);
            var index2 = await store.ReadIndexAsync(account, name, v2.IndexBlob, null);
            var healed = index2.Entries.Single(e => e.Path == "packed/zero.txt");
            Assert.Null(healed.Storage);
            Assert.Equal(0, healed.Length);

            // 自愈之后照样还原得回来。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            var dest = Path.Combine(_dst, "packed", "zero.txt");
            Assert.True(File.Exists(dest), "自愈后的空文件没有被还原出来");
            Assert.Empty(await File.ReadAllBytesAsync(dest));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 同一批里内容相同、却被规则指派成不同存储形态的**非空**文件。store-only 的那份走 raw 直传
    /// （裸字节），另一份压成 7z 归档——两者字节完全不同，可它们 fullHash 相同，于是指向同一个
    /// data/{hash} 地址。没有同批协调时，两个并发任务会各自 HEAD 到同一个空位、各自上传，后写的
    /// 被 UploadIfMissing 跳过，而两条索引条目各记各的 raw 标志：其中一条必然与 blob 里真正躺着的
    /// 字节对不上，还原时把归档本身当成文件内容写出来。
    /// <para>
    /// 只测云端回退接线：本地权威那条路由 LocalDedupResolver 的预约表保护着，本来就不会撞。
    /// </para>
    /// <para>
    /// 老实说：这个用例**没能**在加同批协调之前复现出错误（撤掉修复跑 6 轮全绿）。原因想清楚了——
    /// raw 是拷贝、7z 是压缩，非空内容下拷贝总是先落地，压缩那个再去 HEAD 时已经看得见 blob，
    /// 于是去重命中并继承 raw=true，两条条目自然一致。也就是说这里的正确性一直靠"拷贝比压缩快"
    /// 这个时序差撑着，而不是靠设计——分卷会让 store-only 退回 7z、高压缩比的小文件会让压缩变得
    /// 和拷贝一样快（空文件就是极端情形，见上一个用例），假设随时可能不成立。
    /// 所以这条守的是不变量本身：**同内容不论被指派成哪种形态，最终必须指向同一个 blob、
    /// 且 raw 标志一致**。它是回归护栏，不是那个竞态的复现器。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Same_Content_In_Different_Storage_Shapes_Agrees_On_One_Blob()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore) = Build(localAuthoritative: false);
        var account = AzuriteAccount();
        var name = RandomName("shapes-");
        var container = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 不可压缩的内容：raw 与归档的字节差异因此明显，谁顶替了谁一望即知。
            var payloads = new List<byte[]>();
            for (var i = 0; i < 6; i++)
            {
                var buf = new byte[40_000];
                new Random(1000 + i).NextBytes(buf);
                payloads.Add(buf);
                Directory.CreateDirectory(Path.Combine(_src, "solo"));
                Directory.CreateDirectory(Path.Combine(_src, "raw"));
                // 同一份内容，一份走 7z 单文件 blob，一份走 raw 直传。
                await File.WriteAllBytesAsync(Path.Combine(_src, "solo", $"c{i}.bin"), buf);
                await File.WriteAllBytesAsync(Path.Combine(_src, "raw", $"c{i}.bin"), buf);
            }

            await backup.RunAsync(new BackupRequest
            {
                Account = account,
                Container = name,
                LocalRoot = _src,
                Name = "shapes",
                Options = EngineOptions(),
            });

            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });

            for (var i = 0; i < payloads.Count; i++)
            {
                foreach (var dir in new[] { "solo", "raw" })
                {
                    var path = Path.Combine(_dst, dir, $"c{i}.bin");
                    Assert.True(File.Exists(path), $"{dir}/c{i}.bin 没有被还原出来");
                    var got = await File.ReadAllBytesAsync(path);
                    Assert.True(payloads[i].SequenceEqual(got),
                        $"{dir}/c{i}.bin 还原后的字节与源不符（长度 {got.Length}，应为 {payloads[i].Length}）");
                }
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
