using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 决策 5：一个文件本轮读不开（被占用/无权限），索引不能把它当成删除处理，也不能编造一条
/// 内容指向为空的坏条目——应沿用它上一版本的条目并打 UnreadableAt。
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnreadableIndexEntryTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public UnreadableIndexEntryTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-unread-" + Guid.NewGuid().ToString("N"));
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

    private void WriteText(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>指定路径的读取一律抛给定异常，其余文件照常算 hash（同 BackupDifferUnreadableTests 的做法）。</summary>
    private sealed class ThrowingHasher(string lockedPath, Exception toThrow) : IFileHasher
    {
        public Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("head-" + Path.GetFileName(path));

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("tail-" + Path.GetFileName(path));

        public Task<string> FullHashAsync(string path, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("full-" + Path.GetFileName(path));
    }

    /// <summary>构造一个可运行的编排器；differ 缺省时用真实 hasher，传入自定义 differ 可模拟某文件读不开。</summary>
    private (BackupOrchestrator Orchestrator, IBackupInfoStore Store, BlobClientFactory Factory) Build(BackupDiffer? differ = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var tag = Guid.NewGuid().ToString("N");
        var staging = new StagingArea(
            Path.Combine(_temp, "compress-" + tag), Path.Combine(_temp, "staged-" + tag), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), differ ?? new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher());
        return (orchestrator, store, factory);
    }

    private BackupRequest Request(Account account, string container, RetentionPolicy? retention = null) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions
        {
            // 单文件阈值压到 1：每个文件各自成一个 data/{hash} blob，便于直接按 hash 断言 blob 是否被清理。
            Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
            Retention = retention ?? new RetentionPolicy(),
        },
    };

    [SkippableFact]
    public async Task A_Previously_Backed_Up_File_Carries_Its_Old_Entry_Forward()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator1, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("unreadfw-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "hello");
            await orchestrator1.RunAsync(Request(account, name)); // v1：正常备份

            var info1 = await store.ReadInfoAsync(account, name, null);
            var idx1 = await store.ReadIndexAsync(account, name, info1!.Versions[0].IndexBlob, null);
            var prevEntry = Assert.Single(idx1.Entries);

            // v2：注入对 a.txt 抛异常的 hasher，模拟本轮读不开；同时改变文件长度，确保不会被 length+mtime
            // 判定为 Unchanged 而根本不去读它——必须真正走到哈希阶段才会触发 Unreadable。
            var (orchestrator2, _, _) = Build(new BackupDiffer(new ThrowingHasher("a.txt",
                new IOException("The process cannot access the file 'a.txt' because it is being used by another process."))));
            File.WriteAllText(Path.Combine(_root, "a.txt"), "hello world!");
            await orchestrator2.RunAsync(Request(account, name)); // v2：读不开

            var info2 = await store.ReadInfoAsync(account, name, null);
            var idx2 = await store.ReadIndexAsync(account, name, info2!.Versions[^1].IndexBlob, null);
            var entry = Assert.Single(idx2.Entries);

            Assert.NotNull(entry.UnreadableAt);
            // 新条目指向与旧条目完全相同的已上传内容——沿用旧值，而不是拿本轮扫描到的新长度/新内容瞎编。
            Assert.Equal(prevEntry.Storage!.Ref, entry.Storage!.Ref);
            Assert.Equal(prevEntry.FullHash, entry.FullHash);
            Assert.Equal(prevEntry.Length, entry.Length); // 5（"hello"），不是本轮的 12（"hello world!"）
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>决策 5 的护栏。不可读被当成删除的话，保留策略滚过几轮就会
    /// 把一个仅是长期被占用的文件从所有版本里抹掉——每轮告警看起来都只是「跳过一个文件」。</summary>
    [SkippableFact]
    public async Task An_Unreadable_File_Is_Never_Recorded_As_Deleted()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 2 };
        var (orchestrator1, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("unreaddel-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("a.txt", "hello");
            await orchestrator1.RunAsync(Request(account, name, retention)); // v1：正常备份，上传 data/{hash}

            var info1 = await store.ReadInfoAsync(account, name, null);
            var idx1 = await store.ReadIndexAsync(account, name, info1!.Versions[0].IndexBlob, null);
            var hash = Assert.Single(idx1.Entries).FullHash!;

            // 连续三轮 a.txt 都读不开。MaxVersions=2 会在这几轮里退役 v1——若 a.txt 被当成删除，
            // 后续版本都不再引用 data/{hash}，v1 退役时它就会被当成「独占数据」一并清理，文件永久丢失。
            var (orchestrator2, _, _) = Build(new BackupDiffer(new ThrowingHasher("a.txt",
                new IOException("locked by another process"))));
            for (var i = 0; i < 3; i++)
                await orchestrator2.RunAsync(Request(account, name, retention));

            var infoFinal = await store.ReadInfoAsync(account, name, null);
            var idxFinal = await store.ReadIndexAsync(account, name, infoFinal!.Versions[^1].IndexBlob, null);

            // 仍出现在最新版本索引里——没有被当成已删除而从条目里消失。
            Assert.Contains(idxFinal.Entries, e => e.Path == "a.txt");
            // 且它引用的数据 blob 仍然存在：证明每一轮都持续被「在保留的版本」引用，
            // 而不是被退役版本独占后随之清理掉（那正是被当成删除时会发生的事）。
            Assert.True(await container.GetBlobClient("data/" + hash).ExistsAsync(),
                "unreadable file's data blob was reclaimed by retention as if the file had been deleted");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task A_Brand_New_Unreadable_File_Is_Absent_From_The_Version()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, store, factory) = Build(new BackupDiffer(new ThrowingHasher("new.txt",
            new IOException("locked by another process"))));
        var account = AzuriteAccount();
        var name = RandomName("unreadnew-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("new.txt", "content nobody has ever successfully read");
            await orchestrator.RunAsync(Request(account, name)); // v1：从未成功读过一次，没有旧条目可沿用

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            Assert.DoesNotContain(idx.Entries, e => e.Path == "new.txt"); // 没内容可指向，编造条目是撒谎
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
