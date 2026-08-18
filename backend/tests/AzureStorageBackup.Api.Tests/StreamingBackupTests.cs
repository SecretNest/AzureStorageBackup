using System.Net.Sockets;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// End-to-end acceptance after single-file blobs moved to a single read pass (hash and compress while reading), phase 2.
/// What this carries: the bytes sitting in the cloud must be byte-for-byte identical to the source file — and that must hold for all
/// four combinations of encryption, volume splitting, store-only and raw direct upload; content that already exists is still skipped
/// wholesale by the pre-filter, never recompressed for nothing just because "the name is only known once compression is done".
/// </summary>
[Trait("Category", "Integration")]
public sealed class StreamingBackupTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _root;
    private readonly string _temp;

    public StreamingBackupTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-sbk-" + Guid.NewGuid().ToString("N"));
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

    private async Task<string> WriteSourceAsync(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(full, bytes);
        return full;
    }

    /// <summary>Pull an index entry's blob back down, extract the content, and compare it byte for byte against the source file.
    /// Deliberately not comparing hashes: the hash and the index come from the same read pass, so using it as proof would be circular.</summary>
    private async Task AssertBlobMatchesSourceAsync(
        BlobContainerClient container, IndexEntry entry, string? password)
    {
        var dir = Path.Combine(_temp, "check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var firstVolume = await VolumeBlobIO.DownloadAsync(container, entry.Storage!.Ref, dir, CancellationToken.None);

        var restored = Path.Combine(dir, "restored.bin");
        if (entry.Storage.Raw)
        {
            File.Copy(firstVolume, restored, overwrite: true);
        }
        else
        {
            await using var output = File.Create(restored);
            await new SevenZipCompressor().ExtractToStreamAsync(firstVolume, entry.Path, password, output);
        }

        var source = Path.Combine(_root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(restored));
        Assert.Equal(new FileInfo(source).Length, entry.Length);
    }

    [SkippableTheory]
    [InlineData(null, null, false, false)] // compressed, single volume
    [InlineData("pw", null, false, false)] // encryption + header encryption
    [InlineData("pw", 64 * 1024L, false, false)] // encryption + volume splitting
    [InlineData(null, null, true, true)]   // store-only + no password + no splitting → raw direct upload
    [InlineData("pw", null, true, false)]  // store-only but encrypted → still wrapped by 7z
    public async Task Stored_Bytes_Match_The_Source_File(
        string? password, long? volumeBytes, bool dontCompress, bool expectRaw)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);

        var account = AzuriteAccount();
        var name = RandomName("sbk-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await WriteSourceAsync("media/clip.bin", 250_000);

            await orchestrator.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _root, Name = "stream", Password = password,
                Options = new BackupEngineOptions
                {
                    VolumeBytes = volumeBytes,
                    DontCompress = dontCompress ? new IgnoreRuleSet(["*.bin"]) : null,
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                },
            });

            var info = await store.ReadInfoAsync(account, name, password);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, password);
            var entry = Assert.Single(idx.Entries);

            Assert.Equal("blob", entry.Storage!.Kind);
            Assert.Equal(expectRaw, entry.Storage.Raw);
            await AssertBlobMatchesSourceAsync(container, entry, password);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Counts how many times streaming compression was invoked. The blob name is only known once compression is done, so
    /// "content that already exists" has to be stopped up front by the pre-filter — fail to stop it and a renamed 4 GB file gets recompressed for nothing on every single backup.</summary>
    private sealed class CountingStreamCompressor(IFileCompressor inner) : IFileCompressor
    {
        private int _streamCompressions;
        public int StreamCompressions => Volatile.Read(ref _streamCompressions);

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _streamCompressions);
            return inner.CompressStreamAsync(request, writeSource, ct);
        }

        public Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default)
            => inner.CompressAsync(request, ct);

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
    }

    [SkippableFact]
    public async Task Content_Already_In_The_Backup_Is_Never_Recompressed()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        conn.Open();
        using var db = new AzureStorageBackup.Api.Data.AppDbContext(
            new DbContextOptionsBuilder<AzureStorageBackup.Api.Data.AppDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();

        var compressor = new CountingStreamCompressor(new SevenZipCompressor());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            compressor, new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher(),
            indexCache: new LocalIndexCache(db, store),
            trackedInfo: new TrackedInfoStore(store, new LocalBackupStateStore(db)));

        var account = AzuriteAccount();
        var name = RandomName("sbkd-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        BackupRequest Request() => new()
        {
            Account = account, Container = name, LocalRoot = _root, Name = "dedup",
            Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
        };

        try
        {
            await WriteSourceAsync("big.bin", 300_000);
            await orchestrator.RunAsync(Request());
            Assert.Equal(1, compressor.StreamCompressions);

            // Rename it: not one byte of the content changed, yet the diff sees a new file. The pre-filter has to recognize this content before compressing.
            File.Move(Path.Combine(_root, "big.bin"), Path.Combine(_root, "renamed.bin"));
            await orchestrator.RunAsync(Request());

            Assert.Equal(1, compressor.StreamCompressions); // not compressed even one more time
            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var entry = Assert.Single(idx.Entries, e => e.Path == "renamed.bin");
            Assert.True(await VolumeBlobIO.ExistsAsync(container, entry.Storage!.Ref, CancellationToken.None));

            // The two deduplicated records must point at the same blob instead of each storing its own copy.
            var dataBlobs = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, "data/", CancellationToken.None))
                dataBlobs.Add(b.Name);
            Assert.Single(dataBlobs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    // ------------------------------------------------------------------------------------------------------
    // Uploading a raw blob from where it already is (docs/raw-upload-without-staging-design.md).
    //
    // A store-only, unencrypted file that fits one volume used to be copied into the staging area in full and
    // uploaded from the copy. The copy was what fixed the content between hashing and uploading; it is now
    // replaced by stat'ing on both sides of the upload. The four cases below pin both halves of that trade:
    // the saving (nothing is staged, ever) and the guarantee it must not cost (the object in the container is
    // always named for the hash of its own bytes).
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Holds data-blob uploads at <paramref name="gate"/> before letting them reach the real uploader, and
    /// signals the moment the first one arrives.
    /// <para>
    /// Two signals, not one, and both are load-bearing. The gate is what keeps an item parked mid-upload; the
    /// <see cref="Entered"/> task is what lets a case observe the pipeline **while** it is parked. Every
    /// assertion below is about that instant: the staged pool only holds a copy for as long as the upload that
    /// would drain it has not run, and the rewrite race can only be forced in the window between "the uploader
    /// was handed a path" and "the uploader opened it".
    /// </para>
    /// <para>
    /// Only <c>data/</c> names are held. The info file and the version index travel through this same uploader
    /// in the run's wrap-up, and a gate across those would hang the wrap-up rather than the item under test —
    /// and would make <see cref="Entered"/> fire for an object no case here is about.
    /// </para>
    /// </summary>
    private sealed class BlockingUploader(Task gate, IBlobUploader inner) : IBlobUploader
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dataUploads;

        /// <summary>Completes when a data blob's upload has been handed its path and is parked on the gate.</summary>
        public Task Entered => _entered.Task;

        /// <summary>How many data blobs were handed to the uploader — the count includes an upload that a later
        /// guard throws away, which is exactly what the rewrite case needs to be able to see.</summary>
        public int DataUploads => Volatile.Read(ref _dataUploads);

        /// <summary>The path the first data upload was handed. On the raw route that is the source file, and with
        /// two byte-identical files in one run it is the only way to tell which of them is the one being uploaded
        /// and which is the one waiting on its reservation.</summary>
        public string? FirstPath { get; private set; }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (blobName.StartsWith("data/", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _dataUploads) == 1)
                    FirstPath = filePath;
                _entered.TrySetResult();
                await gate.WaitAsync(ct);
            }
            return await inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
    }

    /// <summary>
    /// The other way an upload ends: the commit lands and the acknowledgement does not.
    /// <para>
    /// It parks the first data upload exactly where <see cref="BlockingUploader"/> does, then lets it through to
    /// the real uploader — so the object really is written — and only then reports the failure the wire would
    /// have reported: a status-0 <see cref="RequestFailedException"/>, which is what Azure.Core produces when a
    /// connection dies with the request already served, and what <see cref="TransientErrors"/> calls transient.
    /// That combination is the whole point: to the run it looks like an ordinary NAS-to-Azure blip and the item is
    /// retried, while the container is holding an object nobody in the run believes is there.
    /// </para>
    /// <para>
    /// Only the **first** data upload is treated this way. The retry has to be allowed through, or the run never
    /// reaches the state this case is about (one object per address, each named for its own content).
    /// </para>
    /// </summary>
    private sealed class LostAckUploader(Task gate, IBlobUploader inner) : IBlobUploader
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dataUploads;

        /// <summary>Completes when the first data blob's upload has been handed its path and is parked on the gate.</summary>
        public Task Entered => _entered.Task;

        public int DataUploads => Volatile.Read(ref _dataUploads);

        /// <summary>The path the first data upload was handed. On the raw route this is the **source file**, which
        /// is what makes rewriting it while the upload is parked mean anything at all.</summary>
        public string? FirstPath { get; private set; }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (!blobName.StartsWith("data/", StringComparison.Ordinal)
                || Interlocked.Increment(ref _dataUploads) != 1)
                return await inner.UploadIfMissingAsync(
                    account, container, blobName, filePath, tier, retry, ct, metadata);

            FirstPath = filePath;
            _entered.TrySetResult();
            await gate.WaitAsync(ct);
            // Committed, and then lost on the way back. Nothing above this line ever learns that it landed.
            await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
            throw new RequestFailedException(0, "the connection dropped after the commit");
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
    }

    /// <summary>
    /// Hands out working clients until <see cref="Armed"/>, and from then on refuses with the shape a NAS-to-Azure
    /// blip has: a status-0 <see cref="RequestFailedException"/>, which is transient, so whatever retry sits above
    /// it really does try again before giving up.
    /// </summary>
    private sealed class RefusesWhenArmed(IBlobClientFactory inner) : IBlobClientFactory
    {
        public bool Armed { get; set; }

        public BlobServiceClient CreateServiceClient(Account account) => Armed
            ? throw new RequestFailedException(0, "the connection dropped")
            : inner.CreateServiceClient(account);

        public Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default)
            => inner.TestConnectionAsync(account, ct);
    }

    /// <summary>
    /// Watches the progress stream for an item parked on a same-batch reservation
    /// (<see cref="StageProgress.WaitingOnPeer"/>), which is the only outside sign that one file really is waiting
    /// on another file's upload rather than getting on with its own.
    /// </summary>
    private sealed class PeerWaitWatcher : IProgress<BackupProgress>
    {
        private int _seen;

        public bool Seen => Volatile.Read(ref _seen) != 0;

        public void Report(BackupProgress value)
        {
            if (value.Details.Any(d => d.WaitingOnPeer > 0))
                Volatile.Write(ref _seen, 1);
        }
    }

    /// <summary>An operation log that is itself broken: it counts what it was asked to write and then fails.
    /// The path this exercises reaches SQLite and a webhook, and "SQLite Error 5: database is locked" has come out
    /// of exactly that path in this repository before.</summary>
    private sealed class ThrowingOperationLog(Func<string, bool> breaksOn) : IOperationLog
    {
        private readonly List<string> _asked = [];

        /// <summary>Only the messages that hit the break, so a count here is a count of attempts at that one report.</summary>
        public IReadOnlyList<string> Asked { get { lock (_asked) return [.. _asked]; } }

        public Task AppendAsync(
            OperationLogLevel level, string source, string message, CancellationToken ct = default,
            bool? durable = null)
        {
            if (!breaksOn(message))
                return Task.CompletedTask;
            lock (_asked) _asked.Add(message);
            throw new InvalidOperationException("the operation log is down");
        }

        public Task<IReadOnlyList<LogEntry>> QueryAsync(
            OperationLogLevel? minLevel, string? source, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LogEntry>>([]);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;
        public Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>Keeps what the run wrote to the operation log, which is where an operator reads it.</summary>
    private sealed class RecordingOperationLog : IOperationLog
    {
        private readonly List<(OperationLogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(OperationLogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public Task AppendAsync(
            OperationLogLevel level, string source, string message, CancellationToken ct = default,
            bool? durable = null)
        {
            lock (_entries) _entries.Add((level, message));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LogEntry>> QueryAsync(
            OperationLogLevel? minLevel, string? source, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LogEntry>>([]);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;
        public Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Samples the staging pool for the whole life of a run and keeps the largest reading.
    /// <para>
    /// The peak is the reading that means anything here. The pool is back at zero by the time a run ends whether
    /// or not anything was ever copied into it, so an end-state assertion would pass on the code this change
    /// replaces. Polling can in principle miss a short spike, which is why the cases below **also** read the pool
    /// at a moment they have pinned open with <see cref="BlockingUploader"/>: on the copying route the copy is
    /// provably still in the pool at that instant, because the upload that releases it has not run yet.
    /// </para>
    /// </summary>
    private sealed class PoolPeak : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private long _peak;

        public PoolPeak(StagingArea staging) =>
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    var now = staging.StagedBytes;
                    if (now > Interlocked.Read(ref _peak))
                        Interlocked.Exchange(ref _peak, now);
                    try { await Task.Delay(1, _stop.Token); }
                    catch (OperationCanceledException) { return; }
                }
            });

        public long Peak => Interlocked.Read(ref _peak);

        public void Dispose()
        {
            _stop.Cancel();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
            _stop.Dispose();
        }
    }

    /// <param name="cloud">The factory the **orchestrator itself** reaches the container through, for the point
    /// operations it does outside the uploader — clearing leftover volumes, and taking back a raw upload the guard
    /// rejected. Everything else (the uploader, the info store, the cleaner) keeps the real one, so sabotaging this
    /// one sabotages exactly those operations and nothing else. Null = the real one, like every other case here.</param>
    private (BackupOrchestrator Orchestrator, StagingArea Staging, BackupRequest Request) Build(
        IBlobUploader? uploader, string container, string? password, bool dontCompress,
        IBlobClientFactory? cloud = null, IOperationLog? opLog = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "c-" + container), Path.Combine(_temp, "s-" + container), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader ?? new BlobUploader(factory), cloud ?? factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(),
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked, notifier: null, opLog: opLog);
        var request = new BackupRequest
        {
            Account = AzuriteAccount(),
            Container = container,
            LocalRoot = _root,
            Name = "raw",
            Password = password,
            Options = new BackupEngineOptions
            {
                DontCompress = dontCompress ? new IgnoreRuleSet(["*.bin"]) : null,
                // One item per file: no packing to reason about, so "what is in the pool" is this one file's doing.
                Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
            },
        };
        return (orchestrator, staging, request);
    }

    private static async Task<IndexEntry> SingleEntryAsync(
        IBackupInfoStore store, Account account, string container, string? password)
    {
        var info = await store.ReadInfoAsync(account, container, password);
        var idx = await store.ReadIndexAsync(account, container, info!.Versions[^1].IndexBlob, password);
        return Assert.Single(idx.Entries);
    }

    /// <summary>The content identity this project addresses blobs by, computed over bytes already in hand.</summary>
    private static string FullHashOf(byte[] bytes)
    {
        var hasher = new StreamingHasher(0, 0);
        hasher.Append(bytes);
        return hasher.FullHash;
    }

    private static async Task<IReadOnlyList<(string Name, byte[] Bytes)>> DataBlobsAsync(
        BlobContainerClient container)
    {
        var found = new List<(string, byte[])>();
        await foreach (var b in container.GetBlobsAsync(
            BlobTraits.None, BlobStates.None, "data/", CancellationToken.None))
            found.Add((b.Name, (await container.GetBlobClient(b.Name).DownloadContentAsync())
                .Value.Content.ToArray()));
        return found;
    }

    /// <summary>
    /// The saving this change exists for: a store-only, unencrypted file that fits one volume is uploaded from
    /// where it already sits, so not one byte of it is ever charged to the staging pool.
    /// <para>
    /// The assertion is on the **peak**, not on the end state. Everything the pool holds is released when the
    /// item settles, so it reads zero at the end of the run either way and an end-state assertion would be
    /// satisfied by the very code this replaces. The parked read below is the sharper half of the same claim:
    /// with the upload held open, the copying route provably still has its copy in the pool, because the upload
    /// is what releases it.
    /// </para>
    /// <para>
    /// The raw flag on the stored entry is the anti-vacuity check — without it a case that merely failed to take
    /// the raw route at all (mis-set DontCompress, say) would pass with an empty pool for the wrong reason.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Raw_Upload_Never_Lets_The_Staged_Pool_Rise_Above_Zero()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        await WriteSourceAsync("media/clip.bin", 250_000);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var name = RandomName("sbkraw-");
        var uploader = new BlockingUploader(block.Task, new BlobUploader(factory));
        var (orchestrator, staging, request) = Build(uploader, name, password: null, dontCompress: true);

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        using var peak = new PoolPeak(staging);
        try
        {
            var run = orchestrator.RunAsync(request);
            long whileParked;
            try
            {
                await uploader.Entered.WaitAsync(TimeSpan.FromSeconds(60));
                whileParked = staging.StagedBytes;
                // Long enough for the sampler above to take hundreds of readings inside the window it is the
                // whole point of this case to look into.
                await Task.Delay(300);
            }
            finally
            {
                block.SetResult();
            }

            await run.WaitAsync(TimeSpan.FromMinutes(2));

            Assert.Equal(
                0, whileParked); // the upload was parked, so a copy made for it would still be in the pool
            Assert.Equal(0, peak.Peak);
            // The pool is an accounting number; this is the disk it accounts for. Nothing was staged at all, so
            // the directory is not merely empty — the run never had reason to create it.
            var stagedTemp = Path.Combine(_temp, "s-" + name);
            var leftBehind = Directory.Exists(stagedTemp)
                ? Directory.EnumerateFileSystemEntries(stagedTemp).ToList()
                : [];
            Assert.True(leftBehind.Count == 0, $"staged-temp still holds {string.Join(", ", leftBehind)}");

            var entry = await SingleEntryAsync(store, AzuriteAccount(), name, password: null);
            Assert.True(entry.Storage!.Raw, "the file did not take the raw route, so an empty pool proves nothing");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// A raw blob's address is the hash of the bytes that are in it. Nothing else in the system re-derives that:
    /// dedup, restore and check all trust the index, so a blob whose name disagrees with its content is a
    /// corruption nobody notices until a restore fails.
    /// <para>
    /// Hashing the **downloaded** bytes and comparing with the object's own name is not circular the way
    /// comparing two numbers from the same read pass would be: one side came off the wire, the other is the name
    /// the container filed it under.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Raw_Blob_Round_Trips_Byte_Identically()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var source = await WriteSourceAsync("media/clip.bin", 250_000);

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var name = RandomName("sbkraw-");
        var (orchestrator, _, request) = Build(uploader: null, name, password: null, dontCompress: true);

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await orchestrator.RunAsync(request);

            var entry = await SingleEntryAsync(store, AzuriteAccount(), name, password: null);
            Assert.True(entry.Storage!.Raw);

            var stored = Assert.Single(await DataBlobsAsync(container));
            Assert.Equal(entry.Storage.Ref, stored.Name);
            Assert.Equal(await File.ReadAllBytesAsync(source), stored.Bytes);
            Assert.Equal("data/" + FullHashOf(stored.Bytes), stored.Name);
            Assert.Equal(FullHashOf(stored.Bytes), entry.FullHash);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The guard the copy used to be. With the upload reading the source directly, a file rewritten between
    /// being hashed and being sent would put bytes into <c>data/{hash}</c> that hash to something else — the one
    /// outcome this change is not allowed to make possible.
    /// <para>
    /// The window is forced rather than raced for: <see cref="BlockingUploader"/> parks the upload **after** the
    /// pipeline has handed it the path and **before** it opens it, the source is rewritten while it is parked,
    /// and only then is it released. So the bytes that go over the wire are provably not the bytes that were
    /// hashed.
    /// </para>
    /// <para>
    /// The assertion is about the objects in the container, not about an exception: the design leaves the run
    /// free to recover (delete the mismatched object and retry through the copying route, which uploads a
    /// snapshot and is immune), so what has to be true afterwards is only that every <c>data/</c> object is
    /// named for the hash of its own content, and that the index entry points at one of them.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_File_Rewritten_During_Its_Upload_Leaves_No_Blob_That_Contradicts_Its_Name()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var source = await WriteSourceAsync("media/clip.bin", 250_000);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var name = RandomName("sbkraw-");
        var uploader = new BlockingUploader(block.Task, new BlobUploader(factory));
        var (orchestrator, _, request) = Build(uploader, name, password: null, dontCompress: true);

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var run = orchestrator.RunAsync(request);
            try
            {
                await uploader.Entered.WaitAsync(TimeSpan.FromSeconds(60));
                // A different length as well as different bytes: length alone already moves the metadata the
                // guard tests, so the window does not depend on the filesystem's timestamp resolution.
                var rewritten = new byte[311_111];
                Random.Shared.NextBytes(rewritten);
                await File.WriteAllBytesAsync(source, rewritten);
            }
            finally
            {
                block.SetResult();
            }

            await run.WaitAsync(TimeSpan.FromMinutes(2));

            var entry = await SingleEntryAsync(store, AzuriteAccount(), name, password: null);
            var blobs = await DataBlobsAsync(container);
            Assert.NotEmpty(blobs);
            foreach (var (blobName, bytes) in blobs)
                Assert.Equal("data/" + FullHashOf(bytes), blobName);

            var stored = Assert.Single(blobs, b => b.Name == entry.Storage!.Ref);
            Assert.Equal(entry.FullHash, FullHashOf(stored.Bytes));
            Assert.Equal(entry.Length, stored.Bytes.LongLength);

            // Anti-vacuity, and the evidence that the window really opened. The first upload sent the rewritten
            // bytes under the address of the bytes that were hashed; the guard found the metadata moved, deleted
            // that object and sent the item round again through the copying route, which re-read the file — so
            // there were two data uploads and the surviving one is a snapshot of the **new** content. The deleted
            // one leaving nothing behind is what `Assert.Single` above says.
            Assert.True(uploader.DataUploads >= 2,
                $"only {uploader.DataUploads} data upload(s): this run never asked the guard anything — either the "
                + "upload was reading a snapshot rather than the source, or the rewrite landed outside the window.");
            Assert.Equal(311_111, entry.Length);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The same guarantee, on the ending the guard cannot reach by watching an upload return: the commit lands and
    /// the acknowledgement does not.
    /// <para>
    /// This is the routine NAS-to-Azure failure, not an exotic one. Azure writes the blob, the connection dies
    /// before the response arrives, the SDK's own retries exhaust, and what comes out is a status-0
    /// <see cref="RequestFailedException"/>. The upload never returned, so a guard placed only after it is never
    /// asked whether the source moved — and the object is in the container all the same, at an address this run
    /// cannot vouch for. Nothing sweeps it afterwards: the in-flight purge runs for Stop now alone, and the
    /// closing orphan sweep only runs when the round adopted or voided a journal, or is this config's first on the
    /// container.
    /// </para>
    /// <para>
    /// What that leaves is the one shape this project cannot detect afterwards — <c>data/{H}</c> holding something
    /// other than <c>H</c>. A later run that legitimately produces <c>H</c> claims that address, the single-volume
    /// path clears nothing, and the if-missing upload is told "already there" without a byte being read: the index
    /// then records <c>data/{H}</c> as holding <c>H</c>, and it does not.
    /// </para>
    /// <para>
    /// Deliberately run **without** a <c>BackupRunControl</c>, which is what makes the scene readable. With one,
    /// the status-0 error is transient, the item is retried, and the run ends by committing an index — and the
    /// closing sweep of a first round would then delete the orphan for reasons that have nothing to do with the
    /// guard, which is exactly how this case passed before it was written properly. Without one, the run fails on
    /// the injected error and nothing but the code under test can have touched the container.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Commit_Whose_Acknowledgement_Is_Lost_Leaves_No_Blob_That_Contradicts_Its_Name()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var source = await WriteSourceAsync("media/clip.bin", 250_000);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var name = RandomName("sbkraw-");
        var uploader = new LostAckUploader(block.Task, new BlobUploader(factory));
        var (orchestrator, _, request) = Build(uploader, name, password: null, dontCompress: true);

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var run = orchestrator.RunAsync(request);
            try
            {
                await uploader.Entered.WaitAsync(TimeSpan.FromSeconds(60));
                // Rewritten to a different length as well as different bytes, so the guard's verdict does not
                // depend on the filesystem's timestamp resolution.
                var rewritten = new byte[311_111];
                Random.Shared.NextBytes(rewritten);
                await File.WriteAllBytesAsync(source, rewritten);
            }
            finally
            {
                block.SetResult();
            }

            // The injected failure is not retried without a control, so it takes the run down. That is this case's
            // scene, not its subject: what matters is what the container holds afterwards.
            var ex = await Assert.ThrowsAnyAsync<RequestFailedException>(
                () => run.WaitAsync(TimeSpan.FromMinutes(2)));
            Assert.Equal(0, ex.Status);

            var blobs = await DataBlobsAsync(container);
            // Named first, so a failure says which object contradicts itself rather than merely how many there are.
            foreach (var (blobName, bytes) in blobs)
                Assert.Equal("data/" + FullHashOf(bytes), blobName);
            // And in this scene the invariant has a sharper form: exactly one object was ever committed, the run
            // never established that it holds the content its name claims, and no retry followed that could have
            // replaced it — so nothing at all may be left behind.
            Assert.Empty(blobs);

            // Anti-vacuity. The upload was handed the source file itself, so this really was the raw route and
            // rewriting that file really did change what went over the wire; and it was reached exactly once, so
            // the commit this case is about really was attempted.
            Assert.Equal(source, uploader.FirstPath);
            Assert.Equal(1, uploader.DataUploads);
            Assert.Equal(311_111, new FileInfo(source).Length);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The guard trips on a file another file in the same run is waiting for. The run has to survive it.
    /// <para>
    /// Two byte-identical files share one content identity, so the second to arrive does not upload anything: it
    /// parks on the first one's dedup reservation and takes its result. When the first one's guard trips, that
    /// reservation is failed — the peer must never be handed an address that was just deleted — and what it is
    /// failed with is the guard's own exception. It arrives at the peer inside the resolver, **outside** the catch
    /// that answers it for the item itself, so unless it counts as transient the peer is not retried and the whole
    /// run dies over a duplicate file.
    /// </para>
    /// <para>
    /// This is not a sub-second window. The peer waits for the guilty item's **whole** upload — minutes for a
    /// multi-GB file — and byte-identical duplicates are ordinary in a media library, which is exactly the workload
    /// the store-only rule exists for.
    /// </para>
    /// <para>
    /// The wait is not assumed: the case waits for the pipeline to report an item parked on a peer before it
    /// rewrites anything, so a run where the second file had already resolved on its own would time out here rather
    /// than pass without testing anything.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Same_Content_Peer_Survives_The_Guard_Tripping()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var first = await WriteSourceAsync("media/a.bin", 250_000);
        // Byte-identical, so both files resolve to one content identity and one address.
        var second = Path.Combine(_root, "media", "b.bin");
        File.Copy(first, second);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var name = RandomName("sbkraw-");
        var uploader = new BlockingUploader(block.Task, new BlobUploader(factory));
        var (orchestrator, _, request) = Build(uploader, name, password: null, dontCompress: true);
        var watcher = new PeerWaitWatcher();

        // A run control, because the retry this case is about is the pause gate's: without one, nothing in the
        // pipeline retries anything and every failure is fatal by construction. Its schedule is turned down to
        // milliseconds — the production 30 seconds would be spent waiting for what is settled immediately.
        var journals = new BackupJournalStore(Path.Combine(_temp, "journal-" + name));
        await using var control = new BackupRunControl(journals, configId: 1, runId: "raw-peer", new PauseGate(
            schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
            patience: TimeSpan.FromSeconds(30)));

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var run = orchestrator.RunAsync(request, watcher, ct: default, control: control);
            try
            {
                await uploader.Entered.WaitAsync(TimeSpan.FromSeconds(60));
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                while (!watcher.Seen && DateTime.UtcNow < until)
                    await Task.Delay(10);
                Assert.True(watcher.Seen, "no item ever parked on a peer's reservation, so this case tests nothing");

                // Rewrite the one that is being uploaded, not the one that is waiting: the guard is about the file
                // whose bytes are on the wire, and only the uploader knows which of the two that is.
                var rewritten = new byte[311_111];
                Random.Shared.NextBytes(rewritten);
                await File.WriteAllBytesAsync(uploader.FirstPath!, rewritten);
            }
            finally
            {
                block.SetResult();
            }

            // The whole point: the run finishes. The guilty item goes round through the copying route, and the peer
            // — woken with an exception it did nothing to deserve — uploads its own content itself.
            await run.WaitAsync(TimeSpan.FromMinutes(2));

            var info = await store.ReadInfoAsync(AzuriteAccount(), name, password: null);
            var idx = await store.ReadIndexAsync(AzuriteAccount(), name, info!.Versions[^1].IndexBlob, null);
            Assert.Equal(2, idx.Entries.Count);

            var blobs = await DataBlobsAsync(container);
            foreach (var e in idx.Entries)
            {
                var stored = Assert.Single(blobs, b => b.Name == e.Storage!.Ref);
                Assert.Equal("data/" + FullHashOf(stored.Bytes), stored.Name);
                // Each entry holds the content of its own file as it now stands — the rewritten one for the file
                // that moved, the original for the one that did not.
                var file = Path.Combine(_root, e.Path.Replace('/', Path.DirectorySeparatorChar));
                Assert.Equal(await File.ReadAllBytesAsync(file), stored.Bytes);
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The one ending the code cannot put right, and therefore the one it has to talk about: the guard rejected
    /// the upload, and the container will not let the object go.
    /// <para>
    /// Everything else here is about leaving nothing behind. This case is about the residue that cannot be
    /// removed — and about it not being silent. No later check finds it: check and restore both read the index,
    /// the index agrees with the name, and the name is the only thing about the object that is wrong. The address
    /// has to reach the operator, because an operator with the address can delete it in one command and nothing
    /// else in this system will.
    /// </para>
    /// <para>
    /// Only the orchestrator's own view of the container is sabotaged, and only from the instant the source is
    /// rewritten: the upload itself goes through the real client and really does commit, so the object under
    /// discussion is genuinely there — which the last assertion checks, since a report about an object that was
    /// removed after all would prove nothing.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Blob_That_Cannot_Be_Taken_Back_Is_Reported_By_Address()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var source = await WriteSourceAsync("media/clip.bin", 250_000);
        // The address the upload in flight is claiming: the content identity of the bytes as they are now.
        var doomed = "data/" + FullHashOf(await File.ReadAllBytesAsync(source));

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var name = RandomName("sbkraw-");
        var uploader = new BlockingUploader(block.Task, new BlobUploader(factory));
        var cloud = new RefusesWhenArmed(factory);
        var log = new RecordingOperationLog();
        var (orchestrator, _, request) = Build(uploader, name, password: null, dontCompress: true, cloud, log);

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var run = orchestrator.RunAsync(request);
            try
            {
                await uploader.Entered.WaitAsync(TimeSpan.FromSeconds(60));
                var rewritten = new byte[311_111];
                Random.Shared.NextBytes(rewritten);
                await File.WriteAllBytesAsync(source, rewritten);
                // From here the guard will find the source moved and try to take its upload back, and every
                // attempt at it will be refused.
                cloud.Armed = true;
            }
            finally
            {
                block.SetResult();
            }

            // The run itself survives: the item is re-staged through the copying route, which is immune, and the
            // failure to clean up is not allowed to cost the operator the backup as well.
            await run.WaitAsync(TimeSpan.FromMinutes(2));

            var told = Assert.Single(log.Entries, e => e.Message.Contains(doomed, StringComparison.Ordinal));
            Assert.Equal(OperationLogLevel.Error, told.Level);
            Assert.Contains("media/clip.bin", told.Message, StringComparison.Ordinal);

            // Anti-vacuity: the object really is still there, so the report is about something real. And the entry
            // this run committed points at the **other** address, the one named for the bytes it actually sent.
            var blobs = await DataBlobsAsync(container);
            Assert.Contains(blobs, b => b.Name == doomed);
            var entry = await SingleEntryAsync(store, AzuriteAccount(), name, password: null);
            Assert.NotEqual(doomed, entry.Storage!.Ref);
            var stored = Assert.Single(blobs, b => b.Name == entry.Storage.Ref);
            Assert.Equal("data/" + FullHashOf(stored.Bytes), stored.Name);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The same ending as above, with the one channel it has left also broken: the take-back's own report must not
    /// become the failure.
    /// <para>
    /// <c>Record</c> writes SQLite and pushes a webhook, and this repository has had "SQLite Error 5: database is
    /// locked" out of that path on a running backup. It sits in the catch of the take-back, one statement before
    /// the guard raises the exception that sends the item round through the copying route. A throw from it used to
    /// escape ahead of that <c>throw new</c>, and then satisfy the filter of the very next catch — so the take-back
    /// ran a second time, and because what finally surfaced was the logging error rather than
    /// <c>SourceMovedDuringUploadException</c>, the catch that re-queues the file never matched and the file lost
    /// its retry. One broken logger, and a backup that would otherwise have completed does not.
    /// </para>
    /// <para>
    /// So the assertions are the outcome, not the mechanism: the run finishes, the file is recorded at the address
    /// of the bytes it now holds, and the report was attempted exactly once. The last one is what separates "the
    /// throw was swallowed" from "the throw was swallowed twice on the way through the same code".
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Broken_Log_Does_Not_Cost_The_File_Its_Retry()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var source = await WriteSourceAsync("media/clip.bin", 250_000);
        var doomed = "data/" + FullHashOf(await File.ReadAllBytesAsync(source));

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var name = RandomName("sbkraw-");
        var uploader = new BlockingUploader(block.Task, new BlobUploader(factory));
        var cloud = new RefusesWhenArmed(factory);
        // Broken for this one report and nothing else: the case is about what that report's failure costs, not
        // about a run whose whole audit trail is down.
        var log = new ThrowingOperationLog(m => m.Contains(doomed, StringComparison.Ordinal));
        var (orchestrator, _, request) = Build(uploader, name, password: null, dontCompress: true, cloud, log);

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var run = orchestrator.RunAsync(request);
            try
            {
                await uploader.Entered.WaitAsync(TimeSpan.FromSeconds(60));
                var rewritten = new byte[311_111];
                Random.Shared.NextBytes(rewritten);
                await File.WriteAllBytesAsync(source, rewritten);
                cloud.Armed = true;
            }
            finally
            {
                block.SetResult();
            }

            await run.WaitAsync(TimeSpan.FromMinutes(2));

            // The report really was attempted — otherwise this run took some other route and proves nothing —
            // and attempted once, not once per pass through the take-back.
            Assert.Single(log.Asked);

            var entry = await SingleEntryAsync(store, AzuriteAccount(), name, password: null);
            Assert.NotEqual(doomed, entry.Storage!.Ref);
            var blobs = await DataBlobsAsync(container);
            var stored = Assert.Single(blobs, b => b.Name == entry.Storage.Ref);
            Assert.Equal("data/" + FullHashOf(stored.Bytes), stored.Name);
            Assert.Equal(await File.ReadAllBytesAsync(source), stored.Bytes);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The other side of the route predicate: anything whose stored bytes are not the source bytes still has to
    /// be produced into staging first, because there is nothing on disk to upload in place. Pinned so that an
    /// over-eager future edit cannot send an encrypted blob's plaintext, or a compressed blob's uncompressed
    /// source, straight from the source file.
    /// </summary>
    [SkippableTheory]
    [InlineData("pw", true)]    // store-only but encrypted → 7z wraps it, so the stored bytes are not the source's
    [InlineData(null, false)]   // compressed → likewise
    public async Task An_Encrypted_Or_Compressed_Blob_Still_Travels_Through_Staging(
        string? password, bool dontCompress)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        await WriteSourceAsync("media/clip.bin", 250_000);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var name = RandomName("sbkcopy-");
        var uploader = new BlockingUploader(block.Task, new BlobUploader(factory));
        var (orchestrator, staging, request) = Build(uploader, name, password, dontCompress);

        var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        using var peak = new PoolPeak(staging);
        try
        {
            var run = orchestrator.RunAsync(request);
            long whileParked;
            try
            {
                await uploader.Entered.WaitAsync(TimeSpan.FromSeconds(60));
                whileParked = staging.StagedBytes;
            }
            finally
            {
                block.SetResult();
            }

            await run.WaitAsync(TimeSpan.FromMinutes(2));

            Assert.True(whileParked > 0, "the archive was not in the pool while its upload was parked");
            Assert.True(peak.Peak > 0, "nothing was ever staged");

            var entry = await SingleEntryAsync(store, AzuriteAccount(), name, password);
            Assert.False(entry.Storage!.Raw);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
