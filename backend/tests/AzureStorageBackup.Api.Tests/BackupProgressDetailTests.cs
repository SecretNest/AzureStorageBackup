using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// What the user actually hit: after creating a backup the UI sat on `Diffing 0% (0 changed)` for a long time, with
/// no way to tell what it was doing or whether it had hung. The cause was that each stage reported only once, on
/// **entry**, while a first backup's diff has to read every file in full to hash it (files with no previous go
/// through AddedAsync → HeadHash + FullHash) and can run for hours; and `TotalItems=0` pinned the percentage at 0.
/// The scanning stage had exactly the same problem.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupProgressDetailTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;

    public BackupProgressDetailTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-progress-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    private sealed class CapturingProgress : IProgress<BackupProgress>
    {
        public List<BackupProgress> Reports { get; } = [];
        public void Report(BackupProgress value) { lock (Reports) Reports.Add(value); }
    }

    [SkippableFact]
    public async Task Scanning_And_Diffing_Report_What_They_Are_Working_On()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var progress = new CapturingProgress();

        var account = new Account
        {
            Name = "azurite",
            BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };
        var name = "progress-" + Guid.NewGuid().ToString("N")[..8];
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // A handful of files, enough for diff to have several steps to report.
            for (var i = 0; i < 40; i++)
            {
                var dir = Path.Combine(_root, "d" + (i % 4));
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, $"f{i:D3}.txt"), new string('x', 500 + i));
            }

            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);

            await orchestrator.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _root, Name = "progress-test",
            }, progress);

            var diffing = progress.Reports.Where(r => r.Stage == BackupStage.Diffing && r.Detail is not null).ToList();
            var scanning = progress.Reports.Where(r => r.Stage == BackupStage.Scanning && r.Detail is not null).ToList();

            // The core: the diff stage must report "which file is being processed" — when it hangs this is the only thing that says where.
            Assert.NotEmpty(diffing);
            Assert.Contains(diffing, r => !string.IsNullOrEmpty(r.Detail!.CurrentItem));

            // And it must run all the way through: before the fix it never reported once and the percentage stayed at 0.
            var lastDiff = diffing[^1].Detail!;
            Assert.Equal(40, lastDiff.Total);
            Assert.Equal(40, lastDiff.Processed);
            Assert.Equal(100, lastDiff.Percent);

            // The scanning stage has an unknown total (the total is what it is computing) → invent no percentage, but report the current directory and entries scanned.
            Assert.NotEmpty(scanning);
            Assert.Null(scanning[^1].Detail!.Percent);
            Assert.Equal(40, scanning[^1].Detail!.Processed);
            Assert.Contains(scanning, r => !string.IsNullOrEmpty(r.Detail!.CurrentItem));

            // Upload stage: transferred bytes must accumulate (the basis for the speed readout), and the wrap-up must
            // force out a final state — otherwise the last batch of bytes stays stuck in the throttle window forever.
            // We do **not** assert "some snapshot happened to catch an in-flight item": that depends on whether the
            // 200ms throttle window happens to fall between BeginItem and EndItem, which is unreliable when local
            // Azurite uploads this fast. The in-flight mechanism is covered deterministically by the unit tests in
            // StageProgressTests; the integration test only verifies the wiring and the final state.
            var uploading = progress.Reports.Where(r => r.Stage == BackupStage.Uploading && r.Detail is not null).ToList();
            Assert.NotEmpty(uploading);
            // Bytes now have exactly one source: the Azure SDK's ProgressHandler reporting as it transfers. This
            // assertion therefore also guards the whole byte-level chain
            // (VolumeBlobIO → IBlobUploader → BlobUploadOptions.ProgressHandler) — break any link and the speed
            // readout is permanently 0, which is exactly what the user saw before the fix.
            Assert.True(uploading[^1].Detail!.Bytes > 0, "uploaded bytes should accumulate for the speed readout");

            // Slot counting is exactly-once: it must never exceed total (in-flight begin/end must not count).
            Assert.All(uploading, r => Assert.True(r.Detail!.Processed <= r.Detail.Total));

            // The queue must drain. Unpaired BeginWork/EndWork (a failure path missing its finally) leaves the UI
            // hanging on "N preparing" forever when nothing is actually running; a missed enqueue leaves "N queued".
            Assert.Equal(0, uploading[^1].Detail!.Preparing);
            Assert.Equal(0, uploading[^1].Detail!.Queued);

            // The index write reports as a stage of its own, and runs to a settled end. It used to be the one blind
            // stretch of the pipeline: a single stage report and then nothing until Finalizing, which at a few
            // million entries is minutes of "Writing index" with nothing on screen.
            var writing = progress.Reports.Where(r => r.Stage == BackupStage.WritingIndex && r.Detail is not null).ToList();
            Assert.NotEmpty(writing);
            Assert.Equal("WritingIndex", writing[^1].Detail!.Stage);
            Assert.True(writing[^1].Detail!.Total > 0, "the transfer count is what the percentage is read off");
            Assert.Equal(writing[^1].Detail!.Total, writing[^1].Detail!.Processed);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The disk-reading check stretches have to be genuinely wired up. What the user hit: half a minute of a
    /// motionless <c>686 of 11,004 objects · 1 object starting upload · 10,317 objects queued</c> —
    /// that item was <c>Stat</c>ing members one by one / reading files in full to rehash them, so it was neither
    /// starting nor uploading, and since these stretches emit not one progress event and the heartbeat only runs
    /// while a stream is transferring, the UI froze on a stale snapshot.
    /// <para>
    /// What is asserted here is the **wiring** (all four call sites really do register, and no pair is missing);
    /// counting semantics and publish timing are covered deterministically by <c>UploadWaitVisibilityTests</c>.
    /// Asserting this way is only possible because <c>BeginChecking</c> forces a publish — had it followed the 200ms
    /// throttle this assertion would be down to luck, which is exactly to say the UI showing anything would be too.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Local_Checking_Work_Shows_Up_In_The_Upload_Stage()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var progress = new CapturingProgress();

        var account = new Account
        {
            Name = "azurite",
            BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };
        var name = "checking-" + Guid.NewGuid().ToString("N")[..8];
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Small files get packed (stat before packing + per-member re-verification after compression), the big
            // file takes the single-file path (dedup pre-screen reading it in full for the three-segment hash) —
            // three of the four registration sites are exercised in this one run; the fourth (encrypted multi-volume
            // leftover clearing) has its own test.
            Directory.CreateDirectory(Path.Combine(_root, "pack"));
            for (var i = 0; i < 8; i++)
                await File.WriteAllTextAsync(Path.Combine(_root, "pack", $"s{i:D2}.txt"), new string('x', 200 + i));
            await File.WriteAllTextAsync(Path.Combine(_root, "big.bin"), new string('y', 8_000));

            var authority = new TestLocalAuthority(store);
            var orchestrator = new BackupOrchestrator(
                new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
                new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
                new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);

            await orchestrator.RunAsync(new BackupRequest
            {
                Account = account, Container = name, LocalRoot = _root, Name = "checking-test",
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1_000 } },
            }, progress);

            var uploading = progress.Reports
                .Where(r => r.Stage == BackupStage.Uploading && r.Detail is not null)
                .Select(r => r.Detail!)
                .ToList();

            Assert.NotEmpty(uploading);
            // Wiring: this stretch is seen at least once. Not seeing it means we are back to "a motionless starting upload on screen".
            Assert.Contains(uploading, d => d.Checking > 0);
            // Breakdown relation: checking is carved out of uploading and cannot exceed it — exceeding it means some
            // registration wandered into the staging leg, which breaks the item-count identity and makes the UI
            // compute a negative "starting upload".
            Assert.All(uploading, d => Assert.True(
                d.Checking <= d.Uploading, $"checking ({d.Checking}) must stay within uploading ({d.Uploading})"));
            // Pairing: the final state must be zero. Miss one EndChecking and this column sticks at an inflated
            // number for the rest of the run — which is exactly how preparing fell over once in this project.
            Assert.Equal(0, uploading[^1].Checking);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
