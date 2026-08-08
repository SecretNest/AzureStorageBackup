using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Migrations;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// 真开一轮（只走开卷 + 挂起这两步，不碰云），好让"采纳旧卷"这件事按生产路径真的发生。
    /// 判据是按**卷**读的，而卷与标记的关系只有走这条路才长得出来——手工 seed 出来的现场
    /// 永远是"一卷配一份标记"，正好绕开了下面几条要说的话。
    /// </summary>
    private static async Task RunAndSuspendAsync(
        BackupJournalStore store, string runId, SuspendReason reason)
    {
        await using var control = new BackupRunControl(store, 7, runId);
        await control.OpenJournalAsync(
            1, "photos", 0, "/src", "plain", DateTimeOffset.UtcNow, default);
        control.MarkSuspended(reason);
    }

    /// <summary>盘上每一份 <c>.suspend</c> 都得有一卷 journal 与之对应。孤儿标记不会报错，
    /// 只会永远躺在那里，并且让"这个配置停在什么状态"多出一个没人认领的答案。</summary>
    private void AssertNoOrphanMarks()
    {
        var dir = Path.Combine(_dir, "1", "photos");
        foreach (var mark in Directory.EnumerateFiles(dir, "*.suspend"))
            Assert.True(
                File.Exists(mark[..^".suspend".Length]),
                $"{Path.GetFileName(mark)} has no journal next to it");
    }

    /// <summary>
    /// **连着两次**计划内重启都得接上，不是只有第一次。
    /// <para>
    /// 第二轮走的是与第一轮完全不同的一条路：它开卷时把第一轮那卷**采纳**下来，然后新开自己那卷，
    /// 于是这个配置底下有了两卷 journal，而判据要求**每一卷**都写着 ShuttingDown。第二轮挂起时
    /// 只给自己那卷写标记的话，第一轮那卷就停在"被采纳时抹掉的空标记"上，判据从此再也凑不齐——
    /// 而它坏掉的方式是完全静默的：第一次重启接上了，看上去这个功能是好的。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_shutdown_cycles_in_a_row_are_both_picked_up()
    {
        var store = Store();

        await RunAndSuspendAsync(store, "run-1", SuspendReason.ShuttingDown);
        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default));

        // 第二轮：接着跑起来的那一轮，采纳了 run-1 那卷，然后又被一次关机停在半路。
        await RunAndSuspendAsync(store, "run-2", SuspendReason.ShuttingDown);
        Assert.Equal(2, (await store.PeekAsync(1, "photos", default)).Count);
        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default));

        // 第三轮同理——第二轮之后就不该再有"第 N 次开始不灵了"这回事。
        await RunAndSuspendAsync(store, "run-3", SuspendReason.ShuttingDown);
        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default));

        AssertNoOrphanMarks();
    }

    /// <summary>
    /// 一卷被采纳的那一刻，它旧主人的标记就该退休：写下那个理由的运行已经被顶替掉了。
    /// <para>
    /// 采纳只是只读地认下旧卷，旧卷本身要等新一轮**成功收尾**才删——而"成功收尾"恰恰是长跑配置
    /// 最不容易到达的那一步。标记不在采纳时清掉的话，它就一直粘着，用一个陈年的理由替当前这一轮
    /// 回答"你停在什么状态"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Adopting_a_volume_retires_the_mark_of_the_run_it_took_over()
    {
        var store = Store();
        await RunAndSuspendAsync(store, "run-old", SuspendReason.AutoSuspended);

        await using var control = new BackupRunControl(store, 7, "run-new");
        await control.OpenJournalAsync(1, "photos", 0, "/src", "plain", DateTimeOffset.UtcNow, default);

        // 旧卷是被**采纳**的，不是被作废删掉的——否则"标记没了"就没说明任何事。
        Assert.Equal(2, (await store.PeekAsync(1, "photos", default)).Count);
        Assert.Null(store.ReadSuspendMark(1, "photos", "run-old"));
    }

    /// <summary>
    /// 操作员按 Run 就是在推翻他自己（或闸门）先前那次暂停——之后的一次计划内重启，仍然该接着跑。
    /// <para>
    /// 判据要求每一卷都是 ShuttingDown，而旧卷的标记只在"某一轮真的跑成功"时才会被删掉。少了
    /// "采纳即退休 + 挂起时连采纳来的卷一起写"这两条，一次 AutoSuspended / UserRequested 就会
    /// 一票否决掉此后的每一次重启，而且是**永久**的——直到某一轮跑完为止。卡在这个状态里的，
    /// 恰恰是那些跑不完一整轮的长跑配置，也就是这个功能存在的理由本身。
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(SuspendReason.AutoSuspended)]
    [InlineData(SuspendReason.UserRequested)]
    public async Task An_earlier_pause_does_not_veto_a_later_shutdown_forever(SuspendReason earlier)
    {
        var store = Store();
        await RunAndSuspendAsync(store, "run-paused", earlier);
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default));

        // 操作员按了 Run，这一轮又撞上一次计划内重启。
        await RunAndSuspendAsync(store, "run-after-run", SuspendReason.ShuttingDown);

        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default));
        AssertNoOrphanMarks();
    }

    /// <summary>
    /// 被否掉的配置必须留下一句话。这个部署形态是 NAS 上的成品机：操作员没有 shell，没有任何
    /// 看标记文件的工具，界面上那个开关还明晃晃开着。少了这一句，"重启之后备份怎么没接上"
    /// 在他那边是彻底无从下手的。
    /// </summary>
    [Fact]
    public async Task A_declined_config_says_which_volume_held_it_back()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-user");
        store.MarkSuspended(1, "photos", "run-user", SuspendReason.UserRequested);

        var log = new RecordingLogger();
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default, log));

        var line = Assert.Single(log.Messages);
        Assert.Contains("7", line);
        Assert.Contains("run-user", line);
        Assert.Contains("UserRequested", line);
    }

    /// <summary>没有标记那一类同样要说清，而且要说"没有"，不能说成某个理由。</summary>
    [Fact]
    public async Task A_declined_config_reports_a_missing_mark_as_none()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-crash");

        var log = new RecordingLogger();
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default, log));
        Assert.Contains("none", Assert.Single(log.Messages));
    }

    /// <summary>接上了的配置不该记这一句：它说的是"为什么不接"。</summary>
    [Fact]
    public async Task A_resumable_config_is_not_reported_as_declined()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-boot");
        store.MarkSuspended(1, "photos", "run-boot", SuspendReason.ShuttingDown);

        var log = new RecordingLogger();
        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default, log));
        Assert.Empty(log.Messages);
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

    internal sealed class RecordingLogger : ILogger<AutoResumeService>
    {
        /// <summary>级别一起记下来：这个服务的日志有一半意义在**级别**上（被否掉的配置要说得出口，
        /// 失败的自动恢复要比成功的显眼一档），只比对文字的话那一半是钉不住的。</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IReadOnlyList<string> Messages
        {
            get { lock (Entries) return [.. Entries.Select(e => e.Message)]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// 那个开关本身：**关掉它，就真的什么都不开。**
/// <para>
/// 这是这个功能最该被钉住的一句话，因为它坏掉的样子是静默的：操作员取消勾选，界面照样显示保存成功，
/// 直到某天重启之后一轮他不想要的备份自己跑起来，抢走产出锁、烧掉一晚上的带宽。而这条路上恰好有两处
/// 都会以完全一样的方式静默失效——<c>GlobalSettingsService.UpsertAsync</c> 里那一行赋值（漏了就是
/// 存不进去），和 <c>ExecuteAsync</c> 里那个提前 return（漏了就是存进去了也不看）。
/// </para>
/// <para>
/// 不需要 Azurite，也不需要一轮真备份：配置指向一个**不存在的账号**，
/// <c>BackupRunner.RunCoreAsync</c> 在查账号那一步就摔了，而它照样会登记运行、照样会放行
/// <c>Completion</c>——于是那个串行化的 await 也一并走到了。
/// </para>
/// </summary>
public sealed class AutoResumeSwitchTests : IDisposable
{
    private const string Container = "autoresume-switch";
    private const int AccountId = 987654;   // 故意查无此账号

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "asb-resume-switch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_setting_decides_whether_a_backup_is_started_at_all(bool on)
    {
        using var factory = new TestWebAppFactory();
        var scopes = factory.Services.GetRequiredService<IServiceScopeFactory>();

        int configId;
        using (var scope = scopes.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<IGlobalSettingsService>();
            // 存两次：第一次建行走的是 Add 分支（整个对象原样落库），只有第二次才走**逐字段赋值**
            // 那条路——而漏掉那一行赋值正是这里要防的失效方式之一。
            await settings.UpsertAsync(new GlobalSettings());
            await settings.UpsertAsync(new GlobalSettings { AutoResumeInterruptedRuns = on });
            Assert.Equal(on, (await settings.GetAsync()).AutoResumeInterruptedRuns);

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var config = new BackupConfig
            {
                AccountId = AccountId,
                ContainerName = Container,
                Name = "switch",
                LocalRoot = _dir,
            };
            db.BackupConfigs.Add(config);
            await db.SaveChangesAsync();
            configId = config.Id;
        }

        // 盘上的现场：一卷 journal + 一份 ShuttingDown 标记 = 挑件人一定会挑中它。
        var journals = new BackupJournalStore(_dir);
        await using (await journals.CreateAsync(AccountId, Container, "run-boot", new JournalHeader
        {
            RunId = "run-boot",
            ConfigId = configId,
            StartedAt = DateTimeOffset.UtcNow,
            BaselineVersion = 0,
            LocalRoot = _dir,
            EncryptionIdentity = "plain",
        }, default)) { }
        journals.MarkSuspended(AccountId, Container, "run-boot", SuspendReason.ShuttingDown);
        Assert.Equal(
            [configId],
            await AutoResumeService.PickResumableAsync(
                journals, [(configId, AccountId, Container)], default));

        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var original = AutoResumeService.Delay;
        AutoResumeService.Delay = TimeSpan.FromMilliseconds(50);
        try
        {
            var log = new AutoResumeTests.RecordingLogger();
            var service = new AutoResumeService(scopes, journals, runner, log);
            await service.StartAsync(default);
            try
            {
                if (on)
                {
                    // 起来了：运行注册表里有它。等一小会儿——StartAsync 之后还要过 50ms 的延时。
                    var started = await SpinAsync(() => runner.Get(configId) is not null);
                    Assert.True(started, "the setting is on, so the interrupted backup should have started");

                    // 这一轮必然失败（账号查无此人），而**失败的自动恢复要比成功的显眼一档**：
                    // 没人守在旁边看这一轮的结果，记成 Information 就等于埋进正常流水里。
                    var reported = await SpinAsync(() =>
                    {
                        lock (log.Entries)
                            return log.Entries.Any(
                                e => e.Level == LogLevel.Warning && e.Message.Contains($"backup {configId} failed"));
                    });
                    Assert.True(reported, "a failed auto-resume has to be reported as a warning, "
                        + $"got: {string.Join(" | ", log.Messages)}");
                }
                else
                {
                    // 关着：给它足够长的时间去做错事，然后确认它什么都没做。
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    Assert.Null(runner.Get(configId));
                }
            }
            finally { await service.StopAsync(default); }
        }
        finally { AutoResumeService.Delay = original; }
    }

    private static async Task<bool> SpinAsync(Func<bool> until)
    {
        for (var i = 0; i < 200; i++)
        {
            if (until())
                return true;
            await Task.Delay(50);
        }
        return false;
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
