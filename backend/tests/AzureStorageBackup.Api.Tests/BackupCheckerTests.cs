using System.Net.Sockets;
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

    /// <param name="checkCompressor">检查侧注入的压缩器，默认 null 时用真的 <see cref="SevenZipCompressor"/>。
    /// 这个口子只为了让某些测试在**解压/算 hash**这一步接一个假的（例如探测"这一刻在途标记是否
    /// 已经摘掉"，见 <see cref="RestoreOrchestratorTests"/> 里同形状的口子），不影响打包过程本身。</param>
    /// <param name="checkerClock">检查侧注入给内部 <see cref="StageTracker"/> 的时间源，见
    /// <see cref="BackupChecker.Clock"/> 上的注释——只为让节流窗口失效，不影响下载/解压本身。</param>
    private (BackupOrchestrator Backup, BackupChecker Checker, BlobClientFactory Factory) Build(
        IFileCompressor? checkCompressor = null, Func<long>? checkerClock = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging, new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher());
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
            Path.Combine(_temp, "repair"), checker: checker);
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

            // blob 仍在但被改成不同尺寸（模拟截断/错包）——本地文件未动。
            await foreach (var b in container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, "data/", CancellationToken.None))
                await container.GetBlobClient(b.Name).UploadAsync(BinaryData.FromString("x"), overwrite: true);

            var report = await checker.CheckAsync(account, name, null, null, new CheckOptions(), _src);

            var f = report.Findings.Single(x => x.Path == "a.txt");
            Assert.Equal(CloudState.MissingOrBad, f.Cloud); // 尺寸不符 → 云端坏
            Assert.Equal(LocalState.Ok, f.Local);           // 本地内容一致
            Assert.True(f.Repairable);                       // 可从本地修复
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

            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "locally edited"); // 本地改动

            // 只查本地内容（云端不查）。
            var report = await checker.CheckAsync(
                account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.None, Local = LocalCheckLevel.Content }, _src);

            var f = report.Findings.Single(x => x.Path == "a.txt");
            Assert.Equal(LocalState.Changed, f.Local);
            Assert.Equal(CloudState.NotChecked, f.Cloud);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>本地文件存在却读不出来时，整轮检查此前会崩掉——而"有文件读不开"恰恰是最需要跑检查
    /// 的时候：备份刚跳过了它，操作员正想知道云端那份还在不在。读不开一律当 Missing（本地拿不出
    /// 可用副本，也不能当修复来源），与"越界""文件不在"的既有处置一致，且检查必须跑完。</summary>
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

            File.SetUnixFileMode(locked, UnixFileMode.None); // 备份之后才读不开

            var report = await checker.CheckAsync(
                account, name, null, null,
                new CheckOptions { Cloud = CloudCheckLevel.None, Local = LocalCheckLevel.Content }, _src);

            var f = report.Findings.Single(x => x.Path == "locked.txt");
            Assert.Equal(LocalState.Missing, f.Local); // 读不开 == 本地拿不出可用副本
            Assert.False(f.Repairable);                 // 更不能拿它去"修复"云端

            // 关键：检查跑完了，同一轮里其余文件照常得到结论。
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
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync(); // 云端 blob 丢失

            var report = await Repairer(factory, checker).RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null);

            Assert.Contains("a.txt", report.Repaired);
            Assert.Empty(report.Unrecoverable);

            // 修复后内容检查通过。
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
                await container.GetBlobClient(b.Name).DeleteIfExistsAsync(); // 云端丢失
            File.Delete(Path.Combine(_src, "a.txt"));                        // 本地也没了 → 无法修复

            var report = await Repairer(factory, checker).RepairAsync(
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null);

            Assert.Contains("a.txt", report.Unrecoverable);
            Assert.Empty(report.Repaired);

            // 版本索引里标记为不可恢复。
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

            // 删除引用的 pack（小文件 a.txt 进 p0001）
            await container.GetBlobClient("packs/p0001.7z").DeleteIfExistsAsync();

            var result = await checker.CheckAsync(account, name, null, null, new CheckOptions());

            Assert.False(result.Ok);
            Assert.Contains("packs/p0001.7z", result.MissingRefs);
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
            // 6MB 随机文件 → 单文件 data blob，1MB 分卷 → 多卷 data/{hash}.001/.002...
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
            // 完整时通过。
            Assert.True((await checker.CheckAsync(account, name, null, null, new CheckOptions())).Ok);

            // 删一个中间分卷 → 按索引记录的分卷数核验应报缺失（旧 base-or-.001 检查会漏报）。
            await container.GetBlobClient($"data/{hash}.002").DeleteIfExistsAsync();
            var result = await checker.CheckAsync(account, name, null, null, new CheckOptions());

            Assert.False(result.Ok);
            Assert.Contains($"data/{hash}", result.MissingRefs);
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
            // v1：小文件 a.txt → pack p0001（引用），大文件 big.bin → 多卷 data blob（引用）。
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

            // 手动往 container 塞真孤儿 + 残余旧卷（模拟非原子替换/失败上传遗留）。
            await container.GetBlobClient("data/ZZZ").UploadAsync(BinaryData.FromString("garbage"), overwrite: true);
            await container.GetBlobClient("packs/p0001.7z.099").UploadAsync(BinaryData.FromString("stale pack volume"), overwrite: true);
            await container.GetBlobClient($"data/{hash}.099").UploadAsync(BinaryData.FromString("stale data volume"), overwrite: true);

            // 列表检查：报告恰好这些孤儿；被引用/信息/索引不在孤儿中。
            var check = await checker.CheckAsync(account, name, null, null, new CheckOptions { ListOrphans = true }, _src);
            Assert.Contains("data/ZZZ", check.OrphanBlobs);
            Assert.Contains("packs/p0001.7z.099", check.OrphanBlobs);
            Assert.Contains($"data/{hash}.099", check.OrphanBlobs);
            Assert.DoesNotContain("packs/p0001.7z", check.OrphanBlobs);
            Assert.DoesNotContain($"data/{hash}.001", check.OrphanBlobs);
            Assert.DoesNotContain(BackupDiscovery.IndexBlobName, check.OrphanBlobs);
            Assert.True(check.Ok); // 孤儿不影响 Ok

            // 修复删孤儿（cleanupOrphans）：即便无坏 blob 也执行删除。
            var report = await Repairer(factory, checker).RepairAsync(
                account, name, null, _src, null,
                new CheckOptions { ListOrphans = true }, Azure.Storage.Blobs.Models.AccessTier.Hot, null,
                dontCompress: null);

            Assert.Contains("data/ZZZ", report.DeletedOrphans);
            Assert.Contains("packs/p0001.7z.099", report.DeletedOrphans);
            Assert.Contains($"data/{hash}.099", report.DeletedOrphans);

            // 孤儿已删。
            Assert.False((await container.GetBlobClient("data/ZZZ").ExistsAsync()).Value);
            Assert.False((await container.GetBlobClient("packs/p0001.7z.099").ExistsAsync()).Value);
            Assert.False((await container.GetBlobClient($"data/{hash}.099").ExistsAsync()).Value);
            // 被引用 blob + 信息文件仍在。
            Assert.True((await container.GetBlobClient("packs/p0001.7z").ExistsAsync()).Value);
            Assert.True((await container.GetBlobClient($"data/{hash}.001").ExistsAsync()).Value);
            Assert.True((await container.GetBlobClient(BackupDiscovery.IndexBlobName).ExistsAsync()).Value);

            // 修复后备份仍完好。
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

            // 用垃圾覆盖 pack blob（存在但解不开）→ 深度校验报损坏
            await container.GetBlobClient("packs/p0001.7z").UploadAsync(BinaryData.FromString("garbage"), overwrite: true);

            var result = await checker.CheckAsync(account, name, null, null, new CheckOptions { Cloud = CloudCheckLevel.Content });

            Assert.False(result.Ok);
            Assert.Contains("a.txt", result.CorruptedPaths);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>检查的进度上报。改成后台 job 之后这是界面上唯一能看到的东西——一次内容级
    /// 检查要把整个备份下载重算 hash，可以跑几小时，没有进度就与卡死无从区分。</summary>
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
                _src, CancellationToken.None,
                onProgress: d => { lock (reports) reports.Add(d); });

            Assert.True(result.Ok);

            // 每个阶段都要露面：改之前一个都没有，界面上只有一个不动的 "Checking" 徽章。
            var stages = reports.Select(r => r.Stage).Distinct().ToList();
            Assert.Contains("LoadingIndex", stages);
            Assert.Contains("Cloud", stages);
            Assert.Contains("Verifying", stages);
            Assert.Contains("Local", stages);

            // 本地阶段总数已知（就是索引里的条目数）→ 必须走到 100%，且报得出在查哪个文件。
            var local = reports.Where(r => r.Stage == "Local").ToList();
            Assert.Equal(12, local[^1].Total);
            Assert.Equal(12, local[^1].Processed);
            Assert.Equal(100, local[^1].Percent);
            Assert.Contains(local, r => !string.IsNullOrEmpty(r.CurrentItem));

            // 深度校验现在按**下载**的字节边传边计（VolumeBlobIO.DownloadAsync 挂了
            // ProgressHandler）：在途窗口只覆盖下载，解压、重算 hash 都在窗口之外，
            // 所以这里只断言字节确实在累计，具体"下载字节≠成员原始大小"由下面
            // Deep_Verify_Credits_Downloaded_Compressed_Bytes_Not_Uncompressed_Member_Sizes 测试钉死。
            var verifying = reports.Where(r => r.Stage == "Verifying").ToList();
            Assert.NotEmpty(verifying);
            Assert.True(verifying[^1].Bytes > 0, "verified bytes should accumulate for the speed readout");

            // 槽位计数恰好一次：在途项的起止不得参与计数（否则会越过 total）。
            Assert.All(verifying, r => Assert.True(r.Processed <= r.Total));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 用户实际会看到的症状（修复前）：校验一个体积很小但压缩率极高的归档（比如整段重复字符的
    /// 大文件）时，速度读数会先按"成员未压缩大小 / 10s"报出一个远超真实网速的数字，随后又跌回 0——
    /// 因为 EndItem 收尾时把整组成员的**原始**字节一次性入账，而真正花在网线上的时间其实很短。
    /// <para>
    /// 这里直接钉死"最终字节数"这一个更硬的不变量：它必须等于云端归档的真实（压缩后）大小，
    /// 而不是原始文件的大小——高压缩比让两者差出两个数量级，任何一处退回"按成员大小计"
    /// 都会让这个断言当场炸掉，比断言"大于 0"更能防回归。
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
            // 同一字符重复两百万次：7z 压缩比极高，压缩后的归档比原始内容小至少一个数量级，
            // 足以把"下载字节"和"成员原始字节"两个数字撑出肉眼可见（也是断言可辨）的差距。
            var big = new string('a', 2_000_000);
            await File.WriteAllTextAsync(Path.Combine(_src, "big.txt"), big);
            await backup.RunAsync(Req(account, name) with
            {
                Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 1 } },
            });

            // 云端归档的真实大小——下载时真正传过网线的字节数，即本次改动之后 Verifying 阶段
            // 应该累计到的数字。
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
                _src, CancellationToken.None,
                onProgress: d => { lock (reports) reports.Add(d); });

            Assert.True(result.Ok);

            var verifying = reports.Where(r => r.Stage == "Verifying").ToList();
            Assert.NotEmpty(verifying);
            Assert.Equal(archivedBytes, verifying[^1].Bytes);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 钉住 VerifyGroupAsync 里那对内层 try/finally：下载一结束就把 <c>EndItem</c> 摘掉，
    /// 解压/算 hash 这段本地 CPU 工作不该继续算"在途"。与
    /// <see cref="RestoreOrchestratorTests.Extraction_Starts_After_Item_Is_Removed_From_ActiveItems"/>
    /// 同一形状的镜像件——两处结构几乎一致（都是"BeginItem 之后拿闸门、下载、EndItem、再解压"），
    /// 此前只有还原侧有测试守着，检查侧被判定"结构相同、不值得再测一遍"；这条补齐它，
    /// 检查侧从此有自己的锚，不必再借还原侧的测试当担保。
    /// <para>
    /// 直接读 onProgress 收到的"最近一次发布"同样靠不住（原因见还原侧那条测试上的详细说明）：
    /// 发布有 200ms 节流，真实时钟下载一个几十字节的测试包全程往往就几十毫秒，EndItem 触发的
    /// 发布多半被同一个节流窗口吞掉，无论 fix 还是 mutant 都可能读到"下载中"的旧快照。
    /// 用注入的假时钟绕开它：每查一次时间就往前跳一大步，节流条件永远不成立，每一次状态变化
    /// 都会被发布——不涉及 Thread.Sleep/Task.Delay，下载/解压仍是对 Azurite 的真实调用，
    /// 只是"现在几点"这一件事被接管了。
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
            // 三个小文件同目录 → 单个 pack、单个组：只有一件在途项，断言不必按名字过滤。
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
            // 解压这一刻，假时钟已经保证了下载结束时那次 EndItem 触发的发布没被节流吞掉——
            // 拿到的就是解压开始那一瞬间真正的在途集合，不是碰运气捞到的一张旧快照。
            Assert.Empty(probe.ActiveItemsAtExtractCall ?? ["<no snapshot captured>"]);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>包一层真压缩器，只在 <c>ExtractToStreamAsync</c> 这一步截住，记下调用那一刻
    /// 最近一次发布的 <see cref="StageProgress.ActiveItems"/>——解压本身仍然照常委托给内层真的
    /// <see cref="SevenZipCompressor"/> 完成，被测的只是"调用顺序"，不是解压结果。检查侧的深度
    /// 校验走 <c>ExtractToStreamAsync</c>（不落盘的流式路径），与
    /// <see cref="RestoreOrchestratorTests.ActiveItemsProbeCompressor"/> 探的 <c>ExtractAsync</c>
    /// 不是同一个方法——两边各探各自真正会调用的那一个。</summary>
    private sealed class ActiveItemsProbeCompressor(IFileCompressor inner) : IFileCompressor
    {
        public StageProgress? LatestPublished { get; set; }
        public IReadOnlyList<string>? ActiveItemsAtExtractCall { get; private set; }
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
