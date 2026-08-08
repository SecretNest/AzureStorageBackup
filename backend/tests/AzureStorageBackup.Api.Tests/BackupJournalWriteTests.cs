using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupJournalWriteTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupJournalWriteTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-jwrite-" + Guid.NewGuid().ToString("N"));
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
        Id = 41,
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

    private void WriteBytes(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
    }

    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory) Build(IBlobUploader? uploader = null)
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
            new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
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

    /// <summary>第 N 次上传起一律抛永久错误，用来把运行卡死在半路。</summary>
    private sealed class FailAfter(IBlobUploader inner, int allowed) : IBlobUploader
    {
        private int _count;

        private void Gate()
        {
            if (Interlocked.Increment(ref _count) > allowed)
                throw new InvalidOperationException("upload refused by test");
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

    /// <summary>卡住每一次上传，直到测试放行——用来在运行"进行到一半"这个真实存在的时间窗口里
    /// 窥探 journal 文件是否已经在磁盘上，从而把"从没建过"和"建过又删了"这两种情况分开。</summary>
    private sealed class GatedUploader(IBlobUploader inner, TaskCompletionSource ready, TaskCompletionSource proceed)
        : IBlobUploader
    {
        private async Task GateAsync()
        {
            ready.TrySetResult();
            await proceed.Task;
        }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            await GateAsync();
            return await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        {
            await GateAsync();
            return await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
        }

        public async Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            await GateAsync();
            await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    /// <summary>只放行地址等于 <paramref name="keepRef"/> 的上传，其余一律拒绝——用来在同一次运行里
    /// 逼一个不相关的文件失败（好让运行整体失败、journal 不被收尾删掉），同时让目标内容
    /// （一个真传、一个 if-missing 命中，两者地址相同）安然走完全程。</summary>
    private sealed class FailExceptRef(IBlobUploader inner, string keepRef) : IBlobUploader
    {
        private static void Gate(string blobName, string keepRef)
        {
            if (!string.Equals(blobName, keepRef, StringComparison.Ordinal))
                throw new InvalidOperationException("upload refused by test");
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate(blobName, keepRef);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        {
            Gate(blobName, keepRef);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate(blobName, keepRef);
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    /// <summary>只拒绝单文件 blob（"data/" 前缀），放行 pack（"packs/" 前缀）——逼一个不相关的
    /// 大文件失败（运行因此整体失败，journal 不被收尾删掉），同时让 pack 安然传完、被记进 journal。</summary>
    private sealed class FailDataBlobs(IBlobUploader inner) : IBlobUploader
    {
        private static void Gate(string blobName)
        {
            if (blobName.StartsWith("data/", StringComparison.Ordinal))
                throw new InvalidOperationException("upload refused by test");
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate(blobName);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        {
            Gate(blobName);
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Gate(blobName);
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    [SkippableFact]
    public async Task Successful_run_deletes_its_journal()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("jw");
        var factoryOnly = new BlobClientFactory(TestSecrets.Reader);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (orchestrator, factory) = Build(new GatedUploader(new BlobUploader(factoryOnly), ready, proceed));
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteText("a.txt", "hello");
            await using var control = new BackupRunControl(_journals, configId: 3, runId: "run-ok");
            var journalPath = _journals.PathFor(account.Id, name, "run-ok");
            var runTask = orchestrator.RunAsync(Request(account, name), null, default, control);

            // 卡在第一次上传前面：开卷发生在扫描分组之后、上传之前，此刻这一卷必然已经落盘。
            // 不看这一眼的话，下面 "journal 不在了" 这句话「从没建过」和「建过又删了」结果一样，
            // 测试分辨不出两者，也就抓不住"提前删了"这种回归。
            await ready.Task;
            Assert.True(File.Exists(journalPath), "journal file should exist while the run is in progress");
            proceed.SetResult();

            await runTask;

            Assert.False(File.Exists(journalPath));
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task IfMissing_hit_is_journalled()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("jw");

        WriteBytes("orig.bin", 6_000_000);
        WriteBytes("dup.bin", 6_000_000);      // 与 orig.bin 字节全同（都是全零）→ 同一个地址
        WriteBytes("trigger.bin", 6_000_001);  // 长度不同 → 内容不同 → 独立地址，被 wrapper 永久拒绝

        // 明文寻址就是 "data/" + fullHash（BlobAddressScheme.DataAddress），Password 为 null，
        // 所以这里能照抄同一套 hash 逻辑，提前把 orig/dup 共享的目标地址算出来。
        var expectedRef = "data/" + await new FileHasher().FullHashAsync(Path.Combine(_root, "orig.bin"), default);

        var factoryOnly = new BlobClientFactory(TestSecrets.Reader);
        var (orchestrator, factory) = Build(new FailExceptRef(new BlobUploader(factoryOnly), expectedRef));
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            await using var control = new BackupRunControl(_journals, configId: 3, runId: "run-ifmiss");
            await Assert.ThrowsAnyAsync<Exception>(
                () => orchestrator.RunAsync(Request(account, name), null, default, control));

            var listed = await _journals.ListAsync(account.Id, name, default);
            var journal = Assert.Single(listed);
            // orig.bin 与 dup.bin 内容、地址全同：不管谁先抢到那次条件写，另一个必然拿到
            // if-missing 命中（UploadIfMissingAsync 返回 false）——brief 明确要求这也要记一行，
            // 不管抢赢的是谁，两条记录都必须在。
            var records = journal.Content.Records.Where(r => r.Kind == "blob" && r.Ref == expectedRef).ToList();
            Assert.Equal(2, records.Count);
            Assert.Equal(
                new[] { "dup.bin", "orig.bin" },
                records.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Pack_record_captures_members_and_volume_sizes()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("jw");
        var factoryOnly = new BlobClientFactory(TestSecrets.Reader);
        var (orchestrator, factory) = Build(new FailDataBlobs(new BlobUploader(factoryOnly)));
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            // 同目录三个小文件 → 合并成一个 pack；big.bin 走单文件通道，被 FailDataBlobs 永久拒绝，
            // 逼整轮运行失败，journal 才不会在收尾时被删掉，好让我们检查它。
            WriteText("d/a.txt", new string('a', 2000));
            WriteText("d/b.txt", new string('b', 2000));
            WriteText("d/c.txt", new string('c', 2000));
            WriteBytes("big.bin", 6_000_000);

            await using var control = new BackupRunControl(_journals, configId: 3, runId: "run-pack");
            await Assert.ThrowsAnyAsync<Exception>(
                () => orchestrator.RunAsync(Request(account, name), null, default, control));

            var listed = await _journals.ListAsync(account.Id, name, default);
            var journal = Assert.Single(listed);
            var record = Assert.Single(journal.Content.Records, r => r.Kind == "pack");

            Assert.False(string.IsNullOrEmpty(record.Ref));
            Assert.False(record.StoreOnly);
            Assert.Equal(3, record.Members.Count);
            var byPath = record.Members.ToDictionary(m => m.Path, StringComparer.Ordinal);
            foreach (var p in new[] { "d/a.txt", "d/b.txt", "d/c.txt" })
            {
                Assert.True(byPath.ContainsKey(p), $"missing member {p}");
                var m = byPath[p];
                Assert.False(string.IsNullOrEmpty(m.EntryName));
                Assert.False(string.IsNullOrEmpty(m.FullHash));
                Assert.Equal(2000, m.Length);
            }
            Assert.NotEmpty(record.VolumeSizes);
            Assert.All(record.VolumeSizes, s => Assert.True(s > 0));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Journal_keeps_what_was_confirmed_before_the_failure()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("jw");
        var factoryOnly = new BlobClientFactory(TestSecrets.Reader);
        // 两个大文件 → 各走单文件 blob 通道；第一个允许传，第二个起就拒。
        var (orchestrator, factory) = Build(new FailAfter(new BlobUploader(factoryOnly), allowed: 1));
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            await using (var control = new BackupRunControl(_journals, configId: 3, runId: "run-boom"))
            {
                await Assert.ThrowsAnyAsync<Exception>(
                    () => orchestrator.RunAsync(Request(account, name), null, default, control));
            }

            var listed = await _journals.ListAsync(account.Id, name, default);
            var journal = Assert.Single(listed);
            Assert.Equal("run-boom", journal.RunId);
            Assert.Equal(3, journal.Content.Header.ConfigId);
            Assert.Equal(0, journal.Content.Header.BaselineVersion);
            Assert.Equal(_root, journal.Content.Header.LocalRoot);
            // 只记下确实传完的那一个；被拒的那个绝不能出现在里面。
            var record = Assert.Single(journal.Content.Records);
            Assert.Equal("blob", record.Kind);
            Assert.False(string.IsNullOrEmpty(record.FullHash));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
