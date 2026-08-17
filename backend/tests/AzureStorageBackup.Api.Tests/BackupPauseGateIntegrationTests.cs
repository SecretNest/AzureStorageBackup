using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
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

    /// <summary>
    /// Incompressible content, so one file's archive is the same size as the file and "how much is in the staged
    /// pool" follows from the file sizes. <see cref="WriteBytes"/>'s all-zero content compresses down to a few
    /// hundred bytes and would never fill a pool of any size — which is exactly why every test in this suite could
    /// run with a 200 MB pool and never notice what happens when it is full.
    /// </summary>
    private void WriteRandom(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
    }

    // Same shape as BackupJournalWriteTests.Build: the real constructor's 13th parameter is the opLog one (notifier
    // comes before it), so an optional parameter is added here, notifier is skipped with a named argument, and the
    // rest (verboseLog/spillFactory) stay at their defaults.
    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory, StagingArea Staging) Build(
        IBlobUploader uploader, IOperationLog? opLog = null, long stagingLimit = 200_000_000)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => stagingLimit);
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
        return (orchestrator, factory, staging);
    }

    private BackupRequest Request(Account account, string container, long? volumeBytes = null) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
            VolumeBytes = volumeBytes,
        },
    };

    /// <summary>Throws a transient error on the first N uploads, then lets them through. Used to verify "a blip
    /// self-heals, it must not be condemned". The real IBlobUploader's parameter order is
    /// (tier, retry, ct, metadata[, progress]) — not the one in the brief's draft (tier, metadata, options, ct);
    /// the real signatures here are copied from FailAfter/GatedUploader in BackupJournalWriteTests.</summary>
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

    /// <summary>An IOperationLog double that collects AppendAsync into a list. The copies already in this project
    /// (OperationLogSourceTests, BackupRepairerTests) are all file-private nested classes, with no public version that
    /// could be reused directly — so here is one more of the same shape, rather than extracting a type shared across
    /// files (the payoff does not justify adding one more layer of indirection to the test infrastructure).</summary>
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
        var (orchestrator, factory, _) = Build(flaky);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big.bin", 6_000_000);
            await using var control = new BackupRunControl(_journals, 5, "run-heal", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
                // Patience only has to outlast "one blip plus a retry". It used to be 5 minutes: when things really
                // break, this test would burn the full 5 minutes before going red, while what it wants to say is
                // settled within a second.
                patience: TimeSpan.FromSeconds(5)));

            var result = await orchestrator.RunAsync(Request(account, name), null, default, control);

            Assert.Equal(1, result.Version);
            Assert.True(flaky.Attempts >= 2);   // one blip, one retry
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
        var (orchestrator, factory, _) = Build(flaky);
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
            // A suspension from a gate downgrade is thrown straight up from deep inside the pipeline, **bypassing**
            // SettleStopAsync — so the mark can only be written in RunAsync's catch. Moving it back into
            // SettleStopAsync (which looks more "cohesive") would leave every gate downgrade without a mark, and a
            // volume with no mark looks exactly like one that was killed or canceled, which nobody dares resume for.
            // This line is what pins that location down: getting ex.Reason right in memory does not count, it has to be on disk.
            Assert.Equal(SuspendReason.AutoSuspended, _journals.ReadSuspendMark(account.Id, name, "run-susp"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// An auto-suspend takes every uploader with it, and the compression stage must go too — not sit waiting for
    /// staging room that only an uploader could ever hand back.
    /// <para>
    /// This is the shape of the whole three-stage split turning against itself. The compressor's one blocking point
    /// is the staging quota, and that quota comes back only from a **live** uploader. When the gate's patience runs
    /// out, every uploader throws <see cref="BackupSuspendedException"/> out of <c>WithPauseAsync</c> at once, while
    /// the compressor sails on — 7z and the local disk never raise the transient errors the gate reacts to. Once the
    /// pool is full of entries nobody will ever claim, <c>StagingArea.WaitForRoomAsync</c> never returns, and with it
    /// the run never ends: no exception surfaces, the busy lock is never released, and the UI shows a backup that is
    /// simply frozen. Before the split all six workers were uploaders as well, so they all left and released their
    /// own quota on the way out, and the run suspended cleanly.
    /// </para>
    /// <para>
    /// The two numbers are the whole test. The pool is 4 MB against 40 MB of **incompressible** source, so it really
    /// does fill — every other test in this file runs a 200 MB pool over a few megabytes of zeroes, which is exactly
    /// why the suite could not see this. And the failure has to surface as the suspension that caused it, not as the
    /// cancellation used to unblock the compressor.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task An_auto_suspend_that_kills_every_uploader_does_not_strand_the_compressor()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("gate");
        var flaky = new FlakyUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), failures: 1000);
        var (orchestrator, factory, staging) = Build(flaky, stagingLimit: 4_000_000);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        // Only used to unblock a hung run so the rest of the session is not left with a compressor parked on a full
        // pool; on the passing path it is never fired, and the run's own token stays untouched.
        using var abort = new CancellationTokenSource();
        try
        {
            // Twenty items, of which the uploaders can consume at most one each before dying (UploadConcurrency 5 →
            // six of them), so there is always work left over for the compressor to jam on.
            for (var i = 0; i < 20; i++)
                WriteRandom($"f{i}.bin", 2_000_000);

            await using var control = new BackupRunControl(_journals, 5, "run-stranded", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(10)], steady: TimeSpan.FromMilliseconds(10),
                patience: TimeSpan.Zero));

            // Single-file blobs only: one item per file, so "how many items are in flight" is exactly the number of
            // files and there is no packing to reason about.
            var request = Request(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            };

            var run = orchestrator.RunAsync(request, null, abort.Token, control);
            if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(2))) != run)
            {
                await abort.CancelAsync();
                _ = run.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                Assert.Fail(
                    "the run never finished. Every uploader was gone and the compressor was still waiting for "
                    + $"staging room only an uploader could free — {staging.StagedBytes} bytes were sitting in a "
                    + "4,000,000-byte pool that nobody was left to drain.");
            }

            var ex = await Assert.ThrowsAsync<BackupSuspendedException>(() => run);
            // The suspension is the real cause and has to be what comes out. Unblocking the compressor with a
            // cancellation is the fix's mechanism, and a mechanism that surfaces instead of the cause would leave the
            // run recorded as Canceled — no suspend mark, so the next startup never resumes it.
            Assert.Equal(SuspendReason.AutoSuspended, ex.Reason);
            Assert.Equal(SuspendReason.AutoSuspended, _journals.ReadSuspendMark(account.Id, name, "run-stranded"));
            // Whatever was still queued when the stages stopped has to hand its archive back. That debt lives on a
            // process-wide singleton and is the backpressure gate on output, so leaking it here would throttle every
            // other backup on the machine until a restart.
            Assert.Equal(0, staging.StagedBytes);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The run Step 6 uses: patience threshold set to 0 so it downgrades the moment it hits the wall, with opLog swapped for a double we can peek into.</summary>
    private async Task RunWithAlwaysFailingUploadAsync(RecordingOperationLog log)
    {
        var account = AzuriteAccount();
        var name = RandomName("gate");
        var flaky = new FlakyUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), failures: 1000);
        var (orchestrator, factory, _) = Build(flaky, log);
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

    /// <summary>Content that compresses, but does not compress away: the first half is seeded pseudo-random
    /// (incompressible), the second half is a copy of the first. The compression ratio therefore sits steadily around
    /// 2:1 and splits into several volumes — all zeros would compress to a few KB and fit in one volume.</summary>
    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        var half = size / 2;
        new Random(20260807).NextBytes(bytes.AsSpan(0, half));
        bytes.AsSpan(0, size - half).CopyTo(bytes.AsSpan(half));
        return bytes;
    }

    /// <summary>
    /// Blip once (and only once) on the **last volume**. Picking the last volume was not arbitrary: the last volume
    /// carries the archive's end header, and the first volume's signature header records that end header's CRC.
    /// Leaving the first volume from compression attempt 1 while the last volume comes from attempt 2 is exactly the
    /// combination per-volume if-missing re-upload can assemble — and the only one that cannot be put back together.
    /// <para>
    /// The last volume = the short one that is not full (volume size 1 MB, last volume a bit over 1 KB). The earlier
    /// volumes have already taken off by then, and the uploader waits for them to settle before throwing the exception
    /// (see VolumeBlobIO's sliding window), so what is left in the cloud is the scene "the first few volumes of
    /// compression attempt 1, last volume missing".
    /// </para>
    /// </summary>
    private sealed class FailOnLastVolume(IBlobUploader inner, long volumeBytes) : IBlobUploader
    {
        private int _seen;
        private int _thrown;

        public int Attempts => _seen;

        private Task<bool> GateAsync(string blobName, string filePath, Func<Task<bool>> call)
        {
            Interlocked.Increment(ref _seen);
            var isVolume = blobName.Contains(".00", StringComparison.Ordinal);
            var isShort = new FileInfo(filePath).Length < volumeBytes;
            if (isVolume && isShort && Interlocked.Exchange(ref _thrown, 1) == 0)
                throw new AggregateException("Retry failed after 6 tries.", new TaskCanceledException("timeout"));
            return call();
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, filePath,
                () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => GateAsync(blobName, filePath,
                () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, filePath, async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    /// <summary>
    /// After the gate retries a **multi-volume** single-file blob, the whole volume family in the cloud must come
    /// from one and the same compression.
    /// <para>
    /// Per-volume upload is if-missing: volumes that landed on attempt 1 get skipped, and the gaps are filled by the
    /// output of compression attempt 2. But a single file is compressed from a pipe via <c>-si</c>, and two runs are
    /// not byte-for-byte identical (see SevenZipDeterminismTests), so the assembly is a family of volumes that will not
    /// open — while the index claims it is perfectly fine. So this does not look at the retry count; it pulls the whole
    /// archive back down from the cloud and extracts it: only getting the original bytes back counts.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Retrying_a_multi_volume_blob_leaves_one_compressions_volumes_not_a_mixture()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("gate");
        var flaky = new FailOnLastVolume(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), 1_000_000);
        var (orchestrator, factory, _) = Build(flaky);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            var payload = Payload(6_000_000);   // over the 5 MB threshold → takes the single-file blob path
            File.WriteAllBytes(Path.Combine(_root, "big.bin"), payload);
            // The backoff has to cross a whole second: 7z records a member's kMTime to the second, so retrying after
            // 20ms puts both compressions inside the same second, the outputs come out identical, and this test would
            // prove nothing (the real backoff starts at 30 seconds, so crossing a second is the norm, not the exception).
            await using var control = new BackupRunControl(_journals, 5, "run-mix", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(1200)], steady: TimeSpan.FromMilliseconds(1200),
                patience: TimeSpan.FromSeconds(10)));

            var result = await orchestrator.RunAsync(
                Request(account, name, volumeBytes: 1_000_000), null, default, control);
            Assert.Equal(1, result.Version);
            Assert.True(flaky.Attempts >= 2);

            // Pull this archive back down from the cloud and lay the volumes out in order.
            var volumes = new List<string>();
            await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", default))
                volumes.Add(b.Name);
            volumes.Sort(StringComparer.Ordinal);
            Assert.True(volumes.Count > 1,
                $"this test needs a multi-volume archive, but there is only {volumes.Count} volume(s) — the volume size or the compression ratio changed, put the setup back first.");

            var pulled = Path.Combine(_temp, "pulled");
            Directory.CreateDirectory(pulled);
            for (var i = 0; i < volumes.Count; i++)
            {
                var local = Path.Combine(pulled, $"a.7z.{i + 1:D3}");
                await cc.GetBlobClient(volumes[i]).DownloadToAsync(local);
            }

            await using var sink = new MemoryStream();
            var written = await new SevenZipCompressor().ExtractToStreamAsync(
                Path.Combine(pulled, "a.7z.001"), entryName: null, password: null, sink);

            Assert.Equal(payload.Length, written);
            Assert.True(sink.ToArray().AsSpan().SequenceEqual(payload), "the restored bytes do not match the original file.");
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    // A suspension is not a failure. Reporting it as an Error leaves this backup sitting under red text in the UI,
    // needing a manual Reset to clear — while the work in progress is plainly safe and the next run picks right up.
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
