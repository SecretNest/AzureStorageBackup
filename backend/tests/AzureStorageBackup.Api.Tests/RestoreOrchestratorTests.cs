using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>Collects phase reports synchronously (Progress&lt;T&gt; queues its callbacks onto the synchronization context, where the test can't read them).</summary>
internal sealed class SyncProgress : IProgress<string>
{
    public List<string> Messages { get; } = [];
    public void Report(string value) { lock (Messages) Messages.Add(value); }
}

[Trait("Category", "Integration")]
public sealed class RestoreOrchestratorTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _src;
    private readonly string _dst;
    private readonly string _temp;

    public RestoreOrchestratorTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-restore-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_base, "src");
        _dst = Path.Combine(_base, "dst");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_temp);
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

    private void WriteSrc(string rel, string content)
    {
        var full = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <param name="restoreCompressor">The compressor injected on the restore side; when null (the default) the real <see cref="SevenZipCompressor"/> is used.
    /// The backup side always uses the real one — this hook exists only so that certain tests can splice a fake in at the **extraction** step (e.g. to probe
    /// "has the in-flight marker already been dropped at the moment of extraction"), without affecting the packing process itself.</param>
    /// <param name="restoreClock">The time source injected into the restore side's internal <see cref="StageTracker"/>; see
    /// the comment on <see cref="RestoreOrchestrator.Clock"/> — purely to disable the throttle window, it doesn't affect the download/extraction itself.</param>
    /// <param name="caseProbe">Stands in for the real "does this filesystem fold case" probe (directory → folds). CI runs on ext4, which never folds,
    /// so without this the whole case-collision half of the restore would be unreachable from a test. Taking the directory as a parameter matters:
    /// the target root and the extraction directory are probed separately and can legitimately differ.</param>
    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store, BlobClientFactory Factory) Build(
        IFileCompressor? restoreCompressor = null, Func<long>? restoreClock = null, Func<string, bool>? caseProbe = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging, new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
        var restore = new RestoreOrchestrator(
            factory, store, restoreCompressor ?? new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"))
        { Clock = restoreClock, CaseProbe = caseProbe };
        return (backup, restore, store, factory);
    }

    private BackupRequest BackupReq(Account account, string container, string? password = null) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _src,
        Name = "photos",
        Password = password,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 } },
    };

    [SkippableFact]
    public async Task Restore_Rehydrates_Archived_Blob_Then_Restores()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rhyd-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "archived content");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            // Set the data blob to Archive; skip if Azurite doesn't support it.
            try
            {
                await foreach (var b in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                    await container.GetBlobClient(b.Name).SetAccessTierAsync(Azure.Storage.Blobs.Models.AccessTier.Archive);
            }
            catch (Azure.RequestFailedException)
            {
                Skip.If(true, "Azurite does not support Archive tier");
            }

            // The restore should rehydrate automatically and then fetch the content (with a small poll interval).
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst, RehydratePollSeconds = 1,
            });

            Assert.Equal("archived content", File.ReadAllText(Path.Combine(_dst, "a.txt")));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Restore_Substitutes_Unrecoverable_File_From_Chosen_Version()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rsub-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "version one");
            WriteSrc("keep.txt", "unchanged file");
            await backup.RunAsync(BackupReq(account, name));   // v1
            WriteSrc("a.txt", "version two");
            await backup.RunAsync(BackupReq(account, name));   // v2

            // Mark v2's a.txt as unrecoverable (simulating a repair that couldn't recover it from local files).
            var info = await store.ReadInfoAsync(account, name, null);
            var v2 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v2.IndexBlob, null);
            idx.UnrecoverablePaths.Add("a.txt");
            await store.WriteIndexAsync(account, name, v2.Version, idx, null);

            // No substitution given → a.txt is skipped (everything else proceeds as usual).
            await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst, Version = 2 });
            Assert.False(File.Exists(Path.Combine(_dst, "a.txt")));
            Assert.True(File.Exists(Path.Combine(_dst, "keep.txt")));

            // Substitute from v1 → a.txt is restored with v1's content.
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst, Version = 2,
                Substitutions = new Dictionary<string, int> { ["a.txt"] = 1 },
            });
            Assert.Equal("version one", File.ReadAllText(Path.Combine(_dst, "a.txt")));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Substitution_To_Missing_Version_Skips_Path_Without_Failing_Whole_Restore()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rsubm-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "version one");
            WriteSrc("b.txt", "unchanged file");
            await backup.RunAsync(BackupReq(account, name));   // v1
            WriteSrc("a.txt", "version two");
            await backup.RunAsync(BackupReq(account, name));   // v2

            // Mark v2's a.txt as unrecoverable (simulating a repair that couldn't recover it from local files).
            var info = await store.ReadInfoAsync(account, name, null);
            var v2 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v2.IndexBlob, null);
            idx.UnrecoverablePaths.Add("a.txt");
            await store.WriteIndexAsync(account, name, v2.Version, idx, null);

            // Declare a substitution to a version that doesn't exist (e.g. already removed by retention cleanup) → it should fall back to skipping rather than failing the whole run.
            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst, Version = 2,
                Substitutions = new Dictionary<string, int> { ["a.txt"] = 99 }, // a version that doesn't exist
            });

            Assert.True(result.SkippedFiles >= 1);                    // a.txt falls back to being skipped
            Assert.False(File.Exists(Path.Combine(_dst, "a.txt")));
            Assert.True(File.Exists(Path.Combine(_dst, "b.txt")));    // b.txt restores normally
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Encrypted_Keyed_Backup_RoundTrips_Through_Restore()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rste-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("dir/small.txt", "grouped");       // pack member
            WriteSrc("big.bin", new string('y', 6_000_000)); // single-file data blob with keyed addressing

            await backup.RunAsync(BackupReq(account, name, password: "pw"));
            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst, Password = "pw",
            });

            Assert.Equal(2, result.RestoredFiles);
            Assert.Equal("grouped", File.ReadAllText(Path.Combine(_dst, "dir", "small.txt")));
            Assert.Equal(6_000_000, new FileInfo(Path.Combine(_dst, "big.bin")).Length);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Staged_Backup_By_Removing_Ignores_Final_Version_Equals_Whole()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rststg-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a/1.txt", "alpha");
            WriteSrc("b/2.txt", "bravo");
            WriteSrc("c/3.txt", "charlie");

            BackupRequest Req(params string[] ignore) => BackupReq(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
                    Ignore = new IgnoreRuleSet(ignore),
                },
            };

            var v1 = await backup.RunAsync(Req("b/", "c/")); // stage 1: only a
            var v2 = await backup.RunAsync(Req("c/"));       // stage 2: stop ignoring b → b is added
            var v3 = await backup.RunAsync(Req());           // stage 3: stop ignoring c → c is added (now complete)

            // Each stage only processes the newly un-ignored files; the older ones carry over and are not re-uploaded.
            Assert.Equal(1, v1.ChangedFiles);
            Assert.Equal(1, v2.ChangedFiles); // only b/2.txt
            Assert.Equal(1, v3.ChangedFiles); // only c/3.txt

            var info = await store.ReadInfoAsync(account, name, null);
            var idx1 = await store.ReadIndexAsync(account, name, info!.Versions[0].IndexBlob, null);
            var idx3 = await store.ReadIndexAsync(account, name, info.Versions[^1].IndexBlob, null);
            Assert.Equal(["a/1.txt"], idx1.Entries.Select(e => e.Path).OrderBy(x => x)); // v1 is incomplete
            Assert.Equal(["a/1.txt", "b/2.txt", "c/3.txt"], idx3.Entries.Select(e => e.Path).OrderBy(x => x)); // v3 is complete

            // Restoring the final version = every file.
            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });
            Assert.Equal(3, result.RestoredFiles);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(_dst, "a", "1.txt")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(_dst, "b", "2.txt")));
            Assert.Equal("charlie", File.ReadAllText(Path.Combine(_dst, "c", "3.txt")));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Deduped_Identical_Files_Both_Restore()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        foreach (var storeOnly in new[] { false, true }) // both flavors of single-file blob: non-raw (7z) and raw
        {
            var (backup, restore, _, factory) = Build();
            var account = AzuriteAccount();
            var name = RandomName("rstdup-");
            var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
            await container.CreateIfNotExistsAsync();
            var dst = Path.Combine(_dst, storeOnly ? "raw" : "z");

            try
            {
                WriteSrc("x.txt", "identical content");
                WriteSrc("y.txt", "identical content"); // same content → same hash → they share one data blob (dedup)

                await backup.RunAsync(BackupReq(account, name) with
                {
                    Options = new BackupEngineOptions
                    {
                        Plan = new PlanOptions { SingleFileThresholdBytes = 1 }, // single-file blob
                        DontCompress = storeOnly ? new IgnoreRuleSet(["*"]) : null,
                    },
                });

                var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = dst });

                Assert.Equal(2, result.RestoredFiles); // both files referencing the same blob get restored
                Assert.Equal("identical content", File.ReadAllText(Path.Combine(dst, "x.txt")));
                Assert.Equal("identical content", File.ReadAllText(Path.Combine(dst, "y.txt")));
            }
            finally
            {
                await container.DeleteIfExistsAsync();
            }
        }
    }

    [SkippableFact]
    public async Task Raw_Stored_File_RoundTrips_Through_Restore()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstraw-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("keep.bin", "raw bytes not compressed");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    DontCompress = new IgnoreRuleSet(["*"]), // store-only → uploaded raw
                },
            });

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });

            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal("raw bytes not compressed", File.ReadAllText(Path.Combine(_dst, "keep.bin")));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Restores_Files_And_Empty_Dirs_To_Target()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rst-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            WriteSrc("big.bin", new string('x', 6_000_000)); // > 5M -> data blob
            Directory.CreateDirectory(Path.Combine(_src, "emptydir"));

            await backup.RunAsync(BackupReq(account, name));

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });

            Assert.Equal(1, result.Version);
            Assert.Equal(3, result.RestoredFiles);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(_dst, "a.txt")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(_dst, "dir", "b.txt")));
            Assert.Equal(6_000_000, new FileInfo(Path.Combine(_dst, "big.bin")).Length);
            Assert.True(Directory.Exists(Path.Combine(_dst, "emptydir")));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Volume_Split_Backup_RoundTrips_Through_Restore()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstv-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("big.bin", new string('x', 60_000));
            var req = BackupReq(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    // Lower the threshold → big.bin goes through a single-file blob; no compression + 20KB volumes → multiple volumes
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1000 },
                    DontCompress = new IgnoreRuleSet(["*.bin"]),
                    VolumeBytes = 20_000,
                },
            };
            await backup.RunAsync(req);

            // Should produce a multi-volume data blob (data/{hash}.001 exists)
            var volumeBlobs = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", default))
                volumeBlobs.Add(b.Name);
            Assert.Contains(volumeBlobs, n => n.EndsWith(".001"));

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal(60_000, new FileInfo(Path.Combine(_dst, "big.bin")).Length);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// End-to-end backstop: run the real backup/restore pipeline to produce a genuine multi-volume 7z archive, then assert that the byte count
    /// the Restoring stage finally accumulates matches the real (compressed) size of the archive in the cloud.
    /// <para>
    /// This one does not detect the "the factory got replaced by a shared instance" defect itself — verified by mutation: this project's 7z volumes are naturally
    /// a size sequence of "every volume equal except the last, which is the smallest", and <c>DeltaProgress</c>'s regression check (see
    /// <see cref="StageTracker"/>) self-corrects under such a sequence, so a shared instance still adds up to exactly the same
    /// correct total this test expects and would not turn this assertion red. What really pins down the Part 1 contract ("call the factory once per volume, getting a distinct
    /// instance each time") is <c>VolumeBlobIOTests.DownloadAsync_Calls_Progress_Factory_Once_Per_Volume_With_A_Fresh_Instance</c> —
    /// that one tests <c>DownloadAsync</c> directly, without going through the indirection of the volume-size sequence. This test is kept to cover
    /// the more mundane invariant that "the byte accounting across the whole restore path wasn't broken by this change", and has nothing to do with mutation detection.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Multi_Volume_Restore_Sums_Downloaded_Bytes_Without_Compounding()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstb-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Small volumes force a genuinely multi-volume archive (5+ volumes) — only with enough volumes do the accumulated results of
            // "a fresh progress instance per volume" and "one shared instance throughout" diverge noticeably.
            WriteSrc("big.bin", new string('x', 100_000));
            var req = BackupReq(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1000 },
                    DontCompress = new IgnoreRuleSet(["*.bin"]),
                    VolumeBytes = 20_000,
                },
            };
            await backup.RunAsync(req);

            // The real size of the archive in the cloud: the byte count the download genuinely puts over the wire, i.e. the number the Restoring stage
            // should accumulate to after this change.
            long archivedBytes = 0;
            var volumeCount = 0;
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
            {
                archivedBytes += b.Properties.ContentLength ?? 0;
                volumeCount++;
            }
            Assert.True(volumeCount >= 4, "fixture should produce several volumes, or this test doesn't exercise the per-volume factory");

            var reports = new List<StageProgress>();
            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst },
                CancellationToken.None, phase: null, onProgress: p => { lock (reports) reports.Add(p); });

            Assert.Equal(1, result.RestoredFiles);

            var restoring = reports.Where(r => r.Stage == "Restoring").ToList();
            Assert.NotEmpty(restoring);
            Assert.Equal(archivedBytes, restoring[^1].Bytes);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Second_Restore_Skips_Unchanged_Files()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rst2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            await backup.RunAsync(BackupReq(account, name));

            var req = new RestoreRequest { Account = account, Container = name, TargetRoot = _dst };
            var first = await restore.RunAsync(req);
            Assert.Equal(2, first.RestoredFiles);

            var second = await restore.RunAsync(req); // local copies already identical → everything is skipped
            Assert.Equal(0, second.RestoredFiles);
            Assert.Equal(2, second.SkippedFiles);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task Selective_Restore_Writes_Only_Selected_Pack_Members()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstsel-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Three small files → one and the same pack (below the default 5M threshold they go through grouping).
            WriteSrc("dir/a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            WriteSrc("dir/c.txt", "charlie");
            await backup.RunAsync(BackupReq(account, name));

            // Confirm they really were packed into a single pack (the premise of the download-once semantics).
            var info = await store.ReadInfoAsync(account, name, null);
            var idx = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var packRefs = idx.Entries.Where(e => e.Storage?.Kind == "pack").Select(e => e.Storage!.Ref).Distinct().ToList();
            Assert.Single(packRefs); // all three members belong to the same pack

            // Select only one member inside the pack → only that member lands on disk, the rest are not over-restored.
            var seen = new List<StageProgress>();
            var result = await restore.RunAsync(
                new RestoreRequest
                {
                    Account = account, Container = name, TargetRoot = _dst,
                    SelectedPaths = ["dir/a.txt"],
                },
                onProgress: p => { lock (seen) seen.Add(p); });

            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(_dst, "dir", "a.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "dir", "b.txt"))); // unselected members don't land on disk
            Assert.False(File.Exists(Path.Combine(_dst, "dir", "c.txt")));

            // …and the row naming the extraction describes **the batch**, not the pack. The pack holds three
            // members; only one was asked for, so only one may be spoken of. Reaching for the manifest instead of
            // the filtered set is a one-word change at the call site that nothing else here would notice, and on a
            // real backup it puts directories the user did not select on their screen — this is the guard for it.
            List<string> preparing;
            lock (seen)
                preparing = [.. seen.Select(s => s.PreparingItem).OfType<string>().Distinct()];
            Assert.Contains("/dir — 1 file", preparing);
            Assert.DoesNotContain(preparing, p => p.Contains("3 files"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// Pins down that inner try/finally pair in RestoreGroupAsync: <c>EndItem</c> is dropped as soon as the download ends, and the local CPU work of
    /// extraction/disk writes must not keep counting as "in flight". This is not a harmless detail — the speed denominator only recognizes the in-flight window,
    /// extracting a large pack can take tens of seconds, and counting that in halves the displayed speed.
    /// <para>
    /// Reading the "most recent publish" that <c>onProgress</c> received directly is unreliable: publishing is throttled at 200ms, and on the real clock downloading
    /// a test pack of a few dozen bytes often takes a few dozen milliseconds end to end; during the download the SDK reports progress at least once (the first call always publishes),
    /// after which the publishes from EndItem and BeginPacking are most likely swallowed by that very same throttle window — so with either the fix or
    /// the mutant, the observed "most recent" one may still be stuck on the "downloading" snapshot and the test measures nothing at all
    /// (this failure mode was verified empirically with a Diagnostic probe).
    /// </para>
    /// <para>
    /// Sidestep it with an injected fake clock: every time query jumps a long way forward, so the throttle condition <c>now - last &lt; 200ms</c>
    /// never holds and every state change gets published — this is not gambling on the real clock happening to cross the throttle window,
    /// it makes the throttle window entirely ineffective for this test. No Thread.Sleep/Task.Delay is involved; the download/extraction are still real calls against
    /// Azurite, only the single question of "what time is it now" has been taken over.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Extraction_Starts_After_Item_Is_Removed_From_ActiveItems()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var probe = new ActiveItemsProbeCompressor(new SevenZipCompressor());
        long fakeNow = 0;
        var (backup, restore, _, factory) = Build(probe, restoreClock: () => Interlocked.Add(ref fakeNow, 1000));
        var account = AzuriteAccount();
        var name = RandomName("rstextract-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Three small files in the same directory → a single pack, a single group: only one in-flight item, so the assertion doesn't have to filter by name.
            WriteSrc("dir/a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            WriteSrc("dir/c.txt", "charlie");
            await backup.RunAsync(BackupReq(account, name));

            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst },
                onProgress: d => probe.LatestPublished = d);

            Assert.Equal(3, result.RestoredFiles);
            Assert.True(probe.ExtractCallCount > 0, "fake compressor's ExtractAsync should have been invoked");
            // At the moment of extraction the fake clock has already guaranteed that the publish triggered by EndItem at the end of the download wasn't swallowed by the throttle —
            // what we get is the genuine in-flight set at the instant extraction began, not an old snapshot scooped up by luck.
            // Failing to capture a snapshot at all is itself a failure — this used to be expressed by seeding a non-empty sentinel set; now we just assert non-null, same meaning but more direct.
            Assert.NotNull(probe.ActiveItemsAtExtractCall);
            Assert.Empty(probe.ActiveItemsAtExtractCall);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>Wraps the real compressor, intercepting only at the <see cref="ExtractAsync"/> step to record the
    /// <see cref="StageProgress.ActiveItems"/> of the most recent publish at the moment of the call — the extraction itself is still delegated as usual to the inner real
    /// <see cref="SevenZipCompressor"/>; what is under test is only the "call ordering", not the extraction result.</summary>
    private sealed class ActiveItemsProbeCompressor(IFileCompressor inner) : IFileCompressor
    {
        public StageProgress? LatestPublished { get; set; }
        public IReadOnlyList<ActiveTransfer>? ActiveItemsAtExtractCall { get; private set; }
        public int ExtractCallCount { get; private set; }

        public Task<CompressionResult> CompressAsync(CompressionRequest request, CancellationToken ct = default) =>
            inner.CompressAsync(request, ct);

        public Task ExtractAsync(string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
        {
            ExtractCallCount++;
            ActiveItemsAtExtractCall = LatestPublished?.ActiveItems;
            return inner.ExtractAsync(firstVolumePath, outputDir, password, ct);
        }

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default) => inner.CompressStreamAsync(request, writeSource, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default) =>
            inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default) => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
    }

    [SkippableFact]
    public async Task Conflict_Skip_Leaves_Existing_File_Untouched()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstskip-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "cloud content");
            await backup.RunAsync(BackupReq(account, name));

            // The target already exists with different content → Skip mode should leave it exactly as is: no overwrite, nothing added.
            Directory.CreateDirectory(_dst);
            File.WriteAllText(Path.Combine(_dst, "a.txt"), "local content");

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
                Conflict = RestoreConflictMode.Skip,
            });

            Assert.Equal(0, result.RestoredFiles);
            Assert.Equal(1, result.SkippedFiles);
            Assert.Equal("local content", File.ReadAllText(Path.Combine(_dst, "a.txt"))); // not overwritten
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Conflict_RenameKeep_Preserves_Existing_And_Writes_Restored()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstrk-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "cloud content");
            await backup.RunAsync(BackupReq(account, name));

            // The target already exists with different content → RenameKeep: the old content is renamed and preserved, the restored content lands under the original name.
            Directory.CreateDirectory(_dst);
            File.WriteAllText(Path.Combine(_dst, "a.txt"), "local content");

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
                Conflict = RestoreConflictMode.RenameKeep,
            });

            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal("cloud content", File.ReadAllText(Path.Combine(_dst, "a.txt"))); // original name = the restored content
            var baks = Directory.GetFiles(_dst, "a.txt.bak-*");
            Assert.Single(baks);
            Assert.Equal("local content", File.ReadAllText(baks[0])); // the old content is never lost
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Restore_Skips_Index_Entry_That_Would_Escape_Target_Root()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstesc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Drop the threshold to 1 byte → a.txt goes through a single-file data blob (rather than a pack),
            // so that on restore the content is reused as a whole blob and doesn't depend on the entry name inside the archive —
            // only then does the malicious entry really write the content to the (escaping) path it declares for itself,
            // faithfully reproducing the writes a zero-trust index can trigger in the /import scenario.
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            // Simulate an index that was tampered with / came from an untrusted container (/import can import any container): append an entry
            // whose Path contains .., reusing a.txt's Storage (the same download group),
            // and verify the pre-write escape check blocks it rather than letting it land outside TargetRoot.
            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            idx.Entries.Add(aEntry with { Path = "../pwned.txt" });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.True(File.Exists(Path.Combine(_dst, "a.txt")));       // the normal entry restores as usual
            Assert.False(File.Exists(Path.Combine(_base, "pwned.txt"))); // the escaping entry was not written outside the target root
            Assert.Equal(1, result.FailedFiles);                         // counted as a failure, everything else proceeds, the whole restore is not aborted
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// C1 (Critical): restore creates symlink entries **first** and writes file entries **after**. One
    /// <c>evil -&gt; &lt;outside the root&gt;</c> entry plus one <c>evil/x</c> entry in the index, and the lexical check sees <c>&lt;root&gt;/evil/x</c> as entirely inside the root,
    /// while File.Copy follows that link and lands the content outside it — no race involved, purely restore's own ordering.
    /// Only a check operating on the resolved real path can stop it.
    /// </summary>
    [SkippableFact]
    public async Task Restore_Does_Not_Write_Through_A_Symlink_Entry_That_Points_Outside_The_Target()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstsym1-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);

        try
        {
            // Threshold 1 → a.txt goes through a single-file data blob and the content is copied wholesale to the path the entry declares,
            // so the malicious entry really does write the content where it says it should (faithfully reproducing the /import scenario).
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");

            // Malicious index: a symlink entry pointing at a directory outside the root + a file entry "underneath it".
            idx.Entries.Add(aEntry with { Path = "evil", Kind = "symlink", Target = outside, Storage = null });
            idx.Entries.Add(aEntry with { Path = "evil/x" });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            // Core assertion: nothing whatsoever may appear outside the target root.
            Assert.False(Path.Exists(Path.Combine(outside, "x")));
            Assert.Empty(Directory.GetFileSystemEntries(outside));

            Assert.Equal("safe content", File.ReadAllText(Path.Combine(_dst, "a.txt"))); // the legitimate entry proceeds as usual
            Assert.Equal(1, result.FailedFiles);                                          // the write-through-link is counted as a failure
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>C6: a symlink entry whose own path escapes (<c>../</c>) has to be blocked, and counted as a failure rather than silently skipped (C3).</summary>
    [SkippableFact]
    public async Task Restore_Rejects_Symlink_Entry_Whose_Own_Path_Escapes_The_Target_Root()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstsym2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);

        try
        {
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            idx.Entries.Add(aEntry with { Path = "../evil-link", Kind = "symlink", Target = outside, Storage = null });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var reports = new SyncProgress();
            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst },
                phase: reports);

            Assert.False(Path.Exists(Path.Combine(_base, "evil-link"))); // no link was created outside the root
            Assert.Equal(1, result.FailedFiles);                         // counted as a failure, not a skip
            Assert.True(File.Exists(Path.Combine(_dst, "a.txt")));

            // C3: a security check that fires has to be visible, it must not be as silent as "unchanged".
            Assert.Contains(reports.Messages, m => m.Contains("../evil-link", StringComparison.Ordinal));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// C6: the two escape routes for empty-directory entries — the lexical <c>../</c>, and passing through a symlink **left behind by the previous restore** that points outside the root.
    /// Also pins down C4: RestoredDirs reports the number that were actually created successfully.
    /// </summary>
    [SkippableFact]
    public async Task Restore_Skips_Empty_Dir_Entries_That_Would_Escape_Target_Root()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstdir-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);

        try
        {
            WriteSrc("a.txt", "safe content");
            Directory.CreateDirectory(Path.Combine(_src, "emptydir"));
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            Assert.Contains("emptydir", idx.EmptyDirs);
            idx.EmptyDirs.Add("../pwned-dir");   // lexical escape
            idx.EmptyDirs.Add("leftover/sub");   // escape by passing through an existing symlink
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            // A real symlink: simulates a link left inside the root by a previous restore (or by the user) that points outside it.
            Directory.CreateDirectory(_dst);
            Directory.CreateSymbolicLink(Path.Combine(_dst, "leftover"), outside);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.False(Directory.Exists(Path.Combine(_base, "pwned-dir")));
            Assert.False(Directory.Exists(Path.Combine(outside, "sub")));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
            Assert.True(Directory.Exists(Path.Combine(_dst, "emptydir")));
            Assert.Equal(1, result.RestoredDirs);  // C4: of the three entries, only one was created successfully
            Assert.Equal(2, result.FailedFiles);   // M1: the two escaping empty-directory entries have to count as failures too, not just be reported through phase
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>M1: with a malicious index containing nothing but escaping EmptyDirs, FailedFiles used to be frozen at 0 — the only signal was the phase stream.
    /// Same principle as symlink escapes (C3): a security check that fires has to count towards FailedFiles, because the operator's summary is what they actually look at.</summary>
    [SkippableFact]
    public async Task Restore_With_Only_Escaping_Empty_Dirs_Reports_Nonzero_FailedFiles()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstdironly-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            idx.EmptyDirs.Clear();               // leave only the malicious escaping entry, with no legitimate empty-directory entries at all
            idx.EmptyDirs.Add("../pwned-dir-only");
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.False(Directory.Exists(Path.Combine(_base, "pwned-dir-only")));
            Assert.Equal(0, result.RestoredDirs);
            Assert.Equal(1, result.FailedFiles); // the only entry is the escaping directory that got blocked — this must not be 0
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// C2: an escaping entry is blocked as early as the "does this need restoring" stage. It used to do a File.Exists +
    /// full hash on the out-of-root path first (an existence/content side channel), and when a file with identical content already existed outside the root it was judged "skipped" —
    /// so it never reached the check at the write site, counted as neither a failure nor a report, and a blocked escape was completely invisible.
    /// </summary>
    [SkippableFact]
    public async Task Escaping_Entry_Whose_Out_Of_Root_Twin_Exists_Is_Counted_As_Failed_Not_Skipped()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstorc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            idx.Entries.Add(aEntry with { Path = "../twin.txt" });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            // A "twin" file with identical content already exists outside the root → the old code would judge it "no restore needed" and count it as skipped.
            Directory.CreateDirectory(_base);
            File.WriteAllText(Path.Combine(_base, "twin.txt"), "safe content");

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.Equal(1, result.FailedFiles);
            Assert.Equal(0, result.SkippedFiles);
            Assert.Equal("safe content", File.ReadAllText(Path.Combine(_base, "twin.txt"))); // the out-of-root file was never touched
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// When the target root **itself** is reached through a symlink, restore has to keep working — the check resolves the root as well, otherwise every legitimate entry
    /// would be misjudged as an escape. This is the main regression risk of switching the check over to "resolved paths".
    /// </summary>
    [SkippableFact]
    public async Task Restore_Into_A_Target_Root_Reached_Through_A_Symlink_Still_Works()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstlnkroot-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        var real = Path.Combine(_base, "real-dst");
        var link = Path.Combine(_base, "link-dst");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, real); // a real symlink, not a mock

        try
        {
            WriteSrc("a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            Directory.CreateDirectory(Path.Combine(_src, "emptydir"));
            await backup.RunAsync(BackupReq(account, name));

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = link });

            Assert.Equal(0, result.FailedFiles);
            Assert.Equal(2, result.RestoredFiles);
            Assert.Equal(1, result.RestoredDirs);
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(real, "a.txt")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(real, "dir", "b.txt")));
            Assert.True(Directory.Exists(Path.Combine(real, "emptydir")));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// A legitimate backup faithfully records an **absolute** symlink pointing outside the root, and restoring it is the correct behavior (what is forbidden is only writing through a link).
    /// Restoring the same link again has to still be judged "unchanged"; it must not be judged an escape just because the final segment is already that link.
    /// </summary>
    [SkippableFact]
    public async Task Symlink_Entry_Targeting_Outside_The_Root_Restores_And_Second_Restore_Is_A_No_Op()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstsym3-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);

        try
        {
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            idx.Entries.Add(aEntry with { Path = "dir/link", Kind = "symlink", Target = outside, Storage = null });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var req = new RestoreRequest { Account = account, Container = name, TargetRoot = _dst };

            var first = await restore.RunAsync(req);
            Assert.Equal(0, first.FailedFiles);
            Assert.Equal(outside, new FileInfo(Path.Combine(_dst, "dir", "link")).LinkTarget);

            var second = await restore.RunAsync(req); // idempotent: the link is unchanged → skipped, not an escape failure
            Assert.Equal(0, second.FailedFiles);
            Assert.Equal(outside, new FileInfo(Path.Combine(_dst, "dir", "link")).LinkTarget);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// M3: a symlink entry missing its Target (a corrupt/tampered cloud index) used to be judged <c>SymlinkOutcome.Unchanged</c> —
    /// the same outcome as "the link is already correct, nothing happened", but a malformed entry has never been restored successfully, so the operator should see it,
    /// it should count towards FailedFiles, and it must not be quietly counted as Skipped under the guise of "unchanged".
    /// </summary>
    [SkippableFact]
    public async Task Restore_Symlink_Entry_With_Missing_Target_Fails_That_Entry_Not_Marked_Unchanged()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstmalsym-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "safe content");
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            // Malformed symlink entry: Kind=symlink but Target is missing.
            idx.Entries.Add(aEntry with { Path = "malformed-link", Kind = "symlink", Target = null, Storage = null });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var reports = new SyncProgress();
            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst },
                phase: reports);

            Assert.False(Path.Exists(Path.Combine(_dst, "malformed-link"))); // failed to restore, nothing was created
            Assert.Equal(1, result.FailedFiles); // counts as a failure, not SkippedFiles — "unchanged" semantics don't apply to "never succeeded in the first place"
            Assert.True(File.Exists(Path.Combine(_dst, "a.txt")));
            Assert.Contains(reports.Messages, m => m.Contains("malformed-link", StringComparison.Ordinal));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// M6: two entries in the index share the same Path (/import can import any container, and the index itself can be self-contradictory).
    /// <c>index.Entries.ToDictionary(e =&gt; e.Path, ...)</c> used to throw <see cref="ArgumentException"/> outright,
    /// aborting the whole restore — taking completely unrelated, completely normal entries like keep.txt down with it.
    /// Decision: the two contradict each other and there is no telling which is authoritative, so write neither (no guessing at last-wins/first-wins);
    /// that Path counts as exactly one failure, and the remaining entries restore as usual.
    /// </summary>
    [SkippableFact]
    public async Task Restore_With_Duplicate_Index_Entry_Path_Fails_That_One_Entry_Not_The_Whole_Run()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstdup2-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("a.txt", "safe content");
            WriteSrc("keep.txt", "unrelated file");
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "a.txt");
            // Duplicate Path: the index contradicts itself. The ToDictionary here used to throw outright and abort the whole restore.
            idx.Entries.Add(aEntry with { Path = "dup.txt" });
            idx.Entries.Add(aEntry with { Path = "dup.txt" });
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.False(File.Exists(Path.Combine(_dst, "dup.txt"))); // neither is written — no telling which one is authoritative
            Assert.True(File.Exists(Path.Combine(_dst, "a.txt")));    // the remaining entries weren't dragged down by an aborted restore
            Assert.True(File.Exists(Path.Combine(_dst, "keep.txt")));
            Assert.Equal(1, result.FailedFiles); // one duplicated Path counts as one failure, not once per duplicate entry
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// C5: a malformed entry (Path is "" or "." → the destination is TargetRoot itself) may only fail itself.
    /// A file entry used to fail the **whole group** of legitimate entries, and a symlink entry went further and made the **whole restore** throw and abort.
    /// </summary>
    [SkippableFact]
    public async Task Degenerate_Entry_Path_Fails_Only_That_Entry()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, store, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rstdeg-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Two small files in the same pack: if a malformed entry bubbles up to the group handler, it fails them along with it.
            WriteSrc("dir/a.txt", "alpha");
            WriteSrc("dir/b.txt", "bravo");
            await backup.RunAsync(BackupReq(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = info!.Versions[^1];
            var idx = await store.ReadIndexAsync(account, name, v1.IndexBlob, null);
            var aEntry = idx.Entries.Single(e => e.Path == "dir/a.txt");
            idx.Entries.Add(aEntry with { Path = "" });                                            // file entry → destination = the root (a directory)
            idx.Entries.Add(aEntry with { Path = ".", Kind = "symlink", Target = "/tmp", Storage = null }); // symlink entry → throws
            await store.WriteIndexAsync(account, name, v1.Version, idx, null);

            var result = await restore.RunAsync(new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.Equal(2, result.RestoredFiles); // all legitimate entries restored
            Assert.Equal(2, result.FailedFiles);   // the two malformed entries count one each
            Assert.Equal("alpha", File.ReadAllText(Path.Combine(_dst, "dir", "a.txt")));
            Assert.Equal("bravo", File.ReadAllText(Path.Combine(_dst, "dir", "b.txt")));
            Assert.True(Directory.Exists(_dst));   // the root is still a directory, not overwritten
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Restores_Encrypted_Backup_With_Password()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rste-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("secret.txt", "classified");
            await backup.RunAsync(BackupReq(account, name, password: "pw"));

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst, Password = "pw",
            });

            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal("classified", File.ReadAllText(Path.Combine(_dst, "secret.txt")));
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>The copy-not-rehydrate contract ("不是活化,而是直接复制一个副本来用"): restoring an archived family
    /// serves the download from disposable Hot copies under restore-tmp/, so the archived ORIGINALS are never
    /// touched — their tier (and 180-day early-deletion clock) survives the restore — and nothing is left
    /// under the temp prefix afterwards.</summary>
    [SkippableFact]
    public async Task An_Archived_Family_Restores_Via_Copies_And_The_Originals_Stay_Archived()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rst-arc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            var content = new byte[2_500_000];
            new Random(23).NextBytes(content);
            File.WriteAllBytes(Path.Combine(_src, "big.bin"), content);
            await backup.RunAsync(BackupReq(account, name) with
            {
                Options = new BackupEngineOptions
                {
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
                    VolumeBytes = 1_000_000,
                },
            });

            var dataVols = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                dataVols.Add(b.Name);
            foreach (var v in dataVols)
                await container.GetBlobClient(v).SetAccessTierAsync(Azure.Storage.Blobs.Models.AccessTier.Archive);

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
                RehydratePollSeconds = 1,
            });

            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_dst, "big.bin")));
            // The originals never left Archive.
            foreach (var v in dataVols)
            {
                var props = (await container.GetBlobClient(v).GetPropertiesAsync()).Value;
                Assert.Equal("Archive", props.AccessTier);
            }
            // And the temp directory is empty again — the prefix is the bookkeeping.
            var leftovers = new List<string>();
            await foreach (var b in container.GetBlobsAsync(
                Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "restore-tmp/", CancellationToken.None))
                leftovers.Add(b.Name);
            Assert.Empty(leftovers);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The blast-radius contract, pinned with a target no rename can replace: one entry whose
    /// destination is occupied by a DIRECTORY fails alone, and its pack companions land as usual.</summary>
    [SkippableFact]
    public async Task A_Destination_Occupied_By_A_Directory_Fails_Alone()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rst-dirocc-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            WriteSrc("d/a.txt", "alpha");
            WriteSrc("d/b.txt", "bravo");
            WriteSrc("d/c.txt", "charlie");
            await backup.RunAsync(BackupReq(account, name));

            Directory.CreateDirectory(Path.Combine(_dst, "d", "b.txt")); // a directory squatting on the file's path

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });

            Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(_dst, "d", "a.txt")));
            Assert.Equal("charlie", await File.ReadAllTextAsync(Path.Combine(_dst, "d", "c.txt")));
            Assert.Equal(2, result.RestoredFiles);
            Assert.Equal(1, result.FailedFiles);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The overwrite decision first reads whatever file already sits at the destination, to see whether it already equals the content to be restored. That read used to be unguarded,
    /// and once it threw it got caught by the **whole group's** catch — so not one other file in the same pack could be restored.
    /// One file's permission problem should not have a blast radius that large: it can fail on its own while its companions land as usual.
    /// <para>Three small files in the same directory → the same pack. An unreadable b.txt is placed at the destination beforehand (permission bits zeroed,
    /// not a fake exception thrown by a stand-in). Before the fix: the whole group failed and a and c never appeared in the destination directory at all.</para>
    /// </summary>
    [SkippableFact]
    public async Task An_Unreadable_Target_File_Does_Not_Sink_Its_Whole_Restore_Group()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rst-unread-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        var blocked = Path.Combine(_dst, "d", "b.txt");

        try
        {
            WriteSrc("d/a.txt", "alpha");
            WriteSrc("d/b.txt", "bravo");
            WriteSrc("d/c.txt", "charlie");
            await backup.RunAsync(BackupReq(account, name)); // default threshold 5MB → the three small files end up in one pack

            // A file with the same name already exists at the destination and can't be read — the very first step of the overwrite decision runs into it.
            Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
            await File.WriteAllTextAsync(blocked, "pre-existing and unreadable");
            File.SetUnixFileMode(blocked, UnixFileMode.None);

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });

            // The companions land as usual — before the blast-radius fix these two were never even touched.
            Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(_dst, "d", "a.txt")));
            Assert.Equal("charlie", await File.ReadAllTextAsync(Path.Combine(_dst, "d", "c.txt")));
            // The unreadable target itself now RESTORES: the verify-then-swap writer lands the content in a
            // sibling part file and renames it over the destination, and a rename needs only directory
            // permissions — the old File.Copy failed purely because it had to open the unreadable target for
            // writing. The blast-radius contract (one entry's failure must not sink its group) is pinned by
            // A_Destination_Occupied_By_A_Directory_Fails_Alone below with a target no rename can replace.
            File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal("bravo", await File.ReadAllTextAsync(blocked));
            Assert.Equal(3, result.RestoredFiles);
            Assert.Equal(0, result.FailedFiles);
        }
        finally
        {
            try { File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }

    /// <summary>Restore used to have only a free-text phase field, and what it actually carried was the **error stream**
    /// (Failed to restore… / Skipped unsafe…), overwritten as a single value: it could never say "how many groups are left",
    /// and per-file failures came down to just the last one. This verifies that structured progress really does get reported.</summary>
    [SkippableFact]
    public async Task Restore_Reports_Structured_Progress_For_Each_Group()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("rst-progress-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Three files in three different directories → three packs → three groups, so progress has several steps to report.
            WriteSrc("a/1.txt", "alpha");
            WriteSrc("b/2.txt", "bravo");
            WriteSrc("c/3.txt", "charlie");
            await backup.RunAsync(BackupReq(account, name));

            var snapshots = new List<StageProgress>();
            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst },
                onProgress: d => { lock (snapshots) snapshots.Add(d); });

            Assert.Equal(3, result.RestoredFiles);
            Assert.NotEmpty(snapshots);

            // The wrap-up has to produce a terminal state with every group complete, otherwise progress is forever one step short.
            var final = snapshots[^1];
            Assert.Equal(final.Total, final.Processed);
            Assert.Equal(100, final.Percent);
            Assert.True(final.Bytes > 0, "restored bytes should accumulate for the speed readout");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// A single-file blob streams straight from the archive to the destination, no longer landing a temporary extracted copy — so "is what got written correct"
    /// has to be verified here: when <c>7z x -so</c> can't get the content it produces empty output but **exit code 0** (this project has been bitten by that once already),
    /// and relying on "nothing was thrown" would pass an empty file or somebody else's content off as a successful restore and stamp it over the user's file.
    /// Here the data blob is swapped for another **legitimate** archive (different content and different length): the extraction step succeeds,
    /// and only the length/hash gate can stop it.
    /// </summary>
    [SkippableFact]
    public async Task Streamed_Blob_Whose_Content_Does_Not_Match_The_Index_Fails_That_Entry_And_Leaves_No_Debris()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, restore, _, factory) = Build();
        var account = AzuriteAccount();
        var good = RandomName("rst-mismatch-");
        var other = RandomName("rst-other-");
        var goodContainer = factory.CreateServiceClient(account).GetBlobContainerClient(good);
        var otherContainer = factory.CreateServiceClient(account).GetBlobContainerClient(other);
        await goodContainer.CreateIfNotExistsAsync();
        await otherContainer.CreateIfNotExistsAsync();

        try
        {
            var blobOnly = BackupReq(account, good) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            };

            WriteSrc("victim.txt", "the content the index describes");
            await backup.RunAsync(blobOnly);

            // A second backup produces a legitimate single-file 7z archive with different content; use it to displace the data blob of the one above.
            File.Delete(Path.Combine(_src, "victim.txt"));
            WriteSrc("decoy.txt", "a different payload of a different length entirely");
            await backup.RunAsync(blobOnly with { Container = other });

            var decoy = await FirstDataBlobAsync(otherContainer);
            var victim = await FirstDataBlobAsync(goodContainer);
            var bytes = (await otherContainer.GetBlobClient(decoy).DownloadContentAsync()).Value.Content;
            await goodContainer.GetBlobClient(victim).UploadAsync(bytes, overwrite: true);

            var phase = new SyncProgress();
            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = good, TargetRoot = _dst },
                phase: phase);

            Assert.Equal(0, result.RestoredFiles);
            Assert.Equal(1, result.FailedFiles);
            Assert.Contains(phase.Messages, m => m.Contains("victim.txt", StringComparison.Ordinal));

            // If the content doesn't match, not a single byte should land at the destination — no truncated file and no temp file left behind.
            Assert.False(File.Exists(Path.Combine(_dst, "victim.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "victim.txt.asb-part")));
        }
        finally
        {
            await goodContainer.DeleteIfExistsAsync();
            await otherContainer.DeleteIfExistsAsync();
        }
    }

    private static async Task<string> FirstDataBlobAsync(Azure.Storage.Blobs.BlobContainerClient container)
    {
        await foreach (var b in container.GetBlobsAsync(
            Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
            return b.Name;
        throw new InvalidOperationException("no data blob was produced by the backup");
    }

    /// <summary>
    /// When the progress callback breaks, the restore must not hang on it. The same-shaped test on the check side is
    /// <c>BackupCheckerTests.A_Broken_Progress_Sink_Does_Not_Wedge_The_Content_Check</c> —
    /// the two are independent, neither backstops the other.
    /// <para>
    /// <c>EndItem</c> calls straight into the caller's publish (external code that writes to the database, pushes SSE and the like), which can throw,
    /// and exceptions on this path are **deliberately** propagated. <c>gate.Release()</c> used to be the statement right after it in the same <c>finally</c>,
    /// so a throw from the first skipped it entirely — and the download permit was gone for good. There is only one permit here, so once the first group swallows it,
    /// the second group waits on the gate forever. That is why **the timeout itself is the failure**; what exception comes out doesn't matter.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_Broken_Progress_Sink_Does_Not_Wedge_The_Restore()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        // The fake clock makes every publish clear the 200ms throttle window; otherwise this test comes down to the luck of how long the download takes.
        long fakeNow = 0;
        var (backup, restore, _, factory) = Build(restoreClock: () => Interlocked.Add(ref fakeNow, 1000));
        var account = AzuriteAccount();
        var name = RandomName("rsink-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // One file in each of two directories → two packs, which is two restore groups; the gate hands out only one permit.
            WriteSrc("d1/a.txt", "alpha");
            WriteSrc("d2/b.txt", "bravo");
            await backup.RunAsync(BackupReq(account, name));

            var run = restore.RunAsync(
                new RestoreRequest
                {
                    Account = account, Container = name, TargetRoot = _dst, DownloadConcurrency = 1,
                },
                // Break only during the download/restore stage: if the whole sink were broken the restore would blow up in an earlier stage and never reach the gate.
                onProgress: d =>
                {
                    if (d.Stage == "Restoring")
                        throw new IOException("progress sink broke");
                });

            var ex = await Xunit.Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(20)));

            Assert.IsNotType<TimeoutException>(ex); // hung = the permit got swallowed
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>The source tree has to be on a case-sensitive filesystem for any of the collision tests to mean
    /// anything — on a folding one "A.txt" and "a.txt" are the same file and the scenario cannot even be set up.</summary>
    private void SkipUnlessSourceIsCaseSensitive() =>
        Skip.If(PathCaseSensitivity.IsCaseInsensitive(_src), "the source tree is on a case-folding filesystem");

    [SkippableFact]
    public async Task Case_Colliding_Paths_Are_Refused_When_The_Target_Folds_Case()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        SkipUnlessSourceIsCaseSensitive();

        // Only the target root folds; the extraction directory stays case-sensitive, so whatever is blocked here was
        // blocked by the target-side gate and not by the pack-side one.
        var (backup, restore, _, factory) = Build(caseProbe: dir => dir == _dst);
        var account = AzuriteAccount();
        var name = RandomName("rcase-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("A.txt", "upper content");
            WriteSrc("a.txt", "lower content");
            WriteSrc("plain.txt", "no twin");
            await backup.RunAsync(BackupReq(account, name));

            var progress = new SyncProgress();
            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst },
                CancellationToken.None, progress);

            // Neither side is written: with no way to tell which one would survive the merge, writing either is
            // writing one file's content under another file's name.
            Assert.False(File.Exists(Path.Combine(_dst, "A.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "a.txt")));
            Assert.Equal(2, result.FailedFiles);           // one failure per refused entry
            Assert.Equal("no twin", File.ReadAllText(Path.Combine(_dst, "plain.txt"))); // everything else proceeds
            Assert.Equal(1, result.RestoredFiles);

            var report = Assert.Single(progress.Messages, m => m.Contains("differ only in case"));
            Assert.Contains("A.txt, a.txt", report);       // both named, in a stable order
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Case_Colliding_Paths_Both_Restore_When_The_Target_Is_Case_Sensitive()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        SkipUnlessSourceIsCaseSensitive();

        // The no-regression guard: on a case-sensitive target the gate must not exist at all.
        var (backup, restore, _, factory) = Build(caseProbe: _ => false);
        var account = AzuriteAccount();
        var name = RandomName("rcases-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("A.txt", "upper content");
            WriteSrc("a.txt", "lower content");
            await backup.RunAsync(BackupReq(account, name));

            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.Equal("upper content", File.ReadAllText(Path.Combine(_dst, "A.txt")));
            Assert.Equal("lower content", File.ReadAllText(Path.Combine(_dst, "a.txt")));
            Assert.Equal(0, result.FailedFiles);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Selecting_One_Side_Of_A_Case_Collision_Restores_It()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        SkipUnlessSourceIsCaseSensitive();

        // Folding target, case-sensitive extraction directory — the ordinary shape of "restoring onto a Windows share
        // from the NAS". Selecting one side leaves a group of one, which has nothing to collide with.
        var (backup, restore, _, factory) = Build(caseProbe: dir => dir == _dst);
        var account = AzuriteAccount();
        var name = RandomName("rcasesel-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("A.txt", "upper content");
            WriteSrc("a.txt", "lower content");
            await backup.RunAsync(BackupReq(account, name));

            var result = await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst, SelectedPaths = ["A.txt"],
            });

            Assert.Equal("upper content", File.ReadAllText(Path.Combine(_dst, "A.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "a.txt")));
            Assert.Equal(1, result.RestoredFiles);
            Assert.Equal(0, result.FailedFiles);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Collision_Is_Judged_On_The_Whole_Path_Not_The_File_Name()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        SkipUnlessSourceIsCaseSensitive();

        var (backup, restore, _, factory) = Build(caseProbe: dir => dir == _dst);
        var account = AzuriteAccount();
        var name = RandomName("rcasedir-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // Same file name, different directories: no collision, this is the ordinary cross-directory case.
            WriteSrc("p/X.txt", "in p");
            WriteSrc("q/x.txt", "in q");
            // Same file name, and the directory segment is what differs only in case: this *is* a collision, and only
            // keying on the full relative path catches it.
            WriteSrc("d/X/f.txt", "under upper d");
            WriteSrc("d/x/f.txt", "under lower d");
            await backup.RunAsync(BackupReq(account, name));

            var result = await restore.RunAsync(
                new RestoreRequest { Account = account, Container = name, TargetRoot = _dst });

            Assert.Equal("in p", File.ReadAllText(Path.Combine(_dst, "p", "X.txt")));
            Assert.Equal("in q", File.ReadAllText(Path.Combine(_dst, "q", "x.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "d", "X", "f.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "d", "x", "f.txt")));
            Assert.Equal(2, result.RestoredFiles);
            Assert.Equal(2, result.FailedFiles);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Pack_Member_With_A_Case_Twin_Is_Refused_When_The_Extraction_Directory_Folds()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");
        SkipUnlessSourceIsCaseSensitive();

        // Everything folds, including the extraction directory. Selecting one side gets past the target-side gate, but
        // inside the pack the twin has already overwritten it during extraction, so the extracted copy cannot be
        // trusted and this entry has to fail rather than hand over content that may belong to the other file.
        var (backup, restore, _, factory) = Build(caseProbe: _ => true);
        var account = AzuriteAccount();
        var name = RandomName("rcasetmp-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteSrc("A.txt", "upper content");   // small files → both land in the same pack
            WriteSrc("a.txt", "lower content");
            await backup.RunAsync(BackupReq(account, name));

            var progress = new SyncProgress();
            var result = await restore.RunAsync(
                new RestoreRequest
                {
                    Account = account, Container = name, TargetRoot = _dst, SelectedPaths = ["A.txt"],
                },
                CancellationToken.None, progress);

            Assert.False(File.Exists(Path.Combine(_dst, "A.txt")));
            Assert.Equal(0, result.RestoredFiles);
            Assert.Equal(1, result.FailedFiles);
            Assert.Contains(progress.Messages, m =>
                m.Contains("A.txt") && m.Contains("differs only in case"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
