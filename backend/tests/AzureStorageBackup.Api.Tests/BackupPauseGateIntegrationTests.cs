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
        private int _attempts;

        /// <summary>
        /// Interlocked, not a plain increment: several uploaders call this concurrently, and the deadlock test
        /// asserts on an exact expected call count with no slack. One lost update there turns that test red for
        /// a reason that has nothing to do with what it is testing.
        /// </summary>
        public int Attempts => Volatile.Read(ref _attempts);

        private void Gate()
        {
            Interlocked.Increment(ref _attempts);
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

    /// <summary>
    /// Holds the first content upload open until the test says otherwise, and then fails the first
    /// <paramref name="wave"/> of them — the double
    /// <see cref="BackupPauseGateIntegrationTests.Every_uploader_retrying_at_once_against_a_full_pool_still_finishes"/>
    /// builds its scene with.
    /// <para>
    /// Holding the first one is what makes that scene assemble instead of race. Concurrency permits are issued per
    /// volume from a pool of <c>UploadConcurrency</c> (see VolumeUploadGate), so with that turned down to one there
    /// is exactly one upload in flight at a time and the rest of the uploaders are queued behind its permit: one held
    /// call therefore stops the whole upload side at a place the test can see (<see cref="FirstUploadHeld"/>). The
    /// test can then arrange what it needs and let go, and every step is caused by the one before it rather than
    /// timed against it. A double that merely fails a count of calls — the one this test used to use — fails them at
    /// whatever moments the pipeline happens to make them, and can only hope the pool is in the state the scene needs
    /// when they land.
    /// </para>
    /// <para>
    /// The later members of the wave need no holding: they cannot even be attempted until the held one gives its
    /// permit back, which happens only when the test releases it, so by then the arrangement is already in place and
    /// they can fail on arrival.
    /// </para>
    /// <para>
    /// Only <c>data/</c> blobs take part. The index and info blobs the same run writes go through this double too,
    /// and holding one of those would stall the run somewhere the scene has nothing to say about.
    /// </para>
    /// </summary>
    /// <param name="wave">How many content uploads to fail. The scene wants **every** live uploader parked at the
    /// gate together, and an uploader reaches the gate by failing once, so this is the uploader count the request's
    /// UploadConcurrency implies — <c>max(2, UploadConcurrency + 1)</c>. Fewer, and an uploader is left outside the
    /// wave that keeps draining the pool.</param>
    private sealed class HoldsTheFirstUploadThenFailsTheWave(IBlobUploader inner, int wave) : IBlobUploader
    {
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // The blobs this double failed. A blob is content-addressed by the hash of the **source file**, not of the
        // archive, so the recompressed retry asks for the very same name — which is what makes a second call under a
        // name in here proof that the uploader got through its re-stage, and not merely that some other item moved.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _failed =
            new(StringComparer.Ordinal);
        private int _seen;
        private int _dataAttempts;
        private int _resends;

        /// <summary>Completes once a content upload is held open inside this double — with one permit to go round,
        /// that is the whole upload side stopped.</summary>
        public Task FirstUploadHeld => _held.Task;

        /// <summary>Completes once every failed blob has been asked for a second time, i.e. every uploader in the
        /// wave has come back through the gate, recompressed, and reached the upload again.</summary>
        public Task WaveResent => _resent.Task;

        public int DataAttempts => Volatile.Read(ref _dataAttempts);

        /// <summary>Let the held upload go, as a failure, and let the rest of the wave fail on arrival.</summary>
        public void FailTheWave() => _fail.TrySetResult();

        private async Task GateAsync(string blobName)
        {
            if (!blobName.StartsWith("data/", StringComparison.Ordinal))
                return;
            Interlocked.Increment(ref _dataAttempts);

            if (_failed.ContainsKey(blobName))
            {
                // The re-send. Reaching here at all is the fact this whole test exists to establish: the uploader
                // came back from the gate, re-staged its archive against a pool that is over its ceiling, and got
                // through. Signalled before the call to the real uploader, so what it reports is the re-stage having
                // finished and nothing that happens afterwards.
                if (Interlocked.Increment(ref _resends) == wave)
                    _resent.TrySetResult();
                return;
            }

            var n = Interlocked.Increment(ref _seen);
            if (n > wave)
                return;   // past the wave: an ordinary upload, let it through untouched

            _failed[blobName] = 0;
            if (n == 1)
                _held.TrySetResult();
            // Already completed for everyone after the first: see the remarks above on why they need no holding.
            await _fail.Task;
            // The shape Azure.Core throws once its own retries are exhausted, which is what TransientErrors.IsTransient
            // recognises — the same one FlakyUploader above uses, so both doubles reach the gate the same way.
            throw new AggregateException("Retry failed after 6 tries.", new TaskCanceledException("timeout"));
        }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            await GateAsync(blobName);
            return await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        {
            await GateAsync(blobName);
            return await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
        }

        public async Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
        {
            await GateAsync(blobName);
            await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
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

    /// <summary>
    /// Every uploader retrying at once, against a pool that is over its ceiling: the run has to come out the other
    /// side.
    /// <para>
    /// A retrying uploader recompresses <b>on its own thread</b> (the single-file closure's
    /// <c>pending ?? StageBlobAsync</c>, the pack group's <c>pending ?? CompressGroupAsync</c>, and the
    /// stranded-member tail), so it queues for staging room like anything else. Everything in that pool is released
    /// by an uploader, though, so an uploader waiting there is waiting for itself — and the failure that sends it
    /// there is systemic by nature. One network blip trips every in-flight upload at once; each disposes its archive
    /// and parks at the gate; the gate's timer then releases every waiter together, and all of them come back to
    /// recompress against a pool held by bytes no uploader owns. Nothing can release, and nothing can notice:
    /// <c>downstreamGone</c> triggers on the uploaders being **gone**, and these are alive; the gate cannot downgrade
    /// because nobody is at it; and <c>RequestStop</c> only fires the abort token for <c>StopNow</c>, so Suspend and
    /// "finish current files" hang along with the run. <see cref="StagingArea.StageWithoutBackpressureAsync"/> is the
    /// fix, and this test is what says it is still wired into the uploader-side re-stage.
    /// </para>
    /// <para>
    /// <b>Why the test fills the pool itself.</b> This test used to build that state the way production reaches it:
    /// leave the compression stage running while the uploaders sat out a five-second backoff, and let it push the
    /// pool to the ceiling on its own. That worked exactly as long as the compressor kept producing while the gate
    /// was shut — and it stopped doing so the moment Pause put a <c>WaitIfPausedAsync</c> at the top of all four
    /// producing loops (874782a, the commit after the fix this guards). A transient error closes the very gate Pause
    /// uses, so the compressor now parks there with the uploaders instead of burning CPU on archives that must wait.
    /// That is an improvement, and it left this test's precondition to luck: the pool got one archive in before the
    /// compressor parked, and on CI's slower machine not even that. It failed two CI runs on main, both times on its
    /// own scene guard — "the pool never got past 2000249 of 4,000,000 bytes" — while asserting nothing about the
    /// product at all.
    /// </para>
    /// <para>
    /// So the pool is pinned by the test, with a <see cref="StagingArea.ReserveAsync"/> reservation, at a moment the
    /// test has stopped the pipeline at. The waiter cannot tell the difference and there is nothing to race: what
    /// <c>HasRoom</c> reads is one number, <c>StagedBytes</c> against the ceiling, and what the scene needs of those
    /// bytes is only that no uploader can hand them back — which a reservation the test holds states as a fact
    /// instead of hoping a producer wins a race for it. Every step below is caused by the one before it: the upload
    /// side is stopped and held before a single failure is injected, the pool is pinned while it is stopped, and it
    /// is handed back only once the double has seen every failed blob asked for a second time. Take the bypass away
    /// and that second ask never comes — not slowly, never — on a machine of any speed.
    /// </para>
    /// <para>
    /// What is not covered here is covered next door: that the bypass itself does not wait, and that it still
    /// compresses one at a time, are pinned without Azurite or 7z in <c>StagingAreaTests</c>. This one is about the
    /// wiring — that the uploader-side re-stage is the caller using it.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Every_uploader_retrying_at_once_against_a_full_pool_still_finishes()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("gate");
        const long ceiling = 4_000_000;
        // UploadConcurrency 1 → max(2, N+1) = two uploaders, and the wave is both of them: the hazard is about *all*
        // the live uploaders retrying at once, not about how many there are, and two is the smallest number that is
        // still "all of them". Turning it down is also what makes one held call enough to stop the upload side —
        // permits are issued per volume from a pool of UploadConcurrency, so at one, the held call owns the only one.
        const int uploaders = 2;
        var flaky = new HoldsTheFirstUploadThenFailsTheWave(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), wave: uploaders);
        var (orchestrator, factory, staging) = Build(flaky, stagingLimit: ceiling);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        // Only used to unblock a hung run so the rest of the session is not left with every stage parked on a full
        // pool; on the passing path it is never fired.
        using var abort = new CancellationTokenSource();
        // The pinned pool. Held in a variable rather than a `using` because handing it back is a step of the test,
        // not cleanup — the compression stage cannot finish the run until it comes back.
        IDisposable? pinned = null;
        try
        {
            // Incompressible, so an archive is the size of its file and the pool's contents follow from these
            // numbers (zeroes — what the rest of this suite writes — compress to a few hundred bytes). Eight of them
            // is 2 MB, deliberately **half** the ceiling: everything this run can possibly stage at once fits, so the
            // pool is below its ceiling whatever order the pipeline runs in, and the reservation below is guaranteed
            // to find the room it needs. Filling the pool is the test's job here, and only the test's.
            for (var i = 0; i < 8; i++)
                WriteRandom($"f{i}.bin", 250_000);

            await using var control = new BackupRunControl(_journals, 5, "run-retry-jam", new PauseGate(
                // The backoff no longer decides anything — it used to have to be long enough for the compressor to
                // reach the ceiling while the uploaders were out of circulation, which is precisely the race this
                // test lost on CI. A second is comfortably long enough for the second uploader to walk into the gate
                // while the first one's backoff still holds it shut, which is what makes them come out together; and
                // if a loaded machine ever stretched past it, the two would simply retry in two rounds instead of
                // one and each would still meet the pinned pool, so the verdict does not turn on it either way.
                // Patience does still decide something: at zero the gate downgrades on the first failure and no
                // uploader ever reaches the retry, which is the difference between this test and its sibling above.
                schedule: [TimeSpan.FromSeconds(1)], steady: TimeSpan.FromSeconds(1),
                patience: TimeSpan.FromSeconds(60)));

            // Single-file blobs only, so one item is one file and one upload call.
            var request = Request(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    UploadConcurrency = 1,
                },
            };

            var run = orchestrator.RunAsync(request, null, abort.Token, control);

            // Wait for a milestone the run has to reach, and say something useful when it does not. Watching `run`
            // alongside matters: a run that dies early would otherwise leave the milestone hanging until the timeout
            // and report "we never got there" instead of the exception that is the real news.
            async Task ReachAsync(Task milestone, string what)
            {
                var reached = await Task.WhenAny(milestone, run, Task.Delay(TimeSpan.FromMinutes(2)));
                if (reached == run)
                {
                    await run;   // faulted: surface it. Completed cleanly: it did so without ever reaching the scene.
                    Assert.Fail($"the run finished before {what}.");
                }
                if (reached != milestone)
                {
                    await abort.CancelAsync();
                    _ = run.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                    Assert.Fail(
                        $"timed out before {what}. The staged pool held {staging.StagedBytes} bytes of its "
                        + $"{ceiling:N0}-byte ceiling — if this is the re-send that never came, every uploader is "
                        + "parked waiting for staging room that only an uploader could hand back, which is the "
                        + "deadlock StageWithoutBackpressureAsync exists to prevent.");
                }
            }

            // Step 1. Stop the pipeline where the scene needs it. One held upload is the whole upload side: it holds
            // the run's only volume permit, so the other uploader is queued behind it, and neither has failed yet.
            await ReachAsync(flaky.FirstUploadHeld, "an upload was being held open");

            // Step 2. Pin the pool over its ceiling with bytes nothing in the pipeline can release. This is the
            // state a network blip leaves behind — a pool full of archives whose owners are all parked — stated
            // rather than raced for.
            pinned = await staging.ReserveAsync(ceiling);
            Assert.True(staging.StagedBytes >= ceiling,
                $"the pool was pinned at {staging.StagedBytes} bytes, under its {ceiling:N0}-byte ceiling, so a "
                + "re-staging uploader would find room and this test would prove nothing.");

            // Step 3. Now fail the wave, which is the blip. The held uploader drops the archive it was sending and
            // parks at the gate; its permit goes to the next one, which fails on arrival and parks beside it. The
            // gate is where they accumulate — the first one's backoff holds it shut while the second walks in — and
            // when it elapses they are let out together.
            flaky.FailTheWave();

            // Step 4. The proof. Every held blob being asked for a second time means every uploader came back from
            // the gate, recompressed **on its own thread** against a pool that is over its ceiling, and got through.
            // Without the bypass this is where the run stops for good.
            await ReachAsync(flaky.WaveResent, "both uploaders re-sent what they had gone back to recompress");

            // Step 5. Hand the pool back, so the compression stage can produce the rest of the run.
            pinned.Dispose();
            pinned = null;

            var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(2)));
            if (finished != run)
            {
                await abort.CancelAsync();
                _ = run.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                Assert.Fail(
                    "the uploaders got past their re-stage, but the run never finished afterwards — "
                    + $"{staging.StagedBytes} bytes were still sitting in the pool.");
            }

            var result = await run;
            Assert.Equal(1, result.Version);
            // Eight items uploaded once each, plus the two the wave failed. Anything less means the injection stopped
            // biting and the run never went near the retry path.
            Assert.True(flaky.DataAttempts >= 10,
                $"expected at least 10 content uploads (8 items + 2 blips), saw {flaky.DataAttempts}.");
            // Whatever the pipeline was carrying has to hand its archive back. That debt lives on a process-wide
            // singleton and is the backpressure gate on output, so leaking it here would throttle every other backup
            // on the machine until a restart.
            Assert.Equal(0, staging.StagedBytes);
        }
        finally
        {
            pinned?.Dispose();
            await container.DeleteIfExistsAsync();
        }
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
