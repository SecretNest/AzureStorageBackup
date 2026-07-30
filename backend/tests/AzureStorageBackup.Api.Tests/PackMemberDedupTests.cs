using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 打包小文件的**文件级**去重。单文件 blob 一直是内容寻址的，同内容只存一份；打包成员却没有——
/// 同一份内容出现在两个箱里就实打实地存两遍。
/// <para>
/// 同一箱内的重复本来就被 7z 的 solid 归档消掉了（字典跨成员匹配），所以这里要覆盖的是**跨箱、
/// 跨版本**那部分：不同箱之间压缩不共享字典。
/// </para>
/// <para>
/// 对已有备份必须是**只读**的：老索引一字不改，只是多一种命中可能；命中后写下的引用形状与从前
/// 逐字节相同，所以保留清理、死重压实、还原三处都不必改。这几条都在下面钉住。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackMemberDedupTests : IDisposable
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

    public PackMemberDedupTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-packdedup-" + Guid.NewGuid().ToString("N"));
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

    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store) Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        // 本地权威接线：打包成员去重靠本地缓存的索引判定，不读云端。
        var indexCache = new LocalIndexCache(_db, store);
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(_db));
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), null, indexCache, tracked),
            new FileHasher(), indexCache: indexCache, trackedInfo: tracked);
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"));
        return (backup, restore, store);
    }

    /// <summary>阈值给足，让这些几十字节的文件全都走 pack 路径。</summary>
    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _src,
        Name = "packdedup",
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
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
    /// 第二个版本新增一个与既有成员**同内容、不同路径**的小文件：不该产生新的 pack，
    /// 新条目直接指向老包里那个成员，而且还原出来的内容必须对。
    /// </summary>
    [SkippableFact]
    public async Task A_New_File_Matching_An_Existing_Pack_Member_Reuses_It()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packdedup-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // 不可压缩的内容：万一真的重装了一箱，体积差异藏不住。
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            Write("docs/original.txt", payload);
            Write("docs/neighbour.txt", "something else entirely");
            await backup.RunAsync(Request(account, name));

            var packsAfterV1 = await CountPacksAsync(cc);
            Assert.True(packsAfterV1 > 0, "v1 应该产生了至少一个包");

            // v2：新增一个同内容、不同路径的文件。
            Write("archive/copy-of-original.txt", payload);
            await backup.RunAsync(Request(account, name));

            Assert.Equal(packsAfterV1, await CountPacksAsync(cc)); // 一个新包都不该有

            var info = await store.ReadInfoAsync(account, name, null);
            var v2 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var original = v2.Entries.Single(e => e.Path == "docs/original.txt");
            var copy = v2.Entries.Single(e => e.Path == "archive/copy-of-original.txt");

            // 指向同一个包的同一个成员——成员名是**最初**那个路径，不是新路径。
            Assert.Equal("pack", copy.Storage!.Kind);
            Assert.Equal(original.Storage!.Ref, copy.Storage.Ref);
            Assert.Equal(original.Storage.EntryName ?? original.Path, copy.Storage.EntryName ?? copy.Path);

            // 而且还原得回来，写到**自己**的路径上。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(
                Path.Combine(_dst, "archive", "copy-of-original.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "docs", "original.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 保留清理必须看得见这种跨版本引用。老版本退役后，那个包仍被新版本的条目引用着——
    /// 删掉它就等于把新版本的数据删了。
    /// </summary>
    [SkippableFact]
    public async Task Retention_Keeps_A_Pack_Still_Referenced_Through_Dedup()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packdedupret-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        var keepOne = Request(account, name) with
        {
            Options = new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
            },
        };

        try
        {
            var payload = string.Concat(Enumerable.Range(0, 300).Select(i => ((char)('m' + i % 13)).ToString()));
            Write("a/one.txt", payload);
            await backup.RunAsync(keepOne);

            // v2 新增同内容文件 → 去重指向 v1 的包；同时 v1 会被退役（只留 1 个版本）。
            Write("b/two.txt", payload);
            await backup.RunAsync(keepOne);

            var info = await store.ReadInfoAsync(account, name, null);
            var v = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var two = v.Entries.Single(e => e.Path == "b/two.txt");

            // 它引用的包必须还在——不然还原就取不到内容了。
            var packBlob = $"packs/{two.Storage!.Ref}.7z";
            var exists = await cc.GetBlobClient(packBlob).ExistsAsync()
                         || await cc.GetBlobClient(packBlob + ".001").ExistsAsync();
            Assert.True(exists, $"{packBlob} 被 b/two.txt 引用着，不该被保留清理删掉");

            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "b", "two.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 打包成员的索引条目要带上尾部 hash——判据与单文件 blob 那条路一致（四项），
    /// 不能两条路各有一套标准。而**未变**的文件也要补上：老索引里一项都没有，
    /// 而未变文件永远走不到会重算 hash 的分支，不补就永远缺。补一次就自愈。
    /// </summary>
    [SkippableFact]
    public async Task Packed_Members_Carry_A_Tail_Hash_And_Missing_Ones_Heal()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, _, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packtail-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            Write("docs/a.txt", new string('a', 400));
            await backup.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var packed = v1.Entries.Single(e => e.Path == "docs/a.txt");
            Assert.Equal("pack", packed.Storage!.Kind);
            Assert.NotNull(packed.TailHash);   // 新写的条目就该有

            // 把它抹掉，做出"老索引"的样子，再跑一轮——文件一个字节都没动（未变路径）。
            await store.WriteIndexAsync(account, name, v1.Version, new VersionIndex
            {
                Version = v1.Version,
                EmptyDirs = v1.EmptyDirs,
                Entries = [.. v1.Entries.Select(e => e with { TailHash = null })],
            }, null);

            await backup.RunAsync(Request(account, name));

            var info2 = await store.ReadInfoAsync(account, name, null);
            var v2 = await store.ReadIndexAsync(account, name, info2!.Versions[^1].IndexBlob, null);
            var healed = v2.Entries.Single(e => e.Path == "docs/a.txt");
            Assert.NotNull(healed.TailHash);   // 未变文件也补上了
            Assert.Equal(packed.TailHash, healed.TailHash);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 内容**不同**的文件绝不能被误判成同一个成员。这条守的是去重键本身：
    /// 三项（fullHash + 长度 + head）里任何一项不同就必须各存一份。
    /// </summary>
    [SkippableFact]
    public async Task Different_Content_Is_Never_Folded_Together()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packdedupdiff-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            Write("x/first.txt", new string('p', 500));
            await backup.RunAsync(Request(account, name));

            // 等长但内容不同：只有长度相同，hash 不同 → 必须各存一份。
            Write("y/second.txt", new string('q', 500));
            await backup.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v2 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var first = v2.Entries.Single(e => e.Path == "x/first.txt");
            var second = v2.Entries.Single(e => e.Path == "y/second.txt");
            Assert.NotEqual(
                (first.Storage!.Ref, first.Storage.EntryName ?? first.Path),
                (second.Storage!.Ref, second.Storage.EntryName ?? second.Path));

            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(new string('p', 500), await File.ReadAllTextAsync(Path.Combine(_dst, "x", "first.txt")));
            Assert.Equal(new string('q', 500), await File.ReadAllTextAsync(Path.Combine(_dst, "y", "second.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
