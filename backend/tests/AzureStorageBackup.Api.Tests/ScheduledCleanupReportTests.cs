using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The scheduled cleanup task used to do its work without saying a word: it retired versions, deleted packs, deleted data blobs, and
/// reported none of it. The cleanup at the tail of a backup now writes what it deleted into the success summary, and the standalone run
/// has no reason to be any quieter — in an unattended deployment the operation log is the only place an operator can go back and check
/// "how much space did retention actually free up last month".
/// <para>Conversely, when nothing was cleaned up it must not write a single word: a nightly "retired 0 version(s)" turns this signal into
/// background noise fast, and the fact that the task really did run can be looked up in the task run records instead.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScheduledCleanupReportTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private readonly string _root;
    private readonly string _temp;

    public ScheduledCleanupReportTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-schedclean-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    private sealed class RootedFactory(string root) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Backup:Root", root);
        }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private static readonly DateTime MtimeBase = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private int _mtimeSeq;

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, MtimeBase.AddMinutes(++_mtimeSeq));
    }

    private static Account AzuriteAccount() => new()
    {
        Name = "azurite",
        BlobEndpoint = AzuriteEndpoint,
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    /// <summary>
    /// Builds its own orchestrator to produce the versions, using the **default** retention policy (100 versions / 180 days) — so these
    /// backup runs never sweep old versions away on the side; retiring is left to the scheduled cleanup that follows, which is the dispatch branch actually under test.
    /// </summary>
    private BackupOrchestrator BuildOrchestrator()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var authority = new TestLocalAuthority(store);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), indexCache: authority.IndexCache, trackedInfo: authority.Tracked), new FileHasher(), authority.IndexCache, authority.Tracked);
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "sched-cleanup",
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 20_000 } },
    };

    [SkippableFact]
    public async Task Scheduled_Cleanup_Logs_What_It_Deleted_And_Stays_Silent_When_It_Deletes_Nothing()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        using var factory = new RootedFactory(Path.GetDirectoryName(_root)!);
        var client = factory.CreateClient();

        var acct = await (await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "azurite", Description: null, BlobEndpoint: AzuriteEndpoint,
            Region: AzureRegion.Global, AccountKey: AzuriteKey,
            UseProxy: false, ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null)))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var container = RandomName("schedclean-");
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var cc = blobFactory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(container);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // A config that keeps only 1 version — retiring is triggered by the scheduled cleanup, not done on the side by the backup itself.
            using (var scope = factory.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().CreateAsync(new BackupConfig
                {
                    AccountId = acct!.Id,
                    ContainerName = container,
                    Name = "sched-cleanup",
                    LocalRoot = _root,
                    MaxVersions = 1,
                    RetentionMode = RetentionMode.VersionOnly,
                });
            }

            var orchestrator = BuildOrchestrator();
            Write("big.bin", new string('a', 60_000));    // > threshold → single-file data blob
            Write("small.txt", new string('a', 5_000));   // < threshold → grouped into a pack
            await orchestrator.RunAsync(Request(AzuriteAccount(), container));
            Write("big.bin", new string('b', 60_000));
            Write("small.txt", new string('b', 5_000));
            await orchestrator.RunAsync(Request(AzuriteAccount(), container));

            var task = new ScheduledTask
            {
                TargetKind = TaskTargetKind.Backup,
                AccountId = acct!.Id,
                ContainerName = container,
                TaskType = ScheduledTaskType.Cleanup,
                CronExpression = "* * * * *",
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            };

            var dispatcher = factory.Services.GetRequiredService<TaskDispatcher>();
            await dispatcher.DispatchAsync(task, CancellationToken.None);

            var source = $"schedule:{acct.Id}/{container}";
            var afterFirst = await ReadLogAsync(factory, source);
            var report = Assert.Single(afterFirst, e => e.Message.Contains("Retention"));
            Assert.Contains("1 version(s)", report.Message);
            // Objects from both storage paths should be counted and the freed amount must be non-zero — otherwise this line is a placeholder with nothing in it.
            Assert.Contains("pack(s)", report.Message);
            Assert.Contains("blob(s)", report.Message);
            Assert.DoesNotContain("freed 0 B", report.Message);

            // Run it once more: there is no version left to retire, so not one further cleanup log line may be written.
            await dispatcher.DispatchAsync(task, CancellationToken.None);

            var afterSecond = await ReadLogAsync(factory, source);
            Assert.Single(afterSecond, e => e.Message.Contains("Retention"));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    private static async Task<IReadOnlyList<LogEntry>> ReadLogAsync(TestWebAppFactory factory, string source)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOperationLog>()
            .QueryAsync(null, source, null, null, 100, CancellationToken.None);
    }
}
