using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 一个列不出内容的目录此前会让整轮备份崩在扫描阶段——但"加个 try 跳过"是**更糟**的答案：
/// 其下整棵子树会因为没被扫到而被 diff 判成删除，于是一次权限故障就把一整棵子树从索引里抹掉，
/// 直到还原时才发现文件没了。本文件盯住的正是这条不变量：读不开 ≠ 删除，整棵子树必须沿用旧条目。
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnreadableDirectoryTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public UnreadableDirectoryTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-unreaddir-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        // 先恢复权限，否则递归删除会被读不开的目录卡住。
        try
        {
            foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
                try { File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch { /* best effort */ }
        }
        catch { /* best effort */ }
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

    /// <summary>捕获 NotifyAsync 调用，供断言通知粒度。</summary>
    private sealed class CapturingNotifier : INotifier
    {
        public List<(NotificationEvents Event, string Title, string Body)> Notifications { get; } = [];
        public Task NotifyAsync(NotificationEvents evt, string title, string body, CancellationToken ct = default)
        {
            lock (Notifications) Notifications.Add((evt, title, body));
            return Task.CompletedTask;
        }
    }

    private BackupOrchestrator Orchestrator(
        BlobClientFactory factory, IBackupInfoStore store, INotifier? notifier = null)
    {
        var staging = new StagingArea(
            Path.Combine(_temp, "compress-" + Guid.NewGuid().ToString("N")),
            Path.Combine(_temp, "staged-" + Guid.NewGuid().ToString("N")), () => 200_000_000);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
            notifier: notifier, verifier: new ProcessingVerifier(new FileHasher()));
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
    };

    /// <summary>核心不变量：v1 备份成功后目录变得列不出来，v2 必须把整棵子树的条目**沿用**下来
    /// 并打上 UnreadableAt，而不是判成删除。判成删除的后果是：这些文件从新版本索引里消失，
    /// 保留策略随后会把它们的数据 blob 当作无人引用而清掉——一次权限故障造成永久数据丢失。</summary>
    [SkippableFact]
    public async Task An_Unreadable_Directory_Carries_Its_Subtree_Forward_Instead_Of_Deleting_It()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var notifier = new CapturingNotifier();

        var account = AzuriteAccount();
        var name = RandomName("unreaddir-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var lockedDir = Path.Combine(_root, "vault");

        try
        {
            WriteText("outside.txt", "not affected");
            WriteText("vault/a.txt", "secret a");
            WriteText("vault/b.txt", "secret b");
            WriteText("vault/deep/c.txt", "secret c"); // 嵌套一层，验证覆盖的是整棵子树而不止直接子项

            // v1：全部可读，正常备份。
            var v1 = await Orchestrator(factory, store).RunAsync(Request(account, name));
            Assert.Equal(1, v1.Version);

            var info1 = await store.ReadInfoAsync(account, name, null);
            var idx1 = await store.ReadIndexAsync(account, name, info1!.Versions[0].IndexBlob, null);
            var storageBefore = idx1.Entries.ToDictionary(e => e.Path, e => e.Storage!.Ref, StringComparer.Ordinal);
            Assert.Equal(4, idx1.Entries.Count);

            // v2：目录整个读不出来了。
            File.SetUnixFileMode(lockedDir, UnixFileMode.None);
            var v2 = await Orchestrator(factory, store, notifier).RunAsync(Request(account, name));

            Assert.Equal(2, v2.Version); // 没有崩在扫描阶段
            Assert.Equal(3, v2.UnreadableFiles); // 子树里三个条目都算不可读

            var info2 = await store.ReadInfoAsync(account, name, null);
            var idx2 = await store.ReadIndexAsync(account, name, info2!.Versions[1].IndexBlob, null);

            // 整棵子树必须还在，且沿用原来的存储引用（没有重传，也没有被判成删除）。
            foreach (var path in new[] { "vault/a.txt", "vault/b.txt", "vault/deep/c.txt" })
            {
                var entry = Assert.Single(idx2.Entries, e => e.Path == path);
                Assert.NotNull(entry.UnreadableAt);
                Assert.Equal(storageBefore[path], entry.Storage!.Ref);
                Assert.True(await container.GetBlobClient(entry.Storage.Ref).ExistsAsync(),
                    $"data blob for {path} must survive"); // 判成删除的话保留策略会把它清掉
            }

            // 目录外的文件完全不受影响。
            Assert.Single(idx2.Entries, e => e.Path == "outside.txt");

            // 通知按目录汇总成一条，而不是子树里每个文件各来一条——一个五千文件的目录
            // 会变成五千条 webhook，既淹没操作员也会把备份卡在推送上。
            var dirNotices = notifier.Notifications
                .Where(n => n.Event == NotificationEvents.UnrecoverableError && n.Title.Contains("vault")).ToList();
            Assert.Single(dirNotices);
            Assert.Contains("Directory unreadable", dirNotices[0].Title);
            Assert.Contains("3 entries", dirNotices[0].Body);
            Assert.DoesNotContain(notifier.Notifications, n => n.Title.Contains("vault/a.txt"));
        }
        finally
        {
            try { File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>UnreadableAt 要回答的是"这份内容从什么时候起就没能再更新"。此前每轮都把它刷成
    /// UtcNow，等于每轮把答案抹掉、只剩一句"刚才也没读到"——操作员再也问不出"这文件多久没备份上了"。
    /// 连跑三轮：第三轮索引里的时间戳必须还是第二轮那一刻的。</summary>
    [SkippableFact]
    public async Task The_Unreadable_Timestamp_Records_When_It_First_Went_Unread()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());

        var account = AzuriteAccount();
        var name = RandomName("unreadstamp-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var lockedDir = Path.Combine(_root, "vault");

        try
        {
            WriteText("vault/a.txt", "content");
            await Orchestrator(factory, store).RunAsync(Request(account, name)); // v1：可读

            File.SetUnixFileMode(lockedDir, UnixFileMode.None);
            await Orchestrator(factory, store).RunAsync(Request(account, name)); // v2：首次读不开

            var info2 = await store.ReadInfoAsync(account, name, null);
            var idx2 = await store.ReadIndexAsync(account, name, info2!.Versions[1].IndexBlob, null);
            var firstSeen = idx2.Entries.Single(e => e.Path == "vault/a.txt").UnreadableAt;
            Assert.NotNull(firstSeen);

            await Task.Delay(1100); // 时间戳有秒级分辨率，确保"若被刷新"会是一个可分辨的新值
            await Orchestrator(factory, store).RunAsync(Request(account, name)); // v3：仍读不开

            var info3 = await store.ReadInfoAsync(account, name, null);
            var idx3 = await store.ReadIndexAsync(account, name, info3!.Versions[2].IndexBlob, null);
            var stillFirstSeen = idx3.Entries.Single(e => e.Path == "vault/a.txt").UnreadableAt;

            Assert.Equal(firstSeen, stillFirstSeen); // 记的是"何时起"，不是"刚才"
        }
        finally
        {
            try { File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>读不开的目录里若有上一版本记录的空目录，也要一并带过来：直接用本轮扫描结果的话，
    /// 这些空目录会从新版本消失，还原出来的目录结构就少了一块。</summary>
    [SkippableFact]
    public async Task Empty_Directories_Under_An_Unreadable_Directory_Are_Carried_Forward()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());

        var account = AzuriteAccount();
        var name = RandomName("unreademptydir-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var lockedDir = Path.Combine(_root, "vault");

        try
        {
            WriteText("vault/a.txt", "content");
            Directory.CreateDirectory(Path.Combine(_root, "vault", "placeholder"));

            await Orchestrator(factory, store).RunAsync(Request(account, name));

            var info1 = await store.ReadInfoAsync(account, name, null);
            var idx1 = await store.ReadIndexAsync(account, name, info1!.Versions[0].IndexBlob, null);
            Assert.Contains("vault/placeholder", idx1.EmptyDirs);

            File.SetUnixFileMode(lockedDir, UnixFileMode.None);
            await Orchestrator(factory, store).RunAsync(Request(account, name));

            var info2 = await store.ReadInfoAsync(account, name, null);
            var idx2 = await store.ReadIndexAsync(account, name, info2!.Versions[1].IndexBlob, null);

            Assert.Contains("vault/placeholder", idx2.EmptyDirs); // 目录结构不能因为读不到就少一块
            Assert.DoesNotContain("vault", idx2.EmptyDirs);       // 而读不开的目录本身绝不能被当成空目录
        }
        finally
        {
            try { File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }
}
