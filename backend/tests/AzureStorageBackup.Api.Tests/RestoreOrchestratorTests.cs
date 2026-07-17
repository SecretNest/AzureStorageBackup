using System.Net.Sockets;
using System.Text;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

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

    private void WriteSrc(string rel, string content)
    {
        var full = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store, BlobClientFactory Factory) Build()
    {
        var factory = new BlobClientFactory();
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), 200_000_000);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging, new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher());
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"));
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
            WriteSrc("dir/small.txt", "grouped");       // pack 成员
            WriteSrc("big.bin", new string('y', 6_000_000)); // 密钥化寻址的单文件 data blob

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
                    DontCompress = new IgnoreRuleSet(["*"]), // store-only → 原始直传
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
                    // 阈值调低 → big.bin 走单文件 blob；不压缩 + 20KB 分卷 → 多卷
                    Plan = new PlanOptions { SingleFileThresholdBytes = 1000 },
                    DontCompress = new IgnoreRuleSet(["*.bin"]),
                    VolumeBytes = 20_000,
                },
            };
            await backup.RunAsync(req);

            // 应产出多卷 data blob（data/{hash}.001 存在）
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

            var second = await restore.RunAsync(req); // 本地已相同 → 全部跳过
            Assert.Equal(0, second.RestoredFiles);
            Assert.Equal(2, second.SkippedFiles);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
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
}
