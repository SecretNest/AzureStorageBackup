using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Migrations;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 挑件人的判据。判据只有一条：**盘上每一卷都写着 <see cref="SuspendReason.ShuttingDown"/>**。
/// <para>
/// 别的一律不碰，包括"没有标记"。没有标记的含义是**说不清**：崩了、被 kill、关机等落盘超时被丢在
/// 半路、操作员按了 Cancel（取消路径照样落盘，但有意不写标记）、或者就是那次写标记本身失败了——
/// 这几种长在盘上一模一样，而其中至少有一种（Cancel）是用户明确表达过"别跑了"的。
/// 分不出来的时候不动，是安全的那一侧：界面上本来就有 Run 按钮，接着跑随时都能由人来点。
/// </para>
/// </summary>
public class AutoResumeTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "asb-resume-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private BackupJournalStore Store() => new(_dir);

    private static readonly (int ConfigId, int AccountId, string Container)[] OneConfig =
        [(7, 1, "photos")];

    private static async Task SeedJournalAsync(BackupJournalStore store, string runId)
    {
        await using var journal = await store.CreateAsync(1, "photos", runId, new JournalHeader
        {
            RunId = runId,
            ConfigId = 7,
            StartedAt = DateTimeOffset.UtcNow,
            BaselineVersion = 0,
            LocalRoot = "/src",
            EncryptionIdentity = "plain",
        }, default);
    }

    // 没有 journal = 上次跑完了（跑完会删掉自己那卷）= 没什么可接的。
    [Fact]
    public async Task Nothing_to_resume_when_no_journal_is_left()
    {
        Assert.Empty(await AutoResumeService.PickResumableAsync(Store(), OneConfig, default));
    }

    // 唯一可以不问自取的那一种：一次计划内的进程退出把它停在这儿的。
    [Fact]
    public async Task Shutdown_suspended_run_is_resumable()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-boot");
        store.MarkSuspended(1, "photos", "run-boot", SuspendReason.ShuttingDown);
        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default));
    }

    // 没有标记的那一卷分不清是崩溃、被 kill、还是操作员按的 Cancel——分不清就别动。
    [Fact]
    public async Task Unmarked_journal_is_left_alone()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-crash");
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default));
    }

    /// <summary>
    /// 上面那条的具体所指，也是判据从"没标记就接"改成"只认 ShuttingDown"的**起因**：
    /// 操作员按 Cancel 停掉的那一轮，盘上留下的东西与崩溃留下的**一模一样**——journal 在、标记没有
    /// （两种取消都落盘，都有意不写标记）。按"没标记就接"，重启会把他刚亲手取消的那一轮重新开跑。
    /// <para>
    /// 这条与 <see cref="Unmarked_journal_is_left_alone"/> 在盘上确实无从分辨，这正是它要说的话；
    /// 那一卷真的由取消路径产生这件事，由本文件下方的集成用例
    /// <c>A_cancelled_run_leaves_no_mark_and_is_not_picked_up</c> 从真跑一轮里核实。
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_cancelled_run_looks_exactly_like_a_crash_and_is_left_alone()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-cancelled");
        Assert.Null(store.ReadSuspendMark(1, "photos", "run-cancelled"));
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default));
    }

    // 闸门耐心耗尽降级停的：那个瞬时错误多半还在（网线还没插上、云端还在 503），
    // 自动接着跑只会立刻再撞一次墙，然后再挂起一次。等人来看一眼。
    [Fact]
    public async Task Auto_suspended_run_is_left_alone()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-auto");
        store.MarkSuspended(1, "photos", "run-auto", SuspendReason.AutoSuspended);
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default));
    }

    // 用户亲手按的暂停：重启替他重开，就是把他的意图擦掉。
    [Fact]
    public async Task User_paused_run_is_left_alone()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-user");
        store.MarkSuspended(1, "photos", "run-user", SuspendReason.UserRequested);
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default));
    }

    // 同一个备份留了两卷（上上次也停在半路），只该起一轮，不是两轮：
    // 接着跑是**一轮新的运行**，它开卷时会自己去认所有还作数的卷。
    [Fact]
    public async Task A_config_is_listed_once_however_many_journals_it_left()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-a");
        await SeedJournalAsync(store, "run-b");
        store.MarkSuspended(1, "photos", "run-a", SuspendReason.ShuttingDown);
        store.MarkSuspended(1, "photos", "run-b", SuspendReason.ShuttingDown);
        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default));
    }

    /// <summary>
    /// 标记是按**卷**记的，不是按配置记的，所以一个配置底下完全可能出现取值互相打架的几卷：
    /// 操作员把 A 按成 UserRequested → 又点了 Run → B 开卷时把 A 那卷采纳过来 → 一次关机把 B
    /// 停成 ShuttingDown。于是同一个配置名下一卷 UserRequested、一卷 ShuttingDown。
    /// <para>
    /// 判据要求**每一卷**都是 ShuttingDown，就不必再去发明一套"哪卷更新说了算"的仲裁——
    /// 而接着跑那一轮会把所有还作数的卷一起认下来，所以只要有一卷是不该动的，动了就等于连它一起动了。
    /// </para>
    /// </summary>
    [Fact]
    public async Task One_unmarked_journal_holds_back_the_whole_config()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-boot");
        await SeedJournalAsync(store, "run-crash");
        store.MarkSuspended(1, "photos", "run-boot", SuspendReason.ShuttingDown);
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default));
    }

    [Fact]
    public void Setting_is_on_by_default()
    {
        Assert.True(new GlobalSettings().AutoResumeInterruptedRuns);
    }

    /// <summary>
    /// 上面那条只管**新装**的实例（CLR 默认值）。升级上来的实例拿到的是迁移里那个
    /// <c>defaultValue</c>，而脚手架给 <c>AddColumn&lt;bool&gt;</c> 生成的是 <c>false</c>——不手改的话，
    /// 老用户默认关、新用户默认开，两条用例只写一条是发现不了的。
    /// <para>
    /// 这个差别的发现方式还特别糟：它不报错、不告警，只在某天一次计划内重启之后备份没接上时，
    /// 由人去猜为什么。所以这里直接把迁移那一步的默认值钉住。
    /// </para>
    /// </summary>
    [Fact]
    public void The_migration_gives_upgraded_installs_the_same_default_as_fresh_ones()
    {
        var add = new AddAutoResumeInterruptedRuns().UpOperations
            .OfType<AddColumnOperation>()
            .Single(o => o.Name == nameof(GlobalSettings.AutoResumeInterruptedRuns));
        Assert.Equal(new GlobalSettings().AutoResumeInterruptedRuns, add.DefaultValue);
    }

    /// <summary>
    /// 自动接着跑与调度器同属"没人按就自己开工"这一类，因此跟着同一个开关走。
    /// <para>
    /// 直接的好处在测试主机上：<see cref="TestWebAppFactory"/> 会把所有 hosted service 都起起来，
    /// 无条件注册的话，任何跑够久的集成用例都可能被它冷不丁开一轮真备份。
    /// </para>
    /// </summary>
    [Fact]
    public void Auto_resume_follows_the_scheduler_switch()
    {
        using var off = new TestWebAppFactory();
        Assert.DoesNotContain(
            off.Services.GetServices<IHostedService>(), s => s is AutoResumeService);
    }

    /// <summary>
    /// 注册次序有意义，理由与 <c>GracefulSuspendService</c> 那条同源：宿主按注册的**逆序**停服务，
    /// 关机挂起必须排在自动接着跑**之后**，才能先于它停下来——反过来的话，关机挂起做完之后
    /// 自动接着跑还醒着，它完全可能在拆服务的当口再开一轮，而那一轮再没有人来挂起它。
    /// </summary>
    [Fact]
    public void Auto_resume_stops_after_graceful_suspend()
    {
        using var factory = new SchedulerOnFactory();
        _ = factory.Services;   // 触发建主机，宿主注册这时才全

        var hosted = factory.Captured!
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        var autoResume = hosted.IndexOf(typeof(AutoResumeService));
        var graceful = hosted.IndexOf(typeof(GracefulSuspendService));
        Assert.True(autoResume >= 0, "auto-resume is supposed to be registered when Scheduler:Enabled=true");
        Assert.True(graceful > autoResume,
            $"graceful suspend is registered at {graceful}, auto-resume at {autoResume}: "
            + "graceful suspend has to come later so that it stops earlier");
    }

    private sealed class SchedulerOnFactory : TestWebAppFactory
    {
        public IServiceCollection? Captured { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Scheduler:Enabled", "true");
            builder.ConfigureServices(services => Captured = services);
        }
    }
}

