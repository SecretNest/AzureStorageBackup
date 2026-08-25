using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// A check has two axes, and the sentinel only speaks for one of them. The cloud copy is still there and still
/// worth verifying when the source is not mounted; the local comparison is not — every file would come back
/// Missing, which is the same false alarm the backup gate exists to prevent, only rendered as a check failure
/// instead of a deleted version. So the local axis is demoted and the cloud axis is left alone.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SentinelCheckTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _base;
    private readonly string _src;
    private readonly string _temp;

    public SentinelCheckTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-sent-chk-" + Guid.NewGuid().ToString("N"));
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

    private (BackupOrchestrator Backup, BackupChecker Checker, BlobClientFactory Factory) Build()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(Path.Combine(_temp, "c"), Path.Combine(_temp, "s"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
        var checker = new BackupChecker(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"));
        return (backup, checker, factory);
    }

    private BackupRequest Req(Account a, string c) => new()
    {
        Account = a, Container = c, LocalRoot = _src, Name = "photos",
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 } },
    };

    [SkippableFact]
    public async Task A_Missing_Sentinel_Demotes_The_Local_Axis_And_Leaves_The_Cloud_Axis_Alone()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = "sent-chk-" + Guid.NewGuid().ToString("N")[..8];
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            await backup.RunAsync(Req(account, name));

            // The source is still on disk and would pass a local check perfectly well. The sentinel is what says
            // it must not be trusted — proving the demotion is driven by the sentinel and nothing else.
            var report = await checker.CheckAsync(
                account, name, null, null, new CheckOptions(), _src,
                sentinelPath: Path.Combine(_base, "not-mounted"));

            var finding = report.Findings.Single(f => f.Path == "a.txt");
            Assert.Equal(LocalState.NotChecked, finding.Local);
            // The cloud half ran in full, and a cloud-only pass is still a pass.
            Assert.Equal(CloudState.Ok, finding.Cloud);
            Assert.True(report.Ok);
            // Carried on the report, not left for the caller to infer from a column of NotChecked — the same
            // lesson OrphansChecked records: the dialog is gone by the time anyone reads this.
            Assert.Contains("not-mounted", report.LocalSkippedSentinel);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task A_Present_Sentinel_Leaves_The_Local_Axis_Running()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = "sent-chk2-" + Guid.NewGuid().ToString("N")[..8];
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            var file = Path.Combine(_src, "a.txt");
            await File.WriteAllTextAsync(file, "alpha");
            await backup.RunAsync(Req(account, name));

            var report = await checker.CheckAsync(
                account, name, null, null, new CheckOptions(), _src, sentinelPath: file);

            var finding = report.Findings.Single(f => f.Path == "a.txt");
            Assert.Equal(LocalState.Ok, finding.Local);
            Assert.Null(report.LocalSkippedSentinel);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task No_Sentinel_Leaves_The_Local_Axis_Running()
    {
        // Every backup that predates the feature passes null here, and must check exactly as it always did.
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (backup, checker, factory) = Build();
        var account = AzuriteAccount();
        var name = "sent-chk3-" + Guid.NewGuid().ToString("N")[..8];
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_src, "a.txt"), "alpha");
            await backup.RunAsync(Req(account, name));

            var report = await checker.CheckAsync(account, name, null, null, new CheckOptions(), _src);

            Assert.Equal(LocalState.Ok, report.Findings.Single(f => f.Path == "a.txt").Local);
            Assert.Null(report.LocalSkippedSentinel);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
