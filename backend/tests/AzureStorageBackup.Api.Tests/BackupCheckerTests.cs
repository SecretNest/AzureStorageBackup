using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupCheckerTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _src;
    private readonly string _temp;

    public BackupCheckerTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-check-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_src);
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

    /// <param name="checkCompressor">Compressor injected on the check side; when left null the real
    /// <see cref="SevenZipCompressor"/> is used. This hook exists only so that some tests can splice a fake into the
    /// **extract/hash** step (for instance to probe "has the in-flight marker come off by this moment", see the
    /// identically shaped hook in <see cref="RestoreOrchestratorTests"/>); it does not affect packing itself.</param>
    /// <param name="checkerClock">Time source injected on the check side into the internal <see cref="StageTracker"/>,
    /// see the comment on <see cref="BackupChecker.Clock"/> — it only defeats the throttle window, it does not affect
    /// the download/extraction themselves.</param>
    private (BackupOrchestrator Backup, BackupChecker Checker, BlobClientFactory Factory) Build(
        IFileCompressor? checkCompressor = null, Func<long>? checkerClock = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging, new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
        var checker = new BackupChecker(
            factory, store, checkCompressor ?? new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"))
        { Clock = checkerClock };
        return (backup, checker, factory);
    }

    private BackupRepairer Repairer(BlobClientFactory factory, BackupChecker checker)
    {
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        return new BackupRepairer(
            factory, store, new SevenZipCompressor(), new FileHasher(), new BlobUploader(factory),
            Path.Combine(_temp, "repair"),
            new StagingArea(Path.Combine(_temp, "rc"), Path.Combine(_temp, "rs"), () => 200_000_000),
            checker: checker);
    }

    /// <summary>Read the id of the one and only pack out of the latest version index. Pack ids carry a per-run
    /// random prefix (unique across runs, see <c>RunState.NextPackId</c>), so a test cannot hardcode "p0001".</summary>
    private static async Task<string> OnlyPackIdAsync(BlobClientFactory factory, Account account, string container)
    {
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var info = await store.ReadInfoAsync(account, container, null);
        var index = await store.ReadIndexAsync(account, container, info!.Versions[^1].IndexBlob, null);
        return index.Entries.Where(e => e.Storage?.Kind == "pack")
            .Select(e => e.Storage!.Ref).Distinct(StringComparer.Ordinal).Single();
    }

    private BackupRequest Req(Account a, string c) => new()
    {
        Account = a, Container = c, LocalRoot = _src, Name = "photos",
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 } },
    };

    [SkippableFact]
    public async Task Intact_Backup_Passes_Check()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chk-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            await backup.RunAsync(Req(account, name));

            var result = await checker.CheckAsync(account, name, null, null, new CheckOptions());

            Assert.True(result.Ok);
            Assert.NotEmpty(result.Findings);
            Assert.Empty(result.MissingRefs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Size_Mismatch_Reported_And_Repairable_From_Local()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chksz-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha payload here");
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            // The blob is still there but has been rewritten to a different size (simulating truncation / a wrong
            // package) — the local file is untouched.
            await foreach (var b in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).UploadAsync(BinaryData.FromString("x"), overwrite: true);

            var report = await checker.CheckAsync(account, name, null, null, new CheckOptions(), _src);

            var f = report.Findings.Single(x => x.Path == "a.txt");
            Assert.Equal(CloudState.MissingOrBad, f.Cloud); // size mismatch → bad in the cloud
            Assert.Equal(LocalState.Ok, f.Local);           // local content matches
            Assert.True(f.Repairable);                       // repairable from local
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Local_Change_Is_Reported()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chkloc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "original");
            await backup.RunAsync(Req(account, name));

            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "locally edited"); // local edit

            // Check the local content only (skip the cloud).
            var report = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.None, Local = LocalCheckLevel.Content }, _src);

            var f = report.Findings.Single(x => x.Path == "a.txt");
            Assert.Equal(LocalState.Changed, f.Local);
            Assert.Equal(CloudState.NotChecked, f.Cloud);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>When a local file exists but cannot be read, the whole check run used to crash — and "some file
    /// cannot be read" is precisely when the check is needed most: the backup just skipped it, and the operator wants
    /// to know whether the cloud copy is still there. Unreadable is always treated as Missing (local cannot produce a
    /// usable copy, and it must not become a repair source either), the same as the existing handling of "out of
    /// bounds" and "not there", and the check must run to completion.</summary>
    [SkippableFact]
    public async Task An_Unreadable_Local_File_Is_Missing_Rather_Than_Failing_The_Check()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chkunread-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var locked = Path.Combine(_src, "locked.txt");

        try
        {
            await File.WriteAllTextAsync(locked, "readable at backup time");
            await File.WriteAllTextAsync(Path.Combine(_src, "plain.txt"), "stays readable");
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            File.SetUnixFileMode(locked, UnixFileMode.None); // only becomes unreadable after the backup

            var report = await checker.CheckAsync(
                account, name, null, null,
                new CheckOptions { Cloud = CloudCheckLevel.None, Local = LocalCheckLevel.Content }, _src);

            var f = report.Findings.Single(x => x.Path == "locked.txt");
            Assert.Equal(LocalState.Missing, f.Local); // unreadable == local cannot produce a usable copy
            Assert.False(f.Repairable);                 // let alone be used to "repair" the cloud

            // The key point: the check ran to completion, and the other files in the same run still got verdicts.
            Assert.Equal(LocalState.Ok, report.Findings.Single(x => x.Path == "plain.txt").Local);
        }
        finally
        {
            try { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Repair_From_Local_Fixes_Broken_Blob()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rep-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "repair me please");
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            await foreach (var b in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync(); // the cloud blob is gone

            var report = await Repairer(factory, checker).RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null);

            Assert.Contains("a.txt", report.Repaired);
            Assert.Empty(report.Unrecoverable);

            // The content check passes after the repair.
            var after = await checker.CheckAsync(account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content }, _src);
            Assert.True(after.Ok);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Unrepairable_File_Is_Marked_Unrecoverable()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var account = AzuriteAccount();
        var name = RandomName("repun-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "cannot repair this");
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            await foreach (var b in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync(); // gone from the cloud
            File.Delete(Path.Combine(_src, "a.txt"));                        // gone locally too → cannot be repaired

            var report = await Repairer(factory, checker).RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null);

            Assert.Contains("a.txt", report.Unrecoverable);
            Assert.Empty(report.Repaired);

            // Marked unrecoverable in the version index.
            var info = await store.ReadInfoAsync(account, name, null);
            var index = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            Assert.Contains("a.txt", index.UnrecoverablePaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Missing_Blob_Is_Reported()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chkm-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            await backup.RunAsync(Req(account, name));

            // Delete the pack that is referenced (the small file a.txt went into it). The pack name is read from
            // the index rather than hardcoded — pack ids carry a per-run random prefix (unique across runs, see
            // RunState.NextPackId), so there is no fixed value to guess.
            var packBlob = $"packs/{await OnlyPackIdAsync(factory, account, name)}.7z";
            await container.GetBlobClient(packBlob).DeleteIfExistsAsync();

            var result = await checker.CheckAsync(account, name, null, null, new CheckOptions());

            Assert.False(result.Ok);
            Assert.Contains(packBlob, result.MissingRefs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Missing_Volume_Of_Split_Blob_Is_Reported()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chkv-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // A 6MB random file → a single-file data blob; 1MB volumes → multi-volume data/{hash}.001/.002...
            var buf = new byte[6_000_000];
            new Random(7).NextBytes(buf);
            await File.WriteAllBytesAsync(Path.Combine(_src, "big.bin"), buf);
            var req = Req(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    VolumeBytes = 1_000_000,
                },
            };
            await backup.RunAsync(req);

            var hash = await new FileHasher().FullHashAsync(Path.Combine(_src, "big.bin"));
            // Passes while intact.
            Assert.True((await checker.CheckAsync(account, name, null, null, new CheckOptions())).Ok);

            // Delete one of the middle volumes → verifying against the volume count recorded in the index must
            // report it missing (the old base-or-.001 check missed this).
            await container.GetBlobClient($"data/{hash}.002").DeleteIfExistsAsync();
            var result = await checker.CheckAsync(account, name, null, null, new CheckOptions());

            Assert.False(result.Ok);
            Assert.Contains($"data/{hash}", result.MissingRefs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The start notification lands in a push message, so the levels are stated in words a person
    /// reads, not enum identifiers: "cloud ExistenceSize, local Content" is code leaking into prose.</summary>
    [Theory]
    [InlineData(CloudCheckLevel.None, LocalCheckLevel.None, "cloud skipped; local skipped")]
    [InlineData(CloudCheckLevel.Metadata, LocalCheckLevel.Attributes,
        "cloud metadata only; local existence, size and permissions")]
    [InlineData(CloudCheckLevel.ExistenceSize, LocalCheckLevel.Content,
        "cloud existence and size; local content hash")]
    [InlineData(CloudCheckLevel.Content, LocalCheckLevel.Content,
        "cloud content (download and rehash); local content hash")]
    public void The_Start_Notification_States_The_Levels_In_Plain_Words(
        CloudCheckLevel cloud, LocalCheckLevel local, string expected)
        => Assert.Equal(expected, BackupChecker.DescribeLevels(new CheckOptions { Cloud = cloud, Local = local }));

    /// <summary>"0 repairable from local" after a check that never hashed a local file is a false verdict —
    /// repairability was not assessed, not absent, and the wording sent a user away from the repair that would
    /// have hashed exactly the affected files and fixed the recoverable ones.</summary>
    [Fact]
    public void The_Closing_Summary_Says_Not_Assessed_When_Local_Was_Not_Checked()
    {
        var report = new CheckReport(9,
        [
            new FileFinding("a.bin", "data/x", CloudState.MissingOrBad, LocalState.NotChecked),
            new FileFinding("b.bin", "data/y", CloudState.MissingOrBad, LocalState.NotChecked),
            new FileFinding("c.bin", "data/z", CloudState.Ok, LocalState.NotChecked),
        ]);
        Assert.Equal(
            "2 problem(s), local repairability not assessed — run repair to hash just the affected files",
            BackupChecker.ProblemsSummary(report));
    }

    [Fact]
    public void The_Closing_Summary_Counts_Repairable_When_Local_Content_Was_Checked()
    {
        var report = new CheckReport(9,
        [
            new FileFinding("a.bin", "data/x", CloudState.MissingOrBad, LocalState.Ok),
            new FileFinding("b.bin", "data/y", CloudState.MissingOrBad, LocalState.Changed),
        ]);
        Assert.Equal("2 problem(s), 1 repairable from local", BackupChecker.ProblemsSummary(report));
    }

    /// <summary>The mixed case: a sentinel demotion or a partial local pass can leave some problems assessed and
    /// others not, and folding the unassessed ones into "not repairable" is the same false verdict as above.</summary>
    [Fact]
    public void The_Closing_Summary_States_The_Unassessed_Separately()
    {
        var report = new CheckReport(9,
        [
            new FileFinding("a.bin", "data/x", CloudState.MissingOrBad, LocalState.Ok),
            new FileFinding("b.bin", "data/y", CloudState.MissingOrBad, LocalState.NotChecked),
        ]);
        Assert.Equal("2 problem(s), 1 repairable from local, 1 not assessed", BackupChecker.ProblemsSummary(report));
    }

    /// <summary>Progress truthfulness on both cloud stages. The HEAD stage's unit of real work is the volume
    /// (one probe each), so its total and ticks count volumes — a thousand-volume object counted as one tick
    /// freezes the bar for minutes while single-volume packs race it forward. The download stage's real work is
    /// bytes, so it declares its workload the way restore does, and the byte-based WorkPercent takes over from
    /// the object count.</summary>
    [SkippableFact]
    public async Task Check_Progress_Counts_Volumes_For_HEADs_And_Bytes_For_Downloads()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chkp-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var buf = new byte[6_000_000];
            new Random(31).NextBytes(buf);
            await File.WriteAllBytesAsync(Path.Combine(_src, "big.bin"), buf);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    VolumeBytes = 1_000_000,
                },
            });

            StageProgress? cloud = null, verifying = null;
            var result = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content }, _src,
                onProgress: p =>
                {
                    if (p.Stage == "Cloud") cloud = p;
                    if (p.Stage == "Verifying") verifying = p;
                });

            Assert.True(result.Ok);
            // One object, six-plus volumes: an object-counting stage would report Total == 1.
            Assert.NotNull(cloud);
            Assert.True(cloud!.Total >= 6, $"Cloud total is {cloud.Total}, an object count, not volumes");
            Assert.Equal(cloud.Total, cloud.Processed);
            // The deep stage declares its byte workload and retires it fully.
            Assert.NotNull(verifying);
            Assert.Equal(6_000_000, verifying!.WorkTotal);
            Assert.Equal(verifying.WorkTotal, verifying.WorkDone);
            Assert.True(verifying.TransferTotal > 0, "download total not declared despite known volume sizes");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The same client wiring as Build()'s factory, with the overlap probe spliced into the pipeline.</summary>
    private sealed class ProbedFactory(HeadOverlapProbe probe) : IBlobClientFactory
    {
        public BlobServiceClient CreateServiceClient(Account account)
        {
            var uri = new Uri(account.BlobEndpoint);
            var credential = new Azure.Storage.StorageSharedKeyCredential(
                BlobClientFactory.ParseAccountName(uri), TestSecrets.Reader.RevealAccountKey(account));
            var options = new BlobClientOptions();
            options.AddPolicy(probe, Azure.Core.HttpPipelinePosition.PerCall);
            return new BlobServiceClient(uri, credential, options);
        }

        public Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default)
            => new BlobClientFactory(TestSecrets.Reader).TestConnectionAsync(account, ct);
    }

    /// <summary>The existence+size stage must hand its concurrency budget down to the volume probing: a family past
    /// ~1000 volumes takes that many HEADs, and issued serially that is a round-trip per volume — minutes on a
    /// single object. This pins the wiring, not just VolumeBlobIO's own capability (see VolumeBlobIOTests).</summary>
    [SkippableFact]
    public async Task ExistenceSize_Check_Overlaps_Volume_HEADs()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chkc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            // A 6MB random file at 1MB volumes → one data blob of several volumes.
            var buf = new byte[6_000_000];
            new Random(23).NextBytes(buf);
            await File.WriteAllBytesAsync(Path.Combine(_src, "big.bin"), buf);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    VolumeBytes = 1_000_000,
                },
            });

            var probe = new HeadOverlapProbe();
            var probed = new ProbedFactory(probe);
            var checker = new BackupChecker(
                probed, new BackupInfoStore(probed, new SevenZipArchiveCodec()),
                new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "probed-check"));

            var result = await checker.CheckAsync(
                account, name, null, null, new CheckOptions(), downloadConcurrency: 4);

            Assert.True(result.Ok);
            Assert.True(probe.Peak >= 2,
                $"volume HEADs never overlapped (peak {probe.Peak}) — the budget did not reach the probing");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The head budget must span objects, not just the volumes of one family. A real container is
    /// dominated by single-volume objects (a pack per group, per version), and probing those one object at a
    /// time is one round-trip each — the stage advances at 1/RTT objects per second no matter what the budget
    /// says, which in the field read as "still 20 per refresh" at a ~50 ms RTT. The budget is one shared gate:
    /// however the container splits into families, total in-flight HEADs stay at the configured number.</summary>
    [SkippableFact]
    public async Task ExistenceSize_Check_Overlaps_HEADs_Across_Objects()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chko-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            // Eight small files, threshold 1 → eight single-volume data blobs: one HEAD each, so any
            // overlap can only come from probing the objects concurrently.
            for (var i = 0; i < 8; i++)
                await File.WriteAllTextAsync(Path.Combine(_src, $"f{i}.txt"), $"payload {i}");
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            var probe = new HeadOverlapProbe();
            var probed = new ProbedFactory(probe);
            var checker = new BackupChecker(
                probed, new BackupInfoStore(probed, new SevenZipArchiveCodec()),
                new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "cross-object-check"));

            var result = await checker.CheckAsync(
                account, name, null, null, new CheckOptions(), downloadConcurrency: 1, headConcurrency: 6);

            Assert.True(result.Ok);
            Assert.True(probe.Peak >= 2,
                $"HEADs never overlapped across objects (peak {probe.Peak}) — the objects were probed one at a time");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>HEADs move no data, so their budget is not the download budget: a user sizing
    /// DownloadConcurrency against a bandwidth cap must not thereby strangle the existence+size stage,
    /// which is round-trip-bound, not bandwidth-bound. Probed with the download budget pinned to 1 —
    /// overlap can then only come from the head budget.</summary>
    [SkippableFact]
    public async Task The_Head_Budget_Is_Separate_From_The_Download_Budget()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chkh-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var buf = new byte[6_000_000];
            new Random(29).NextBytes(buf);
            await File.WriteAllBytesAsync(Path.Combine(_src, "big.bin"), buf);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    VolumeBytes = 1_000_000,
                },
            });

            var probe = new HeadOverlapProbe();
            var probed = new ProbedFactory(probe);
            var checker = new BackupChecker(
                probed, new BackupInfoStore(probed, new SevenZipArchiveCodec()),
                new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "head-budget-check"));

            var result = await checker.CheckAsync(
                account, name, null, null, new CheckOptions(), downloadConcurrency: 1, headConcurrency: 6);

            Assert.True(result.Ok);
            Assert.True(probe.Peak >= 2,
                $"volume HEADs never overlapped (peak {probe.Peak}) — the head budget was ignored");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task List_Check_Detects_Orphans_And_Repair_Deletes_Them_Keeping_Referenced()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("orph-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // v1: the small file a.txt → pack p0001 (referenced), the large file big.bin → a multi-volume data blob (referenced).
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            var buf = new byte[6_000_000];
            new Random(11).NextBytes(buf);
            await File.WriteAllBytesAsync(Path.Combine(_src, "big.bin"), buf);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                    VolumeBytes = 1_000_000,
                },
            });

            var hash = await new FileHasher().FullHashAsync(Path.Combine(_src, "big.bin"));

            // The pack name is read from the index rather than hardcoded (pack ids carry a per-run random prefix, see RunState.NextPackId).
            var packId = await OnlyPackIdAsync(factory, account, name);
            var stalePackVolume = $"packs/{packId}.7z.099";

            // Manually stuff the container with real orphans + leftover old volumes (simulating a non-atomic
            // replacement / the residue of a failed upload).
            await container.GetBlobClient("data/ZZZ").UploadAsync(BinaryData.FromString("garbage"), overwrite: true);
            await container.GetBlobClient(stalePackVolume).UploadAsync(BinaryData.FromString("stale pack volume"), overwrite: true);
            await container.GetBlobClient($"data/{hash}.099").UploadAsync(BinaryData.FromString("stale data volume"), overwrite: true);

            // Listing check: reports exactly these orphans; referenced blobs / the info file / the indexes are not among them.
            var check = await checker.CheckAsync(account, name, null, null, new CheckOptions { ListOrphans = true }, _src);
            Assert.Contains("data/ZZZ", check.OrphanBlobs);
            Assert.Contains(stalePackVolume, check.OrphanBlobs);
            Assert.Contains($"data/{hash}.099", check.OrphanBlobs);
            Assert.DoesNotContain($"packs/{packId}.7z", check.OrphanBlobs);
            Assert.DoesNotContain($"data/{hash}.001", check.OrphanBlobs);
            Assert.DoesNotContain(BackupDiscovery.IndexBlobName, check.OrphanBlobs);
            Assert.True(check.Ok); // orphans do not affect Ok

            // The report says the scan ran. Without this the only evidence is a non-empty OrphanBlobs, and a clean
            // container produces the same empty list as a scan nobody asked for — which is exactly how a completed
            // scan came to report nothing at all on screen (the UI was inferring it from its own checkbox, and the
            // dialog that owns the checkbox closes the instant the check starts).
            Assert.True(check.OrphansChecked);
            Assert.Null(check.OrphanScanIssue);

            // The other half of the distinction, on the same container: not asked for, so nothing is claimed either
            // way. OrphanBlobs is empty here too, which is the whole point — the flag is what tells them apart.
            var unscanned = await checker.CheckAsync(account, name, null, null, new CheckOptions(), _src);
            Assert.False(unscanned.OrphansChecked);
            Assert.Null(unscanned.OrphanScanIssue);
            Assert.Empty(unscanned.OrphanBlobs);

            // Repair deletes the orphans (cleanupOrphans): the deletion runs even when no blob is broken.
            var report = await Repairer(factory, checker).RepairAsync(
                account, name, null, _src, null,
                new CheckOptions { ListOrphans = true }, Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null);

            Assert.Contains("data/ZZZ", report.DeletedOrphans);
            Assert.Contains(stalePackVolume, report.DeletedOrphans);
            Assert.Contains($"data/{hash}.099", report.DeletedOrphans);

            // The orphans are gone.
            Assert.False((await container.GetBlobClient("data/ZZZ").ExistsAsync()).Value);
            Assert.False((await container.GetBlobClient(stalePackVolume).ExistsAsync()).Value);
            Assert.False((await container.GetBlobClient($"data/{hash}.099").ExistsAsync()).Value);
            // The referenced blobs + the info file are still there.
            Assert.True((await container.GetBlobClient($"packs/{packId}.7z").ExistsAsync()).Value);
            Assert.True((await container.GetBlobClient($"data/{hash}.001").ExistsAsync()).Value);
            Assert.True((await container.GetBlobClient(BackupDiscovery.IndexBlobName).ExistsAsync()).Value);

            // The backup is still intact after the repair.
            Assert.True((await checker.CheckAsync(account, name, null, null, new CheckOptions())).Ok);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Deep_Check_Passes_On_Intact_Backup()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chkd-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            await backup.RunAsync(Req(account, name));

            var result = await checker.CheckAsync(account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });

            Assert.True(result.Ok);
            Assert.Empty(result.CorruptedPaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Deep_Check_Reports_Corrupted_Content()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chkc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            await backup.RunAsync(Req(account, name));

            // Overwrite the pack blob with garbage (it exists but cannot be extracted) → deep verification reports corruption
            await container.GetBlobClient($"packs/{await OnlyPackIdAsync(factory, account, name)}.7z")
                .UploadAsync(BinaryData.FromString("garbage"), overwrite: true);

            var result = await checker.CheckAsync(account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });

            Assert.False(result.Ok);
            Assert.Contains("a.txt", result.CorruptedPaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Progress reporting for the check. Since it became a background job this is the only thing visible in
    /// the UI — one content-level check downloads the whole backup and rehashes it, can run for hours, and without
    /// progress it is indistinguishable from a hang.</summary>
    [SkippableFact]
    public async Task Check_Reports_What_Stage_It_Is_In_And_What_It_Is_Working_On()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chkp-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            for (var i = 0; i < 12; i++)
                await File.WriteAllTextAsync(Path.Combine(_src, $"f{i:D2}.txt"), new string('x', 300 + i));
            await backup.RunAsync(Req(account, name));

            var reports = new List<StageProgress>();
            var result = await checker.CheckAsync(
                account, name, null, null,
                new CheckOptions { Cloud = CloudCheckLevel.Content, Local = LocalCheckLevel.Content },
                _src, ct: CancellationToken.None,
                onProgress: d => { lock (reports) reports.Add(d); });

            Assert.True(result.Ok);

            // Every stage has to show up: before this change there were none, and the UI had nothing but a motionless "Checking" badge.
            var stages = reports.Select(r => r.Stage).Distinct().ToList();
            Assert.Contains("LoadingIndex", stages);
            Assert.Contains("Cloud", stages);
            Assert.Contains("Verifying", stages);
            Assert.Contains("Local", stages);

            // The local stage's total is known (it is the entry count in the index) → it must reach 100% and be able to say which file it is checking.
            var local = reports.Where(r => r.Stage == "Local").ToList();
            Assert.Equal(12, local[^1].Total);
            Assert.Equal(12, local[^1].Processed);
            Assert.Equal(100, local[^1].Percent);
            Assert.Contains(local, r => !string.IsNullOrEmpty(r.CurrentItem));

            // Deep verification now counts the **downloaded** bytes as they stream (VolumeBlobIO.DownloadAsync has
            // a ProgressHandler attached): the in-flight window covers the download only, with extraction and
            // rehashing outside it, so all this asserts is that bytes really do accumulate; the specific
            // "downloaded bytes ≠ raw member size" point is pinned below by
            // Deep_Verify_Credits_Downloaded_Compressed_Bytes_Not_Uncompressed_Member_Sizes.
            var verifying = reports.Where(r => r.Stage == "Verifying").ToList();
            Assert.NotEmpty(verifying);
            Assert.True(verifying[^1].Bytes > 0, "verified bytes should accumulate for the speed readout");

            // A slot counts exactly once: starting/ending an in-flight item must not contribute to the count (or it would run past total).
            Assert.All(verifying, r => Assert.True(r.Processed <= r.Total));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The symptom a user actually saw (before the fix): verifying a physically tiny but extremely compressible
    /// archive (say a large file of one repeated character), the speed readout first showed a number far above the
    /// real link speed, computed as "uncompressed member size / 10s", and then dropped back to 0 — because EndItem
    /// booked the whole group's **raw** member bytes in one go at the end, while the time actually spent on the wire
    /// was very short.
    /// <para>
    /// This pins a harder invariant instead, the final byte count: it must equal the real (compressed) size of the
    /// cloud archive, not the size of the original file — the high compression ratio puts two orders of magnitude
    /// between them, so any regression back to "count by member size" blows this assertion up on the spot, which
    /// guards far better than asserting "greater than 0".
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Deep_Verify_Credits_Downloaded_Compressed_Bytes_Not_Uncompressed_Member_Sizes()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("chkc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // The same character repeated two million times: 7z compresses it enormously, leaving an archive at
            // least an order of magnitude smaller than the original content — enough to open a gap between
            // "downloaded bytes" and "raw member bytes" that is visible to the eye (and to an assertion).
            var big = new string('a', 2_000_000);
            await File.WriteAllTextAsync(Path.Combine(_src, "big.txt"), big);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            // The real size of the cloud archive — the bytes that actually cross the wire during the download,
            // which is the number the Verifying stage should accumulate to after this change.
            long archivedBytes = 0;
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                archivedBytes += b.Properties.ContentLength ?? 0;
            Assert.True(archivedBytes > 0 && archivedBytes < big.Length / 10,
                "fixture must compress far below its original size, or this test doesn't distinguish the two accounting methods");

            var reports = new List<StageProgress>();
            var result = await checker.CheckAsync(
                account, name, null, null,
                new CheckOptions { Cloud = CloudCheckLevel.Content },
                _src, ct: CancellationToken.None,
                onProgress: d => { lock (reports) reports.Add(d); });

            Assert.True(result.Ok);

            var verifying = reports.Where(r => r.Stage == "Verifying").ToList();
            Assert.NotEmpty(verifying);
            Assert.Equal(archivedBytes, verifying[^1].Bytes);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Pins the inner try/finally pair in VerifyGroupAsync: <c>EndItem</c> comes off the moment the download ends,
    /// and the local CPU work of extracting/hashing must not keep counting as "in flight". A mirror of
    /// <see cref="RestoreOrchestratorTests.Extraction_Starts_After_Item_Is_Removed_From_ActiveItems"/> — the two
    /// structures are nearly identical (both are "BeginItem, then take the gate, download, EndItem, then extract");
    /// previously only the restore side had a test guarding this while the check side was written off as "same
    /// structure, not worth testing twice". This one fills that in, so the check side now has its own anchor and no
    /// longer borrows the restore-side test as a guarantee.
    /// <para>
    /// Simply reading the "most recent publication" delivered to onProgress is just as unreliable (the reasons are
    /// spelled out in detail on the restore-side test): publication is throttled at 200ms, on a real clock the whole
    /// download of a test pack of a few dozen bytes often takes a few dozen milliseconds, so the publication EndItem
    /// triggers is usually swallowed by that same throttle window, and both the fix and a mutant could read the stale
    /// "downloading" snapshot. An injected fake clock sidesteps it: every time query jumps a long way forward, the
    /// throttle condition never holds, and every state change gets published — no Thread.Sleep/Task.Delay involved,
    /// the download/extraction are still real calls against Azurite, only "what time is it" has been taken over.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Extraction_Starts_After_Item_Is_Removed_From_ActiveItems()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var probe = new ActiveItemsProbeCompressor(new SevenZipCompressor());
        long fakeNow = 0;
        var (backup, checker, factory) = Build(probe, checkerClock: () => Interlocked.Add(ref fakeNow, 1000));
        var account = AzuriteAccount();
        var name = RandomName("chkextract-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Three small files in one directory → a single pack, a single group: only one in-flight item, so the assertions need not filter by name.
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            await File.WriteAllTextAsync(Path.Combine(_src, "b.txt"), "bravo");
            await File.WriteAllTextAsync(Path.Combine(_src, "c.txt"), "charlie");
            await backup.RunAsync(Req(account, name));

            var result = await checker.CheckAsync(
                account, name, null, null,
                new CheckOptions { Cloud = CloudCheckLevel.Content },
                onProgress: d => probe.LatestPublished = d);

            Assert.True(result.Ok);
            Assert.True(probe.ExtractCallCount > 0, "fake compressor's ExtractToStreamAsync should have been invoked");
            // At the moment of extraction the fake clock has already guaranteed that the publication EndItem
            // triggered when the download finished was not swallowed by the throttle — what we hold is the genuine
            // in-flight set at the instant extraction began, not a stale snapshot scooped up by luck.
            // Catching no snapshot at all is itself a failure — that used to be expressed by seeding a non-empty
            // sentinel set; now it simply asserts non-null, which means the same thing but says it more plainly.
            Assert.NotNull(probe.ActiveItemsAtExtractCall);
            Assert.Empty(probe.ActiveItemsAtExtractCall);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// When the progress callback breaks, the content-level check must not wedge.
    /// <para>
    /// <c>EndItem</c> calls straight into the publish the caller supplied (external code that writes to the database,
    /// pushes SSE and the like), it can throw, and an exception on this path is **deliberately** propagated. It used
    /// to be that <c>gate.Release()</c> sat as the next statement in the same <c>finally</c>, so a throw from the one
    /// before it skipped it entirely — and the download permit was gone for good. There is only one permit here: the
    /// first group swallows it, the second waits at the gate forever, and the whole check never comes back (the UI
    /// shows a spinner that never stops, indistinguishable from a hang). So **a timeout is itself the failure**;
    /// which exception comes out does not matter.
    /// </para>
    /// <para>
    /// The fake clock makes every publication clear the 200ms throttle window: otherwise whether the second
    /// <c>EndItem</c> (the backstop in the finally) actually publishes depends on how long the download happened to
    /// take, and this test becomes a coin flip.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Broken_Progress_Sink_Does_Not_Wedge_The_Content_Check()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        long fakeNow = 0;
        var (backup, checker, factory) = Build(checkerClock: () => Interlocked.Add(ref fakeNow, 1000));
        var account = AzuriteAccount();
        var name = RandomName("chksink-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // One file in each of two directories → two packs, which is two verification groups. The gate hands out
            // a single permit (concurrency 1), so they have to come one after the other — if the first group swallows
            // the permit, the second can never start work.
            Directory.CreateDirectory(Path.Combine(_src, "d1"));
            Directory.CreateDirectory(Path.Combine(_src, "d2"));
            await File.WriteAllTextAsync(Path.Combine(_src, "d1", "a.txt"), "alpha");
            await File.WriteAllTextAsync(Path.Combine(_src, "d2", "b.txt"), "bravo");
            await backup.RunAsync(Req(account, name));

            var check = checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content }, _src,
                ct: CancellationToken.None, downloadConcurrency: 1,
                // Break only during the download-verification stretch. If the whole sink broke, the check would blow
                // up back in the listing/metadata stages and never reach the gate at all — that would be testing
                // something else.
                onProgress: d =>
                {
                    if (d.Stage == "Verifying")
                        throw new IOException("progress sink broke");
                });

            var ex = await Xunit.Record.ExceptionAsync(() => check.WaitAsync(TimeSpan.FromSeconds(20)));

            Assert.IsNotType<TimeoutException>(ex); // wedged = the permit was swallowed
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Wraps a real compressor, intercepting only the <c>ExtractToStreamAsync</c> step to record the
    /// <see cref="StageProgress.ActiveItems"/> of the most recent publication at the moment of the call — extraction
    /// itself is still delegated as usual to the real inner <see cref="SevenZipCompressor"/>, since what is under test
    /// is the "call order", not the extraction result. The check side's deep verification goes through
    /// <c>ExtractToStreamAsync</c> (the streaming path that never touches disk), which is not the same method as the
    /// <c>ExtractAsync</c> probed by <see cref="RestoreOrchestratorTests.ActiveItemsProbeCompressor"/> — each side
    /// probes the one it really calls.</summary>
    private sealed class ActiveItemsProbeCompressor(IFileCompressor inner) : IFileCompressor
    {
        public StageProgress? LatestPublished { get; set; }
        public IReadOnlyList<ActiveTransfer>? ActiveItemsAtExtractCall { get; private set; }
        public int ExtractCallCount { get; private set; }

        public Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default) =>
            inner.CompressAsync(request, ct);

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default) =>
            inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default) => inner.CompressStreamAsync(request, writeSource, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default) =>
            inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
        {
            ExtractCallCount++;
            ActiveItemsAtExtractCall = LatestPublished?.ActiveItems;
            return inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
        }
    }
}
