using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime;
using System.Text;
using System.Text.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Task 23 (sqlite-index-catalog plan): records peak process memory of a real backup run, gated behind
/// <c>ASB_BENCH=1</c> so it never runs in ordinary CI — generating hundreds of thousands of files and running a
/// full backup against Azurite takes minutes, not the sub-second budget the rest of the suite holds to.
/// <para>
/// This is the AFTER copy (built against the commit this plan lands on): the orchestrator here takes
/// <see cref="VersionCatalogs"/> (backed by SQLite, not an in-memory index-per-container dictionary) and a
/// <see cref="RunWorkDbFactory"/> for the scan/diff/plan work database. A second copy of this same file, differing
/// only in that constructor wiring, lives at <c>82bed38</c> (the release this plan started from) so the two numbers
/// are comparable — same file counts, same uploader substitute, same sampling, different commit.
/// </para>
/// <para>
/// The upload path is a substitute that discards every byte (uploads are not what this benchmark is about) but
/// still returns a plausible result, so the orchestrator's bookkeeping (versions, dedup, retention) runs exactly as
/// it does against a real blob service. The index and info-file writes are <see cref="IBackupInfoStore"/>'s job,
/// not <see cref="IBlobUploader"/>'s, so those still go to the real Azurite — small enough not to matter here.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class MemoryBenchmarkTests : IDisposable
{
    /// <summary>Identifies which checkout produced a given benchmark-unique.json/benchmark-identical.json entry — the only intentional difference
    /// between this file and its BEFORE twin besides the constructor wiring in <see cref="Build"/>.</summary>
    private const string Commit = "dd201d3";
    private const string CheckoutLabel = "after";

    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    /// <summary>
    /// Deliberately not under <c>Path.GetTempPath()</c>: on this machine <c>/tmp</c> is a 7.6 GB tmpfs, and
    /// 100k-200k files (even at one byte each) plus directory-entry overhead is not worth risking against a RAM-backed
    /// filesystem shared with everything else running there. A real disk under the user's home directory instead.
    /// </summary>
    private static readonly string BenchRoot =
        Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".cache", "asb-bench");

    private readonly string _tempRoot;

    public MemoryBenchmarkTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "asb-bench-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best effort */ }
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

    /// <summary>Discards every uploaded byte and returns a plausible result — the upload path is not the subject
    /// of this benchmark. Only the two members the interface requires; the progress-carrying overloads have a
    /// default implementation that forwards here (see <see cref="IBlobUploader"/>).</summary>
    private sealed class DiscardUploader : IBlobUploader
    {
        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => Task.FromResult(true);

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? metadata = null)
            => Task.CompletedTask;
    }

    /// <summary>Deterministic file generation: <paramref name="dirCount"/> directories, evenly filled, each file's
    /// content unique (its own relative path, UTF-8 encoded) so no two files are pack aliases of one another —
    /// the identical-content generator this replaced made every file after the first a <see cref="PackAliasTable"/>
    /// alias of the same leader, which is not what a realistic tree looks like. Still tiny: a few bytes per file, so
    /// wall-clock time stays dominated by directory-entry creation, not disk I/O.</summary>
    private static void GenerateFiles(string root, int fileCount, int dirCount)
    {
        Directory.CreateDirectory(root);
        var perDir = fileCount / dirCount;
        for (var d = 0; d < dirCount; d++)
        {
            var dirPath = Path.Combine(root, $"d{d:D4}");
            Directory.CreateDirectory(dirPath);
            for (var f = 0; f < perDir; f++)
                File.WriteAllBytes(Path.Combine(dirPath, $"f{f:D4}.bin"), Encoding.UTF8.GetBytes($"d{d:D4}/f{f:D4}"));
        }
    }

    /// <summary>Same wiring as <c>BackupOrchestratorTests.Build</c> (AFTER shape: <see cref="VersionCatalogs"/> +
    /// <see cref="RunWorkDbFactory"/>), parameterized on the source/staging roots since this benchmark uses its
    /// own directories rather than the shared per-test-class temp dir.</summary>
    private static BackupOrchestrator Build(IBlobUploader uploader, string tempRoot)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(tempRoot, "compress"), Path.Combine(tempRoot, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(tempRoot, "compact"),
            staging);
        var authority = new TestLocalAuthority(store);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor, catalogs: authority.Catalogs, trackedInfo: authority.Tracked),
            new FileHasher(), authority.Catalogs, authority.Tracked,
            workFactory: TestWorkDbs.New());
    }

    private sealed record BenchmarkResult(
        string Commit, string Checkout, int Files, int Dirs,
        long PeakWorkingSetBytes, long PeakManagedHeapBytes,
        long PeakForcedHeapBytes, long PeakForcedHeapCommittedBytes,
        long PostRunForcedHeapPreCleanupBytes,
        long PostRunWorkingSetBytes, long PostRunManagedHeapBytes,
        double DurationSeconds);

    /// <summary>Generates the files, runs one backup against Azurite with uploads discarded, samples
    /// <see cref="Process.WorkingSet64"/> and <see cref="GC.GetTotalMemory"/> every 2 s, and records the peak of
    /// each plus the post-run values after an aggressive, compacting, blocking collection — the same shape as
    /// production's <c>BackupRunner.ReleaseRunMemory</c>, so the post-run number means what the doc says it means.
    /// <para>
    /// Round 2 (controller follow-up): <c>GC.GetTotalMemory(false)</c> at peak includes garbage the collector has
    /// not reclaimed yet, and under workstation GC gen2 can grow with the allocation rate rather than with live
    /// data — a rising peak by itself does not prove live data scales with file count. A second, independent
    /// sampler runs every 10 s, calling <c>GC.GetTotalMemory(forceFullCollection: true)</c> (a full blocking
    /// collection before the read), and tracks its own peak — a much closer proxy for "how much is actually live"
    /// at that moment, at the cost of perturbing the run slightly (each call blocks on a real collection).
    /// Alongside the peak forced reading, one <see cref="GC.GetGCMemoryInfo"/> snapshot's committed heap size is
    /// kept too — one heap-size sample, not a running series, just enough to say whether committed-but-unused
    /// space tracks the live-data reading or diverges from it. A further forced reading is taken the instant the
    /// run ends, before the aggressive/compacting cleanup below, to isolate "what one ordinary forced GC sees"
    /// from what that extra LOH-compacting collect additionally reclaims a moment later.
    /// </para></summary>
    private static BenchmarkResult RunBenchmark(int fileCount, int dirCount, string genRoot, string tempRoot)
    {
        var sw = Stopwatch.StartNew();
        GenerateFiles(genRoot, fileCount, dirCount);

        var proc = Process.GetCurrentProcess();
        long peakWs = 0, peakHeap = 0, peakForcedHeap = 0, peakForcedHeapCommitted = 0;
        var sampling = true;
        var sampleLock = new object();

        void Sample()
        {
            proc.Refresh();
            var ws = proc.WorkingSet64;
            var heap = GC.GetTotalMemory(false);
            lock (sampleLock)
            {
                if (ws > peakWs) peakWs = ws;
                if (heap > peakHeap) peakHeap = heap;
            }
        }

        // Forces a full blocking collection before every read, so what it sees is live data, not
        // not-yet-collected garbage — see the class-level round 2 note above.
        void SampleForced()
        {
            var forcedHeap = GC.GetTotalMemory(true);
            var committed = GC.GetGCMemoryInfo().HeapSizeBytes;
            lock (sampleLock)
            {
                if (forcedHeap > peakForcedHeap)
                {
                    peakForcedHeap = forcedHeap;
                    peakForcedHeapCommitted = committed;
                }
            }
        }

        var samplerTask = Task.Run(async () =>
        {
            while (sampling)
            {
                Sample();
                try { await Task.Delay(2000); } catch { /* ignore */ }
            }
        });

        var forcedSamplerTask = Task.Run(async () =>
        {
            while (sampling)
            {
                SampleForced();
                try { await Task.Delay(10000); } catch { /* ignore */ }
            }
        });

        try
        {
            var uploader = new DiscardUploader();
            var orchestrator = Build(uploader, tempRoot);
            var account = AzuriteAccount();
            var container = RandomName("bench");
            var request = new BackupRequest
            {
                Account = account,
                Container = container,
                LocalRoot = genRoot,
                Name = "bench",
                Password = null,
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 } },
            };

            orchestrator.RunAsync(request).GetAwaiter().GetResult();
            Sample(); // catch the true peak, which may land after the sampler's last tick
        }
        finally
        {
            sampling = false;
            samplerTask.GetAwaiter().GetResult();
            forcedSamplerTask.GetAwaiter().GetResult();
        }

        sw.Stop();

        // One more forced-collection reading right here, before the aggressive/compacting cleanup below —
        // isolates "what one ordinary forced GC sees the instant the run ends" from whatever the LOH-compacting
        // aggressive collect additionally reclaims a moment later.
        var postRunForcedHeapPreCleanup = GC.GetTotalMemory(true);

        // Mirror BackupRunner.ReleaseRunMemory: once the run is over and nothing is waiting on this thread, an
        // aggressive, compacting, blocking collection is the honest way to ask "what's actually still live".
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        proc.Refresh();
        var postWs = proc.WorkingSet64;
        var postHeap = GC.GetTotalMemory(true);

        return new BenchmarkResult(
            Commit, CheckoutLabel, fileCount, dirCount, peakWs, peakHeap,
            peakForcedHeap, peakForcedHeapCommitted, postRunForcedHeapPreCleanup,
            postWs, postHeap, sw.Elapsed.TotalSeconds);
    }

    /// <summary>Appends one run's result to benchmark-unique.json in the test output directory — a JSON array so both
    /// the 100k and 200k runs (and, when the BEFORE copy runs in its own worktree, that pair too) land in one
    /// file per checkout.</summary>
    private static void WriteResult(BenchmarkResult result)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "benchmark-unique.json");
        List<BenchmarkResult> results;
        if (File.Exists(path))
        {
            try { results = JsonSerializer.Deserialize<List<BenchmarkResult>>(File.ReadAllText(path)) ?? []; }
            catch { results = []; }
        }
        else results = [];

        results.RemoveAll(r => r.Checkout == result.Checkout && r.Files == result.Files);
        results.Add(result);
        File.WriteAllText(path, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine(
            $"[bench:{result.Checkout}] files={result.Files} dirs={result.Dirs} " +
            $"peakWS={result.PeakWorkingSetBytes / 1024 / 1024}MB peakHeap={result.PeakManagedHeapBytes / 1024 / 1024}MB " +
            $"peakForcedHeap={result.PeakForcedHeapBytes / 1024 / 1024}MB peakForcedHeapCommitted={result.PeakForcedHeapCommittedBytes / 1024 / 1024}MB " +
            $"postRunForcedHeapPreCleanup={result.PostRunForcedHeapPreCleanupBytes / 1024 / 1024}MB " +
            $"postWS={result.PostRunWorkingSetBytes / 1024 / 1024}MB postHeap={result.PostRunManagedHeapBytes / 1024 / 1024}MB " +
            $"duration={result.DurationSeconds:F1}s");
    }

    [SkippableFact]
    public void Bench_100k_Files()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("ASB_BENCH") == "1", "ASB_BENCH not set");
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var genRoot = Path.Combine(BenchRoot, "gen-100k");
        try
        {
            var result = RunBenchmark(100_000, 1_000, genRoot, _tempRoot);
            WriteResult(result);
        }
        finally
        {
            try { Directory.Delete(genRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [SkippableFact]
    public void Bench_200k_Files()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("ASB_BENCH") == "1", "ASB_BENCH not set");
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var genRoot = Path.Combine(BenchRoot, "gen-200k");
        try
        {
            var result = RunBenchmark(200_000, 2_000, genRoot, _tempRoot);
            WriteResult(result);
        }
        finally
        {
            try { Directory.Delete(genRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
