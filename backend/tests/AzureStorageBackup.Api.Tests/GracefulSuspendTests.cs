using System.Diagnostics;
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

    // A user-initiated pause has to be distinguishable from "suspended along the way at shutdown" — Task 15 uses this to decide whether to auto-resume.
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
    /// The mark must not be listed as a journal, and must **not** be a record inside the journal either.
    /// <para>
    /// The second half is what this case really guards: <c>LoadActiveRefsAsync</c> is a two-way split on
    /// <c>r.Kind == "pack" ? packs : blobs</c>, and any extra third Kind gets silently dropped into the blobs bucket,
    /// so the cleaner's "don't delete me" list grows a blob name called <c>ShuttingDown</c> out of thin air.
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

    // Deleting this journal volume has to take the mark with it, otherwise the next runId with the same name reads the previous round's reason.
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

    // A mark file written badly (truncated, hand-edited) is treated as "no mark": better to run one extra round than to throw on the startup path.
    [Fact]
    public void Garbage_mark_reads_as_none()
    {
        var store = Store();
        var path = store.PathFor(1, "c", "run-1") + ".suspend";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not-an-enum-value");
        Assert.Null(store.ReadSuspendMark(1, "c", "run-1"));
    }

    // --- How the reason travels from the "issue a stop" end to the "write the mark" end ---

    /// <summary>No reason specified = the user pressed pause, matching the behavior from before this task.</summary>
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
    /// The user pressed Suspend first and a shutdown follows with another one → the reason stays UserRequested.
    /// The other way round, Task 15 would restart a backup **he stopped himself** for him at the next startup.
    /// </summary>
    [Fact]
    public async Task The_first_reason_wins_when_shutdown_lands_on_an_already_suspending_run()
    {
        await using var c = new BackupRunControl(Store(), 5, "run-1");
        c.RequestStop(StopKind.Suspend, SuspendReason.UserRequested);
        c.RequestStop(StopKind.Suspend, SuspendReason.ShuttingDown);
        Assert.Equal(SuspendReason.UserRequested, c.SuspendReason);
    }

    /// <summary>A run suspended before the journal was opened leaves nothing on disk, so the mark would only become an
    /// orphan pointing at a journal that does not exist — and Task 15 would follow it looking for a journal volume that isn't there.</summary>
    [Fact]
    public async Task No_journal_no_mark()
    {
        var store = Store();
        await using var c = new BackupRunControl(store, 5, "run-1");
        c.MarkSuspended(SuspendReason.ShuttingDown);
        Assert.Null(store.ReadSuspendMark(1, "c", "run-1"));
        Assert.False(Directory.Exists(_dir));
    }

    // --- The shutdown path ---

    /// <summary>With no run in progress, the shutdown hook does nothing and throws nothing.</summary>
    [Fact]
    public async Task Suspend_all_with_nothing_running_stops_nothing()
    {
        using var factory = new TestWebAppFactory();
        var runner = factory.Services.GetRequiredService<BackupRunner>();

        Assert.Equal(0, await runner.SuspendAllAsync(SuspendReason.ShuttingDown, default));

        var service = new GracefulSuspendService(runner, NullLogger<GracefulSuspendService>.Instance);
        await service.StartAsync(default);
        await service.StopAsync(default);   // not throwing is passing
    }

    /// <summary>
    /// <c>_runs</c> is a plain <c>Dictionary</c>: enumerate it without a lock while someone registers a new run and you
    /// get an <c>InvalidOperationException</c> on the spot — and that lands right on the shutdown path, which is where
    /// there is no second chance.
    /// <para>
    /// <c>RunTrackedAsync</c> is used to manufacture registrations: it writes the state into <c>_runs</c> before it
    /// resolves the config, and a nonexistent config fails immediately, so every iteration is one clean dictionary insert.
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
    /// F3: no test ever actually blew past <see cref="BackupRunner.SuspendWaitCap"/> — delete the
    /// <c>capped.CancelAfter(SuspendWaitCap)</c> line and the whole suite stays green, which makes that cap a fiction.
    /// <para>
    /// Reflection is used to stuff a run that "cannot be stopped" straight into <c>_runs</c>: <c>Status = Running</c>
    /// but no <c>Control</c>, so <c>RequestStop</c> takes the <c>state.Cancellation.Cancel()</c> branch — the intent
    /// goes out, but nobody ever settles <c>Completion</c>, simulating a run stuck on the flush path that will not exit.
    /// <see cref="BackupRunner.SuspendWaitCap"/> is dialed from production's 20 seconds down to 100 milliseconds
    /// (copying the technique from the <see cref="Endpoints.BackupConfigEndpoints.StopWaitCap"/> tests next door), then asserts:
    /// <list type="bullet">
    /// <item>hitting the cap does **not** throw — <c>SuspendAllAsync</c> returns normally instead of tossing an
    /// <c>OperationCanceledException</c> at the shutdown hook;</item>
    /// <item>the returned count does **not** include this one — it never landed as <see cref="RunStatus.Suspended"/> and
    /// left no mark on disk, so counting it would have the shutdown log talking big;</item>
    /// <item>the log leaves the "who didn't stop" clue, and names our own <c>SuspendWaitCap</c> rather than the
    /// caller's <c>ct</c> (F2).</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public async Task Suspend_all_gives_up_after_the_cap_and_reports_the_run_as_not_suspended()
    {
        var log = new CapturingLoggerProvider();
        using var factory = new LoggedFactory(log);
        var runner = factory.Services.GetRequiredService<BackupRunner>();

        var stuck = new BackupRunState { Status = RunStatus.Running };   // Completion never settles
        InjectRun(runner, 900_777, stuck);

        var original = BackupRunner.SuspendWaitCap;
        BackupRunner.SuspendWaitCap = TimeSpan.FromMilliseconds(100);
        try
        {
            // One more hard 5-second cap wrapped around the outside. The mutation this test is meant to catch is
            // "delete capped.CancelAfter", and the direct consequence of that deletion is waiting here forever — without
            // this layer the mutation shows up as the whole suite hanging with no idea which case is stuck, which is far
            // worse than one failure with a name attached.
            var stopped = await runner.SuspendAllAsync(SuspendReason.ShuttingDown, default)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(0, stopped);
            Assert.Equal(RunStatus.Running, stuck.Status);   // never landed: not Suspended
            Assert.True(stuck.Cancellation.IsCancellationRequested);   // but the intent really did go out
            // It has to name our own cap, not the caller's ct: both branches now start with "Gave up after"
            // (both report the measured duration), and what tells them apart is the "(cap …s)" segment, which only the
            // cap branch can produce.
            Assert.Contains(log.Messages, m =>
                m.Contains("Gave up after", StringComparison.Ordinal)
                && m.Contains("(cap ", StringComparison.Ordinal)
                && m.Contains("900777", StringComparison.Ordinal));
            Assert.DoesNotContain(log.Messages, m =>
                m.Contains("HostOptions.ShutdownTimeout", StringComparison.Ordinal));
        }
        finally
        {
            BackupRunner.SuspendWaitCap = original;
            RemoveRun(runner, 900_777);
        }
    }

    private static Dictionary<int, BackupRunState> RunsOf(BackupRunner runner) =>
        (Dictionary<int, BackupRunState>)typeof(BackupRunner)
            .GetField("_runs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(runner)!;

    private static void InjectRun(BackupRunner runner, int configId, BackupRunState state) =>
        RunsOf(runner)[configId] = state;

    private static void RemoveRun(BackupRunner runner, int configId) =>
        RunsOf(runner).Remove(configId);

    /// <summary>Captures logs from every category, no category filter — only SuspendAllAsync's warning fires in this test.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Messages) owner.Messages.Add(formatter(state, exception));
            }
        }
    }

    private sealed class LoggedFactory(CapturingLoggerProvider provider) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddLogging(b => b.AddProvider(provider)));
        }
    }

    /// <summary>
    /// The host stops services in **reverse** registration order, so graceful suspend has to be registered **after**
    /// the scheduler to stop before it does — otherwise the scheduler could start another backup halfway through the
    /// suspend, and that round would never get a shutdown hook at all.
    /// </summary>
    [Fact]
    public void Graceful_suspend_stops_before_the_scheduler()
    {
        using var factory = new SchedulerOnFactory();
        _ = factory.Services;   // forces the host to be built; only then is the registration list complete

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
    /// The shutdown timeout has to satisfy both ends at once:
    /// <list type="bullet">
    /// <item>long enough — the default 5 seconds is not, since the suspend itself only writes a few dozen bytes but has
    /// to wait for every worker to back out of its current step first;</item>
    /// <item>short enough — it must be **less than** docker-compose's <c>stop_grace_period</c>. The moment the grace
    /// period expires it is SIGKILL; only if .NET's own timeout fires first is there any time left to write "who didn't
    /// stop" into the log.</item>
    /// </list>
    /// The lower bound standing alone is useless: dialing ShutdownTimeout up to 60s satisfies it just as well, and that
    /// invites SIGKILL right back in. So the upper bound reads that number straight out of the compose file — change
    /// either of the two places and this one fires.
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

    /// <summary>
    /// Reads <c>stop_grace_period</c> out of the repo's docker-compose.yml. If it cannot be read, the case fails:
    /// what this case guards is the relationship between two numbers, and with one of them gone, "passing" is meaningless.
    /// <para>
    /// The regex only accepts whole seconds (<c>45s</c>), and that narrowness is deliberate: writing <c>1m30s</c>,
    /// adding quotes, or trailing the line with a <c>\# comment</c> all make this case fail to read the number and fail
    /// outright, rather than quietly passing on a wrong one. That is the correct side to fail on — docker compose's
    /// <c>stop_grace_period</c> supports several notations, and there is no intent to build a general parser here;
    /// reporting honestly when the expected shape is not there and making someone go check beats guessing a value that
    /// may be wrong.
    /// </para>
    /// <para>
    /// This case's field of view also stops right there: it only sees the docker-compose.yml committed to the repo. If
    /// the real run uses an override file (<c>docker-compose.override.yml</c>) or <c>docker compose -f</c> points at
    /// another file that overrides this value, this case cannot see it — what it guards is "the config written in the
    /// repo is internally consistent", not "the number actually in effect at deployment".
    /// </para>
    /// </summary>
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
    /// With concurrent backups, shutdown must **issue the stop to every run first** and only then wait for the flushes.
    /// <para>
    /// A "send one, wait for one" loop is fatal here: if the one at the front is sitting on a multi-GB upload it eats
    /// the entire shutdown budget by itself, and the runs behind it never even receive a stop request — no flush, no
    /// mark, straight to the axe.
    /// </para>
    /// <para>
    /// This case used to give the two runs different settle delays (the "slow" one processed first, the "fast" one
    /// second), which relied on <c>Dictionary</c> happening to enumerate in insertion order — that is not a contract,
    /// just an implementation detail of this runtime. Review nailed the hole down: revert <c>SuspendAllAsync</c> to
    /// serial signal+wait (the same I1 regression) while also reversing the internal enumeration order
    /// (<c>Enumerable.Reverse</c>), and the old case still passed. Now both runs get **the same** settle delay, with no
    /// slow/fast distinction; it simply records the instant each signal arrives and the instant "the first one actually
    /// settles", and asserts "both signals happened before the first settle" — a statement that does not depend on who
    /// goes first, and that holds for a serial implementation under either enumeration order (whichever one the serial
    /// version processes first, it necessarily waited for that one to settle before processing the second).
    /// </para>
    /// <para>
    /// It pins down the returned count along the way: neither run has reached the point of building a control, so
    /// stopping them is Canceled with no mark on disk, and "how many were suspended" must be 0. Counting 2 would have
    /// the shutdown log claim it saved two states that were never saved at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Shutdown_signals_every_run_before_waiting_for_any_of_them()
    {
        var configs = new StallingConfigs(settleDelay: TimeSpan.FromMilliseconds(500));
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

        var signals = configs.SignalledAt;
        var firstSettledAt = configs.FirstSettledAt;
        Assert.Equal(2, signals.Count);
        Assert.True(firstSettledAt >= 0, "neither run ever settled");
        Assert.True(signals.Max() < firstSettledAt,
            $"a signal ({string.Join(", ", signals)}) arrived at or after the first settle "
            + $"({firstSettledAt}): every run must be signalled before any of them is waited on");
        Assert.DoesNotContain(log.Messages, m => m.Contains("Suspended", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two runs stalled on the config lookup: <c>RunTrackedAsync</c> registers the state into <c>_runs</c> before it
    /// looks the config up, so refusing to return here yields two genuine Running entries. A stop request cancels the ct
    /// passed in (these two runs have no control yet, so they take the cancellation-source branch), which is how this
    /// class also gets to see "who was notified when".
    /// <para>
    /// Both runs get **the same** <paramref name="settleDelay"/>: no "slow" and "fast" distinction, so the test itself
    /// does not fall back into the "only holds thanks to some fixed ordering" trap (see the comment on the test method above).
    /// </para>
    /// </summary>
    private sealed class StallingConfigs(TimeSpan settleDelay) : IBackupConfigService
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<long> _signalledAt = [];
        private long _firstSettledAt = -1;
        private readonly TaskCompletionSource _bothStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        /// <summary>The instant recorded each time the ct is canceled (= each time a run is notified), from a shared monotonic clock.</summary>
        public IReadOnlyList<long> SignalledAt { get { lock (_signalledAt) return [.. _signalledAt]; } }

        /// <summary>The instant the first run settles (after settleDelay); -1 while no run has settled yet.</summary>
        public long FirstSettledAt => Interlocked.Read(ref _firstSettledAt);

        /// <summary>Wait until both runs are really parked here before starting the shutdown — otherwise what gets measured is a startup race, not shutdown ordering.</summary>
        public Task BothRunningAsync() => _bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        public async Task<BackupConfig?> GetAsync(int id, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _started) == 2)
                _bothStarted.TrySetResult();

            var stopped = new TaskCompletionSource();
            await using (ct.Register(() =>
            {
                // Record the instant the signal arrives, right here in the callback — don't record it later on in the
                // method, which would conflate two different instants: "I was notified" and "my turn to be processed came".
                lock (_signalledAt) _signalledAt.Add(_clock.ElapsedTicks);
                stopped.TrySetResult();
            }))
                await stopped.Task;

            await Task.Delay(settleDelay, CancellationToken.None);   // both runs dawdle exactly this long before settling
            // Only the first arrival gets written: the -1 sentinel guarantees "the instant of the first settle" is not overwritten by the second run's settle.
            Interlocked.CompareExchange(ref _firstSettledAt, _clock.ElapsedTicks, -1);

            ct.ThrowIfCancellationRequested();
            return null;
        }

        // The read-only ones get an empty answer: other code touches them while the host starts up, and throwing there would just move the test's failure point somewhere irrelevant.
        public Task<IReadOnlyList<BackupConfig>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BackupConfig>>([]);
        public Task<BackupConfig?> FindAsync(int accountId, string containerName, CancellationToken ct = default)
            => Task.FromResult<BackupConfig?>(null);

        // The writing ones are not used by a single line on the shutdown path, so they stay as "if this gets called, something was written wrong".
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
            // This is exactly the branch under examination: the order of the two when the scheduler is enabled.
            builder.UseSetting("Scheduler:Enabled", "true");
            builder.ConfigureServices(services => Captured = services);
        }
    }

    // --- A real round: when a suspend settles, the reason has to land on disk ---

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
    /// A run that really was stopped must write the reason next to the journal when it settles: Task 15 only trusts
    /// the copy on disk, and the in-memory copy dies with the process — and "the process is gone" is exactly the case
    /// it has to handle.
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

    /// <summary>Issue the stop after the 1st upload, then let it through as usual — what we want is "stopped midway", not "upload failed".</summary>
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
