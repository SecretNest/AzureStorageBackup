using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Acceptance for pipelining the diff with "compress + upload" (phase 3). Three load-bearing points:
/// the output must match the "finish the diff, then upload" era (every file lands in the same place with the same
/// hash, though pack numbers may differ);
/// the two streams really do run at the same time (uploading is already happening before the diff is done);
/// and when one side blows up the other winds down cleanly — an upload failure must surface the original exception,
/// and a cancellation must stop both streams.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PipelinedBackupTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _root;
    private readonly string _temp;

    public PipelinedBackupTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-pipe-" + Guid.NewGuid().ToString("N"));
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

    private BackupOrchestrator Build(
        BlobClientFactory factory, IBackupInfoStore store,
        IFileHasher? hasher = null, IBlobUploader? uploader = null,
        DiffWorkQueueFactory? spill = null)
    {
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(hasher ?? new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked,
            spillFactory: spill);
    }

    private BackupRequest Request(Account account, string container, BackupEngineOptions options) => new()
    {
        Account = account, Container = container, LocalRoot = _root, Name = "pipe", Options = options,
    };

    /// <summary>
    /// The packing result must be exactly what "wait for the whole diff, then pack once" would give. Rather than
    /// reproducing the old code, this takes the packing **pure function** as the baseline: feed the same set of
    /// changed files to <see cref="GroupingPlanner.Plan"/> and the resulting member sets must match the pack members
    /// the pipeline actually produced, one for one (numbers may differ, member grouping may not).
    /// </summary>
    [SkippableFact]
    public async Task Packing_Matches_What_The_Planner_Would_Have_Produced()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipep-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // All three kinds must be present: single files over the threshold, small files merged per directory
            // (past the pack cap, so they get cut into several packs), and small files that hit the cross-directory
            // rule and are scattered across many directories.
            WriteFile("big.bin", 40_000);
            WriteFile("also/big2.bin", 40_000);
            foreach (var dir in new[] { "docs", "docs/deep", "notes" })
                for (var i = 0; i < 6; i++)
                    WriteFile($"{dir}/f{i}.txt", 3_000);
            for (var i = 0; i < 12; i++)
                WriteFile($"shard/{i:D2}/blob.dat", 2_500);

            var options = new BackupEngineOptions
            {
                CrossDirGroup = new IgnoreRuleSet(["shard/"]),
                Plan = new PlanOptions
                {
                    SingleFileThresholdBytes = 10_000,
                    GroupCapBytes = 9_000, // deliberately small: each directory gets cut into several packs
                },
            };
            await Build(factory, store).RunAsync(Request(account, name, options));

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            // Baseline: feed every entry of this run, with the length/hash the index recorded, to the pure packing function.
            var expected = new GroupingPlanner().Plan(
                [.. idx.Entries
                    .OrderBy(e => e.Path, StringComparer.Ordinal)
                    .Select(e => new PlannedFile(e.Path, e.Length, e.FullHash!))],
                options.Plan with { CrossDirGroup = options.CrossDirGroup });

            // First confirm this data really exercised all three paths, otherwise the equality assertions below may just be "one pack on each side".
            Assert.Equal(2, expected.Blobs.Count);
            Assert.True(expected.Packs.Count >= 6, $"expected several packs, got {expected.Packs.Count}");

            Assert.Equal(
                expected.Blobs.Select(b => b.Path).OrderBy(p => p, StringComparer.Ordinal),
                idx.Entries.Where(e => e.Storage!.Kind == "blob").Select(e => e.Path)
                    .OrderBy(p => p, StringComparer.Ordinal));

            // Pack numbers may differ, member grouping may not: normalise both sides into a "set of sets of member paths" before comparing.
            static IEnumerable<string> Signature(IEnumerable<IEnumerable<string>> packs) =>
                packs.Select(m => string.Join('\n', m.OrderBy(p => p, StringComparer.Ordinal)))
                    .OrderBy(s => s, StringComparer.Ordinal);

            var actualPacks = idx.Entries.Where(e => e.Storage!.Kind == "pack")
                .GroupBy(e => e.Storage!.Ref, StringComparer.Ordinal)
                .Select(g => g.Select(e => e.Path));
            Assert.Equal(
                Signature(expected.Packs.Select(p => p.Members.Select(m => m.Path))),
                Signature(actualPacks));

            foreach (var pack in info.Packs)
                Assert.True(await container.GetBlobClient(pack.Value.Blob).ExistsAsync());
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Slow down each time the diff finishes judging a file, and count how many it has judged.
    /// The count is on <c>HeadHashAsync</c>: in a first backup the diff calls it exactly once per file, whichever
    /// path it takes. It must **not** count <c>FullHashAsync</c> — entries classified as single-file blobs never
    /// call it at all (the full-content hash is deferred to the compression pass), so the count would sit at 0
    /// forever and the "is the diff still running" test would be worthless.</summary>
    private sealed class SlowHasher(IFileHasher inner, int delayMs) : IFileHasher
    {
        private int _judged;
        public int Hashed => Volatile.Read(ref _judged);

        public async Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default)
        {
            var hash = await inner.HeadHashAsync(path, headBytes, ct);
            await Task.Delay(delayMs, ct);
            Interlocked.Increment(ref _judged);
            return hash;
        }

        public Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default) =>
            inner.TailHashAsync(path, tailBytes, ct);

        public Task<string> FullHashAsync(string path, CancellationToken ct = default, IProgress<long>? onRead = null) =>
            inner.FullHashAsync(path, ct);
    }

    /// <summary>Records, for each upload, whether the diff was still running.</summary>
    private sealed class OverlapWatchingUploader(IBlobUploader inner, Func<bool> diffRunning) : IBlobUploader
    {
        public int UploadsWhileDiffing { get; private set; }

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (diffRunning())
                UploadsWhileDiffing++;
            return await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
    }

    /// <summary>The whole point of this phase: the network is already uploading while the diff is still reading the
    /// disk. Plan used to be a global barrier — a first backup waited for every file to be hashed before it sent the
    /// first byte.</summary>
    [SkippableFact]
    public async Task Uploading_Starts_While_The_Diff_Is_Still_Running()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipeo-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // All single files over the threshold: each should be uploaded the moment it is judged, without waiting for any group.
            for (var i = 0; i < 8; i++)
                WriteFile($"f{i:D2}.bin", 20_000);

            const int files = 8;
            var hasher = new SlowHasher(new FileHasher(), delayMs: 120);
            var uploader = new OverlapWatchingUploader(new BlobUploader(factory), () => hasher.Hashed < files);
            var orchestrator = Build(factory, store, hasher, uploader);

            await orchestrator.RunAsync(Request(account, name, new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
            }));

            Assert.True(uploader.UploadsWhileDiffing > 0,
                "no upload happened while the diff was still running — the pipeline is still serialised");

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            Assert.Equal(8, idx.Entries.Count);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Turn the overlap off: back to "judge everything first, then upload". It is an escape hatch — when
    /// two read streams drag each other down on a spinning-disk NAS, the user can switch it off from the UI without
    /// any diagnostics at all. The output must be identical to running with it on.</summary>
    [SkippableFact]
    public async Task Overlap_Can_Be_Turned_Off_Without_Changing_The_Result()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipes-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteFile("big.bin", 40_000);
            for (var i = 0; i < 5; i++)
                WriteFile($"docs/f{i}.txt", 3_000);

            const int files = 6;
            var hasher = new SlowHasher(new FileHasher(), delayMs: 60);
            var uploader = new OverlapWatchingUploader(new BlobUploader(factory), () => hasher.Hashed < files);
            var orchestrator = Build(factory, store, hasher, uploader);

            await orchestrator.RunAsync(Request(account, name, new BackupEngineOptions
            {
                OverlapDiffAndUpload = false,
                Plan = new PlanOptions { SingleFileThresholdBytes = 10_000 },
            }));

            Assert.Equal(0, uploader.UploadsWhileDiffing); // not a single byte went out during the diff

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            Assert.Equal(6, idx.Entries.Count);
            Assert.Equal("blob", idx.Entries.Single(e => e.Path == "big.bin").Storage!.Kind);
            Assert.All(idx.Entries.Where(e => e.Path.StartsWith("docs/", StringComparison.Ordinal)),
                e => Assert.Equal("pack", e.Storage!.Kind));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    private sealed class AlwaysFailingUploader : IBlobUploader
    {
        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null) =>
            throw new InvalidOperationException("upload refused by the test");

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null) =>
            throw new InvalidOperationException("upload refused by the test");
    }

    /// <summary>An upload fails while the diff is still running: a state combination only pipelining can produce.
    /// It must surface **the original exception from the upload side** (not the cancellation the diff sees once it
    /// is stopped), and it must not leave a new version behind — otherwise a backup that uploaded nothing
    /// successfully gets recorded as a success.</summary>
    [SkippableFact]
    public async Task An_Upload_Failure_While_Diffing_Surfaces_The_Real_Error()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipef-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            for (var i = 0; i < 12; i++)
                WriteFile($"f{i:D2}.bin", 20_000);

            var hasher = new SlowHasher(new FileHasher(), delayMs: 80);
            var orchestrator = Build(factory, store, hasher, new AlwaysFailingUploader());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                orchestrator.RunAsync(Request(account, name, new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                })));

            Assert.Contains("upload refused by the test", ex.Message);
            Assert.Null(await store.ReadInfoAsync(account, name, null)); // no version left behind
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The user hits stop: RunAsync only returns once both streams have wound down. The moment it returns
    /// the caller releases the busy lock — returning one step early means leaving a pile of compression/upload work
    /// running outside the lock.</summary>
    [SkippableFact]
    public async Task Canceling_Mid_Run_Stops_Both_Streams()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipec-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            for (var i = 0; i < 40; i++)
                WriteFile($"f{i:D2}.bin", 20_000);

            var orchestrator = Build(factory, store, new SlowHasher(new FileHasher(), delayMs: 100));
            using var cts = new CancellationTokenSource();
            var run = orchestrator.RunAsync(Request(account, name, new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
            }), progress: null, cts.Token);

            await Task.Delay(400);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(run.IsCompleted); // returning means it has wound down, no background aftershocks
            Assert.Null(await store.ReadInfoAsync(account, name, null));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Progress: while both streams run at once the details must carry both — report only one and the other row freezes in the UI.</summary>
    [SkippableFact]
    public async Task Progress_Carries_Both_Stages_While_They_Overlap()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipeg-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            for (var i = 0; i < 10; i++)
                WriteFile($"f{i:D2}.bin", 20_000);

            var reports = new List<BackupProgress>();
            var progress = new CollectingProgress(reports);
            await Build(factory, store, new SlowHasher(new FileHasher(), delayMs: 100))
                .RunAsync(Request(account, name, new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                }), progress);

            lock (reports)
            {
                Assert.Contains(reports, r =>
                    r.Details.Count == 2
                    && r.Details.Any(d => d.Stage == "Diffing")
                    && r.Details.Any(d => d.Stage == "Uploading"));
                // The single-value field still works (a caller that only looks at one need not first check whether there is a second).
                Assert.Contains(reports, r => r.Detail is not null);
            }
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Squeeze the memory limits to the minimum, forcing almost every item through a round trip to disk; the output
    /// must be **identical entry for entry** to a run that stayed in memory throughout.
    /// That is the only thing spilling has to prove: it is a transport channel, not a channel that rewrites content.
    /// Two cleanup points ride along: spilling must be reportable in the UI, and the temp file must be gone once the
    /// run ends.
    /// </summary>
    [SkippableFact]
    public async Task Spilling_The_Work_Queue_To_Disk_Produces_An_Identical_Backup()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var spilledName = RandomName("pipesp-");
        var memoryName = RandomName("pipemem-");
        var spilled = factory.CreateServiceClient(account).GetBlobContainerClient(spilledName);
        var memory = factory.CreateServiceClient(account).GetBlobContainerClient(memoryName);
        await spilled.CreateIfNotExistsAsync();
        await memory.CreateIfNotExistsAsync();

        var spillDir = Path.Combine(_temp, "diff-spill");

        try
        {
            // All three paths must be exercised: single-file blob, per-directory pack, cross-directory pack.
            WriteFile("big.bin", 40_000);
            WriteFile("also/big2.bin", 40_000);
            foreach (var dir in new[] { "docs", "docs/deep", "notes" })
                for (var i = 0; i < 6; i++)
                    WriteFile($"{dir}/f{i}.txt", 3_000);
            for (var i = 0; i < 12; i++)
                WriteFile($"shard/{i:D2}/blob.dat", 2_500);

            var options = new BackupEngineOptions
            {
                CrossDirGroup = new IgnoreRuleSet(["shard/"]),
                Plan = new PlanOptions { SingleFileThresholdBytes = 10_000, GroupCapBytes = 9_000 },
            };

            // r holds one item and w flushes as soon as it has one: apart from the very first item, everything really goes through the disk.
            var tiny = new DiffWorkQueueFactory(spillDir, new DiffQueueLimits(
                MaxCachedItems: 1, MaxCachedBytes: long.MaxValue,
                WriteBatchItems: 1, WriteBatchBytes: long.MaxValue,
                RefillBatchItems: 2));
            var reports = new List<BackupProgress>();
            await Build(factory, store, spill: tiny)
                .RunAsync(Request(account, spilledName, options), new CollectingProgress(reports));

            await Build(factory, store).RunAsync(Request(account, memoryName, options));

            var spilledIndex = await ReadOnlyIndexAsync(store, account, spilledName);
            var memoryIndex = await ReadOnlyIndexAsync(store, account, memoryName);

            // Identical entry for entry: path, length, full-content hash. Spilling is only haulage; not one byte should change.
            Assert.Equal(
                memoryIndex.Entries.OrderBy(e => e.Path, StringComparer.Ordinal)
                    .Select(e => (e.Path, e.Length, e.FullHash)),
                spilledIndex.Entries.OrderBy(e => e.Path, StringComparer.Ordinal)
                    .Select(e => (e.Path, e.Length, e.FullHash)));

            // The packing grouping must match too (pack numbers may differ, how members are split may not).
            static IEnumerable<string> PackSignature(VersionIndex idx) =>
                idx.Entries.Where(e => e.Storage!.Kind == "pack")
                    .GroupBy(e => e.Storage!.Ref, StringComparer.Ordinal)
                    .Select(g => string.Join('\n', g.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal)))
                    .OrderBy(s => s, StringComparer.Ordinal);
            Assert.Equal(PackSignature(memoryIndex), PackSignature(spilledIndex));

            // This run really did spill — otherwise the equality assertions above only say "both runs stayed in memory".
            lock (reports)
            {
                Assert.Contains(reports, r => r.Details.Any(d => d.Stage == "Diffing" && d.SpilledItems > 0));
            }

            // No garbage left after the run: a normal shutdown deletes its own spill file (the abnormal-exit path is covered by ClearStale).
            Assert.True(
                !Directory.Exists(spillDir) || Directory.GetFiles(spillDir, "*.spill").Length == 0,
                "no spill file should still be around after the run ends");
        }
        finally
        {
            await spilled.DeleteIfExistsAsync();
            await memory.DeleteIfExistsAsync();
        }
    }

    /// <summary>
    /// The member-count and path-bytes limits must read the same way at **all three** packing sites: the planner's
    /// pure function, the orchestrator's cross-directory accumulator that fills up while the diff runs, and
    /// <c>ProcessPackAsync</c>'s re-split just before compression. Miss any one of them and the real output parts
    /// ways with the planner, and the first things to break are dedup and retention cleanup, which identify packs by
    /// their member grouping.
    /// The pure function is still the baseline here — it is the one and only definition of what the result should look like.
    /// </summary>
    [SkippableFact]
    public async Task Member_And_Path_Limits_Apply_At_Every_Packing_Site()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipelim-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // All small files: the byte cap is never reached, so only the member-count limit can cut a pack.
            foreach (var dir in new[] { "docs", "notes" })
                for (var i = 0; i < 25; i++)
                    WriteFile($"{dir}/f{i:D2}.txt", 40);
            for (var i = 0; i < 30; i++)
                WriteFile($"shard/{i:D2}/blob.dat", 40);

            var options = new BackupEngineOptions
            {
                CrossDirGroup = new IgnoreRuleSet(["shard/"]),
                Plan = new PlanOptions
                {
                    SingleFileThresholdBytes = 10_000,
                    GroupCapBytes = 100 * 1024 * 1024, // deliberately generous: keep the byte limit out of it
                    MaxPackMembers = 7,                // only this one can cut
                },
            };
            await Build(factory, store).RunAsync(Request(account, name, options));

            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);

            var expected = new GroupingPlanner().Plan(
                [.. idx.Entries
                    .OrderBy(e => e.Path, StringComparer.Ordinal)
                    .Select(e => new PlannedFile(e.Path, e.Length, e.FullHash!))],
                options.Plan with { CrossDirGroup = options.CrossDirGroup });

            // First confirm this data really was cut by the member-count limit, otherwise the equality assertions below may just be "one pack on each side".
            Assert.True(expected.Packs.Count >= 12, $"expected many small packs, got {expected.Packs.Count}");
            Assert.All(expected.Packs, p => Assert.True(p.Members.Count <= 7, $"the planner itself went over the limit: {p.Members.Count}"));

            static IEnumerable<string> Signature(IEnumerable<IEnumerable<string>> packs) =>
                packs.Select(m => string.Join('\n', m.OrderBy(p => p, StringComparer.Ordinal)))
                    .OrderBy(s => s, StringComparer.Ordinal);

            var actualPacks = idx.Entries.Where(e => e.Storage!.Kind == "pack")
                .GroupBy(e => e.Storage!.Ref, StringComparer.Ordinal)
                .Select(g => g.Select(e => e.Path))
                .ToList();

            // Every pack actually produced stays within the limit — this directly guards 7z's memory and argv.
            Assert.All(actualPacks, m => Assert.True(m.Count() <= 7, $"actual output over the limit: {m.Count()}"));
            Assert.Equal(Signature(expected.Packs.Select(p => p.Members.Select(m => m.Path))), Signature(actualPacks));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Records whether the diff was still running when a pack upload happened. It only looks at the
    /// <c>packs/</c> prefix — every other entry in this run is a single-file blob (<c>data/</c>), so the prefix is
    /// enough to pick that one pack out.</summary>
    private sealed class PackUploadWatcher(IBlobUploader inner, Func<bool> diffRunning) : IBlobUploader
    {
        private int _whileDiffing;
        public int PackUploadsWhileDiffing => Volatile.Read(ref _whileDiffing);

        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            if (blobName.StartsWith("packs/", StringComparison.Ordinal) && diffRunning())
                Interlocked.Increment(ref _whileDiffing);
            return await inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
    }

    /// <summary>
    /// A full cross-directory pack should go into the queue right away, not hang there waiting for the **next**
    /// cross-directory file to push it out.
    /// <para>
    /// The sealing test <see cref="GroupingPlanner.GroupIsFull"/> asks "would adding this one go over", so it needs
    /// the next file before it can answer. But two of its limits, member count and path bytes, have nothing to do
    /// with the next file — the pack's fate is already settled the moment it fills up. The cost of waiting is
    /// measured in scan order: if no cross-directory candidate turns up for a long stretch afterwards (everything
    /// past this point here is a large file taking the single-file path), the pack hangs all the way to the end of
    /// the diff before the fallback seals it, waiting out the entire diff for nothing.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Full_Cross_Directory_Pack_Goes_Out_Without_Waiting_For_The_Next_File()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("pipexd-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Three cross-directory small files fill one pack exactly (MaxPackMembers = 3), and there is no fourth cross-directory candidate after that.
            for (var i = 0; i < 3; i++)
                WriteFile($"a-shard/{i:D2}/blob.dat", 2_500);
            // The batch after that is all single files over the threshold, purely to drag the diff out: not one of
            // them can go into that pack. Their names sort later (ordinal order), so the scan order guarantees the
            // three shard files are judged first.
            for (var i = 0; i < 8; i++)
                WriteFile($"z-big/f{i:D2}.bin", 20_000);

            const int files = 11;
            var hasher = new SlowHasher(new FileHasher(), delayMs: 120);
            var uploader = new PackUploadWatcher(new BlobUploader(factory), () => hasher.Hashed < files);

            var options = new BackupEngineOptions
            {
                CrossDirGroup = new IgnoreRuleSet(["a-shard/"]),
                Plan = new PlanOptions
                {
                    SingleFileThresholdBytes = 10_000,
                    GroupCapBytes = 100 * 1024 * 1024, // the byte limit is out of reach
                    MaxPackMembers = 3,                // only the member count can seal a pack
                },
            };
            await Build(factory, store, hasher, uploader).RunAsync(Request(account, name, options));

            Assert.True(uploader.PackUploadsWhileDiffing > 0,
                "the full pack hung until the end of the diff before being sealed — it was waiting for the next cross-directory file");

            // Sealing early **does not change the packing result**: the three members are still in one pack.
            var idx = await ReadOnlyIndexAsync(store, account, name);
            var packed = idx.Entries.Where(e => e.Storage!.Kind == "pack").ToList();
            Assert.Equal(3, packed.Count);
            Assert.Single(packed.Select(e => e.Storage!.Ref).Distinct(StringComparer.Ordinal));
            Assert.Equal(8, idx.Entries.Count(e => e.Storage!.Kind == "blob"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    private static async Task<VersionIndex> ReadOnlyIndexAsync(
        IBackupInfoStore store, Account account, string container)
    {
        var info = await store.ReadInfoAsync(account, container, null);
        return await store.ReadIndexAsync(account, container, info!.Versions[0].IndexBlob, null);
    }

    private sealed class CollectingProgress(List<BackupProgress> sink) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) { lock (sink) sink.Add(value); }
    }
}
