using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 回归：修复(§3.2)必须经本地权威状态机(TrackedInfoStore + ILocalIndexCache)写信息文件/版本索引，
/// 否则本地缓存的 ETag 与云端脱节，下一次备份的条件写会 412 一次。
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupRepairerTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _src;
    private readonly string _temp;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public BackupRepairerTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-repair-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_src);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 1,
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

    /// <summary>只记录写入内容的 IOperationLog spy（用于断言修复器留下的审计痕迹）。</summary>
    private sealed class RecordingOperationLog : IOperationLog
    {
        public List<(OperationLogLevel Level, string Source, string Message)> Entries { get; } = [];

        public Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default, bool? durable = null)
        {
            lock (Entries) Entries.Add((level, source, message));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LogEntry>> QueryAsync(
            OperationLogLevel? minLevel, string? source, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LogEntry>>([]);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;
        public Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;
    }

    private (BackupOrchestrator Backup, BackupChecker Checker, BackupRepairer Repairer, TrackedInfoStore Tracked, ILocalIndexCache IndexCache, BlobClientFactory Factory) Build(
        IOperationLog? opLog = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var state = new LocalBackupStateStore(_db);
        var tracked = new TrackedInfoStore(store, state);
        var indexCache = new LocalIndexCache(_db, store);
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
            indexCache: indexCache, trackedInfo: tracked);
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"),
            trackedInfo: tracked);
        var repairer = new BackupRepairer(
            factory, store, new SevenZipCompressor(), new FileHasher(), new BlobUploader(factory),
            Path.Combine(_temp, "repair"), opLog: opLog, checker: checker, trackedInfo: tracked, indexCache: indexCache);
        return (backup, checker, repairer, tracked, indexCache, factory);
    }

    private BackupRequest Req(Account a, string c, IgnoreRuleSet? dontCompress = null, string? password = null) => new()
    {
        Account = a, Container = c, LocalRoot = _src, Name = "photos", Password = password,
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
            DontCompress = dontCompress,
        },
    };

    [SkippableFact]
    public async Task Repair_Updates_Local_Authoritative_State_So_Next_Write_Does_Not_Conflict()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, tracked, indexCache, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rep2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // v1：一个 data blob（走本地权威状态机——回填本地 ETag/索引缓存）。
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "repair me please, local-authoritative");
            await backup.RunAsync(Req(account, name));

            // 云端该 blob 丢失；本地文件仍在（可从本地修复）。
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null);
            Assert.Contains("a.txt", report.Repaired);
            Assert.Empty(report.Unrecoverable);

            // 修复必须经本地权威状态机：索引缓存里的版本 1 应已刷新（identity 匹配本次信息文件）。
            var info = await tracked.LoadAsync(account, name, null);
            Assert.NotNull(info);
            var v1 = info!.Versions.Single(x => x.Version == 1);
            var identity = info.Backup.CreatedAt.UtcTicks;
            var cachedIndex = await indexCache.ReadAsync(account, name, 1, identity, v1.IndexBlob, null);
            Assert.NotNull(cachedIndex);

            // 下一次备份 finalize 的信息写（经 tracked ETag 条件写）不应因修复绕过本地缓存而 412。
            var ex = await Record.ExceptionAsync(() =>
                tracked.WriteAsync(account, name, info, null, Azure.Storage.Blobs.Models.AccessTier.Hot));
            Assert.Null(ex);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// F7：修复重压单文件 blob 时，StoreOnly 必须与全新备份对同一路径的推导一致（按配置的 DontCompress 规则），
    /// 而不是硬编码 false。修好的归档才和全新备份写出的是同一种东西。
    /// <para>
    /// 两个方向一起验，防止「一律只存」蒙混过关：logs/big.log 命中规则（应只存 → 归档≈原文件大小），
    /// data/big.bin 不命中（应压缩 → 归档远小于原文件）。两个文件内容都高度可压缩，故两种模式的
    /// 归档尺寸相差一个数量级以上，断言不靠微小差异。
    /// </para>
    /// <para>修复前（硬编码 StoreOnly: false）：logs/big.log 被重压成 -mx9 的小归档，其尺寸断言失败。</para>
    /// </summary>
    [SkippableFact]
    public async Task Repair_Derives_StoreOnly_From_The_DontCompress_Rules_Like_A_Fresh_Backup_Does()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        // 必须是**加密**备份：未加密的 store-only 文件走原始直传（CopyRawAsync），根本不过 7z，
        // StoreOnly 参数对它没有作用。加密时 store-only 仍要过 7z（-mx0 + 密码），正是被测的那条路径。
        const string password = "repair-store-only-pw";
        var rules = new IgnoreRuleSet(["*.log"]);
        var (backup, _, repairer, _, _, factory) = Build();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("rep-store-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 高度可压缩的内容：-mx0 与 -mx9 的归档尺寸差一个数量级，断言不会卡在边界上。
            // 两个文件内容必须不同——内容寻址会把同内容去重成一个 blob，那样就只剩一条路径可验了。
            Directory.CreateDirectory(Path.Combine(_src, "logs"));
            Directory.CreateDirectory(Path.Combine(_src, "data"));
            await File.WriteAllTextAsync(Path.Combine(_src, "logs", "big.log"), new string('a', 200_000));
            await File.WriteAllTextAsync(Path.Combine(_src, "data", "big.bin"), new string('b', 200_000));
            await backup.RunAsync(Req(account, name, rules, password));

            var info = await store.ReadInfoAsync(account, name, password);
            var v1 = info!.Versions.Single();
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, password);
            var logRef = idx.Entries.Single(e => e.Path == "logs/big.log").Storage!.Ref;
            var binRef = idx.Entries.Single(e => e.Path == "data/big.bin").Storage!.Ref;
            Assert.NotEqual(logRef, binRef); // 内容不同 → 两个独立的 blob，两条路径各自走各自的推导

            async Task<long> SizeOf(string blobRef) =>
                (await container.GetBlobClient(blobRef).GetPropertiesAsync()).Value.ContentLength;

            var freshLog = await SizeOf(logRef);
            var freshBin = await SizeOf(binRef);
            // 先确认全新备份自己确实按规则分了道：只存的那个远大于压缩的那个。
            Assert.True(freshLog > freshBin * 10, $"fresh backup did not honour the rules: log={freshLog} bin={freshBin}");

            await container.GetBlobClient(logRef).DeleteIfExistsAsync();
            await container.GetBlobClient(binRef).DeleteIfExistsAsync();

            var report = await repairer.RepairAsync(
                account, name, password, _src, null, new CheckOptions(), AccessTier.Hot, null, dontCompress: rules);
            Assert.Contains("logs/big.log", report.Repaired);
            Assert.Contains("data/big.bin", report.Repaired);

            // 修好的归档尺寸与全新备份写出的一致（同内容 + 同 StoreOnly → 同一个 7z 命令）。
            var repairedLog = await SizeOf(logRef);
            var repairedBin = await SizeOf(binRef);
            Assert.InRange(repairedLog, (long)(freshLog * 0.9), (long)(freshLog * 1.1));
            Assert.InRange(repairedBin, (long)(freshBin * 0.9), (long)(freshBin * 1.1));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>把某版本索引里指定路径的条目改成「老索引条目」（缺 head/tail），并写回云端。
    /// 返回被改条目引用的 data blob 名。</summary>
    private static async Task<string> StripHashesAsync(
        BackupInfoStore store, Account account, string container, int version, string indexBlob, string path)
    {
        var idx = await store.ReadIndexAsync(account, container, indexBlob, null);
        var i = idx.Entries.FindIndex(e => e.Path == path);
        Assert.True(i >= 0, $"v{version} index has no entry for {path}");
        var blobRef = idx.Entries[i].Storage!.Ref;
        idx.Entries[i] = idx.Entries[i] with { HeadHash = null, TailHash = null };
        await store.WriteIndexAsync(account, container, version, idx, null);
        return blobRef;
    }

    /// <summary>
    /// A1：refs 跨全部引用版本，其先后取决于字典枚举顺序——这是未文档化的 BCL 实现细节，正是
    /// 生产代码（见 BackupRepairer.cs 的注释）自己点明「不可靠」的那个属性，测试不能反过来依赖它。
    /// 故用 [Theory] 覆盖两个方向：一次抹掉 v1 的 head/tail（v2 齐全），一次抹掉 v2 的（v1 齐全）。
    /// 字典的插入顺序在两次运行里相同，只是「哪个版本齐全」互换，所以无论实际枚举顺序是 [v1,v2]
    /// 还是 [v2,v1]，两个方向里必有一个「缺 head/tail 的条目」落在 refs[0] 的位置——只要生产代码退回
    /// entry0，该方向就必定失败，不依赖猜测枚举顺序。
    /// <para>修复前（用 refs[0]）：至少一个方向里 refs[0] 恰是被抹掉的条目，写出的元数据只有 len，
    /// 该方向的 head/tail 断言失败。</para>
    /// </summary>
    [SkippableTheory]
    [InlineData(1)] // 抹掉 v1（v2 齐全）——覆盖「坏条目排在枚举前面」的方向
    [InlineData(2)] // 抹掉 v2（v1 齐全）——覆盖相反方向，使断言不再依赖具体枚举顺序
    public async Task Repair_Prefers_A_Reference_That_Still_Has_Head_And_Tail_Hashes(int stripVersion)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, repairer, _, _, factory) = Build();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("rep-meta-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // v1 与 v2 都引用同一个 data blob（a.txt 内容未变）。
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "two versions reference me");
            await backup.RunAsync(Req(account, name));
            await File.WriteAllTextAsync(Path.Combine(_src, "b.txt"), "just to create a second version");
            await backup.RunAsync(Req(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            Assert.NotNull(info);
            Assert.Equal(2, info!.Versions.Count);
            var v1 = info.Versions.Single(v => v.Version == 1);
            var v2 = info.Versions.Single(v => v.Version == 2);
            var stripTarget = stripVersion == 1 ? v1 : v2;
            var goodVersion = stripVersion == 1 ? v2 : v1;

            // 保留版本（未被抹掉的那个）的条目两项齐全——这就是修复应当拿来写元数据的那条。
            var goodIndex = await store.ReadIndexAsync(account, name, goodVersion.IndexBlob, null);
            var goodEntry = goodIndex.Entries.Single(e => e.Path == "a.txt");
            Assert.NotNull(goodEntry.HeadHash);
            Assert.NotNull(goodEntry.TailHash);

            // 另一版本的同一条目退化成「老索引条目」。
            var blobRef = await StripHashesAsync(store, account, name, stripVersion, stripTarget.IndexBlob, "a.txt");
            Assert.Equal(goodEntry.Storage!.Ref, blobRef);

            await container.GetBlobClient(blobRef).DeleteIfExistsAsync();

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), AccessTier.Hot, null, dontCompress: null);
            Assert.Contains("a.txt", report.Repaired);

            var meta = (await container.GetBlobClient(blobRef).GetPropertiesAsync()).Value.Metadata;
            Assert.Equal(goodEntry.HeadHash, meta["head"]);
            Assert.Equal(goodEntry.TailHash, meta["tail"]);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// A2：一条引用都凑不出 head/tail 时，省略元数据是正确处置（写空串更糟），但那意味着该对象
    /// 的碰撞防护被削弱（密钥化时等于没有），不留痕就是不可见的退化。必须记一条可审计的日志。
    /// <para>修复前：没有任何日志，本测试的 Single 断言失败。</para>
    /// </summary>
    [SkippableFact]
    public async Task Repair_Records_A_Warning_When_Collision_Metadata_Must_Be_Omitted()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var opLog = new RecordingOperationLog();
        var (backup, _, repairer, _, _, factory) = Build(opLog);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("rep-degr-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "the only reference is a legacy entry");
            await backup.RunAsync(Req(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions.Single();
            var blobRef = await StripHashesAsync(store, account, name, 1, v1.IndexBlob, "a.txt");
            await container.GetBlobClient(blobRef).DeleteIfExistsAsync();

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), AccessTier.Hot, null, dontCompress: null);
            Assert.Contains("a.txt", report.Repaired);

            // 退化确实发生了：写出的对象不带 head/tail。
            var meta = (await container.GetBlobClient(blobRef).GetPropertiesAsync()).Value.Metadata;
            Assert.False(meta.ContainsKey("head"));
            Assert.False(meta.ContainsKey("tail"));

            // 而且留下了恰好一条可审计的痕迹（不噪：每个受影响对象一条）。
            var degraded = Assert.Single(opLog.Entries, e => e.Message.Contains("Collision guard degraded"));
            Assert.Equal(OperationLogLevel.Warning, degraded.Level);
            Assert.Contains(blobRef, degraded.Message);
            Assert.Contains("head and tail", degraded.Message);
            Assert.Equal($"repair:{account.Id}/{name}", degraded.Source);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 修复要从本地找一份内容一致的文件当修复源，此前那次读取毫无保护。而外层逐个 blob 的循环
    /// 也没有兜底，于是一个读不开的本地文件会让**整个修复操作**中途失败——已经修好的 blob 早已
    /// 上传，但它们的索引改动统一在循环之后才写回，那部分成果一并丢失。
    /// <para>
    /// 触发条件一点都不罕见：修复恰恰是在检查报出问题之后跑的。检查器现在会把读不开的本地文件
    /// 报成 Missing 并跑完全程（上一轮修的），用户看完报告就来点修复——然后修复倒在同一个文件上。
    /// </para>
    /// <para>本测试让两个文件的云端 blob 都损坏，其中一个的本地副本读不开：另一个必须照常修好，
    /// 读不开的那个走既有的「本地取不到 → 标记不可恢复」路径，而不是让整轮修复抛出。</para>
    /// </summary>
    [SkippableFact]
    public async Task An_Unreadable_Local_File_Does_Not_Abort_The_Whole_Repair()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var (backup, _, repairer, _, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rep-unread-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var locked = Path.Combine(_src, "locked.txt");

        try
        {
            await File.WriteAllTextAsync(locked, "readable at backup time, locked before the repair");
            await File.WriteAllTextAsync(Path.Combine(_src, "fine.txt"), "stays readable throughout");
            await backup.RunAsync(Req(account, name)); // 阈值为 1 → 两个各自成 data blob

            // 两份云端数据都没了；修复要靠本地。
            await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync();

            File.SetUnixFileMode(locked, UnixFileMode.None); // 备份之后、修复之前变得读不开

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), AccessTier.Hot, null, dontCompress: null);

            // 读得到的那个照常修好——修复前，整轮会在 locked.txt 上抛出，这一条根本走不到。
            Assert.Contains("fine.txt", report.Repaired);

            var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions.Single().IndexBlob, null);
            var fineRef = idx.Entries.Single(e => e.Path == "fine.txt").Storage!.Ref;
            Assert.True(await container.GetBlobClient(fineRef).ExistsAsync()); // 数据真的回到了云端

            // 读不开的那个走既有处置：本地拿不出可用副本 → 标记不可恢复，而不是拿它去覆盖云端。
            Assert.Contains("locked.txt", report.Unrecoverable);
            Assert.DoesNotContain("locked.txt", report.Repaired);
        }
        finally
        {
            try { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }
}
