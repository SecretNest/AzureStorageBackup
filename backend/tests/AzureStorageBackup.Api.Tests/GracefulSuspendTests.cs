using System.Net.Sockets;
using System.Text.RegularExpressions;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AzureStorageBackup.Api.Tests;

public class GracefulSuspendTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "asb-mark-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private BackupJournalStore Store() => new(_dir);

    [Fact]
    public void No_mark_means_nobody_wrote_one()
    {
        Assert.Null(Store().ReadSuspendMark(1, "c", "run-1"));
    }

    [Fact]
    public void Mark_round_trips()
    {
        var store = Store();
        store.MarkSuspended(1, "c", "run-1", SuspendReason.ShuttingDown);
        Assert.Equal(SuspendReason.ShuttingDown, store.ReadSuspendMark(1, "c", "run-1"));
    }

    // 用户主动暂停的那一条必须能与"关机顺手挂起的"分开——Task 15 靠这个决定要不要自动接着跑。
    [Fact]
    public void User_requested_is_distinguishable_from_shutting_down()
    {
        var store = Store();
        store.MarkSuspended(1, "c", "run-user", SuspendReason.UserRequested);
        store.MarkSuspended(1, "c", "run-boot", SuspendReason.ShuttingDown);
        Assert.Equal(SuspendReason.UserRequested, store.ReadSuspendMark(1, "c", "run-user"));
        Assert.Equal(SuspendReason.ShuttingDown, store.ReadSuspendMark(1, "c", "run-boot"));
    }

    /// <summary>
    /// 标记不能被当成 journal 列出来，也**不能**是 journal 里的一条记录。
    /// <para>
    /// 后半句是这条用例真正守着的东西：<c>LoadActiveRefsAsync</c> 是
    /// <c>r.Kind == "pack" ? packs : blobs</c> 的二分，多出来的第三种 Kind 会被静默丢进 blobs 桶，
    /// 于是清理器的"别删我"名单里凭空多出一个叫 <c>ShuttingDown</c> 的 blob 名。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Mark_is_not_listed_as_a_journal()
    {
        var store = Store();
        await using (await store.CreateAsync(1, "c", "run-1", Header("run-1"), default)) { }
        store.MarkSuspended(1, "c", "run-1", SuspendReason.ShuttingDown);

        var listed = await store.ListAsync(1, "c", default);
        Assert.Equal(["run-1"], listed.Select(x => x.RunId));
        Assert.Empty(listed[0].Content.Records);

        var refs = await store.LoadActiveRefsAsync(1, "c", default);
        Assert.Empty(refs.Blobs);
        Assert.Empty(refs.Packs);
    }

    // 删这一卷 journal 时标记也得跟着走，否则下次同名 runId 会读到上一次的理由。
    [Fact]
    public async Task Delete_takes_the_mark_with_it()
    {
        var store = Store();
        await using (await store.CreateAsync(1, "c", "run-1", Header("run-1"), default)) { }
        store.MarkSuspended(1, "c", "run-1", SuspendReason.UserRequested);

        store.Delete(1, "c", "run-1");

        Assert.Null(store.ReadSuspendMark(1, "c", "run-1"));
        Assert.Empty(await store.ListAsync(1, "c", default));
    }

    // 标记文件被写坏（半截、手改）时按"没有标记"处理：宁可多跑一轮，不要在启动路径上抛。
    [Fact]
    public void Garbage_mark_reads_as_none()
    {
        var store = Store();
        var path = store.PathFor(1, "c", "run-1") + ".suspend";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not-an-enum-value");
        Assert.Null(store.ReadSuspendMark(1, "c", "run-1"));
    }

    // --- 理由怎么从"下达停止"的那一端走到写标记的那一端 ---

    /// <summary>不指定理由 = 用户按的暂停，与本任务之前的行为一致。</summary>
    [Fact]
    public async Task Suspend_without_a_reason_is_user_requested()
    {
        await using var c = new BackupRunControl(Store(), 5, "run-1");
        c.RequestStop(StopKind.Suspend);
        Assert.Equal(SuspendReason.UserRequested, c.SuspendReason);
    }

    [Fact]
    public async Task Shutdown_reason_rides_along_with_the_stop_request()
    {
        await using var c = new BackupRunControl(Store(), 5, "run-1");
        c.RequestStop(StopKind.Suspend, SuspendReason.ShuttingDown);
        Assert.Equal(SuspendReason.ShuttingDown, c.SuspendReason);
    }

    /// <summary>
    /// 用户先按了 Suspend，关机随后也来一次 → 理由仍是 UserRequested。
    /// 反了的话 Task 15 会在下次启动时替他把一次**他自己按停的**备份重新开跑。
    /// </summary>
    [Fact]
    public async Task The_first_reason_wins_when_shutdown_lands_on_an_already_suspending_run()
    {
        await using var c = new BackupRunControl(Store(), 5, "run-1");
        c.RequestStop(StopKind.Suspend, SuspendReason.UserRequested);
        c.RequestStop(StopKind.Suspend, SuspendReason.ShuttingDown);
        Assert.Equal(SuspendReason.UserRequested, c.SuspendReason);
    }

    /// <summary>还没开卷就挂起的运行盘上什么都没留，标记只会变成一个指向不存在 journal 的孤儿——
    /// 而 Task 15 会照着它去找一卷根本不在的 journal。</summary>
    [Fact]
    public async Task No_journal_no_mark()
    {
        var store = Store();
        await using var c = new BackupRunControl(store, 5, "run-1");
        c.MarkSuspended(SuspendReason.ShuttingDown);
        Assert.Null(store.ReadSuspendMark(1, "c", "run-1"));
        Assert.False(Directory.Exists(_dir));
    }

    // --- 关机路径 ---

    /// <summary>没有在跑的运行时，关机钩子什么也不做、也不抛。</summary>
    [Fact]
    public async Task Suspend_all_with_nothing_running_stops_nothing()
    {
        using var factory = new TestWebAppFactory();
        var runner = factory.Services.GetRequiredService<BackupRunner>();

        Assert.Equal(0, await runner.SuspendAllAsync(SuspendReason.ShuttingDown, default));

        var service = new GracefulSuspendService(runner, NullLogger<GracefulSuspendService>.Instance);
        await service.StartAsync(default);
        await service.StopAsync(default);   // 不抛就是过
    }

    /// <summary>
    /// <c>_runs</c> 是把普通 <c>Dictionary</c>：不加锁地枚举它，一边有人登记新运行就会当场
    /// <c>InvalidOperationException</c>——而这一下正好落在关机路径上，那是没有第二次机会的地方。
    /// <para>
    /// 拿 <c>RunTrackedAsync</c> 制造登记：它先把状态写进 <c>_runs</c> 再去解析配置，配置不存在时
    /// 立刻失败，于是每一轮都是一次干净的字典插入。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Suspend_all_survives_runs_being_registered_underneath_it()
    {
        using var factory = new TestWebAppFactory();
        var runner = factory.Services.GetRequiredService<BackupRunner>();

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 400; i++)
                await runner.RunTrackedAsync(900_000 + i, CancellationToken.None);
        });

        while (!writer.IsCompleted)
            await runner.SuspendAllAsync(SuspendReason.ShuttingDown, default);

        await writer;
    }

    /// <summary>
    /// 宿主按注册的**逆序**停服务，所以关机挂起必须注册在调度器**之后**才能先于它停下来——
    /// 不然调度器可能在挂起进行到一半时又起一轮备份，而那一轮永远等不到关机钩子了。
    /// </summary>
    [Fact]
    public void Graceful_suspend_stops_before_the_scheduler()
    {
        using var factory = new SchedulerOnFactory();
        _ = factory.Services;   // 触发建主机，宿主注册这时才全

        var hosted = factory.Captured!
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        var scheduler = hosted.IndexOf(typeof(SchedulerService));
        var graceful = hosted.IndexOf(typeof(GracefulSuspendService));
        Assert.True(scheduler >= 0, "the scheduler is supposed to be registered when Scheduler:Enabled=true");
        Assert.True(graceful > scheduler,
            $"graceful suspend is registered at {graceful}, the scheduler at {scheduler}: "
            + "it has to come later so that it stops earlier");
    }

    /// <summary>
    /// 关机超时要同时满足两头：
    /// <list type="bullet">
    /// <item>够长——默认的 5 秒不够，挂起本身只写几十字节，但要先等每个工作者从当前这步退出来；</item>
    /// <item>够短——必须**小于** docker-compose 的 <c>stop_grace_period</c>。宽限期一到就是 SIGKILL；
    /// 只有 .NET 自己的超时先到，才还剩一段时间把"谁没停下来"写进日志。</item>
    /// </list>
    /// 下界单独立着没用：把 ShutdownTimeout 调到 60s 一样满足它，而那正好把 SIGKILL 请了回来。
    /// 所以上界直接去 compose 文件里读那个数——两处哪一处被改了，这条都会响。
    /// </summary>
    [Fact]
    public void Shutdown_timeout_fits_inside_the_container_grace_period()
    {
        using var factory = new TestWebAppFactory();
        var options = factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.True(options.ShutdownTimeout >= TimeSpan.FromSeconds(30),
            $"ShutdownTimeout is {options.ShutdownTimeout}, too short to park a run");

        var grace = ComposeStopGracePeriod();
        Assert.True(options.ShutdownTimeout < grace,
            $"ShutdownTimeout is {options.ShutdownTimeout} but docker-compose gives only {grace} of grace: "
            + "docker would SIGKILL before the app's own shutdown timeout ever fires");
    }

    /// <summary>从仓库里的 docker-compose.yml 读出 <c>stop_grace_period</c>。读不到就让用例失败：
    /// 这条用例守的就是两个数之间的关系，其中一个不见了，"通过"是没有意义的。</summary>
    private static TimeSpan ComposeStopGracePeriod()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")))
            dir = dir.Parent;
        Assert.True(dir is not null, "docker-compose.yml not found above " + AppContext.BaseDirectory);

        var text = File.ReadAllText(Path.Combine(dir!.FullName, "docker-compose.yml"));
        var m = Regex.Match(text, @"^\s*stop_grace_period:\s*(\d+)s\s*$", RegexOptions.Multiline);
        Assert.True(m.Success, "no `stop_grace_period: <n>s` in docker-compose.yml");
        return TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value));
    }

    /// <summary>
    /// 并发备份下，关机必须**先给每一个运行下达停止**，再统一等落盘。
    /// <para>
    /// 逐个"发一个、等一个"的写法在这里是致命的：排头那个若压着一个几 GB 的上传，它一个人就吃掉整个
    /// 关机预算，后面的运行连停止请求都收不到——没落盘、没标记、直接挨砍。这条用例摆的正是这个局：
    /// 900_001 收到停止后要磨蹭 2 秒才落地，900_002 则立刻应声。串行版里 900_002 只能等到 900_001
    /// 落地之后才被通知到，<c>_secondSignalledWhileFirstStillSettling</c> 就是 false。
    /// </para>
    /// <para>
    /// 顺带钉住返回的条数：这两个运行都还没走到建 control 那一步，停下来是 Canceled、盘上没有标记，
    /// 所以"挂起了几个"必须是 0。数成 2 的话，关机日志会宣称保住了两个根本没保住的现场。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Shutdown_signals_every_run_before_waiting_for_any_of_them()
    {
        var configs = new StallingConfigs(slowId: 900_001, settleDelay: TimeSpan.FromSeconds(2));
        using var factory = new StalledRunFactory(configs);
        var runner = factory.Services.GetRequiredService<BackupRunner>();

        var runs = Task.WhenAll(
            runner.RunTrackedAsync(900_001, CancellationToken.None),
            runner.RunTrackedAsync(900_002, CancellationToken.None));
        await configs.BothRunningAsync();

        var log = new RecordingLogger();
        var service = new GracefulSuspendService(runner, log);
        await service.StopAsync(default);

        await runs;
        Assert.True(configs.SecondSignalledWhileFirstStillSettling,
            "900_002 was only told to stop after 900_001 had finished settling");
        Assert.DoesNotContain(log.Messages, m => m.Contains("Suspended", StringComparison.Ordinal));
    }

    /// <summary>
    /// 两个卡在配置查询上的运行：<c>RunTrackedAsync</c> 先把状态登记进 <c>_runs</c> 再来查配置，
    /// 所以在这里赖着不返回，就得到两个货真价实的 Running。停止请求会取消传进来的 ct（这两个运行
    /// 还没有 control，走的是取消源那条），于是这里也就看得见"谁是什么时候被通知到的"。
    /// </summary>
    private sealed class StallingConfigs(int slowId, TimeSpan settleDelay) : IBackupConfigService
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _firstSettled;

        public bool SecondSignalledWhileFirstStillSettling { get; private set; }

        /// <summary>等到两个运行都真的挂在这里，再动手关机——否则测的是启动竞速，不是关机顺序。</summary>
        public Task BothRunningAsync() => Task.WhenAll(_first.Task, _second.Task).WaitAsync(TimeSpan.FromSeconds(30));

        public async Task<BackupConfig?> GetAsync(int id, CancellationToken ct = default)
        {
            (id == slowId ? _first : _second).TrySetResult();
            var stopped = new TaskCompletionSource();
            await using (ct.Register(() => stopped.TrySetResult()))
                await stopped.Task;

            if (id == slowId)
            {
                await Task.Delay(settleDelay, CancellationToken.None);   // 慢吞吞地收尾
                Interlocked.Exchange(ref _firstSettled, 1);
            }
            else
                SecondSignalledWhileFirstStillSettling = Volatile.Read(ref _firstSettled) == 0;

            ct.ThrowIfCancellationRequested();
            return null;
        }

        // 只读的那几个给个空答案：主机起来时别处也会碰它们，在那里抛只会把测试的失败点搬到无关的地方。
        public Task<IReadOnlyList<BackupConfig>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BackupConfig>>([]);
        public Task<BackupConfig?> FindAsync(int accountId, string containerName, CancellationToken ct = default)
            => Task.FromResult<BackupConfig?>(null);

        // 写的那几个关机路径一句都用不到，留成"叫到就是写错了"。
        public Task<BackupConfig> CreateAsync(BackupConfig config, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BackupConfig?> UpdateAsync(int id, BackupConfig update, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BackupConfig?> ChangeLocalRootAsync(int id, string newRoot, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetErrorAsync(int id, string message, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetNormalAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResetStatusAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StalledRunFactory(IBackupConfigService configs) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBackupConfigService>();
                services.AddScoped(_ => configs);
            });
        }
    }

    private sealed class RecordingLogger : ILogger<GracefulSuspendService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
                Messages.Add(formatter(state, exception));
        }
    }

    private sealed class SchedulerOnFactory : TestWebAppFactory
    {
        public IServiceCollection? Captured { get; private set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // 这一条正是要看的分支：调度器开着的时候两者的先后。
            builder.UseSetting("Scheduler:Enabled", "true");
            builder.ConfigureServices(services => Captured = services);
        }
    }

    // --- 真跑一轮：挂起收尾时理由要落到盘上 ---

    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static Account AzuriteAccount() => new()
    {
        Id = 44,
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

    /// <summary>
    /// 一次真的被叫停的运行，收尾时必须把理由写在 journal 旁边：Task 15 只认盘上这一份，
    /// 内存里那份随进程一起没了——而"进程没了"恰恰是它要处理的那种情形。
    /// </summary>
    [SkippableTheory]
    [InlineData(SuspendReason.UserRequested)]
    [InlineData(SuspendReason.ShuttingDown)]
    public async Task A_suspended_run_leaves_its_reason_next_to_the_journal(SuspendReason reason)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZipArchiveCodec.TryResolveExecutable() is not null, "7z executable not available");

        var root = Path.Combine(_dir, "src");
        var temp = Path.Combine(_dir, "temp");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(temp);
        var journals = new BackupJournalStore(Path.Combine(temp, "journal"));

        for (var i = 0; i < 3; i++)
        {
            var bytes = new byte[6_000_000 + i];
            for (var k = 0; k < bytes.Length; k += 4096) bytes[k] = (byte)(i + 1);
            await File.WriteAllBytesAsync(Path.Combine(root, $"f{i}.bin"), bytes);
        }

        var account = AzuriteAccount();
        var name = "mark" + Guid.NewGuid().ToString("N")[..8];
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var container = blobFactory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            BackupRunControl? control = null;
            var uploader = new StopAfterFirst(
                new BlobUploader(blobFactory), () => control!.RequestStop(StopKind.Suspend, reason));
            var orchestrator = Build(temp, uploader, blobFactory);

            await using (var c = new BackupRunControl(journals, 9, "run-mark"))
            {
                control = c;
                var ex = await Assert.ThrowsAsync<BackupSuspendedException>(
                    () => orchestrator.RunAsync(Request(account, name, root), null, default, c));
                Assert.Equal(reason, ex.Reason);
            }

            Assert.Equal(reason, journals.ReadSuspendMark(account.Id, name, "run-mark"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>第 1 次上传之后叫停，然后照常放行——要的是"停在半路"，不是"上传失败"。</summary>
    private sealed class StopAfterFirst(IBlobUploader inner, Action stop) : IBlobUploader
    {
        private int _count;

        private async Task<T> RunAsync<T>(Func<Task<T>> call)
        {
            var n = Interlocked.Increment(ref _count);
            var result = await call();
            if (n == 1) stop();
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

    private static BackupOrchestrator Build(string temp, IBlobUploader uploader, BlobClientFactory factory)
    {
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(temp, "compress"), Path.Combine(temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(temp, "compact"),
            staging);
        var authority = new TestLocalAuthority(store);
        return new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
    }

    private static BackupRequest Request(Account account, string container, string root) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = root,
        Name = "photos",
        Options = new BackupEngineOptions
        {
            UploadConcurrency = 1,
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    private static JournalHeader Header(string runId) => new()
    {
        RunId = runId,
        ConfigId = 5,
        StartedAt = DateTimeOffset.UtcNow,
        BaselineVersion = 0,
        LocalRoot = "/src",
        EncryptionIdentity = "plain",
    };
}
