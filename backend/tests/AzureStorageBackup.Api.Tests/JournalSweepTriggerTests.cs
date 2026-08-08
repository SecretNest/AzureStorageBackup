using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 孤儿扫描是**谁**要求的：两个调用点各自那一个布尔值。
/// <para>
/// <see cref="RetentionCleaner"/> 自己的判据另有 <c>RetentionCleanerJournalTests</c> 逐项钉着，
/// 而那些用例一律直接把 <c>sweepOrphans: true</c> 递进去——于是"生产里到底有没有人递真"这件事
/// 一个字都没被钉住：把 <see cref="TaskDispatcher"/> 那句改成 <c>false</c>、把编排器收尾那句
/// 改成常量，全套用例照样绿，而线上的表现是取消/崩溃留下的块永远无人收。
/// 这几条用例走的都是真调用点，不碰 <c>sweepOrphans</c> 这个参数本身。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class JournalSweepTriggerTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";
    private const int ConfigId = 9;

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public JournalSweepTriggerTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-sweeptrig-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
        _journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount(int id = 46) => new()
    {
        Id = id,
        Name = "azurite",
        BlobEndpoint = AzuriteEndpoint,
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

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>造一个谁都不引用的 data blob——取消/崩溃在容器里留下的正是这种东西。</summary>
    private static async Task PlantOrphanAsync(BlobContainerClient container)
        => await container.GetBlobClient("data/orphan").UploadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("nobody references me")), overwrite: true);

    private static async Task<bool> OrphanExistsAsync(BlobContainerClient container)
        => (await container.GetBlobClient("data/orphan").ExistsAsync()).Value;

    /// <summary>
    /// 一台编排器。<paramref name="authority"/> 由调用方持有，好让同一份"本地权威状态"跨轮沿用——
    /// 每轮各造一份的话，每一轮都会被当成第一轮（见 <see cref="A_first_run_sweeps_what_the_deleted_config_left_behind"/>）。
    /// </summary>
    private BackupOrchestrator BuildOrchestrator(TestLocalAuthority authority, BackupInfoStore store)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(),
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked, journals: _journals),
            new FileHasher(), authority.IndexCache, authority.Tracked);
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "sweep-trigger",
        // 默认保留策略（100 版 / 180 天）：这几轮备份一个版本都不会退役，所以清理器
        // 只剩"有没有人要求扫"这一个理由能动手——正是这几条用例要测的东西。
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 20_000 } },
    };

    private sealed class RootedFactory(string root) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Backup:Root", root);
        }
    }

    /// <summary>
    /// 定时 Cleanup 任务永远扫孤儿。这条路是那批块**唯一**的兜底：备份自己那次收尾清理只在
    /// 采纳/作废/第一轮时才扫，用户若一直不再动那份备份，就只剩这里会来收。
    /// </summary>
    [SkippableFact]
    public async Task The_scheduled_cleanup_task_always_sweeps()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        using var app = new RootedFactory(Path.GetDirectoryName(_root)!);
        var client = app.CreateClient();

        var acct = await (await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "azurite", Description: null, BlobEndpoint: AzuriteEndpoint,
            Region: AzureRegion.Global, AccountKey: AzuriteKey,
            UseProxy: false, ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null)))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var name = RandomName("sweeptrig-");
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var container = blobFactory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            using (var scope = app.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().CreateAsync(new BackupConfig
                {
                    AccountId = acct!.Id,
                    ContainerName = name,
                    Name = "sweep-trigger",
                    LocalRoot = _root,
                });
            }

            // 先有一个版本：没有任何已提交版本的容器，独立清理那条路会直接返回（判据的一半读不出来）。
            var store = new BackupInfoStore(blobFactory, new SevenZipArchiveCodec());
            Write("big.bin", new string('a', 60_000));
            await BuildOrchestrator(new TestLocalAuthority(store), store)
                .RunAsync(Request(AzuriteAccount(acct!.Id), name));

            await PlantOrphanAsync(container);

            var task = new ScheduledTask
            {
                TargetKind = TaskTargetKind.Backup,
                AccountId = acct.Id,
                ContainerName = name,
                TaskType = ScheduledTaskType.Cleanup,
                CronExpression = "* * * * *",
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            };
            await app.Services.GetRequiredService<TaskDispatcher>().DispatchAsync(task, CancellationToken.None);

            // 一个版本都没退役（默认保留策略），孤儿仍然被收走了。
            Assert.False(await OrphanExistsAsync(container), "the scheduled cleanup must sweep orphans");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 备份收尾那次清理照 <see cref="BackupRunControl.SweepNeeded"/> 办事——两个方向都要钉：
    /// 有作废的 journal 就扫，什么都没发生就不扫。
    /// <para>
    /// 只钉"该扫时扫了"是不够的：把那句改成常量 <c>true</c> 同样能过，而那意味着每一轮备份
    /// 收尾都多列两遍 data/ 与 packs/ 前缀，几十万对象的容器上这是每次备份都要付的账。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task The_backup_tail_sweeps_only_when_the_run_control_says_so()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var account = AzuriteAccount();
        var name = RandomName("sweeptrig");
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(blobFactory, new SevenZipArchiveCodec());
        var container = blobFactory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        // 同一份本地权威状态贯穿三轮：第二、三轮因此都不是"第一轮"，SweepNeeded 只会由 journal 决定。
        var authority = new TestLocalAuthority(store);
        try
        {
            Write("big.bin", new string('a', 60_000));
            await using (var c1 = new BackupRunControl(_journals, ConfigId, "run-1"))
                await BuildOrchestrator(authority, store).RunAsync(Request(account, name), null, default, c1);

            // 第二轮：盘上放一卷判据对不上的 journal（configId 不同 = 配置删了又建的陈迹）→
            // 开卷时作废 → SweepNeeded。
            await PlantOrphanAsync(container);
            await PlantStaleJournalAsync(account.Id, name);
            Write("big.bin", new string('b', 60_000));
            await using (var c2 = new BackupRunControl(_journals, ConfigId, "run-2"))
            {
                await BuildOrchestrator(authority, store).RunAsync(Request(account, name), null, default, c2);
                Assert.True(c2.SweepNeeded);
            }
            Assert.False(await OrphanExistsAsync(container), "a voided journal must trigger the tail sweep");

            // 第三轮：什么都没发生（既没采纳也没作废，也不是第一轮）→ 不扫。
            await PlantOrphanAsync(container);
            Write("big.bin", new string('c', 60_000));
            await using (var c3 = new BackupRunControl(_journals, ConfigId, "run-3"))
            {
                await BuildOrchestrator(authority, store).RunAsync(Request(account, name), null, default, c3);
                Assert.False(c3.SweepNeeded);
            }
            Assert.True(
                await OrphanExistsAsync(container),
                "an ordinary run must not pay for a full orphan sweep");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 删配置（保留容器）后又在同一个容器上重建：第一轮备份收尾必须把旧配置留下的孤儿收走。
    /// <para>
    /// 删配置那一步会把这个容器的 journal 全部丢掉（<c>BackupConfigEndpoints</c>），那批
    /// "云上有、索引里还没有"的块从此失去保护；端点写下的承诺是"等这个容器上再有配置时，
    /// 第一次清理会用完整判据把真孤儿扫掉"。而重建后的第一次清理正是**备份收尾**那次：
    /// 那时 journal 目录刚被删空，既没采纳也没作废——没有"第一轮必扫"这一条，这句承诺就是空的。
    /// </para>
    /// <para>
    /// 这里用"换一份本地权威状态"来模拟删配置：删配置端点删的正是这份本地状态
    /// （<c>localState.RemoveAsync</c>），而编排器判"是不是第一轮"看的也正是它。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_first_run_sweeps_what_the_deleted_config_left_behind()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var account = AzuriteAccount();
        var name = RandomName("sweeptrig");
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(blobFactory, new SevenZipArchiveCodec());
        var container = blobFactory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            Write("big.bin", new string('a', 60_000));
            await using (var c1 = new BackupRunControl(_journals, ConfigId, "run-1"))
                await BuildOrchestrator(new TestLocalAuthority(store), store)
                    .RunAsync(Request(account, name), null, default, c1);

            // 配置删了：本地状态没了、journal 也没了，容器和这个块还在。
            await PlantOrphanAsync(container);
            _journals.DeleteAll(account.Id, name);

            // 重建配置后的第一轮。盘上一卷 journal 都没有，所以扫这件事只能由"第一轮"这一项要求。
            Write("big.bin", new string('b', 60_000));
            await using (var c2 = new BackupRunControl(_journals, ConfigId, "run-2"))
            {
                await BuildOrchestrator(new TestLocalAuthority(store), store)
                    .RunAsync(Request(account, name), null, default, c2);
                Assert.True(c2.SweepNeeded);
            }

            Assert.False(
                await OrphanExistsAsync(container),
                "the first run after a config is recreated must make good on the delete endpoint's promise");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>盘上放一卷判据对不上的 journal：开卷时它会被当场作废删掉。</summary>
    private async Task PlantStaleJournalAsync(int accountId, string container)
    {
        await using var j = await _journals.CreateAsync(accountId, container, "run-stale", new JournalHeader
        {
            RunId = "run-stale",
            ConfigId = ConfigId + 1,          // 另一个配置留下的陈迹 → 作废
            StartedAt = DateTimeOffset.UnixEpoch,
            BaselineVersion = 0,
            LocalRoot = _root,
            EncryptionIdentity = "plain",
        }, default);
        await j.AppendAsync(
            new JournalRecord { Kind = "blob", Ref = "data/stale", Path = "stale.bin", FullHash = "stale" }, default);
    }
}
