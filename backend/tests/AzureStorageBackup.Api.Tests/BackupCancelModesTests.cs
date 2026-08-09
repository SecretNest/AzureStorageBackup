using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupCancelModesTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupCancelModesTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-stop-" + Guid.NewGuid().ToString("N"));
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
        Id = 43,
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

    /// <summary>Write incompressible content (fixed seed, reproducible). 7z squeezes an all-zero file down to a few KB,
    /// so even with VolumeBytes set it will not split into many volumes — to get multiple volumes, compression has to get nowhere.</summary>
    private void WriteIncompressible(string rel, int size, int seed)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>An IOperationLog double that collects AppendAsync into a list (durable recorded along with it: a cancel
    /// must never be durable). The copies already in this project are all file-private nested classes, so here is one
    /// more of the same shape.</summary>
    private sealed class RecordingOperationLog : IOperationLog
    {
        public List<(OperationLogLevel Level, string Source, string Message, bool? Durable)> Entries { get; } = [];

        public Task AppendAsync(
            OperationLogLevel level, string source, string message, CancellationToken ct = default,
            bool? durable = null)
        {
            lock (Entries) Entries.Add((level, source, message, durable));
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

    private sealed class RecordingNotifier : INotifier
    {
        public List<(NotificationEvents Event, string Title, string Body)> Sent { get; } = [];

        public Task NotifyAsync(NotificationEvents evt, string title, string body, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add((evt, title, body));
            return Task.CompletedTask;
        }
    }

    /// <param name="reuse">Share one and the same local authority (info file + index cache) across two Builds.
    /// The "run one successful round first, then stop during the second round's version index read" cases cannot do
    /// without it: with a fresh one built each time, the second round cannot see the version the first round wrote, so
    /// there is no index to read.</param>
    /// <param name="wrapIndexCache">Wraps only the index cache the **orchestrator** holds; retention cleanup still gets the real one.</param>
    /// <param name="compactUploader">Dead-weight compaction's own uploader (kept entirely separate from the one the main
    /// backup uploads with). Builds a real one by default; the cases pinning "stop intent at the cleanup tail" push an
    /// artificially delayed double in here, using its elapsed time to tell the two outcomes apart: "compaction really
    /// was invoked" versus "compaction was skipped".</param>
    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory, TestLocalAuthority Authority) Build(
        IBlobUploader uploader, IOperationLog? opLog = null, INotifier? notifier = null,
        TestLocalAuthority? reuse = null, Func<ILocalIndexCache, ILocalIndexCache>? wrapIndexCache = null,
        IBlobUploader? compactUploader = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            compactUploader ?? new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(),
            Path.Combine(_temp, "compact"), staging);
        var authority = reuse ?? new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), wrapIndexCache?.Invoke(authority.IndexCache) ?? authority.IndexCache, authority.Tracked,
            notifier: notifier, opLog: opLog);
        return (orchestrator, factory, authority);
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
            // Upload concurrency 1 = only one volume in flight at any moment, so the upload ordinal is deterministic
            // and "stop on the 2nd upload" is a statement that actually means something.
            UploadConcurrency = 1,
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
            VolumeBytes = volumeBytes,
        },
    };

    /// <summary>Issue the given stop kind on the Nth upload; <paramref name="thenThrow"/> simulates "interrupted in flight".
    /// <para>
    /// The real <see cref="IBlobUploader"/>'s parameter order is (tier, retry, ct, metadata[, progress]), and both
    /// UploadIfMissing overloads have to be intercepted: the one with progress **has a default implementation** on the
    /// interface, and the main backup path (when a VolumeUploadScope is in play) goes through exactly that one — hook
    /// only the overload without progress and this double never intercepts a single backup upload.
    /// </para></summary>
    private sealed class StopAt(IBlobUploader inner, int at, Action stop, bool thenThrow) : IBlobUploader
    {
        private int _count;

        /// <summary>How many uploads actually reached the uploader. Once the stop takes effect there should not be a single one more.</summary>
        public int Calls => Volatile.Read(ref _count);

        private async Task<T> RunAsync<T>(Func<Task<T>> call)
        {
            var n = Interlocked.Increment(ref _count);
            var result = await call();
            if (n == at)
            {
                stop();
                if (thenThrow)
                    throw new OperationCanceledException("aborted mid-flight");
            }
            return result;
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync(() => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct,
            IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => RunAsync(() => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync<bool>(async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    private static async Task<List<string>> DataBlobsAsync(Azure.Storage.Blobs.BlobContainerClient container)
    {
        var names = new List<string>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/", default))
            names.Add(b.Name);
        return names;
    }

    // The following four don't touch Azurite: the division of labor between the two tokens is the foundation of the
    // whole stop semantics and has to be nailed down. An integration test cannot prove "Suspend never touched
    // AbortToken" — it only sees the on-disk result, and the flush leg runs on CancellationToken.None anyway, so
    // firing AbortToken along with it would still come out green.
    private BackupRunControl NewControl() => new(_journals, 8, "run-" + Guid.NewGuid().ToString("N")[..8]);

    [Theory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    public async Task Suspend_and_finish_current_files_stop_the_diff_but_never_abort(StopKind kind)
    {
        await using var c = NewControl();
        Assert.Equal(StopKind.None, c.Stop);

        c.RequestStop(kind);

        Assert.Equal(kind, c.Stop);
        Assert.True(c.StopToken.IsCancellationRequested);      // the diff should stop
        Assert.False(c.AbortToken.IsCancellationRequested);    // in-flight uploads must never be interrupted
    }

    [Fact]
    public async Task Stop_now_fires_both_tokens()
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.StopNow);

        Assert.Equal(StopKind.StopNow, c.Stop);
        Assert.True(c.StopToken.IsCancellationRequested);
        Assert.True(c.AbortToken.IsCancellationRequested);
    }

    [Fact]
    public async Task None_is_a_no_op()
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.None);

        Assert.Equal(StopKind.None, c.Stop);
        Assert.False(c.StopToken.IsCancellationRequested);
        Assert.False(c.AbortToken.IsCancellationRequested);
    }

    /// <summary>The user clicked Suspend, found himself stuck behind a huge multi-volume file, and switched to Stop now:
    /// that escalation has to really take effect, or the API returns success for a stop kind that was never applied.</summary>
    [Fact]
    public async Task A_stronger_stop_kind_escalates_and_fires_abort()
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.Suspend);
        Assert.False(c.AbortToken.IsCancellationRequested);

        c.RequestStop(StopKind.StopNow);

        Assert.Equal(StopKind.StopNow, c.Stop);
        Assert.True(c.StopToken.IsCancellationRequested);
        Assert.True(c.AbortToken.IsCancellationRequested);   // this one line is the entire point of escalation
    }

    [Fact]
    public async Task Finish_current_files_escalates_over_suspend()
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.Suspend);
        c.RequestStop(StopKind.FinishCurrentFiles);

        Assert.Equal(StopKind.FinishCurrentFiles, c.Stop);
        Assert.False(c.AbortToken.IsCancellationRequested);   // it still must not interrupt in-flight uploads
    }

    /// <summary>The reverse does not hold: residual volumes already interrupted and already deleted do not come back to life because of a gentler request.</summary>
    [Theory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    public async Task A_weaker_stop_kind_after_stop_now_is_ignored(StopKind weaker)
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.StopNow);
        c.RequestStop(weaker);

        Assert.Equal(StopKind.StopNow, c.Stop);
    }

    [Fact]
    public async Task The_same_stop_kind_twice_is_a_no_op()
    {
        await using var c = NewControl();

        c.RequestStop(StopKind.Suspend);
        c.RequestStop(StopKind.Suspend);

        Assert.Equal(StopKind.Suspend, c.Stop);
        Assert.False(c.AbortToken.IsCancellationRequested);
    }

    /// <summary>Concurrent escalation must not drop <c>_abort.Cancel()</c>: firing belongs to the thread that wins the
    /// CAS, so as long as Stop now wins once, AbortToken is guaranteed to be fired.</summary>
    [Fact]
    public async Task Concurrent_escalation_never_loses_the_abort()
    {
        for (var round = 0; round < 200; round++)
        {
            await using var c = NewControl();
            using var start = new ManualResetEventSlim(false);
            var racers = new[] { StopKind.Suspend, StopKind.StopNow, StopKind.FinishCurrentFiles }
                .Select(k => Task.Run(() => { start.Wait(); c.RequestStop(k); }))
                .ToArray();

            start.Set();
            await Task.WhenAll(racers);

            Assert.Equal(StopKind.StopNow, c.Stop);
            Assert.True(c.AbortToken.IsCancellationRequested);
        }
    }

    [SkippableFact]
    public async Task Suspend_keeps_the_journal_and_ends_as_suspended()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        BackupRunControl? control = null;
        var uploader = new StopAt(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), at: 1,
            stop: () => control!.RequestStop(StopKind.Suspend), thenThrow: false);
        var (orchestrator, factory, _) = Build(uploader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            WriteBytes("big3.bin", 6_000_002);
            await using var c = new BackupRunControl(_journals, 8, "run-suspend");
            control = c;

            var ex = await Assert.ThrowsAsync<BackupSuspendedException>(
                () => orchestrator.RunAsync(Request(account, name), null, default, c));
            Assert.Equal(SuspendReason.UserRequested, ex.Reason);

            // The first item did finish: the journal keeps it, and so does the cloud.
            var journal = Assert.Single(await _journals.ListAsync(account.Id, name, default));
            Assert.NotEmpty(journal.Content.Records);
            Assert.NotEmpty(await DataBlobsAsync(container));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Blocks on the 2nd volume of the first item until the test releases it; after release, **checks the token
    /// that was handed down** before actually uploading.
    /// <para>
    /// That check is where this case's whole force lies: after Suspend / Finish current files, the token handed down to
    /// the upload layer must still be clean. If the orchestrator wired the consumer's working token to StopToken
    /// (instead of AbortToken), this throws cancellation on the spot and not one volume after the 2nd gets uploaded —
    /// and "finish the current item, including all of its volumes" becomes an empty promise.
    /// </para></summary>
    private sealed class BlockOnSecondVolume(IBlobUploader inner) : IBlobUploader
    {
        private readonly Lock _lock = new();
        private readonly List<string> _uploaded = [];
        private string? _first;

        /// <summary>The 2nd volume has arrived and is blocked.</summary>
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Released once the test has issued the stop.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The blob names actually uploaded (in completion order).</summary>
        public IReadOnlyList<string> Uploaded
        {
            get { lock (_lock) return [.. _uploaded]; }
        }

        /// <summary>Strip the .001-style volume-number suffix to get the family's base name.</summary>
        private static string BaseOf(string name)
        {
            var dot = name.LastIndexOf('.');
            return dot > 0 && name.Length - dot == 4 && name.AsSpan(dot + 1).ToString().All(char.IsAsciiDigit)
                ? name[..dot]
                : name;
        }

        private async Task<T> RunAsync<T>(string blobName, CancellationToken ct, Func<Task<T>> call)
        {
            bool hold;
            lock (_lock)
            {
                _first ??= BaseOf(blobName);
                hold = BaseOf(blobName) == _first && blobName.EndsWith(".002", StringComparison.Ordinal);
            }
            if (hold)
            {
                Blocked.TrySetResult();
                await Release.Task;
            }
            ct.ThrowIfCancellationRequested();
            var result = await call();
            lock (_lock) _uploaded.Add(blobName);
            return result;
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync(blobName, ct, () => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct,
            IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => RunAsync(blobName, ct, () => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync<bool>(blobName, ct, async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    /// <summary>
    /// "Finish the current item, including all of its volumes, then stop" — that promise rests entirely on **how** the
    /// orchestrator uses those two tokens: the consumer's working token must be wired to AbortToken, never to StopToken.
    /// <para>
    /// The unit cases above pin down BackupRunControl itself; conflate the two tokens inside the orchestrator and not
    /// one of them turns red. This one pins it from the outside: press stop while a large file split into several
    /// volumes is on its 2nd volume, and after release every remaining volume has to be uploaded, not one missing, and
    /// the item has to land in the journal — landing in the journal is what "finished" means, and what makes it
    /// reusable next round.
    /// </para>
    /// </summary>
    [SkippableTheory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    public async Task A_gentle_stop_finishes_every_volume_of_the_item_in_flight(StopKind kind)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var uploader = new BlockOnSecondVolume(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)));
        var (orchestrator, factory, _) = Build(uploader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            // 6 MB incompressible + 1 MB per volume = around seven volumes; the single-file threshold sits below that at 5 MB, so this takes the single-file blob path.
            WriteIncompressible("big.bin", 6_000_000, seed: 20260808);
            await using var c = new BackupRunControl(_journals, 8, "run-gentle-" + (int)kind);
            var run = orchestrator.RunAsync(Request(account, name, volumeBytes: 1_000_000), null, default, c);
            try
            {
                await uploader.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(30));
                c.RequestStop(kind);                 // the 2nd volume is hanging in mid-air
                uploader.Release.TrySetResult();

                await RunAndCatchAsync(run.WaitAsync(TimeSpan.FromSeconds(30)), kind);

                // The item finished in full: the journal has it, and the volume count matches what it reports.
                var journal = Assert.Single(await _journals.ListAsync(account.Id, name, default));
                var record = Assert.Single(journal.Content.Records);
                Assert.True(record.Volumes >= 3, $"expected a multi-volume item, got {record.Volumes}");

                // Not one volume missing in the cloud — that is what the "including all of its volumes" half of the sentence actually means.
                var expected = VolumeBlobIO.VolumeNames(record.Ref, record.Volumes).Order(StringComparer.Ordinal);
                Assert.Equal(expected, (await DataBlobsAsync(container)).Order(StringComparer.Ordinal));
                Assert.Equal(record.Volumes, uploader.Uploaded.Count);
            }
            finally
            {
                // When the Blocked signal never arrives (exactly what the regression looks like), run is still parked
                // on Release in the background: release it here and wait for it to settle, otherwise it keeps running
                // with an unobserved exception after Dispose() has deleted the temp directory, burning 120s for nothing
                // before the case goes red. TrySetResult is idempotent, so on the normal path the repeat call has no side effects.
                uploader.Release.TrySetResult();
                await run.ContinueWith(_ => { });
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Blocks in the version index read and never returns until **the token handed down** is canceled.
    /// A 500,000-entry index really does take several seconds to read (measured in this repo), and a few dozen versions
    /// add up to minutes; if the token is not wired into this step (still the run's own ct), this double hangs forever
    /// and the case goes red on timeout.</summary>
    private sealed class BlockingIndexCache(ILocalIndexCache inner) : ILocalIndexCache
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<VersionIndex> ReadAsync(
            Account account, string container, int version, long identityTicks,
            string indexBlob, string? password, CancellationToken ct = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);   // only the token can save it
            return await inner.ReadAsync(account, container, version, identityTicks, indexBlob, password, ct);
        }

        public Task PutAsync(int accountId, string container, int version, long identityTicks, VersionIndex index,
            CancellationToken ct = default) => inner.PutAsync(accountId, container, version, identityTicks, index, ct);

        public Task RemoveAsync(int accountId, string container, int version, CancellationToken ct = default)
            => inner.RemoveAsync(accountId, container, version, ct);

        public Task RemoveForContainerAsync(int accountId, string container, CancellationToken ct = default)
            => inner.RemoveForContainerAsync(accountId, container, ct);
    }

    private static async Task<Exception> RunAndCatchAsync(Task run, StopKind kind) =>
        kind == StopKind.Suspend
            ? await Assert.ThrowsAsync<BackupSuspendedException>(() => run)
            : await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

    /// <summary>Artificially stalls the re-upload step of dead-weight compaction until the given token is canceled (or the delay expires).
    /// <para>
    /// Stalls <c>UploadOverwriteAsync</c> only — that is exactly what <see cref="VolumeBlobIO.ReplaceAsync"/> uses, and
    /// it is the moment compaction's "upload the new volume" lands. <c>UploadIfMissingAsync</c> passes through
    /// untouched: compaction does not go that way, and stalling it would only drag down unrelated things.
    /// </para>
    /// <param name="onFirstCall">Fired synchronously the first time execution really reaches this step, running on the
    /// caller's (compaction's own) thread. Exclusively for the "mid-compaction" case: issuing the stop intent from in
    /// here makes "compaction is already running" a forced ordering, not a hope that a progress callback happens to
    /// land before it.</param>
    /// </summary>
    private sealed class DelayedOverwriteUploader(IBlobUploader inner, TimeSpan delay, Action? onFirstCall = null)
        : IBlobUploader
    {
        private int _overwriteCalls;

        /// <summary>How many times execution really reached the re-upload step — used to confirm whether compaction was ever invoked at all.</summary>
        public int OverwriteCalls => Volatile.Read(ref _overwriteCalls);

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);

        public async Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (Interlocked.Increment(ref _overwriteCalls) == 1)
                onFirstCall?.Invoke();
            // Observe the ct passed in: before the fix this received the run's own ct (which never fires on a user
            // stop), so it only moved on once the delay expired; after the fix it receives stopProducing.Token, and it
            // is interrupted the moment the user presses stop.
            await Task.Delay(delay, ct);
            await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    /// <summary>Fires the callback synchronously the moment a given stage is entered. Implements <see cref="IProgress{T}"/>
    /// directly rather than using the convenience class <c>Progress&lt;T&gt;</c>: the latter forwards via
    /// SynchronizationContext.Post/the thread pool, and with no synchronization context in tests it gets queued onto the
    /// thread pool and runs asynchronously — by the time the callback runs the orchestrator has long moved on, and
    /// "the stop intent must land before the cleanup decision" can no longer be pinned down. Implementing the interface
    /// directly makes Report run to completion synchronously on the orchestrator's own thread.</summary>
    private sealed class StopOnStage(BackupStage stage, Action stop) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value)
        {
            if (value.Stage == stage)
                stop();
        }
    }

    /// <summary>Sets up the scene "a member is deleted after v1 is committed, and v1 is due to retire the moment v2
    /// commits": shared by the two cleanup-tail cases. Three small files in one directory, packed by directory into the
    /// same pack, each with different content (different seeds) — all-zero content would be collapsed by local dedup
    /// into a single physical member, and then deleting one would produce no dead weight at all. On return, a is already
    /// deleted and request has MaxVersions pinned to 1 (the moment v2 commits, v1 is immediately due to retire); the
    /// caller just runs v2 to get dead-weight compaction invoked.</summary>
    private async Task<BackupRequest> SeedPackDueForCompactionAsync(BackupOrchestrator orchestrator, Account account, string name)
    {
        WriteIncompressible("pack/a.bin", 2000, seed: 1);
        WriteIncompressible("pack/b.bin", 2000, seed: 2);
        WriteIncompressible("pack/c.bin", 2000, seed: 3);
        var baseRequest = Request(account, name);
        var request = baseRequest with
        {
            Options = baseRequest.Options with
            {
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
            },
        };
        Assert.Equal(1, (await orchestrator.RunAsync(request, null, default, null)).Version);

        // v2: delete a — b and c are unchanged and still reuse v1's pack; a's share inside that pack becomes dead
        // weight (2000 / 6000 ≈ 33% > the 30% threshold), which triggers an in-place recompression.
        File.Delete(Path.Combine(_root, "pack", "a.bin"));
        return request;
    }

    /// <summary>Stop intent at the cleanup tail, the leg missed at the Task 9 handoff: the version index and the info
    /// file are already committed, then the orchestrator goes straight into <c>cleaner.CleanupAsync</c> with neither
    /// token wired in — so dead-weight compaction downloads, recompresses and re-uploads all the way to the end, while
    /// CancelAsync/SuspendAsync do not return until the terminal state, so the HTTP request behind the button the user
    /// pressed hangs for just as long.
    /// <para>
    /// This one pins the **skip** half — the stop intent has already landed before cleanup is entered
    /// (<c>BackupOrchestrator.cs:1028</c>), so <c>cleaner.CleanupAsync</c> is never invoked at all. The half where
    /// compaction is interrupted midway is in
    /// <see cref="A_stop_requested_mid_compaction_lets_the_run_finish_promptly"/>: that one is where the double that
    /// stalls the re-upload is needed, this one does not need it — you cannot stall a call that is never made, and
    /// forcing one in would only make people think mid-compaction is tested here too. So this one uses a double with no
    /// delay and pins "compaction was never invoked once" directly via <c>OverwriteCalls</c>.
    /// </para></summary>
    [SkippableTheory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    [InlineData(StopKind.StopNow)]
    public async Task A_stop_pending_before_cleanup_skips_it_and_finishes_promptly(StopKind kind)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var real = new BlobUploader(new BlobClientFactory(TestSecrets.Reader));
        // No delay — compaction should not be invoked at all; OverwriteCalls is kept solely to verify that.
        var compact = new DelayedOverwriteUploader(real, TimeSpan.Zero);
        var (orchestrator, factory, _) = Build(real, compactUploader: compact);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            var request = await SeedPackDueForCompactionAsync(orchestrator, account, name);

            await using var c = new BackupRunControl(_journals, 8, "run-cleanup-skip-" + (int)kind);
            // Issue the stop intent synchronously on the CleaningUp frame: it runs on the orchestrator's own thread,
            // strictly before the step where it decides "should I call CleanupAsync", leaving no time window.
            var progress = new StopOnStage(BackupStage.CleaningUp, () => c.RequestStop(kind));

            var run = orchestrator.RunAsync(request, progress, default, c);
            try
            {
                var result = await run.WaitAsync(TimeSpan.FromSeconds(3));

                // The version is committed: this is still a successful backup, not Suspended/Canceled — cleanup is
                // just incidental maintenance, and skipping it does not change the fact that this round succeeded.
                Assert.Equal(2, result.Version);
                // Compaction was never invoked once — that is what the word "skip" actually means here.
                Assert.Equal(0, compact.OverwriteCalls);
            }
            finally
            {
                await run.ContinueWith(_ => { });
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The other half of the cleanup tail, and the dangerous half: dead-weight compaction really has been
    /// invoked and is midway through downloading/recompressing/re-uploading when the user presses stop. The
    /// <c>catch (OperationCanceledException) when (...)</c> at <c>BackupOrchestrator.cs:1050</c> catches exactly this
    /// cancellation — miss it and an already-committed successful backup gets docked down to Suspended/Canceled; get the
    /// guard wrong and it swallows the host's genuine shutdown cancellation along with it
    /// (see <see cref="A_genuine_cancellation_mid_compaction_still_propagates_as_a_failure"/>).
    /// <para>
    /// The stop intent is issued from <see cref="DelayedOverwriteUploader"/>'s <c>onFirstCall</c> hook — precisely the
    /// moment <c>UploadOverwriteAsync</c> (the step where <see cref="VolumeBlobIO.ReplaceAsync"/> lands the re-upload of
    /// the new volume) is really called, long since inside <c>cleaner.CleanupAsync</c>: that way "compaction is already
    /// running" is a forced ordering, not a hope that some progress callback happens to land before it. The 8-second
    /// artificial delay sits there on purpose: a genuinely interrupted settle takes nowhere near that long, and only the
    /// delay can cross the 3-second settle deadline.
    /// </para></summary>
    [SkippableTheory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    [InlineData(StopKind.StopNow)]
    public async Task A_stop_requested_mid_compaction_lets_the_run_finish_promptly(StopKind kind)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var real = new BlobUploader(new BlobClientFactory(TestSecrets.Reader));
        BackupRunControl? control = null;
        var slowCompact = new DelayedOverwriteUploader(
            real, TimeSpan.FromSeconds(8), onFirstCall: () => control!.RequestStop(kind));
        var (orchestrator, factory, _) = Build(real, compactUploader: slowCompact);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            var request = await SeedPackDueForCompactionAsync(orchestrator, account, name);

            await using var c = new BackupRunControl(_journals, 8, "run-cleanup-mid-" + (int)kind);
            control = c;

            var run = orchestrator.RunAsync(request, null, default, c);
            try
            {
                // The 8-second artificial delay sits there on purpose: a genuinely interrupted settle takes nowhere near that long.
                var result = await run.WaitAsync(TimeSpan.FromSeconds(3));

                // The version is committed: compaction being interrupted midway does not change the fact that this
                // round succeeded, so it is not Suspended/Canceled — the interrupted cleanup is left for the next
                // round's cleaner to finish.
                Assert.Equal(2, result.Version);
                // Compaction really was invoked once — unlike the skip path, which never touches UploadOverwriteAsync at all.
                Assert.Equal(1, slowCompact.OverwriteCalls);
            }
            finally
            {
                await run.ContinueWith(_ => { });
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Mid-compaction, what gets canceled is **the caller's own token** (the host-shutdown path), not the
    /// user's stop button: the <c>!ct.IsCancellationRequested</c> clause in the guard at
    /// <c>BackupOrchestrator.cs:1050</c> exists precisely so this kind is not swallowed. No
    /// <see cref="BackupRunControl"/> here — what this case pins down is exactly "nobody pressed stop, something outside
    /// cut the token" — and this cancellation must propagate untouched, to be recorded as a genuine failure by the
    /// catch-all <c>catch (Exception)</c> at the top of <c>RunAsync</c>, rather than quietly swallowed by that catch into
    /// <c>CleanupReport.Empty</c> and reported as a "success".</summary>
    [SkippableFact]
    public async Task A_genuine_cancellation_mid_compaction_still_propagates_as_a_failure()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var real = new BlobUploader(new BlobClientFactory(TestSecrets.Reader));
        using var hostShutdown = new CancellationTokenSource();
        var slowCompact = new DelayedOverwriteUploader(
            real, TimeSpan.FromSeconds(8), onFirstCall: () => hostShutdown.Cancel());
        var (orchestrator, factory, _) = Build(real, compactUploader: slowCompact);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            var request = await SeedPackDueForCompactionAsync(orchestrator, account, name);

            // No control: v2 itself runs on this token that gets cut, not on some BackupRunControl's StopToken.
            var run = orchestrator.RunAsync(request, null, hostShutdown.Token, null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The stop is issued before the scan even starts: the scan has to see it right there, and has to exit
    /// **through the settle path** — producing the exception type that matches the stop kind, rather than letting a bare
    /// cancellation escape and get recorded as Failed by BackupRunner.
    /// <para>
    /// "No journal was opened" is the criterion for "the scan really was cut short": opening the journal comes **after**
    /// the scan and the index read, so if the token is not wired into the scan, this round runs all the way to the
    /// journal open before the diff stops it, and a journal volume is left on disk.
    /// </para></summary>
    [SkippableTheory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.FinishCurrentFiles)]
    [InlineData(StopKind.StopNow)]
    public async Task A_stop_before_the_scan_settles_without_ever_opening_a_journal(StopKind kind)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var uploader = new StopAt(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), at: -1, stop: () => { }, thenThrow: false);
        var (orchestrator, factory, _) = Build(uploader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            await using var c = new BackupRunControl(_journals, 8, "run-early-" + (int)kind);
            c.RequestStop(kind);

            await RunAndCatchAsync(orchestrator.RunAsync(Request(account, name), null, default, c), kind);

            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
            Assert.Equal(0, uploader.Calls);
            Assert.Empty(await DataBlobsAsync(container));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Stop pressed halfway through reading the version indexes: it has to react right there, not wait for
    /// every version's index to finish loading. After the user presses stop, SuspendAsync/CancelAsync do not return
    /// until the terminal state, so not reacting on the spot means the HTTP request hangs dead.</summary>
    [SkippableTheory]
    [InlineData(StopKind.Suspend)]
    [InlineData(StopKind.StopNow)]
    public async Task A_stop_during_the_version_index_load_settles_instead_of_waiting_for_it(StopKind kind)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        var real = new BlobUploader(new BlobClientFactory(TestSecrets.Reader));
        var (first, factory, authority) = Build(real);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("small1.bin", 1024);
            WriteBytes("small2.bin", 2048);
            // Run one successful round first: only then does the second round have a version index to read.
            Assert.Equal(1, (await first.RunAsync(Request(account, name), null, default)).Version);

            BlockingIndexCache? blocking = null;
            var (second, _, _) = Build(
                real, reuse: authority, wrapIndexCache: inner => blocking = new BlockingIndexCache(inner));

            await using var c = new BackupRunControl(_journals, 8, "run-index-" + (int)kind);
            // This token is the only "release" mechanism this case has: BlockingIndexCache parks on
            // Task.Delay(Infinite, ct) and there is no separate Release signal. If Reading is never reached, or the stop
            // intent never makes it into that ct (exactly the regression this case pins down), run may still be parked in
            // the background — the finally block cancels it via the test's own CTS as a backstop.
            using var runCts = new CancellationTokenSource();
            var run = second.RunAsync(Request(account, name), null, runCts.Token, c);
            try
            {
                await blocking!.Reading.Task.WaitAsync(TimeSpan.FromSeconds(30));
                c.RequestStop(kind);

                // If the token is not wired into the index read step, this line hangs until the timeout — exactly the regression this case pins down.
                await RunAndCatchAsync(run.WaitAsync(TimeSpan.FromSeconds(30)), kind);

                // We stopped before the journal was opened, so no journal should be left on disk; nor should a second version be written.
                Assert.Empty(await _journals.ListAsync(account.Id, name, default));
                var info = await authority.Tracked.LoadAsync(account, name, null, default);
                Assert.Single(info!.Versions);
            }
            finally
            {
                // Settle regardless of whether the above succeeded: if run is still parked, cancel its own external
                // token here rather than relying on the code path under test, then wait for it to really finish, so it
                // does not keep running with an unobserved exception after Dispose() has deleted the temp directory.
                runCts.Cancel();
                await run.ContinueWith(_ => { });
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>A cancel the user pressed is not an incident: no durable Error entry, and no failure webhook.
    /// Before Task 9 this held on the coincidence that "Cancel canceled the ct, so Record threw by itself and recorded
    /// nothing"; now that the ct is no longer touched, something has to pin it down.</summary>
    [SkippableFact]
    public async Task Cancel_leaves_no_error_audit_entry_and_fires_no_failure_webhook()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        BackupRunControl? control = null;
        var opLog = new RecordingOperationLog();
        var notifier = new RecordingNotifier();
        var uploader = new StopAt(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), at: 1,
            stop: () => control!.RequestStop(StopKind.StopNow), thenThrow: false);
        var (orchestrator, factory, _) = Build(uploader, opLog, notifier);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            await using var c = new BackupRunControl(_journals, 8, "run-cancel-audit");
            control = c;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => orchestrator.RunAsync(Request(account, name), null, default, c));

            List<(OperationLogLevel Level, string Source, string Message, bool? Durable)> entries;
            lock (opLog.Entries) entries = [.. opLog.Entries];
            List<(NotificationEvents Event, string Title, string Body)> sent;
            lock (notifier.Sent) sent = [.. notifier.Sent];

            Assert.DoesNotContain(entries, e => e.Level >= OperationLogLevel.Warning);
            Assert.DoesNotContain(entries, e => e.Message.Contains("Backup failed", StringComparison.Ordinal));
            Assert.DoesNotContain(sent, n => n.Event == NotificationEvents.BackupFailure);

            // But the audit trail does have to keep "this round was stopped by someone, it did not crash": Info, and non-durable.
            var canceled = Assert.Single(
                entries, e => e.Message.Contains("Backup canceled", StringComparison.Ordinal));
            Assert.Equal(OperationLogLevel.Info, canceled.Level);
            Assert.False(canceled.Durable);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Stop_now_deletes_the_in_flight_residue_but_keeps_finished_blocks()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        BackupRunControl? control = null;
        // The 2nd upload: it goes up, but is then "interrupted in flight" — the upload confirmation never returns, so
        // it never enters the journal and its in-flight registration is never cleared. Exactly the kind of residue
        // Stop now has to clean up.
        var uploader = new StopAt(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), at: 2,
            stop: () => control!.RequestStop(StopKind.StopNow), thenThrow: true);
        var (orchestrator, factory, _) = Build(uploader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            WriteBytes("big3.bin", 6_000_002);
            await using var c = new BackupRunControl(_journals, 8, "run-stopnow");
            control = c;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => orchestrator.RunAsync(Request(account, name), null, default, c));

            // Stop now interrupts the in-flight upload: the third item should not even start uploading.
            Assert.Equal(2, uploader.Calls);

            var journal = Assert.Single(await _journals.ListAsync(account.Id, name, default));
            var kept = Assert.Single(journal.Content.Records);       // only the first item was confirmed complete
            var blobs = await DataBlobsAsync(container);
            // What uploaded in full stays (reused next round); the in-flight one's residue is deleted clean.
            Assert.Equal([kept.Ref], blobs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
