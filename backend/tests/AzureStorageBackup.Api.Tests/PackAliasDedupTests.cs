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

    /// <param name="deadWeightCompaction">
    /// 给保留清理接上真正的 <see cref="DeadWeightCompactor"/>（生产里 Program.cs 就是这么接的，
    /// 且与备份共用同一个 <see cref="StagingArea"/>）。默认不接：多数用例只关心装箱与还原，
    /// 接上等于每轮备份收尾都多下载/重压一遍包。
    /// </param>
    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store) Build(
        IFileCompressor? compressor = null, bool deadWeightCompaction = false)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var indexCache = new LocalIndexCache(_db, store);
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(_db));
        var compactor = deadWeightCompaction
            ? new DeadWeightCompactor(
                new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(),
                Path.Combine(_temp, "compact"), staging)
            : null;
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            compressor ?? new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor, indexCache, tracked),
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

    /// <summary>把某个包解开，列出归档里**实际**有哪些成员（含目录段，ordinal 序）。
    /// 索引怎么写是一回事，归档里到底装了几个成员是另一回事——去重与压实都要看后者才算数。</summary>
    private async Task<List<string>> PackEntryNamesAsync(Azure.Storage.Blobs.BlobContainerClient cc, string packId)
    {
        var work = Path.Combine(_temp, "peek-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var first = await VolumeBlobIO.DownloadAsync(cc, $"packs/{packId}.7z", work, CancellationToken.None);
        var extracted = Path.Combine(work, "x");
        await new SevenZipCompressor().ExtractAsync(first, extracted, null, CancellationToken.None);
        return [.. Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(extracted, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(x => x, StringComparer.Ordinal)];
    }

    /// <summary>某个包在容器里占的总字节（含全部分卷）。压实前后比一比，就知道包是不是真被重写了。</summary>
    private static async Task<long> PackBytesAsync(Azure.Storage.Blobs.BlobContainerClient cc, string packId)
    {
        long total = 0;
        await foreach (var b in cc.GetBlobsAsync(
            BlobTraits.None, BlobStates.None, $"packs/{packId}.7z", CancellationToken.None))
            total += b.Properties.ContentLength ?? 0;
        return total;
    }

    /// <summary>确定性的伪随机字母串：压不动多少，尺寸差异藏不住。同 seed 出同一串，不同 seed 内容必不同。</summary>
    private static string Noise(int seed, int length)
    {
        var rnd = new Random(seed);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = (char)('a' + rnd.Next(26));
        return new string(chars);
    }

    /// <summary>
    /// T1 + T2：**同一轮**里三个小文件、其中两个同内容，一箱只装一个成员。
    /// 去重生效时只该有两个包（不是三个），第二条条目指向第一条那个成员，而且两条都还原得回来。
    /// <para>
    /// T2 的后半句同样要钉：两条条目共用同一个归档成员，但 mtime 与权限是**各自**的。
    /// 归档里只躺着 leader 那一份字节，元数据却必须来自各条目自己（<c>RestoreOrchestrator</c> 的
    /// <c>ApplyMetadata(dest, entry)</c>）。写错的形状是"别名还原出来带着 leader 的时间戳/权限"——
    /// 内容对、元数据错，还原结果看着没问题，下一轮备份却会因为 mtime 变了而把它整个重备一遍，
    /// 而权限错则是实打实的安全问题（0600 的文件还原成 0644）。
    /// </para>
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

            // 两条路径的元数据必须**不同**，否则"各自正确"与"都取了 leader 的"是同一个结果，测不出东西。
            // mtime 由 Write 自增（leader = base+1min，别名 = base+3min）；权限在这里再拉开一档。
            var leaderSrc = Path.Combine(_src, "a", "first.txt");
            var aliasSrc = Path.Combine(_src, "c", "second.txt");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(leaderSrc, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // 0600
                File.SetUnixFileMode(
                    aliasSrc,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead);                            // 0644
            }
            var leaderMtime = File.GetLastWriteTimeUtc(leaderSrc);
            var aliasMtime = File.GetLastWriteTimeUtc(aliasSrc);
            Assert.NotEqual(leaderMtime, aliasMtime);

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
            var leaderDst = Path.Combine(_dst, "a", "first.txt");
            var aliasDst = Path.Combine(_dst, "c", "second.txt");
            Assert.Equal(payload, await File.ReadAllTextAsync(leaderDst));
            Assert.Equal(payload, await File.ReadAllTextAsync(aliasDst));

            // 元数据各归各的：别名拿到的绝不能是 leader 那一份。
            Assert.Equal(leaderMtime, File.GetLastWriteTimeUtc(leaderDst));
            Assert.Equal(aliasMtime, File.GetLastWriteTimeUtc(aliasDst));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(leaderDst));
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    File.GetUnixFileMode(aliasDst));
            }
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
            // 这条测试的前提是 v1 已经退役（MaxVersions = 1），包才只能靠别名条目钉住。
            // 不断言这一条，v1 万一没退役，"包没被删"就会因为 leader 自己在 v1 里的旧条目
            // 而通过——测试变成一个测不出东西的假象，根本没验证到别名钉包这件事。
            Assert.Single(info!.Versions);
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

    /// <summary>
    /// T7：两条条目指向同一个 pack 成员，检查必须把两条都判健康。
    /// <para>
    /// BackupChecker 逐条查 actual[entryName]，两条查的是同一项、内容当然相同；而
    /// "归档吐出的成员数 == 列举出的成员数" 那条前提也不受影响——别名不进归档。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Check_Reports_Both_Entries_Healthy()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, _, _) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packaliaschk-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            Write("a/first.txt", payload);
            Write("c/second.txt", payload);
            await backup.RunAsync(Request(account, name));

            var checker = new BackupChecker(
                factory, new BackupInfoStore(factory, new SevenZipArchiveCodec()),
                new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"));
            var report = await checker.CheckAsync(account, name, null, null, new CheckOptions());

            // 一条损坏都不该有，两条条目都要出现在结论里。
            Assert.True(report.Ok);
            Assert.Empty(report.CorruptedPaths);
            Assert.Contains(report.Findings, f => f.Path == "a/first.txt");
            Assert.Contains(report.Findings, f => f.Path == "c/second.txt");
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 生产里最常见的那个形状：**同一个目录**里放了两份一样的文件（下载两遍、复制一份改个名）。
    /// <para>
    /// 上面所有别名用例都是跨目录的，而同目录这一档的行为是真的变了：从前两个文件是同一个 solid
    /// 归档里的两个成员（7z 的字典跨成员匹配，本来就几乎不占额外字节），现在第二个成了别名、
    /// 归档里只剩一个成员。逻辑上等价——两条索引条目、一份内容——但"等价"是推出来的，没跑过。
    /// </para>
    /// <para>
    /// 一箱只装一个成员（MaxPackMembers = 1），所以没有去重就是两个包；有去重是一个包、
    /// 且归档里只有一个成员。两个数都断言：只看包数，万一将来变成"一箱两个成员"也照样过。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Same_Content_In_The_Same_Directory_Is_Stored_Once()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packaliassamedir-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            var payload = Noise(20260807, 4000);
            Write("d/one.txt", payload);        // leader（同目录内 ordinal 路径序最先）
            Write("d/two.txt", payload);        // 别名，同目录

            await backup.RunAsync(Request(account, name));

            Assert.Equal(1, await CountPacksAsync(cc));

            var info = await store.ReadInfoAsync(account, name, null);
            var packId = Assert.Single(info!.Packs.Keys);
            // 归档里实际只有一个成员——这才是"只存了一份"的直接证据。
            Assert.Equal(["d/one.txt"], await PackEntryNamesAsync(cc, packId));

            var v1 = await store.ReadIndexAsync(account, name, info.Versions[^1].IndexBlob, null);
            var one = v1.Entries.Single(e => e.Path == "d/one.txt");
            var two = v1.Entries.Single(e => e.Path == "d/two.txt");
            Assert.Equal("pack", two.Storage!.Kind);
            Assert.Equal(one.Storage!.Ref, two.Storage.Ref);
            Assert.Equal("d/one.txt", two.Storage.EntryName);

            // 两条都要还原到自己的路径上。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "d", "one.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "d", "two.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// T5 的端到端版，也是本特性最凶的一条链：**本特性产出的别名** → 触发死重压实**重写**那个包
    /// → 压实之后别名仍能还原出正确内容。
    /// <para>
    /// 与 <c>DeadWeightCompactorTests</c> 里那些用例的区别：那边的包和 liveByPack 都是手工构造的
    /// 历史形状，且压实完从没跑过一次还原。这里从头到尾是真的——包由本轮备份封出来、别名由本
    /// 特性产生、liveByPack 由 <c>RetentionCleaner</c> 自己扫索引归组、压实由阈值真的触发、
    /// 最后真的还原一次。
    /// </para>
    /// <para>
    /// 挑的是 T3 叠加压实这个最凶的组合：v2 把 leader 那个**路径**连同三个死重一起删掉，于是
    /// <list type="bullet">
    /// <item>包里唯一的存活成员 <c>a/leader.txt</c> 只由别名条目 <c>c/alias.txt</c> 独自钉住
    /// （liveByPack 按 EntryName 归组）；</item>
    /// <item><c>hasAbsentLocal</c> 为真（本地已经没有 a/leader.txt 了），压实走的是**下载重组**
    /// 那条路：下载旧包 → 解压 → 只把存活成员放进 compose 目录 → 重压覆盖同一个 packId。</item>
    /// </list>
    /// 这条链每一环理论上都通，但从没实跑过。踩断其中任何一环（比如归组改按 fullHash、或者
    /// 本地探测不到就整包放弃），别名的数据就在一次自动清理里静默没了。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task An_Alias_Survives_Dead_Weight_Compaction_That_Rewrites_Its_Pack()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        // 接上真正的死重压实器（生产 DI 就是这么接的）。
        var (backup, restore, store) = Build(deadWeightCompaction: true);
        var account = AzuriteAccount();
        var name = RandomName("packaliascompact-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        // 默认 MaxPackMembers（不是 1）：a/ 整个目录装一箱，leader 才可能和死重同处一包——
        // 一箱一个成员的话，死重和存活成员各在各的包里，压实根本无从发生。
        // MaxVersions = 1：v1 退役，死重才会出现（死重只在版本退役时增加）。
        // DeadWeightThreshold 用默认的 0.30，AllowRepackDownload 用默认的 true（下载重组要它）。
        var request = Request(account, name) with
        {
            Options = new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
            },
        };

        try
        {
            var payload = Noise(1, 400);
            Write("a/leader.txt", payload);       // leader：v2 之后是包里唯一的存活成员
            Write("a/dead1.txt", Noise(2, 20_000));   // 三份将来的死重，各不相同（否则它们之间也会去重）
            Write("a/dead2.txt", Noise(3, 20_000));
            Write("a/dead3.txt", Noise(4, 20_000));
            Write("c/alias.txt", payload);        // 别名，另一个目录，本轮不入箱

            await backup.RunAsync(request);

            var infoV1 = await store.ReadInfoAsync(account, name, null);
            var packId = Assert.Single(infoV1!.Packs.Keys);
            // 前提确认：五个文件、一个包、包里四个成员（别名不入箱）。
            Assert.Equal(1, await CountPacksAsync(cc));
            Assert.Equal(4, infoV1.Packs[packId].Members.Count);
            Assert.Equal(
                ["a/dead1.txt", "a/dead2.txt", "a/dead3.txt", "a/leader.txt"],
                await PackEntryNamesAsync(cc, packId));
            var v1 = await store.ReadIndexAsync(account, name, infoV1.Versions[^1].IndexBlob, null);
            // 前提确认：c/alias.txt 确实是**本特性产出的别名**，不是碰巧各存各的。
            Assert.Equal("a/leader.txt", v1.Entries.Single(e => e.Path == "c/alias.txt").Storage!.EntryName);
            var bytesBefore = await PackBytesAsync(cc, packId);

            // v2：整个 a/ 目录删掉。leader 那个路径连同三份死重一起消失，包里只剩 a/leader.txt
            // 这一个成员还被引用着——而引用它的只有别名条目。
            Directory.Delete(Path.Combine(_src, "a"), recursive: true);
            var run2 = await backup.RunAsync(request);

            // 压实真的触发了吗？逐条钉死，不靠"跑完没报错"：
            // ① v1 必须真的退役，死重才存在（不退役 → 三份死重仍被 v1 引用 → ratio = 0 → 不触发）。
            Assert.Equal(1, run2.Cleanup.RetiredVersions);
            var infoV2 = await store.ReadInfoAsync(account, name, null);
            Assert.Single(infoV2!.Versions);
            // ② 包还在（别名钉住了它），且**同一个 packId**——压实是原地重写，不是新建。
            Assert.Contains(packId, infoV2.Packs.Keys);
            // ③ 成员表从 4 缩到 1，死重归零：这只可能是 RecompactAsync 走完 newSizes.Count > 0
            //    那一支才写得出来（放弃压实那一支只改 DeadBytes，成员表原封不动）。
            Assert.Single(infoV2.Packs[packId].Members);
            Assert.Equal(0, infoV2.Packs[packId].DeadBytes);
            Assert.Equal(payload.Length, infoV2.Packs[packId].OriginalBytes);
            // ④ 云端归档本身被改写了：只剩存活成员，尺寸大幅缩小（三份 2 万字节的伪随机文本没了）。
            Assert.Equal(["a/leader.txt"], await PackEntryNamesAsync(cc, packId));
            var bytesAfter = await PackBytesAsync(cc, packId);
            Assert.True(bytesAfter < bytesBefore / 2,
                $"pack should have been rewritten much smaller, was {bytesBefore} → {bytesAfter}");
            // ⑤ 走的确实是**下载重组**那条路：压实时本地根本没有 a/leader.txt 可用作修复源，
            //    唯一的来源只能是下载旧包解压出来的那一份。
            Assert.False(Directory.Exists(Path.Combine(_src, "a")));

            // 决定性的一条：压实重写过的包里，别名仍然还原得出正确内容。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "alias.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "a", "leader.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 悬空重跑按压法分两组（<c>orphanAliases.ToLookup(… DontCompress …)</c>）。此前所有用例的
    /// <c>DontCompress</c> 都是 null，<c>ToLookup</c> 永远只出一组，那个 <c>foreach</c> 有一半从没执行过。
    /// <para>
    /// 这里让悬空的别名**跨越两种压法**：leader 在压缩窗口里被改写 → 挂在它身上的两个别名一起悬空，
    /// 一个命中不压缩规则、一个不命中。两组都得被跑到，且各自用对的压法——一箱只能有一种压法，
    /// 混装的话规则对被打包的文件就等于不存在。
    /// </para>
    /// <para>
    /// 怎么确认两组**都**走到了、而且是分开走的：两个别名落在**不同**的包上，且这两个包的
    /// <c>PackInfo.StoreOnly</c> 一真一假，云端尺寸也一大一小（只存的那箱约等于原文件大小，
    /// 压缩的那箱小两三个数量级）。要是分流被去掉、两个别名塞进同一次 ProcessPackAsync，
    /// 它们的压法就会是同一个，这三条断言会一起变红。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Orphan_Aliases_Are_Rerun_On_Both_Sides_Of_The_Dont_Compress_Rule()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var account = AzuriteAccount();
        var name = RandomName("packaliasstoreonly-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // 高度可压缩且够大：只存的那一箱与压缩的那一箱尺寸差距因此是肉眼级的，压法有没有落到
            // 7z 上不必靠推理。
            const int filler = 200_000;
            var payload = new string('q', filler);
            const string mutated = "leader got rewritten while it was being compressed";
            Write("a/first.txt", payload);        // leader（ordinal 路径序最先）
            Write("c/second.txt", payload);       // 别名一：不命中规则 → 压缩箱
            Write("n/third.log", payload);        // 别名二：命中 *.log → 只存箱

            // 压缩之后改写 leader：重校验发现它变了 → overrides 记下新 hash → 两个别名一起悬空。
            var (backup, restore, store) = Build(
                new MutatingAfterCompressCompressor(new SevenZipCompressor(), _src, "a/first.txt", mutated));

            await backup.RunAsync(Request(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000, MaxPackMembers = 1 },
                    DontCompress = new IgnoreRuleSet(["*.log"]),
                },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            var index = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var leader = index.Entries.Single(e => e.Path == "a/first.txt");
            var second = index.Entries.Single(e => e.Path == "c/second.txt");
            var third = index.Entries.Single(e => e.Path == "n/third.log");

            // 前提确认：两个别名真的悬空了（没有指向 leader 那份新内容），各自另存了一份。
            Assert.NotEqual(leader.FullHash, second.FullHash);
            Assert.Equal(second.FullHash, third.FullHash);
            Assert.Equal("pack", second.Storage!.Kind);
            Assert.Equal("pack", third.Storage!.Kind);
            // 悬空别名之间不再互相去重（设计里写明的取舍），所以它们必然各在一箱。
            Assert.NotEqual(second.Storage.Ref, third.Storage.Ref);

            // 两组都走到了、且各用各的压法。
            Assert.False(info.Packs[second.Storage.Ref].StoreOnly);
            Assert.True(info.Packs[third.Storage.Ref].StoreOnly);

            // 压法真的落到 7z 上了：只存的那箱约等于原文件大小，压缩的那箱小得多。
            var compressedBytes = await PackBytesAsync(cc, second.Storage.Ref);
            var storedBytes = await PackBytesAsync(cc, third.Storage.Ref);
            Assert.True(storedBytes > filler * 0.9,
                $"store-only pack should be about the original size, was {storedBytes}");
            Assert.True(compressedBytes < filler / 10,
                $"compressed pack should be far smaller than the original, was {compressedBytes}");

            // 决定性的一条：三条路径全都还原出各自应有的内容。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(mutated, await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "n", "third.log")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 装箱决定点那段顺序论证的用例："leader 若命中既有包，后来的同内容文件用同一张 _packMembers、
    /// 同一套四项判据也会命中第一档，根本走不到别名表。"——至今只靠推理成立。
    /// <para>
    /// 两轮备份：v1 存下某内容；v2 里新增**两个**同内容的新文件。两个都该命中跨版本去重、指向 v1
    /// 那个包的成员，不产生任何新包，也不该形成"别名指向本轮 leader"的形状。
    /// </para>
    /// <para>
    /// 判据就在 <c>EntryName</c> 上：命中跨版本去重时它是 v1 那个路径（<c>a/first.txt</c>）；
    /// 若两档的先后被调换、或者别名表抢先一步，第二、三条会指向本轮的 <c>c/second.txt</c>，
    /// 而且会多出一个包。两条都不是无害的差别——引用聚到本轮的新包上，老包一退役就要被重写，
    /// 而 <c>LocalDedupResolver</c> 特意把引用聚到老包正是为了避免这件事。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Later_Duplicates_Hit_Cross_Version_Dedup_Instead_Of_The_Alias_Table()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packaliascrossver-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            var payload = Noise(777, 4000);
            Write("a/first.txt", payload);
            await backup.RunAsync(Request(account, name));

            var packsAfterV1 = await CountPacksAsync(cc);
            Assert.Equal(1, packsAfterV1);
            var infoV1 = await store.ReadInfoAsync(account, name, null);
            var packId = Assert.Single(infoV1!.Packs.Keys);

            // v2：两个同内容的**新**文件。都该指向 v1 那个包的成员。
            Write("c/second.txt", payload);
            Write("d/third.txt", payload);
            await backup.RunAsync(Request(account, name));

            // 一个新包都不该有——两条都在跨版本那一档就被拦下了，根本没进装箱。
            Assert.Equal(packsAfterV1, await CountPacksAsync(cc));

            var infoV2 = await store.ReadInfoAsync(account, name, null);
            Assert.Equal([packId], infoV2!.Packs.Keys);
            var v2 = await store.ReadIndexAsync(account, name, infoV2.Versions[^1].IndexBlob, null);
            foreach (var path in new[] { "a/first.txt", "c/second.txt", "d/third.txt" })
            {
                var storage = v2.Entries.Single(e => e.Path == path).Storage!;
                Assert.Equal("pack", storage.Kind);
                Assert.Equal(packId, storage.Ref);
                // 关键：成员名是 **v1** 那个路径。别名表抢了先的话，后两条会指向 c/second.txt。
                Assert.Equal("a/first.txt", storage.EntryName);
            }

            // 三条都还原得回来。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "d", "third.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
