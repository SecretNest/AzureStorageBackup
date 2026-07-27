using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 一个文件读不开不该悄无声息——操作员必须在操作日志里看到记录，且知道系统给出的原因原文
/// （"被占用"「权限不足」「设备读错误」各自需要不同处理，压成一句「无法读取」等于没告诉操作员任何事）。
/// 操作日志是 pull-only，单用户无人值守部署下没人会主动去看；因此还须复用 UnrecoverableError 通知事件
/// 推送出去——这是本文件要覆盖的实际修复点，日志级别也随该事件映射为 Error（不再是 Warning）。
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnreadableWarningTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public UnreadableWarningTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-unreadwarn-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>指定路径的读取一律抛给定异常，其余文件照常算 hash（同 UnreadableIndexEntryTests 的做法）。</summary>
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

    /// <summary>捕获 AppendAsync 调用（等级/来源/消息），供断言警告内容。</summary>
    private sealed class CapturingLog : IOperationLog
    {
        public List<(OperationLogLevel Level, string Source, string Message)> Entries { get; } = [];
        public Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default, bool? durable = null)
        {
            lock (Entries) Entries.Add((level, source, message));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<LogEntry>> QueryAsync(OperationLogLevel? l, string? s, DateTimeOffset? f, DateTimeOffset? t, int n, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LogEntry>>([]);
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;
        public Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>捕获 NotifyAsync 调用（事件/标题/正文），供断言"读不开的文件推送了通知"。</summary>
    private sealed class CapturingNotifier : INotifier
    {
        public List<(NotificationEvents Event, string Title, string Body)> Notifications { get; } = [];
        public Task NotifyAsync(NotificationEvents evt, string title, string body, CancellationToken ct = default)
        {
            lock (Notifications) Notifications.Add((evt, title, body));
            return Task.CompletedTask;
        }
    }

    /// <summary>构造一个可运行的编排器；differ 缺省时用真实 hasher，传入自定义 differ 可模拟某文件读不开。</summary>
    private (BackupOrchestrator Orchestrator, IBackupInfoStore Store, BlobClientFactory Factory) Build(
        BackupDiffer? differ = null, IOperationLog? opLog = null, INotifier? notifier = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var tag = Guid.NewGuid().ToString("N");
        var staging = new StagingArea(
            Path.Combine(_temp, "compress-" + tag), Path.Combine(_temp, "staged-" + tag), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), differ ?? new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
            notifier: notifier, opLog: opLog);
        return (orchestrator, store, factory);
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 } },
    };

    [SkippableFact]
    public async Task Each_Unreadable_File_Produces_One_Log_Entry_Carrying_The_System_Reason()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var log = new CapturingLog();
        const string reason = "The process cannot access the file 'locked.mdf' because it is being used by another process.";
        var differ = new BackupDiffer(new ThrowingHasher("locked.mdf", new IOException(reason)));
        var (orchestrator, _, factory) = Build(differ, log);
        var account = AzuriteAccount();
        var name = RandomName("unreadwarn-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("locked.mdf", "database content");
            WriteText("plain.txt", "ordinary file");

            await orchestrator.RunAsync(Request(account, name));

            var expectedSource = $"backup:{account.Id}/{name}";
            // 复用 UnrecoverableError 事件后，日志级别随事件映射变为 Error（不再是 Warning）——这是有意的
            // 结果：读不开的文件现在与"处理中反复变化"同级上报。
            var entry = Assert.Single(log.Entries, e => e.Level == OperationLogLevel.Error);
            Assert.Equal(expectedSource, entry.Source);
            Assert.Contains("locked.mdf", entry.Message);
            Assert.Contains(reason, entry.Message); // 原因原文必须原样保留，不能被压成一句「无法读取」
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>本次修复的核心断言：读不开的文件不再只落进 pull-only 的操作日志，还须走通知 webhook 推送出去
    /// （复用既有 UnrecoverableError 事件，无需新增开关）——否则无人值守部署下永远没人知道。</summary>
    [SkippableFact]
    public async Task Each_Unreadable_File_Raises_An_UnrecoverableError_Notification()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var notifier = new CapturingNotifier();
        const string reason = "The process cannot access the file 'locked.mdf' because it is being used by another process.";
        var differ = new BackupDiffer(new ThrowingHasher("locked.mdf", new IOException(reason)));
        var (orchestrator, _, factory) = Build(differ, notifier: notifier);
        var account = AzuriteAccount();
        var name = RandomName("unreadnotify-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("locked.mdf", "database content");
            WriteText("plain.txt", "ordinary file");

            await orchestrator.RunAsync(Request(account, name));

            var notification = Assert.Single(notifier.Notifications, n => n.Event == NotificationEvents.UnrecoverableError);
            Assert.Contains("locked.mdf", notification.Title);
            Assert.Contains(reason, notification.Body); // 原因原文必须一并推送，不能被压平
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task The_Run_Result_Counts_Unreadable_Files()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var differ = new BackupDiffer(new ThrowingHasher("locked.mdf",
            new IOException("The process cannot access the file because it is being used by another process.")));
        var (orchestrator, _, factory) = Build(differ);
        var account = AzuriteAccount();
        var name = RandomName("unreadcnt-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("locked.mdf", "database content");
            WriteText("plain.txt", "ordinary file");

            var result = await orchestrator.RunAsync(Request(account, name));

            Assert.Equal(1, result.UnreadableFiles);
            Assert.Equal(1, result.Version); // 备份本身照常成功完成，产出新版本
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>决策 8：长期被占用的文件每轮都告警。这是有意的——它确实没被备起来。
    /// 若第二轮静默，操作员会以为问题自己好了。</summary>
    [SkippableFact]
    public async Task Two_Consecutive_Runs_Each_Report_About_The_Same_File()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var log = new CapturingLog();
        var differ = new BackupDiffer(new ThrowingHasher("locked.mdf",
            new IOException("locked by another process")));
        var (orchestrator, _, factory) = Build(differ, log);
        var account = AzuriteAccount();
        var name = RandomName("unreadrpt-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("locked.mdf", "database content");

            var r1 = await orchestrator.RunAsync(Request(account, name)); // 第一轮：产生一条 Warning
            var r2 = await orchestrator.RunAsync(Request(account, name)); // 第二轮：文件仍锁着，须再产生一条，而非静默

            Assert.Equal(1, r1.Version);
            Assert.Equal(2, r2.Version); // 第二轮同样成功完成，不因文件一直读不开而失败
            Assert.Equal(1, r1.UnreadableFiles);
            Assert.Equal(1, r2.UnreadableFiles);

            var warnings = log.Entries.Where(e => e.Level == OperationLogLevel.Error && e.Message.Contains("locked.mdf")).ToList();
            Assert.Equal(2, warnings.Count); // 两轮各一条，长期占用不能只报一次就沉默
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
