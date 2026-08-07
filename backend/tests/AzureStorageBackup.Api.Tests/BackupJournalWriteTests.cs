using System.Net.Sockets;
using Azure.Storage.Blobs;
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

    [SkippableFact]
    public async Task Successful_run_deletes_its_journal()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("jw");
        var (orchestrator, factory) = Build();
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteText("a.txt", "hello");
            await using var control = new BackupRunControl(_journals, configId: 3, runId: "run-ok");
            await orchestrator.RunAsync(Request(account, name), null, default, control);

            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
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
