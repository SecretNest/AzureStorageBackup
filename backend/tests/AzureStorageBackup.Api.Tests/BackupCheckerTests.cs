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
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging, new RetentionCleaner(factory, store, new RetentionEvaluator()));
        return (backup, new BackupChecker(factory, store), factory);
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

            var result = await checker.CheckAsync(account, name, null, null);

            Assert.True(result.Ok);
            Assert.True(result.CheckedRefs >= 1);
            Assert.Empty(result.MissingRefs);
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

            var result = await checker.CheckAsync(account, name, null, null);

            Assert.False(result.Ok);
            Assert.Contains("packs/p0001.7z", result.MissingRefs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
