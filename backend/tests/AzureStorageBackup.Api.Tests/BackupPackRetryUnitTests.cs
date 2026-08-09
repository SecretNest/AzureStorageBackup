using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// When the gate retries a pack, the **unit** of the retry must be one group, not a whole pool.
/// <para>
/// One pack work item is a pool; <c>ProcessPackAsync</c> cuts it into groups by GroupIsFull, and each group draws
/// its own pack number. Retrying the whole item means one blip on group 9 tears down all 8 groups before it, and
/// the redo draws **new** pack numbers: the archives those 8 groups already uploaded can no longer be reached from
/// any index and merely take up room in the container (retention cleanup only collects them next run),
/// info.Packs keeps one record per orphan, and progress writes off a few extra items on top.
/// </para>
/// <para>
/// The pool here is cut into two groups by "a member changed during compression" — that is a path the orchestrator
/// already has (a changed member is re-queued with its new hash and naturally lands in the next group), not a
/// contraption built for the test: packing on the diff side seals by the same three limits, so normally one pool is
/// one group, and multiple groups can only arise this way.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupPackRetryUnitTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupPackRetryUnitTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-pack-" + Guid.NewGuid().ToString("N"));
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

    private const string TargetLeaf = "m3.bin";

    /// <summary>One directory, 6 small files: by the three limits this is **one** pool and one group.</summary>
    private void WritePool()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d"));
        for (var i = 1; i <= 6; i++)
            File.WriteAllBytes(Path.Combine(_root, "d", $"m{i}.bin"), new byte[20_000 + i]);
    }

    /// <summary>
    /// On the compression of all 6 members (and only then), mutate one of them: the orchestrator's post-compression
    /// re-verification excludes it from the archive and re-queues it with a new hash, so the same pool gets cut into
    /// two groups — a multi-group scene **without touching the code under test**.
    /// It also records how many times each pack number was compressed, to answer "did the group that had already
    /// uploaded get recompressed".
    /// </summary>
    private sealed class MutatingCompressor(IFileCompressor inner, string root) : IFileCompressor
    {
        private readonly List<string> _compressed = [];
        private int _mutations;

        /// <summary>Records the pack number of every compression in call order (the same number may appear more than once).</summary>
        public IReadOnlyList<string> Compressed
        {
            get { lock (_compressed) return [.. _compressed]; }
        }

        public Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var packId = Path.GetFileNameWithoutExtension(request.OutputArchivePath);
            lock (_compressed) _compressed.Add(packId);

            // Only act on the "compress the whole group at once" call: after the changed member is dropped the
            // recompression has 5 members left and the following group has just 1, neither containing the target, so
            // there is no endless "changed again" (which would run all the way into ProcessingMaxAttempts and
            // degrade to single files).
            var target = request.Entries.FirstOrDefault(
                e => e.EndsWith(TargetLeaf, StringComparison.Ordinal));
            if (request.Entries.Count > 1 && target is not null)
            {
                // Change it to a **different length** every time. The first stage of post-compression re-verification
                // is a metadata comparison; change only the content and not the length and a rewrite within the same
                // second yields the same (mtime, length), so the comparison says "this member did not change" — and
                // then the old whole-item retry behaviour would masquerade as correct (only one group left after a
                // blip), and the test would lose all its discriminating power.
                var n = Interlocked.Increment(ref _mutations);
                File.WriteAllBytes(
                    Path.Combine(root, target.Replace('/', Path.DirectorySeparatorChar)),
                    new byte[33_000 + (n * 1_000)]);
            }

            return inner.CompressAsync(request, ct);
        }

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
            => inner.CompressStreamAsync(request, writeSource, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
    }

    /// <summary>Blip on the Nth **pack**'s first upload (once only). N=2 means "the earlier group had already finished uploading when things went wrong".</summary>
    private sealed class FlakyOnNthPack(IBlobUploader inner, int nth) : IBlobUploader
    {
        private readonly HashSet<string> _packs = new(StringComparer.Ordinal);
        private int _thrown;

        private Task<bool> GateAsync(string blobName, Func<Task<bool>> call)
        {
            if (blobName.StartsWith("packs/", StringComparison.Ordinal))
            {
                bool trip;
                lock (_packs)
                {
                    var id = blobName[..(blobName.IndexOf(".7z", StringComparison.Ordinal) + 3)];
                    trip = _packs.Add(id) && _packs.Count == nth;
                }
                if (trip && Interlocked.Exchange(ref _thrown, 1) == 0)
                    throw new AggregateException("Retry failed after 6 tries.", new TaskCanceledException("timeout"));
            }
            return call();
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    /// <summary>Blip once on every **pack**'s first upload: there is always a success sandwiched between the two blips.</summary>
    private sealed class FlakyOnEveryPackOnce(IBlobUploader inner) : IBlobUploader
    {
        private readonly HashSet<string> _packs = new(StringComparer.Ordinal);
        private int _thrown;

        public int Thrown => _thrown;

        private Task<bool> GateAsync(string blobName, Func<Task<bool>> call)
        {
            if (blobName.StartsWith("packs/", StringComparison.Ordinal))
            {
                bool trip;
                lock (_packs)
                    trip = _packs.Add(blobName[..(blobName.IndexOf(".7z", StringComparison.Ordinal) + 3)]);
                if (trip)
                {
                    Interlocked.Increment(ref _thrown);
                    throw new AggregateException("Retry failed after 6 tries.", new TaskCanceledException("timeout"));
                }
            }
            return call();
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    /// <summary>Records only the pack number each compression used, changing neither content nor behaviour — used to count "how many times was this group compressed".</summary>
    private sealed class CountingCompressor(IFileCompressor inner) : IFileCompressor
    {
        private readonly List<string> _compressed = [];

        public IReadOnlyList<string> Compressed
        {
            get { lock (_compressed) return [.. _compressed]; }
        }

        public Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
        {
            var packId = Path.GetFileNameWithoutExtension(request.OutputArchivePath);
            lock (_compressed) _compressed.Add(packId);
            return inner.CompressAsync(request, ct);
        }

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
            => inner.CompressStreamAsync(request, writeSource, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
    }

    /// <summary>An uploader that trips the cancellation token and throws OperationCanceledException: used to verify the gate does not swallow a cancellation.</summary>
    private sealed class CancellingUploader(IBlobUploader inner, CancellationTokenSource cts) : IBlobUploader
    {
        private Task<bool> GateAsync(string blobName, Func<Task<bool>> call)
        {
            if (blobName.StartsWith("packs/", StringComparison.Ordinal))
            {
                cts.Cancel();
                // Exactly the same shape as "cancelled halfway through an upload": a real cancellation is thrown from right here.
                throw new OperationCanceledException(cts.Token);
            }
            return call();
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct, IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => GateAsync(blobName, () => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => GateAsync(blobName, async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory, IBackupInfoStore Store) Build(
        IBlobUploader uploader, IFileCompressor compressor, VerboseFileLog? verboseLog = null)
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
            compressor, uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked,
            verboseLog: verboseLog);
        return (orchestrator, factory, store);
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

    /// <summary>The pack archives in the container (.7z base names) and the ones the index actually references. The two must be equal.</summary>
    private static async Task<(HashSet<string> InContainer, HashSet<string> Referenced)> PacksAsync(
        Azure.Storage.Blobs.BlobContainerClient cc, IBackupInfoStore store, Account account, string container)
    {
        var inContainer = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, "packs/", default))
            inContainer.Add(b.Name[..(b.Name.IndexOf(".7z", StringComparison.Ordinal) + 3)]);

        var info = await store.ReadInfoAsync(account, container, null);
        var index = await store.ReadIndexAsync(account, container, info!.Versions[^1].IndexBlob, null);
        var referenced = index.Entries
            .Where(e => e.Storage is { Kind: "pack" })
            .Select(e => $"packs/{e.Storage!.Ref}.7z")
            .ToHashSet(StringComparer.Ordinal);
        return (inContainer, referenced);
    }

    /// <summary>
    /// One blip on the second group's upload: only the second group is redone; the first is neither recompressed nor
    /// re-uploaded, its pack number is unchanged, and no orphan is left in the container.
    /// </summary>
    [SkippableFact]
    public async Task A_blip_in_the_second_group_reruns_only_that_group()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        var compressor = new MutatingCompressor(new SevenZipCompressor(), _root);
        var flaky = new FlakyOnNthPack(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), nth: 2);
        var (orchestrator, factory, store) = Build(flaky, compressor);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();
            await using var control = new BackupRunControl(_journals, 5, "run-pack", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
                patience: TimeSpan.FromSeconds(5)));

            var result = await orchestrator.RunAsync(Request(account, name), null, default, control);
            Assert.Equal(1, result.Version);

            var compressed = compressor.Compressed;
            var packs = compressed.Distinct(StringComparer.Ordinal).ToList();
            // Precondition for this scene: the pool really was cut into two groups. Without the cut, the assertions below spin idle.
            Assert.Equal(2, packs.Count);

            // Group 1: compressed once for the whole group + once more after the changed member was dropped, and
            // that is all. If the second group's redo tore down the whole pool, a third compression would show up
            // here (and hanging off a **new** pack number at that).
            Assert.Equal(2, compressed.Count(p => p == packs[0]));
            // Group 2: one blip, two compressions — on the **same pack number**. A changed number means one more unreferenced archive left in the cloud.
            Assert.Equal(2, compressed.Count(p => p == packs[1]));

            var (inContainer, referenced) = await PacksAsync(cc, store, account, name);
            Assert.Equal(referenced, inContainer);   // no orphan pack in the container that the index cannot reach
            Assert.Equal(2, referenced.Count);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Progress is written off exactly once per group, however many blips there were. A whole-item retry writes off
    /// a group that was already written off a second time, and uploaded is inflated from then on (once it passes
    /// total, the speed and the remaining time both go wrong).
    /// </summary>
    [SkippableFact]
    public async Task Each_group_reports_progress_exactly_once_however_many_retries()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        var compressor = new MutatingCompressor(new SevenZipCompressor(), _root);
        var flaky = new FlakyOnNthPack(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), nth: 2);
        var (orchestrator, factory, _) = Build(flaky, compressor);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();
            await using var control = new BackupRunControl(_journals, 5, "run-once", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
                patience: TimeSpan.FromSeconds(5)));

            var peak = 0;
            var progress = new Progress<BackupProgress>(p => peak = Math.Max(peak, p.UploadedItems));
            await orchestrator.RunAsync(Request(account, name), progress, default, control);

            // Two groups → exactly two write-offs. With a whole-item retry group 1 gets written off again and this becomes 3.
            Assert.Equal(2, peak);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>A cancellation the user pressed is not "the network hiccupped": it must propagate as-is, and must not be waited out by the gate into a suspend.</summary>
    [SkippableFact]
    public async Task User_cancellation_still_propagates_through_the_group_retry()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        using var cts = new CancellationTokenSource();
        var uploader = new CancellingUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), cts);
        var (orchestrator, factory, _) = Build(uploader, new SevenZipCompressor());
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();
            await using var control = new BackupRunControl(_journals, 5, "run-cancel", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
                patience: TimeSpan.FromSeconds(5)));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => orchestrator.RunAsync(Request(account, name), null, cts.Token, control));

            // The exception type alone is not enough: what really has to hold is "the cancellation never entered the
            // gate at all". If the transient test is handed a token other than the run's own, a cancellation gets
            // taken for a blip and waits at the gate again and again until patience runs out and the run is declared
            // suspended — at which point the user pressed cancel but the UI says "suspended, will resume
            // automatically later".
            Assert.False(control.Gate.IsDowngraded, "the gate swallowed the cancellation and turned it into an automatic suspend.");
            Assert.Null(control.Gate.Current);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>Runs without a control (the old path outside scheduled jobs) behave unchanged: still two groups, still no orphans.</summary>
    [SkippableFact]
    public async Task Runs_without_a_control_behave_exactly_as_before()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        var compressor = new MutatingCompressor(new SevenZipCompressor(), _root);
        var (orchestrator, factory, store) = Build(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), compressor);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();
            var result = await orchestrator.RunAsync(Request(account, name), null, default);

            Assert.Equal(1, result.Version);
            Assert.Equal(2, compressor.Compressed.Distinct(StringComparer.Ordinal).Count());
            var (inContainer, referenced) = await PacksAsync(cc, store, account, name);
            Assert.Equal(referenced, inContainer);
            Assert.Equal(2, referenced.Count);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// A transient error hit only after the upload was confirmed (the journal append / oplog step) no longer drags
    /// the whole group through a redo.
    /// <para>
    /// An exclusive file lock is parked on today's verbose log file for the duration: every <c>LogFileAsync</c> write
    /// runs straight into an <see cref="IOException"/> (a genuine sharing conflict, which
    /// <see cref="TransientErrors"/> judges transient). The lock is never released, which forces out the difference
    /// between retrying and not — with a retry, every collision with the lock first recompresses and re-uploads the
    /// whole group, so the compression count climbs along with the number of collisions; without one, compression
    /// can only happen on the one successful upload, after which this step's own error propagates as-is and the
    /// compression count stays at 1 forever.
    /// </para>
    /// <para>
    /// The compression count is precisely the ledger for "uploaded bytes": <c>state.AddUploaded</c> corresponds
    /// one-to-one with each successful <c>UploadStagedPackAsync</c>, which in turn corresponds one-to-one with each
    /// compression. Compress once and the uploaded bytes can only be counted once — which is exactly the "no double
    /// counting" this group of tests guards.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_failure_after_upload_confirm_does_not_retry_the_group()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        var compressor = new CountingCompressor(new SevenZipCompressor());
        var verboseRoot = Path.Combine(_temp, "verbose");
        var verboseLog = new VerboseFileLog(verboseRoot);
        var (orchestrator, factory, _) = Build(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), compressor, verboseLog);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();   // one pool, one group (see the class comment)

            // Lock today's verbose log file up front: once it is open exclusively, the File.AppendAllTextAsync inside
            // AppendAsync hits a sharing conflict the moment it opens and throws a bare IOException. The lock lives
            // in a using and is not released until this test method ends — leaving a retry no "this time it worked"
            // window at all.
            var logDir = Path.Combine(verboseRoot, name);
            Directory.CreateDirectory(logDir);
            var logFile = Path.Combine(logDir, DateTimeOffset.UtcNow.ToString("yyyyMMdd") + ".log");
            File.WriteAllText(logFile, "");
            using var block = new FileStream(logFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var request = Request(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                    VerboseLogging = true,
                },
            };

            await using var control = new BackupRunControl(_journals, 5, "run-record", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
                patience: TimeSpan.FromSeconds(2)));

            // A failure in the bookkeeping stage now propagates as-is: it no longer goes through the suspend gate's "wait a bit and come back".
            await Assert.ThrowsAnyAsync<IOException>(
                () => orchestrator.RunAsync(request, null, default, control));

            // Compression ran exactly once — the group whose upload was already confirmed was not recompressed or
            // re-uploaded. More than 1 means a bookkeeping-stage failure still drags the whole group into a retry and
            // state.AddUploaded counts it a second time (the double-count bug this group of tests guards).
            Assert.Single(compressor.Compressed);
            // A bookkeeping-stage failure should not go through the gate at all: it is already outside the retry
            // scope, and the gate should not even record one consecutive failure for it.
            Assert.False(control.Gate.IsDowngraded, "the gate took a bookkeeping-stage failure for a transient blip and waited on it.");
            Assert.Null(control.Gate.Current);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Getting a piece of work done resets the gate's consecutive-failure count. <c>ReportSuccess</c> used to have
    /// no test guarding it at all — delete that line and every test above still goes green.
    /// <para>
    /// What it guards is this: the gate's patience means "nothing has gone right since the first hiccup". If a
    /// success in between does not reset it, a handful of scattered blips over a day are enough to use up the
    /// patience and declare a backup that uploaded normally from start to finish automatically suspended — and the
    /// longer the blips go on, the more it looks like the network is broken, when in fact every one of them healed
    /// itself on the spot.
    /// </para>
    /// <para>
    /// The scene uses **two groups within the same pool**, not two concurrent work items: one pool is processed
    /// sequentially by one consumer, so the order "group 1 retries successfully → reset → only then does group 2 go
    /// wrong" is guaranteed by program order, not left to the luck of wait timings.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_success_between_two_blips_resets_the_gates_failure_count()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("pack");
        var compressor = new MutatingCompressor(new SevenZipCompressor(), _root);
        var flaky = new FlakyOnEveryPackOnce(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)));
        var (orchestrator, factory, _) = Build(flaky, compressor);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WritePool();
            // 400ms backoff: the suspend scene has to hang on the gate long enough for the 10ms sampling below to see
            // its consecutive-failure count. Patience is set generously — this test is not asking "will patience run out".
            await using var control = new BackupRunControl(_journals, 5, "run-reset", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(400)], steady: TimeSpan.FromMilliseconds(400),
                patience: TimeSpan.FromSeconds(30)));

            var run = orchestrator.RunAsync(Request(account, name), null, default, control);
            var peak = 0;
            while (!run.IsCompleted)
            {
                if (control.Gate.Current is { } paused) peak = Math.Max(peak, paused.Failures);
                await Task.Delay(10);
            }
            var result = await run;

            Assert.Equal(1, result.Version);
            Assert.Equal(2, flaky.Thrown);   // two blips really happened, with a success sandwiched between them
            // The consecutive-failure count when the second blip opens the gate: 1 if it was reset, 2 if it was not.
            Assert.Equal(1, peak);
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
