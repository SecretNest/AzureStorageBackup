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

    private BackupRequest Req(Account a, string c) => new()
    {
        Account = a, Container = c, LocalRoot = _src, Name = "photos",
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
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
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null);
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
    /// A1：refs 跨全部引用版本，其先后取决于字典枚举顺序。若首条恰是缺 head/tail 的老索引条目，
    /// 而兄弟引用两项齐全，照首条走会白丢手上已有的碰撞防护。修复必须挑齐全的那条。
    /// <para>修复前（用 refs[0]）：v1 被改成老条目，写出的元数据只有 len，本测试的 head/tail 断言失败。</para>
    /// </summary>
    [SkippableFact]
    public async Task Repair_Prefers_A_Reference_That_Still_Has_Head_And_Tail_Hashes()
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

            // v2 的条目两项齐全——这就是修复应当拿来写元数据的那条。
            var v2Index = await store.ReadIndexAsync(account, name, v2.IndexBlob, null);
            var v2Entry = v2Index.Entries.Single(e => e.Path == "a.txt");
            Assert.NotNull(v2Entry.HeadHash);
            Assert.NotNull(v2Entry.TailHash);

            // v1 的同一条目退化成「老索引条目」——枚举顺序上它排在 v2 前面。
            var blobRef = await StripHashesAsync(store, account, name, 1, v1.IndexBlob, "a.txt");
            Assert.Equal(v2Entry.Storage!.Ref, blobRef);

            await container.GetBlobClient(blobRef).DeleteIfExistsAsync();

            var report = await repairer.RepairAsync(
                account, name, null, _src, null, new CheckOptions(), AccessTier.Hot, null);
            Assert.Contains("a.txt", report.Repaired);

            var meta = (await container.GetBlobClient(blobRef).GetPropertiesAsync()).Value.Metadata;
            Assert.Equal(v2Entry.HeadHash, meta["head"]);
            Assert.Equal(v2Entry.TailHash, meta["tail"]);
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
                account, name, null, _src, null, new CheckOptions(), AccessTier.Hot, null);
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
}