/// <summary>
/// 把整条环路真的走一遍：跑一轮 → 关机挂起（盘上留下 journal + ShuttingDown 标记）→ 挑件人挑中它
/// → 接着跑那一轮**采纳**旧卷而不是作废它。
/// <para>
/// 上面那些单元用例证的只是那个谓词，证不了"接着跑真能省下已经传上去的东西"——而后者才是这个功能
/// 的全部意义。这里的两条从真 Azurite 上核实它。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AutoResumeIntegrationTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public AutoResumeIntegrationTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-autoresume-" + Guid.NewGuid().ToString("N"));
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

    private static Account AzuriteAccount() => new()
    {
        Id = 46,
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

    /// <summary>每个文件内容互不相同，否则三个文件会去重成一个 blob，上传次数就说明不了问题。</summary>
    private void WriteBytes(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        for (var i = 0; i < bytes.Length; i += 4096) bytes[i] = (byte)rel.Length;
        File.WriteAllBytes(full, bytes);
    }

    private (BackupOrchestrator Orchestrator, BackupInfoStore Store, BlobClientFactory Factory) Build(
        IBlobUploader uploader)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"),
            staging);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
        return (orchestrator, store, factory);
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions
        {
            // 上传额度 1 = 任一时刻只有一卷在传，"第 1 次上传之后叫停"这个下达时刻才是准的。
            UploadConcurrency = 1,
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    /// <summary>数上传次数；到第 <c>stopAt</c> 次时回调一次（做完那一次之后才叫停，
    /// 要的是"停在半路"而不是"上传失败"）。</summary>
    private sealed class CountingUploader(IBlobUploader inner, int stopAt = 0, Action? stop = null)
        : IBlobUploader
    {
        private int _count;

        public int Uploads => Volatile.Read(ref _count);

        private async Task<T> RunAsync<T>(Func<Task<T>> call)
        {
            var n = Interlocked.Increment(ref _count);
            var result = await call();
            if (n == stopAt) stop?.Invoke();
            return result;
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync(() => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry, CancellationToken ct,
            IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
            => RunAsync(() => inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata, progress));

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
            => RunAsync<bool>(async () =>
            {
                await inner.UploadOverwriteAsync(
                    account, container, blobName, filePath, tier, retry, ct, metadata);
                return true;
            });
    }

    /// <summary>
    /// 环路的全程：关机把一轮停在半路 → 盘上是 journal + ShuttingDown → 挑件人挑中这个配置 →
    /// 接着跑那一轮把旧卷**采纳**下来，已经确认传上去的一个字节都不重传。
    /// <para>
    /// 最后那一句是这条用例真正的分量所在。"采纳"与"作废"在挑件人眼里没有任何区别——两种情况下
    /// <c>PickResumableAsync</c> 都照样把配置挑出来、备份都照样跑完、云上也都照样是对的，
    /// 差别只在**重传了多少**。所以这里不看跑没跑完，看的是上传次数与索引里那几条指向谁。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task A_shutdown_suspended_run_is_picked_up_and_its_journal_is_adopted_not_voided()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("autoresume");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("a.bin", 6_000_000);
            WriteBytes("b.bin", 6_000_001);
            WriteBytes("c.bin", 6_000_002);

            // --- 关机：传完一件就按 ShuttingDown 挂起 ---
            BackupRunControl? first = null;
            var stopping = new CountingUploader(
                new BlobUploader(factory0), stopAt: 1,
                stop: () => first!.RequestStop(StopKind.Suspend, SuspendReason.ShuttingDown));
            await using (var c = new BackupRunControl(_journals, 11, "run-shutdown"))
            {
                first = c;
                var (o1, _, _) = Build(stopping);
                var ex = await Assert.ThrowsAsync<BackupSuspendedException>(
                    () => o1.RunAsync(Request(account, name), null, default, c));
                Assert.Equal(SuspendReason.ShuttingDown, ex.Reason);
            }

            // 盘上的现场：一卷 journal，旁边一份 ShuttingDown 标记。
            var done = (await _journals.ListAsync(account.Id, name, default))[0].Content.Records;
            Assert.NotEmpty(done);
            Assert.True(done.Count < 3, $"the run was supposed to be interrupted, it did all {done.Count}");
            Assert.Equal(
                SuspendReason.ShuttingDown, _journals.ReadSuspendMark(account.Id, name, "run-shutdown"));

            // --- 重启：挑件人认得这个现场 ---
            Assert.Equal(
                [11],
                await AutoResumeService.PickResumableAsync(
                    _journals, [(11, account.Id, name)], default));

            // --- 接着跑：新的 runId（BackupRunner 每轮都新生成一个），开卷时采纳旧卷 ---
            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming);
            await using (var c2 = new BackupRunControl(_journals, 11, "run-resumed"))
            {
                var result = await o2.RunAsync(Request(account, name), null, default, c2);
                Assert.Equal(1, result.Version);
                Assert.False(c2.Resume.IsEmpty, "the suspended run's journal was voided, not adopted");
                Assert.Equal(done.Count, c2.Resume.RecordCount);
            }

            // 采纳的实证：上一轮做完的那几件一件都没重传。作废的话这里会是 3。
            Assert.Equal(3 - done.Count, resuming.Uploads);

            // 而且它们确实进了索引，指的正是上一轮传上去的那个 blob——省下的重传没有变成缺失。
            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(
                account, name, info!.Versions[^1].IndexBlob, null, default);
            Assert.Equal(3, index.Entries.Count(e => e.Storage is not null));
            foreach (var r in done)
                Assert.Equal(r.Ref, index.Entries.Single(e => e.Path == r.Path).Storage!.Ref);

            // 收尾：两卷 journal 一起功成身退，标记也不该留着——留着的话下次重启会照它再接一轮。
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
            Assert.Null(_journals.ReadSuspendMark(account.Id, name, "run-shutdown"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 取消路径确实**不写标记**——这是判据里"没标记就别动"那一条的事实依据，不是推测。
    /// <para>
    /// 这一条要是哪天不成立了（比如有人顺手让取消也写一份标记），单元用例里那条
    /// <c>A_cancelled_run_looks_exactly_like_a_crash_and_is_left_alone</c> 说的话就落空了，
    /// 而它落空的方式是悄无声息的：判据仍然"对"，只是它保护的那个人不见了。
    /// </para>
    /// </summary>
    [SkippableTheory]
    [InlineData(StopKind.FinishCurrentFiles)]
    [InlineData(StopKind.StopNow)]
    public async Task A_cancelled_run_leaves_no_mark_and_is_not_picked_up(StopKind kind)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("autocancel");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("a.bin", 6_000_000);
            WriteBytes("b.bin", 6_000_001);
            WriteBytes("c.bin", 6_000_002);

            BackupRunControl? control = null;
            var stopping = new CountingUploader(
                new BlobUploader(factory0), stopAt: 1, stop: () => control!.RequestStop(kind));
            await using (var c = new BackupRunControl(_journals, 12, "run-cancelled"))
            {
                control = c;
                var (o, _, _) = Build(stopping);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => o.RunAsync(Request(account, name), null, default, c));
            }

            // journal 落了盘（取消照样落），标记没有——与崩溃留下的现场一模一样。
            Assert.NotEmpty(await _journals.ListAsync(account.Id, name, default));
            Assert.Null(_journals.ReadSuspendMark(account.Id, name, "run-cancelled"));

            // 所以重启不该替他重新开跑。
            Assert.Empty(await AutoResumeService.PickResumableAsync(
                _journals, [(12, account.Id, name)], default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
