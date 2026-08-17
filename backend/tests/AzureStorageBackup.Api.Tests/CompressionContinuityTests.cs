using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The compression stage must not be throttled by the upload stage.
/// <para>
/// Before this rework one worker owned an item from compression through the last volume of its upload, and there
/// were only UploadConcurrency + 1 workers. Once that many items were uploading, no worker could reach StageAsync
/// and compression stopped outright — measured in production with 23 items queued, 4.5 GB in the pool, and both
/// preparing and waitingOnArchive at zero. The staging limit was never the binding constraint, so the setting had
/// no effect at any value.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CompressionContinuityTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private const int FileSize = 2 * 1024 * 1024;

    private readonly string _base;
    private readonly string _root;
    private readonly string _temp;

    public CompressionContinuityTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-cont-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
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

    private void WriteFile(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>
    /// Every upload hangs on <paramref name="gate"/> before being let through to the real uploader, so the run
    /// still completes normally once the gate opens. Only the 8-argument overload needs implementing: the
    /// progress-reporting one has a default implementation that forwards to it (see IBlobUploader).
    /// </summary>
    private sealed class BlockingUploader(Task gate, IBlobUploader inner) : IBlobUploader
    {
        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            await gate.WaitAsync(ct);
            return await inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public async Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            await gate.WaitAsync(ct);
            await inner.UploadOverwriteAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    /// <param name="describe">What the pool looked like when patience ran out. Without it the failure reads
    /// "condition not met", which cannot tell "compression stalled" (the regression) from "the staging limit was
    /// set too low for this test" — and those want opposite reactions.</param>
    private static async Task WaitUntil(Func<bool> condition, TimeSpan patience, Func<string> describe)
    {
        var deadline = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Condition was not met in time. {describe()}");
    }

    private (BackupOrchestrator Orchestrator, StagingArea Staging, BackupRequest Request) Build(
        IBlobUploader? uploader, long stagingLimit, int uploadConcurrency, string container)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => stagingLimit);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(),
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
        var request = new BackupRequest
        {
            Account = AzuriteAccount(),
            Container = container,
            LocalRoot = _root,
            Name = "continuity",
            // Single-file blobs only: one item per file, so "how many items are in flight" is exactly the number
            // of files, with no packing to reason about.
            Options = new BackupEngineOptions
            {
                UploadConcurrency = uploadConcurrency,
                Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
            },
        };
        return (orchestrator, staging, request);
    }

    [SkippableFact]
    public async Task Compression_Keeps_Running_While_Every_Uploader_Is_Blocked()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        // Twelve items against three workers (concurrency 2 + 1): on the old code the pool plateaus at what
        // three in-flight items hold, because no worker is left to reach StageAsync.
        for (var i = 0; i < 12; i++)
            WriteFile($"f{i}.bin", FileSize);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var name = RandomName("cont");
        var (orchestrator, staging, request) = Build(
            new BlockingUploader(block.Task, new BlobUploader(factory)),
            stagingLimit: 200_000_000, uploadConcurrency: 2, container: name);

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        try
        {
            var run = orchestrator.RunAsync(request);
            try
            {
                // Every uploader is stuck on the gate. Compression must keep going regardless — more than the
                // three items' worth the old worker pool allowed. The files are random bytes, so each archive is
                // about FileSize; four of them is comfortably past the old ceiling and well under the staging limit.
                await WaitUntil(
                    () => staging.StagedBytes > 4L * FileSize, TimeSpan.FromSeconds(60),
                    () => $"Pool plateaued at {staging.StagedBytes} bytes, needed more than {4L * FileSize}; "
                        + "the staging limit was 200,000,000, so it was never the binding constraint.");
            }
            finally
            {
                block.SetResult();
            }

            await run;
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }
}
