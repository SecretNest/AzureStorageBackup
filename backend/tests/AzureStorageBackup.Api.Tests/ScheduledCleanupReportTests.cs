using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 定时清理任务此前是只做不说的：它退役版本、删 pack、删 data blob，删完一声不吭。备份收尾那次
/// 清理现在会把删掉的东西写进成功摘要，独立跑的这一次没有理由更沉默——无人值守部署下，操作日志
/// 是操作员唯一能回头查"上个月保留策略到底腾出了多少空间"的地方。
/// <para>反过来，什么都没清掉时必须一个字都不写：每晚一条 "retired 0 version(s)" 会让这条信号
/// 迅速变成背景噪音，而任务确实跑过了这件事另有任务运行记录可查。</para>
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
    /// 自己搭一台编排器造版本，用的是**默认**保留策略（100 版 / 180 天）——这几轮备份因此绝不会
    /// 顺手把旧版本清掉，退役留给后面那次定时清理去做，测的才是那条分发分支。
    /// </summary>
    private BackupOrchestrator BuildOrchestrator()
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator()), new FileHasher());
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
            // 只留 1 个版本的配置——退役由定时清理触发，而不是备份自己顺手做掉。
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
            Write("big.bin", new string('a', 60_000));    // > 阈值 → 单文件 data blob
            Write("small.txt", new string('a', 5_000));   // < 阈值 → 成组进 pack
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
            // 两条存储路径的对象都该被数出来，且释放量非零——否则这行就是个没内容的占位。
            Assert.Contains("pack(s)", report.Message);
            Assert.Contains("blob(s)", report.Message);
            Assert.DoesNotContain("freed 0 B", report.Message);

            // 再跑一次：已经没有可退役的版本了，不得再写任何一条清理日志。
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
