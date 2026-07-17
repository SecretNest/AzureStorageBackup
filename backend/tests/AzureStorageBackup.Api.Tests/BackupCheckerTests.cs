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
        AccountKey = AzuriteKey,
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private (BackupOrchestrator Backup, BackupChecker Checker, BlobClientFactory Factory) Build()
    {
        var factory = new BlobClientFactory();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), 200_000_000);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging, new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher());
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"));
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
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null);

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
                account, name, null, _src, null, new CheckOptions(), Azure.Storage.Blobs.Models.AccessTier.Hot, null);

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
}
