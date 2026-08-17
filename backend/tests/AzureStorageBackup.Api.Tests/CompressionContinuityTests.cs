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

    /// <summary>
    /// The point of the whole rework: the staging limit, not the worker pool, is what stops compression.
    /// Before it the pool saturated first, so this setting had no effect at any value — 10 GB, 2 GB and
    /// 40 GB all produced identical behaviour.
    /// </summary>
    [SkippableFact]
    public async Task The_Staging_Limit_Is_What_Stops_Compression()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        for (var i = 0; i < 12; i++)
            WriteFile($"f{i}.bin", FileSize);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var name = RandomName("cont");
        // Four items' worth of room. HasRoom admits a caller whose current usage is below the limit, so a
        // single archive may overshoot it — hence the slack in the assertion below.
        var limit = 4L * FileSize;
        var (orchestrator, staging, request) = Build(
            new BlockingUploader(block.Task, new BlobUploader(factory)),
            stagingLimit: limit, uploadConcurrency: 2, container: name);

        var seen = new List<StageProgress>();
        var progress = new Progress<BackupProgress>(p =>
        {
            if (p.Detail is { Stage: "Uploading" } d)
                lock (seen) seen.Add(d);
        });

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        try
        {
            var run = orchestrator.RunAsync(request, progress);
            try
            {
                // The compressor now queues on the quota instead of on a free worker, and says so on screen.
                // That column reading non-zero is the visible evidence the operator never sees today.
                await WaitUntil(
                    () => { lock (seen) return seen.Any(s => s.WaitingOnArchive > 0); },
                    TimeSpan.FromSeconds(60),
                    () =>
                    {
                        lock (seen)
                            return $"WaitingOnArchive never went above zero across {seen.Count} snapshots; "
                                + "the staging limit was never the binding constraint.";
                    });
                Assert.True(staging.StagedBytes <= limit + FileSize,
                    $"pool grew past the limit plus one archive: {staging.StagedBytes}");
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

    /// <summary>
    /// Both queues, and the assertion is on the pool because that is what a leak costs: the quota is booked
    /// on a process-wide singleton, so anything left behind throttles every other backup on the machine until
    /// the process restarts.
    /// </summary>
    [SkippableFact]
    public async Task Stop_Releases_Everything_Still_Queued()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        for (var i = 0; i < 12; i++)
            WriteFile($"f{i}.bin", FileSize);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var name = RandomName("cont");
        var (orchestrator, staging, request) = Build(
            new BlockingUploader(block.Task, new BlobUploader(factory)),
            stagingLimit: 200_000_000, uploadConcurrency: 2, container: name);

        var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
        await using var control = new BackupRunControl(journals, configId: 1, runId: "stop-drain");

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        try
        {
            var run = orchestrator.RunAsync(request, progress: null, ct: default, control: control);
            await WaitUntil(
                () => staging.StagedBytes > 4L * FileSize, TimeSpan.FromSeconds(60),
                () => $"Pool never grew past {4L * FileSize} bytes, staged={staging.StagedBytes}.");

            // FinishCurrentFiles is the ordinary stop: the item in hand finishes, nothing new starts.
            // Everything the compressor produced that no uploader claimed has to be handed back.
            control.RequestStop(StopKind.FinishCurrentFiles);
            block.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            Assert.Equal(0, staging.StagedBytes);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_temp, "staged")));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// The identity the operator uses to judge "did work vanish": processed + preparing + queued +
    /// waitingOnArchive + uploading == total. Entries parked in either queue fall under `uploading`
    /// (inWork - inStaging), so the sum must not drift while both queues are full.
    /// <para>
    /// The staging limit is deliberately the same small one the limit test uses, and the wait is on
    /// waitingOnArchive rather than on the pool size: with a roomy limit that term stays 0 for the
    /// whole run, and the identity would be checked with one of its five terms never exercised —
    /// which is exactly the term this pipeline rework introduced a way to hold non-zero.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task The_Item_Ledger_Balances_With_Entries_Parked_In_The_Queues()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        for (var i = 0; i < 12; i++)
            WriteFile($"f{i}.bin", FileSize);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var name = RandomName("cont");
        var (orchestrator, staging, request) = Build(
            new BlockingUploader(block.Task, new BlobUploader(factory)),
            stagingLimit: 4L * FileSize, uploadConcurrency: 2, container: name);

        var seen = new List<StageProgress>();
        var progress = new Progress<BackupProgress>(p =>
        {
            if (p.Detail is { Stage: "Uploading" } d)
                lock (seen) seen.Add(d);
        });

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        try
        {
            var run = orchestrator.RunAsync(request, progress);
            try
            {
                await WaitUntil(
                    () => { lock (seen) return seen.Any(s => s.WaitingOnArchive > 0); },
                    TimeSpan.FromSeconds(60),
                    () => $"waitingOnArchive never became non-zero, so the identity would be checked "
                        + $"with that term dead; staged={staging.StagedBytes}.");

                // The total only settles once the diff finishes, so only snapshots that have one can be checked.
                List<StageProgress> settled;
                lock (seen) settled = [.. seen.Where(s => s.Total > 0)];
                Assert.NotEmpty(settled);
                foreach (var s in settled)
                    Assert.Equal(
                        s.Total,
                        s.Processed + s.Preparing + s.Queued + s.WaitingOnArchive + s.Uploading);
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
