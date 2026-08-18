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
    /// <para>
    /// <see cref="Uploads"/> counts the entries the upload loop has taken past its gate. It is counted here rather
    /// than read off a progress column because it must be exact and must not depend on anything being published:
    /// the assertion it carries in <see cref="Pause_Holds_The_Pipeline_And_Resume_Picks_It_Up"/> is that this number
    /// never grows past one entry per uploader once the hold is up.
    /// </para>
    /// </summary>
    private sealed class BlockingUploader(Task gate, IBlobUploader inner) : IBlobUploader
    {
        private int _uploads;

        public int Uploads => Volatile.Read(ref _uploads);

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            Interlocked.Increment(ref _uploads);
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

    /// <summary>
    /// Every upload costs <paramref name="delay"/> before it is let through. Unlike <see cref="BlockingUploader"/>
    /// this never stops the pipeline — it only makes the run long enough that a stop pressed part-way through still
    /// has work in flight to reach.
    /// <para>
    /// Note what it is **not** good for: proving that the upload loop honours the gate. A throttle leaves the
    /// compressor as the stage running ahead, so by the time a pause lands the uploaders hold everything staged and
    /// the queue behind them is empty — with nothing left to take, "processed stopped advancing" is true whether or
    /// not the loop checks anything. <see cref="Pause_Holds_The_Pipeline_And_Resume_Picks_It_Up"/> uses
    /// <see cref="BlockingUploader"/> for exactly that reason.
    /// </para>
    /// </summary>
    private sealed class SlowUploader(TimeSpan delay, IBlobUploader inner) : IBlobUploader
    {
        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            await Task.Delay(delay, ct);
            return await inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public async Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            await Task.Delay(delay, ct);
            await inner.UploadOverwriteAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    /// <summary>
    /// Counts every probe and holds the **first** one open until <paramref name="release"/> completes.
    /// <para>
    /// Both halves are load-bearing for <see cref="A_Pause_Holds_The_Prober_And_The_Compressor"/>. The count is the
    /// prober's own observable: <c>ProbeForDedupAsync</c> calls <c>HeadHashAsync</c> as the first thing it does with
    /// an item it has claimed (see BackupOrchestrator.ProbeForDedupAsync), so "how many probes have happened" is exactly "how many
    /// items the prober has taken past its gate" — no progress column in between, nothing to publish, nothing to
    /// throttle. The hold is what makes the observation deterministic rather than a race: it keeps the prober inside
    /// its first item until the pause is already standing, so the work the loops would consume if their gates were
    /// gone becomes available **after** the hold goes up, not before.
    /// </para>
    /// <para>
    /// Only the orchestrator's own hasher is wrapped. The diff has its own <see cref="FileHasher"/> instance (see
    /// <see cref="Build"/>), so the walk that fills the work queue runs at full speed and is not counted here.
    /// <see cref="ContentIdentityAsync"/> is forwarded rather than left to the interface's default, which would
    /// reach back into <see cref="HeadHashAsync"/> and inflate the count from a caller that is not the prober.
    /// </para>
    /// </summary>
    private sealed class GatedHasher(Task release, IFileHasher inner) : IFileHasher
    {
        private int _probes;
        private int _held;

        public int Probes => Volatile.Read(ref _probes);

        public async Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _probes);
            if (Interlocked.Exchange(ref _held, 1) == 0)
                await release.WaitAsync(ct);
            return await inner.HeadHashAsync(path, headBytes, ct);
        }

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            inner.TailHashAsync(path, tailBytes, ct);

        public Task<string> FullHashAsync(
            string path, CancellationToken ct = default, IProgress<long>? onRead = null) =>
            inner.FullHashAsync(path, ct, onRead);

        public Task<ContentIdentity> ContentIdentityAsync(
            string path, int segmentBytes, CancellationToken ct = default) =>
            inner.ContentIdentityAsync(path, segmentBytes, ct);
    }

    /// <summary>
    /// Every "Uploading" line out of the pipeline reporter, in order.
    /// <para>
    /// Reads <c>Details</c> rather than <c>Detail</c> on purpose: while the diff is still walking, the reporter
    /// publishes both lines and <c>Detail</c> is <c>Details[0]</c>, which is the **diff's** — so a filter on
    /// <c>Detail</c> silently drops every upload snapshot taken before the diff finishes. That is invisible in a
    /// test that only ever reads the last one, and fatal in a test whose assertion is that a particular snapshot
    /// never appeared.
    /// </para>
    /// </summary>
    private static Progress<BackupProgress> UploadSnapshots(List<StageProgress> into) =>
        new(p =>
        {
            foreach (var d in p.Details.Where(d => d.Stage == "Uploading"))
                lock (into) into.Add(d);
        });

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

    /// <param name="hasher">The orchestrator's own hasher, i.e. the one the **probe** goes through. Null = the real
    /// one. The diff keeps its own instance either way, so substituting this one slows or counts the pipeline's
    /// first stage without touching the walk that feeds it.</param>
    private (BackupOrchestrator Orchestrator, StagingArea Staging, BackupRequest Request) Build(
        IBlobUploader? uploader, long stagingLimit, int uploadConcurrency, string container,
        IFileHasher? hasher = null)
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
            hasher ?? new FileHasher(), authority.IndexCache, authority.Tracked);
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

    /// <summary>
    /// Pause holds the **uploader's** loop, and holds the work rather than discarding it — that is the whole
    /// difference from Suspend. A resumed run must not have lost, or have to redo, what it had already staged.
    /// <para>
    /// One gate, not four. <c>Processed</c> only advances when an upload completes, which is strictly downstream of
    /// the uploader's gate, so this case is blind to the other three: park the uploaders and the compressor can go
    /// on staging, the prober on probing and the diff on enqueuing without moving this number by one.
    /// <see cref="A_Pause_Holds_The_Prober_And_The_Compressor"/> pins the middle two and
    /// <see cref="A_Suspend_Reaches_A_Run_Paused_Before_The_Diff_Ever_Started"/> the first; measured by deleting
    /// each gate in turn and watching which case went red (see the fix report).
    /// </para>
    /// <para>
    /// The backlog is built before the hold goes up, and it is built with a blocked uploader rather than a slow one.
    /// A throttle is not enough here, and the difference is the whole difference between a test of the gate and a
    /// test of the clock: with the uploads merely slowed, the compressor is the stage running ahead, so by the time
    /// the pause lands the uploaders hold everything that has been staged and the queue behind them is empty —
    /// delete their gate and there is nothing for them to take, so <c>processed</c> stands still either way and the
    /// case passes on the broken code. (Measured: with the uploader's gate deleted, the throttled version of this
    /// case stayed green.) Blocking the uploads instead lets the compressor stage the whole fixture while not one
    /// upload completes, so when the hold goes up there are eight-odd entries queued and at most one per uploader
    /// claimed — a real backlog, and no way to consume it except past the gate.
    /// </para>
    /// <para>
    /// The block is released **after** the pause, which is what makes the reading deterministic rather than a race:
    /// the entries in hand finish and their uploaders come back to the gate and park. So the count of upload calls
    /// settles at one per uploader and stops, while with the gate gone it runs on through the queue.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Pause_Holds_The_Pipeline_And_Resume_Picks_It_Up()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        const int files = 12;
        // UploadLoopAsync runs Max(2, UploadConcurrency + 1) consumers (see the `uploaders` local in
        // BackupOrchestrator.RunAsync), and each holds at most one queue entry — so this is the most entries that
        // can be in hand, and therefore the most upload calls the run may still make after the hold goes up.
        const int uploaders = 3;
        for (var i = 0; i < files; i++)
            WriteFile($"f{i}.bin", FileSize);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var name = RandomName("cont");
        var uploader = new BlockingUploader(block.Task, new BlobUploader(factory));
        var (orchestrator, staging, request) = Build(
            uploader, stagingLimit: 200_000_000, uploadConcurrency: 2, container: name);

        var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
        await using var control = new BackupRunControl(journals, configId: 1, runId: "pause-hold");

        var seen = new List<StageProgress>();

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        try
        {
            var run = orchestrator.RunAsync(request, UploadSnapshots(seen), ct: default, control: control);
            // Eight archives in the pool with every upload blocked = at least five entries queued behind the three
            // an uploader can be holding. That margin is what the assertion below rests on: five is more than one
            // per uploader, so "no more than one per uploader was uploaded" cannot be satisfied by an empty queue.
            await WaitUntil(
                () => staging.StagedBytes >= 8L * FileSize, TimeSpan.FromSeconds(90),
                () => $"the pool only reached {staging.StagedBytes} bytes, so there was no backlog for the pause "
                    + $"to hold; the staging limit was 200,000,000 and {files} files were written.");

            control.Gate.PauseByUser();
            Assert.Equal(PauseSource.User, control.Gate.Current!.Source);
            // Now let the entries in hand run to completion. "Finish the item in hand, then hold" means each
            // uploader may make its one call and must then park; three seconds is many times what these 2 MB
            // uploads to a local Azurite need.
            block.SetResult();
            await Task.Delay(3000);

            Assert.True(uploader.Uploads <= uploaders,
                $"{uploader.Uploads} uploads for {uploaders} uploaders: the queue kept being consumed after the "
                + "hold went up, so the upload loop is not checking the gate.");
            StageProgress atPause;
            lock (seen) atPause = seen[^1];
            // Without this the test would pass on a run that was simply over: two equal readings prove nothing
            // if there was nothing left to do between them.
            Assert.True(atPause.Processed < files,
                $"the run had already processed {atPause.Processed} of {files} items when the pause was pressed — "
                + "there was no work left for the pause to hold, so this run proves nothing.");

            await Task.Delay(3000);
            int processedLater;
            lock (seen) processedLater = seen[^1].Processed;
            Assert.Equal(atPause.Processed, processedLater);

            control.Gate.ResumeByUser();
            var result = await run.WaitAsync(TimeSpan.FromMinutes(3));
            Assert.Equal(1, result.Version);
            // The other half of the backlog assertion: everything the hold stopped the uploaders from taking was
            // still there to take afterwards.
            Assert.True(uploader.Uploads >= files,
                $"only {uploader.Uploads} uploads for {files} files: work queued at the pause never came back.");

            // Held, not discarded: every item the run started with reaches processed. BackupRunResult has no
            // "files uploaded" member (its file counts are all diff-derived), so the ledger is the witness —
            // processed == total on the far side of the pause is exactly "nothing was dropped at the gate".
            // Snapshots are delivered through Progress<T>, i.e. on the thread pool, so the last one may still be
            // in flight when the run's task completes.
            await WaitUntil(
                () => { lock (seen) return seen[^1] is { Total: files } s && s.Processed == files; },
                TimeSpan.FromSeconds(30),
                () =>
                {
                    lock (seen)
                        return $"the run ended with processed={seen[^1].Processed} of total={seen[^1].Total}: "
                            + "work queued when the pause landed never came back.";
                });
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// The two middle gates: with the hold up, the prober takes no further item out of the work queue and the
    /// compressor stages nothing out of the probed queue — however much work is put in front of them.
    /// <para>
    /// The difficulty this fixture exists to solve is that a producing stage is normally blocked on something other
    /// than the gate. Left to run, the pipeline settles into one of two states, and its gates are invisible in both:
    /// with a small fixture every stage drains its input and waits on an empty queue, and with a large one every
    /// stage fills its output and waits on backpressure (probed queue capacity 128, staging quota in bytes). Either
    /// way, deleting a gate changes nothing observable and a test built on "these two readings are equal" passes on
    /// the broken code. So the work is made available **after** the pause is already standing: the prober is held
    /// inside its first probe by <see cref="GatedHasher"/>, the pause goes up, and only then is the probe released.
    /// The prober then walks back to the top of its loop with seven items waiting in the queue behind it, and the
    /// compressor finds the one item that prober just handed over sitting in front of it — both with their inputs
    /// non-empty, both with room downstream, and both with nothing to stop them except the gate.
    /// </para>
    /// <para>
    /// The observables are direct rather than progress columns. <c>GatedHasher.Probes</c> counts what the prober
    /// claimed (<c>HeadHashAsync</c> is the first thing <c>ProbeForDedupAsync</c> does with a claimed item) and
    /// <c>StagingArea.StagedBytes</c> counts what the compressor produced (<c>StageCoreAsync</c> adds to it only
    /// once a compression and its move-to-staged are both complete). Neither depends on a snapshot being published:
    /// <c>Enqueue</c> and <c>BeginWork</c> publish nothing at all, and the heartbeat that would otherwise refresh
    /// the numbers stops when no stream is in flight — which is precisely the state a pause produces. A reading
    /// taken off <c>StageProgress</c> here could be a stale copy of the one taken before the pause, and two equal
    /// readings of the same stale snapshot prove nothing.
    /// </para>
    /// <para>
    /// The resume at the end is the anti-vacuity half: it is what proves the seven items really were queued and the
    /// eighth really was compressible, i.e. that the frozen readings above were work being held rather than work
    /// that did not exist.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Pause_Holds_The_Prober_And_The_Compressor()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        const int files = 8;
        for (var i = 0; i < files; i++)
            WriteFile($"f{i}.bin", FileSize);

        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hasher = new GatedHasher(probing.Task, new FileHasher());
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var name = RandomName("cont");
        var (orchestrator, staging, request) = Build(
            uploader: null, stagingLimit: 200_000_000, uploadConcurrency: 2, container: name, hasher: hasher);

        var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
        await using var control = new BackupRunControl(journals, configId: 1, runId: "pause-feeding");

        var seen = new List<StageProgress>();

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        try
        {
            var run = orchestrator.RunAsync(request, UploadSnapshots(seen), ct: default, control: control);

            // Two conditions, and the second is what makes the prober's half of this test mean anything: a settled
            // total is the diff saying it has finished enqueuing, so the other seven items are provably **in the
            // work queue**, not merely undiscovered. Without it a slow walk could leave the prober parked at its
            // gate with nothing behind it, and "it took no further item" would be true of a queue that was empty.
            await WaitUntil(
                () =>
                {
                    lock (seen) return hasher.Probes == 1 && seen.Any(s => s.Total == files);
                },
                TimeSpan.FromSeconds(60),
                () =>
                {
                    lock (seen)
                        return $"probes={hasher.Probes} (want 1) and the diff "
                            + (seen.Any(s => s.Total == files) ? "settled" : "never settled")
                            + $" its total across {seen.Count} upload snapshots.";
                });

            control.Gate.PauseByUser();
            // The hold is up first, then the work appears: the released probe finishes, its item lands in the
            // probed queue, and the prober comes back round to a queue with seven items in it. Both loops now have
            // something to take and nothing but the gate stopping them.
            probing.SetResult();
            await Task.Delay(3000);

            Assert.Equal(1, hasher.Probes);
            Assert.Equal(0, staging.StagedBytes);

            control.Gate.ResumeByUser();
            var result = await run.WaitAsync(TimeSpan.FromMinutes(3));
            Assert.Equal(1, result.Version);
            // Held, not lost. Every file was probed after the resume, so the queue the prober walked away from was
            // full the whole time it stood still — the two assertions above were measuring a hold, not an
            // already-finished run. (The count is a lower bound: the index-entry override re-reads the head hash
            // of each file it uploads, through this same hasher.)
            Assert.True(hasher.Probes >= files,
                $"only {hasher.Probes} probes for {files} files: the pause dropped work rather than holding it.");
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// A suspend must not wait for work it is about to throw away. The probe reads a whole candidate file to derive
    /// a content identity that is persisted nowhere, and the compressor's output goes into a queue the suspend tail
    /// drains — so finishing either is pure cost, paid in a stretch where the operator is watching a progress bar
    /// that has stopped meaning anything. Only the upload in flight is worth finishing, because only it can be
    /// journalled.
    /// <para>
    /// The shared 2 MB <see cref="FileSize"/> compresses in well under a second, which is exactly why it is not used
    /// here: with it, the suspend would almost always land between items rather than inside one, and the bound below
    /// would pass regardless of whether the fix is present. This test needs a compression that is still running,
    /// with certainty, at the moment Suspend is pressed — so <c>bigFileSize</c> is chosen to make one archive take
    /// tens of seconds through 7z -mx9 (measured ~35s for 150 MB of incompressible bytes on the CI machine via the
    /// same <c>-si</c> streaming path <see cref="SevenZipCompressor.CompressStreamAsync"/> uses).
    /// </para>
    /// <para>
    /// The wait below gates on <c>staging.StagedBytes >= FileSize</c>, not on a progress flag: <c>StageProgress</c>'s
    /// <c>Uploading</c> counts an item from the moment it enters the pre-compression dedup check (see the
    /// <c>Checking</c> field doc on <see cref="StageProgress"/>), so it can be non-zero for both files before either
    /// has finished compressing — a wait built on it can be satisfied while only the small file, not the big one, is
    /// in flight, which lets Suspend land too early and passes the test whether or not the fix is present.
    /// <see cref="StagingArea.StagedBytes"/> has no such ambiguity: <c>StageCoreAsync</c> only adds to it after a
    /// file's compression and move-to-staged are both complete, so once it reflects the small file's full size, the
    /// small file is provably done compressing. Compression is strictly serial (one lock in <see cref="StagingArea"/>),
    /// so whatever is in the compress-temp directory at that point can only be the big file's archive.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Suspend_Does_Not_Wait_For_The_Feeding_Stages()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        const int bigFileSize = 150 * 1024 * 1024;
        // Ordinal names so LocalFileScanner's sort (see LocalFileScanner.cs:69) puts the small file first: the
        // single compressor is serial, so this is what guarantees the small file is the one already uploading —
        // not the one whose compression gets abandoned — by the time both conditions below are checked.
        WriteFile("0-small.bin", FileSize);
        WriteFile("1-big.bin", bigFileSize);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var name = RandomName("cont");
        var (orchestrator, staging, request) = Build(
            new BlockingUploader(block.Task, new BlobUploader(factory)),
            stagingLimit: 200_000_000, uploadConcurrency: 2, container: name);

        var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
        await using var control = new BackupRunControl(journals, configId: 1, runId: "staged-stop");

        var compressDir = Path.Combine(_temp, "compress");
        bool BigFileCompressing() => Directory.Exists(compressDir) && Directory.EnumerateFiles(compressDir).Any();

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        try
        {
            var run = orchestrator.RunAsync(request, progress: null, ct: default, control: control);
            await WaitUntil(
                () => staging.StagedBytes >= FileSize && BigFileCompressing(), TimeSpan.FromSeconds(60),
                () => $"pipeline never reached both stages at once; staged={staging.StagedBytes}, "
                    + $"compressing={BigFileCompressing()}.");

            var pressed = System.Diagnostics.Stopwatch.StartNew();
            control.RequestStop(StopKind.Suspend);
            block.SetResult();
            // Suspend maps to BackupSuspendedException (see BackupOrchestrator.SettleStopAsync), not a raw
            // OperationCanceledException — that distinction is the whole point of the exception, so the run's
            // journal is treated as a resumable midpoint rather than a failure.
            await Assert.ThrowsAsync<BackupSuspendedException>(() => run);
            pressed.Stop();

            // The bound is the point: without the StopToken link the run has to finish compressing the 150 MB file,
            // which measured ~35s on this machine, and on a real backup can be minutes. With it, the feeding stages
            // are cancelled within about as long as it takes to kill the 7z process and let one CopyToAsync chunk
            // observe cancellation, and only the released upload — already small and already in flight — has to drain.
            Assert.True(pressed.Elapsed < TimeSpan.FromSeconds(20),
                $"suspend took {pressed.Elapsed.TotalSeconds:F1}s — the feeding stages were not cancelled.");
            Assert.Equal(0, staging.StagedBytes);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// Paused is not a dead end. Suspend from a paused run must reach every worker parked at the gate — the
    /// assertion behind <c>Downgrade</c> piercing a user pause (<c>PauseGate.ReleaseLocked</c>'s
    /// <c>proceed &amp;&amp; _pausedByUser</c> guard is never taken for a downgrade, whose <c>proceed</c> is always
    /// false) — and leave a journal the next run can pick up.
    /// <para>
    /// The uploader is throttled rather than blocked, which is the right choice here and the wrong one one test up:
    /// with the real uploader, twelve 2 MB files against a local Azurite can finish before the pause even
    /// lands, and a pipeline with nothing left to hold proves nothing about whether Suspend reaches a parked
    /// worker. What this case asserts is that the suspend arrives and the journal is written, not that a queue
    /// stopped being consumed, so it does not need <see cref="SlowUploader"/>'s blind spot closed.
    /// The delay also gives the three-second settling window below room to let at least one upload — begun
    /// before the pause, since uploads run on <c>working</c> and are never gated — actually land, so the journal
    /// assertion at the end is not vacuously checking an empty file.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Paused_Run_Can_Still_Be_Suspended()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        for (var i = 0; i < 12; i++)
            WriteFile($"f{i}.bin", FileSize);

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var name = RandomName("cont");
        var (orchestrator, staging, request) = Build(
            new SlowUploader(TimeSpan.FromSeconds(2), new BlobUploader(factory)),
            stagingLimit: 200_000_000, uploadConcurrency: 2, container: name);

        var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
        await using var control = new BackupRunControl(journals, configId: 1, runId: "paused-suspend");

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        try
        {
            var run = orchestrator.RunAsync(request, progress: null, ct: default, control: control);
            await WaitUntil(
                () => staging.StagedBytes > 0, TimeSpan.FromSeconds(60),
                () => "the pipeline never got going before the pause.");

            control.Gate.PauseByUser();
            // One item per stage may still be in hand; let the whole pipeline park before pressing Suspend.
            await Task.Delay(3000);
            Assert.Equal(PauseSource.User, control.Gate.Current!.Source);

            var pressed = System.Diagnostics.Stopwatch.StartNew();
            // Intent first, then Downgrade (inside RequestStop) does the releasing — no separate ReleaseNow call.
            // See the task report for why an extra one would be redundant.
            control.RequestStop(StopKind.Suspend);
            await Assert.ThrowsAsync<BackupSuspendedException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
            pressed.Stop();

            // Had any loop stayed parked behind the pause, this would time out rather than throw.
            Assert.True(pressed.Elapsed < TimeSpan.FromSeconds(25),
                $"suspend took {pressed.Elapsed.TotalSeconds:F1}s — a loop stayed parked behind the pause.");
            Assert.Equal(0, staging.StagedBytes);

            // The journal is the resumable midpoint the design promises: at least the upload already on the wire
            // when the pause landed must have been written down for the next run to pick up.
            var journal = Assert.Single(await journals.ListAsync(request.Account.Id, name, default));
            Assert.NotEmpty(journal.Content.Records);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// The severest case: a pause pressed before the run even starts must not leave the run permanently silent.
    /// Design §3 says pausing "stops the disk being read at all" — the diff's own gate, at the very top of
    /// <c>OnChangeAsync</c>, is what makes that true — and this pins that a Suspend still terminates cleanly when
    /// the diff's gate is the *only* one any loop ever reached.
    /// <para>
    /// Unlike <see cref="A_Paused_Run_Can_Still_Be_Suspended"/>, this is not independent proof of
    /// <c>PauseGate.Downgrade</c>'s piercing: the diff waits on <c>stopProducing.Token</c>, which
    /// <c>BackupOrchestrator.RunAsync</c> links straight to <c>control.StopToken</c> (see the comment above
    /// <c>stopProducing</c>'s declaration), and <c>RequestStop</c> cancels that token unconditionally, independently
    /// of whatever the gate itself does. So this loop would unblock on a Suspend even if <c>Downgrade</c> stopped
    /// releasing the gate outright — confirmed by temporarily disabling the piercing in <c>PauseGate</c> and
    /// re-running both tests: <see cref="A_Paused_Run_Can_Still_Be_Suspended"/> failed (timeout), this one still
    /// passed. It is kept anyway because it pins a real and distinct risk — "paused before anything was produced"
    /// is the shape most likely to hang — regardless of which of the two independent mechanisms is what saves it.
    /// </para>
    /// <para>
    /// 400 tiny files rather than the shared 2 MB <see cref="FileSize"/>: were neither mechanism holding, this many
    /// small files would start landing in <c>staging</c> within a couple of seconds even through the real
    /// 7z/Azurite path, so <c>staging.StagedBytes</c> staying at exactly 0 for the whole 8-second wait below is
    /// direct evidence the diff never got past its very first parked item — not just that this particular run
    /// happened to be slow. <c>run.IsCompleted</c> staying false is the same evidence from the other side: a
    /// pipeline that was never actually held would have nothing left to do with 400 tiny files and no throttling,
    /// and would simply finish.
    /// </para>
    /// <para>
    /// This is also where the **diff's own** gate is pinned, and neither of those two readings does it: the prober's
    /// gate is up as well, so with the diff's gate deleted all 400 items would be enqueued and then sit in the work
    /// queue — nothing staged, nothing completed, both readings unchanged. What separates the two is whether the
    /// pipeline was ever offered anything, and the settled total is where that shows: <c>SetTotal</c> is called once
    /// the diff has finished walking and force-publishes, followed immediately by <c>Settle</c>, so an upload
    /// snapshot carrying a total (or anything queued) is proof the walk ran. With the gate in place the diff never
    /// gets past its first change callback, so no such snapshot can exist.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Suspend_Reaches_A_Run_Paused_Before_The_Diff_Ever_Started()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        const int files = 400;
        for (var i = 0; i < files; i++)
            WriteFile($"f{i}.bin", 256);

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var name = RandomName("cont");
        var (orchestrator, staging, request) = Build(
            uploader: null, stagingLimit: 200_000_000, uploadConcurrency: 2, container: name);

        var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
        await using var control = new BackupRunControl(journals, configId: 1, runId: "paused-before-start");

        // Closed before the run is even started: the diff's very first WaitIfPausedAsync call parks before
        // OnChangeAsync ever offers the pipeline a single item.
        control.Gate.PauseByUser();

        var seen = new List<StageProgress>();

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        try
        {
            var run = orchestrator.RunAsync(request, UploadSnapshots(seen), ct: default, control: control);

            await Task.Delay(8000);
            Assert.False(run.IsCompleted,
                "the run finished before the pause was ever exercised — this proves nothing.");
            Assert.Equal(0, staging.StagedBytes);
            // Not one item was ever offered to the pipeline: the diff parked in its own change callback, so it
            // never reached the end of the walk that settles the total, and never enqueued anything for the
            // stages behind it to hold.
            lock (seen)
                Assert.DoesNotContain(seen, s => s.Total > 0 || s.Queued > 0);

            var pressed = System.Diagnostics.Stopwatch.StartNew();
            control.RequestStop(StopKind.Suspend);
            await Assert.ThrowsAsync<BackupSuspendedException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
            pressed.Stop();

            Assert.True(pressed.Elapsed < TimeSpan.FromSeconds(25),
                $"suspend took {pressed.Elapsed.TotalSeconds:F1}s — the diff stayed parked behind the pause.");
            Assert.Equal(0, staging.StagedBytes);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// A sink that runs on the reporting thread, unlike <see cref="Progress{T}"/>, which posts the callback
    /// elsewhere and swallows whatever it throws. The orchestrator's own comment above <c>SetTotal</c> says these
    /// calls go "straight into the caller's progress sink, which is somebody else's code" — that is only true of a
    /// synchronous sink, and it is the shape RestoreOrchestrator and BackupChecker are handed in production.
    /// </summary>
    private sealed class SynchronousSink(Action<BackupProgress> on) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => on(value);
    }

    /// <summary>
    /// A run that fails in the tail of its own body must still end, even with the operator's pause standing.
    /// <para>
    /// The teardown for such a failure waits for the producing loops to settle, and those loops park at the pause
    /// gate. A user pause is deliberately without a timer and without patience (design §4: an automatic downgrade
    /// would convert a pause into a suspend exactly when nobody is watching), so before the fix that wait had
    /// nothing that could ever end it: no token on this path is cancelled, and the only other caller of
    /// <c>Downgrade</c> is <c>RequestStop</c>. <c>RunAsync</c> never returned, so the busy lock stayed taken and
    /// the process-wide staging quota stayed booked until the container was restarted.
    /// </para>
    /// <para>
    /// The fault is injected where the orchestrator itself names the hazard — the caller's progress sink, reached
    /// from <c>SetTotal</c> once the diff has settled the denominator. Injecting it there rather than in a consumer
    /// is the point: a consumer's fault cancels the producing side on its way out, while this one leaves every
    /// token untouched, which is the state that hung.
    /// </para>
    /// <para>
    /// The sink closes the gate itself, one statement before it throws, so the ordering the case needs — held
    /// first, failed second — is caused rather than waited for. What the loops do afterwards is not asserted here
    /// beyond "they finish": a downgrade releases them to drain what is queued and exit, which is exactly what
    /// they do when a fault lands on a run nobody paused.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Run_That_Fails_While_Paused_Still_Ends()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        const int files = 24;
        for (var i = 0; i < files; i++)
            WriteFile($"f{i}.bin", 4096);

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var name = RandomName("cont");
        var (orchestrator, staging, request) = Build(
            uploader: null, stagingLimit: 200_000_000, uploadConcurrency: 2, container: name);

        var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
        await using var control = new BackupRunControl(journals, configId: 1, runId: "fail-while-paused");

        var held = 0;
        var thrown = 0;
        // The first publish carrying a non-zero Uploading total is SetTotal's own: until the diff has finished,
        // every other publish of that stage reports a total of 0.
        var sink = new SynchronousSink(p =>
        {
            if (!p.Details.Any(d => d.Stage == "Uploading" && d.Total > 0))
                return;
            if (Interlocked.Exchange(ref thrown, 1) != 0)
                return;
            if (control.Gate.PauseByUser())
                Interlocked.Exchange(ref held, 1);
            throw new InvalidOperationException("the progress sink broke");
        });

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        try
        {
            var run = orchestrator.RunAsync(request, sink, ct: default, control: control);

            var settled = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(
                ReferenceEquals(settled, run),
                "the run never returned. The pause was still holding the gate its producing loops park at, and "
                + "nothing on the failure path takes that hold down — so the busy lock and the process-wide "
                + "staging quota are held until the process restarts.");

            Assert.Equal(1, Volatile.Read(ref thrown));   // the scene really was built
            Assert.Equal(1, Volatile.Read(ref held));     // ...with the hold standing when it was
            await Assert.ThrowsAsync<InvalidOperationException>(() => run);
            Assert.Equal(0, staging.StagedBytes);         // and the queues were drained, not abandoned
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }
}
