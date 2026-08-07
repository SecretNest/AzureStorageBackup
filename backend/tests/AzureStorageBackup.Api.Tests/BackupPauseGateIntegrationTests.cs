using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupPauseGateIntegrationTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupPauseGateIntegrationTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-gate-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
        _journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 42,
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

    private void WriteBytes(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
    }

    // 与 BackupJournalWriteTests.Build 同形：真实构造器的第 13 个参数才是 opLog（notifier 在它前面），
    // 所以这里补一个可选参数，用命名实参跳过 notifier，其余（verboseLog/spillFactory）维持默认。
    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory) Build(
        IBlobUploader uploader, IOperationLog? opLog = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"),
            staging);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked,
            notifier: null, opLog: opLog);
        return (orchestrator, factory);
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

    /// <summary>头 N 次上传抛瞬时错误，之后放行。用来验证"抖一下会自愈，不该判死"。
    /// 真实 IBlobUploader 的参数顺序是 (tier, retry, ct, metadata[, progress])——不是 brief
    /// 草稿里那个（tier, metadata, options, ct）；这里照 BackupJournalWriteTests 里的
    /// FailAfter/GatedUploader 抄真实签名。</summary>
    private sealed class FlakyUploader(IBlobUploader inner, int failures) : IBlobUploader
    {
        private int _left = failures;

        public int Attempts { get; private set; }

        private void Gate()
        {
            Attempts++;
            if (Interlocked.Decrement(ref _left) >= 0)
                throw new AggregateException("Retry failed after 6 tries.", new TaskCanceledException("timeout"));
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate();
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        {
            Gate();
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate();
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    /// <summary>把 AppendAsync 收进列表的 IOperationLog 替身。项目里已有的几份（OperationLogSourceTests、
    /// BackupRepairerTests）都是各自文件私有的嵌套类，没有可以直接复用的公共版本——这里照同一形状
    /// 再写一份，而不是抽一个跨文件共享类型（收益不足以抵消给测试基础设施再添一层间接）。</summary>
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

    [SkippableFact]
    public async Task Transient_failure_pauses_then_heals()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("gate");
        var flaky = new FlakyUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), failures: 1);
        var (orchestrator, factory) = Build(flaky);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big.bin", 6_000_000);
            await using var control = new BackupRunControl(_journals, 5, "run-heal", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
                patience: TimeSpan.FromMinutes(5)));

            var result = await orchestrator.RunAsync(Request(account, name), null, default, control);

            Assert.Equal(1, result.Version);
            Assert.True(flaky.Attempts >= 2);   // 抖了一次，重试了一次
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Patience_running_out_suspends_instead_of_failing()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("gate");
        var flaky = new FlakyUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), failures: 1000);
        var (orchestrator, factory) = Build(flaky);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big.bin", 6_000_000);
            await using var control = new BackupRunControl(_journals, 5, "run-susp", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(10)], steady: TimeSpan.FromMilliseconds(10),
                patience: TimeSpan.Zero));

            var ex = await Assert.ThrowsAsync<BackupSuspendedException>(
                () => orchestrator.RunAsync(Request(account, name), null, default, control));
            Assert.Equal(SuspendReason.AutoSuspended, ex.Reason);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Step 6 用的跑法：耐心阈值设成 0，一撞墙就降级，同时把 opLog 换成能窥探的替身。</summary>
    private async Task RunWithAlwaysFailingUploadAsync(RecordingOperationLog log)
    {
        var account = AzuriteAccount();
        var name = RandomName("gate");
        var flaky = new FlakyUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), failures: 1000);
        var (orchestrator, factory) = Build(flaky, log);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big.bin", 6_000_000);
            await using var control = new BackupRunControl(_journals, 5, "run-warn", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(10)], steady: TimeSpan.FromMilliseconds(10),
                patience: TimeSpan.Zero));

            await orchestrator.RunAsync(Request(account, name), null, default, control);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    // 挂起不是失败。报成 Error 会让这份备份在界面上顶着红字，还要手动 Reset 才消——
    // 而现场明明保着，下次跑就接上了。
    [SkippableFact]
    public async Task Auto_suspend_is_logged_as_a_warning_not_an_error()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var log = new RecordingOperationLog();
        await Assert.ThrowsAsync<BackupSuspendedException>(
            () => RunWithAlwaysFailingUploadAsync(log));

        var suspended = Assert.Single(log.Entries, e => e.Message.Contains("Backup suspended"));
        Assert.Equal(OperationLogLevel.Warning, suspended.Level);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("Backup failed"));
    }
}
