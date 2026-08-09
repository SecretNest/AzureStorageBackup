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
/// The picker's criteria. There is exactly one: **every volume on disk reads <see cref="SuspendReason.ShuttingDown"/>**.
/// <para>
/// Everything else is left untouched, "no mark" included. No mark means **indeterminate**: a crash, a kill, a shutdown flush that timed out and left it
/// halfway, the operator pressing Cancel (the cancel path still flushes, but deliberately writes no mark), or the mark write itself having failed —
/// all of these look exactly the same on disk, and at least one of them (Cancel) is the user having said "stop" in so many words.
/// Not acting when you cannot tell them apart is the safe side: the UI has a Run button anyway, so a person can resume it whenever he likes.
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

    // No journal = last time it finished (a finished run deletes its own volume) = nothing to pick up.
    [Fact]
    public async Task Nothing_to_resume_when_no_journal_is_left()
    {
        Assert.Empty(await AutoResumeService.PickResumableAsync(Store(), OneConfig, default));
    }

    // The one kind we may resume without asking: a planned process exit stopped it here.
    [Fact]
    public async Task Shutdown_suspended_run_is_resumable()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-boot");
        store.MarkSuspended(1, "photos", "run-boot", SuspendReason.ShuttingDown);
        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default));
    }

    // A volume with no mark could be a crash, a kill, or the operator pressing Cancel — if you cannot tell, do not touch it.
    [Fact]
    public async Task Unmarked_journal_is_left_alone()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-crash");
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default));
    }

    /// <summary>
    /// What the line above concretely refers to, and the **reason** the criteria changed from "resume when unmarked" to "only accept ShuttingDown":
    /// a run the operator stopped with Cancel leaves **exactly** what a crash leaves on disk — journal present, mark absent
    /// (both kinds of cancel flush, and both deliberately write no mark). Under "resume when unmarked", a restart would start the run he just cancelled by hand all over again.
    /// <para>
    /// This case really is indistinguishable on disk from <see cref="Unmarked_journal_is_left_alone"/>, and that is exactly its point;
    /// that such a volume really is produced by the cancel path is verified from a real run by the integration test
    /// <c>A_cancelled_run_leaves_no_mark_and_is_not_picked_up</c> further down this file.
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

    // The gate ran out of patience and stepped down: that transient error is most likely still there (the cable is still unplugged, the cloud is still returning 503),
    // so resuming automatically would just hit the same wall again and then suspend again. Wait for a human to take a look.
    [Fact]
    public async Task Auto_suspended_run_is_left_alone()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-auto");
        store.MarkSuspended(1, "photos", "run-auto", SuspendReason.AutoSuspended);
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default));
    }

    // A pause the user pressed himself: restarting it for him at boot erases his intent.
    [Fact]
    public async Task User_paused_run_is_left_alone()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-user");
        store.MarkSuspended(1, "photos", "run-user", SuspendReason.UserRequested);
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default));
    }

    // One backup left two volumes (the run before last also stopped halfway), and that should start one run, not two:
    // resuming is **a new run**, and when it opens its volume it adopts every still-valid volume itself.
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
    /// Marks are recorded per **volume**, not per config, so one config can perfectly well end up with several volumes whose values disagree:
    /// the operator pauses A into UserRequested → presses Run again → B adopts A's volume when it opens its own → a shutdown stops B
    /// as ShuttingDown. So under one config there is now one UserRequested volume and one ShuttingDown volume.
    /// <para>
    /// Because the criteria require **every** volume to be ShuttingDown, there is no need to invent an arbitration scheme for "which volume is newer and gets the say" —
    /// and the resuming run adopts every still-valid volume, so if one volume should have been left alone, touching anything means touching that one too.
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
    /// Really start a run (only the open-volume and suspend steps, no cloud access), so that "adopting an old volume" actually happens along the production path.
    /// The criteria read per **volume**, and the relationship between volumes and marks only grows along this path — a hand-seeded scene
    /// always gives you "one volume, one mark", which sidesteps precisely what the cases below are trying to say.
    /// </summary>
    private static async Task RunAndSuspendAsync(
        BackupJournalStore store, string runId, SuspendReason reason)
    {
        await using var control = new BackupRunControl(store, 7, runId);
        await control.OpenJournalAsync(
            1, "photos", 0, "/src", "plain", DateTimeOffset.UtcNow, default);
        control.MarkSuspended(reason);
    }

    /// <summary>Every <c>.suspend</c> on disk has to have a journal volume to go with it. An orphan mark raises no error,
    /// it just lies there forever and adds one more unclaimed answer to "what state did this config stop in".</summary>
    private void AssertNoOrphanMarks()
    {
        var dir = Path.Combine(_dir, "1", "photos");
        foreach (var mark in Directory.EnumerateFiles(dir, "*.suspend"))
            Assert.True(
                File.Exists(mark[..^".suspend".Length]),
                $"{Path.GetFileName(mark)} has no journal next to it");
    }

    /// <summary>
    /// **Two planned restarts in a row** both have to be picked up, not just the first.
    /// <para>
    /// The second run takes a completely different path from the first: when it opens its volume it **adopts** the first run's volume and then opens its own,
    /// so this config now has two journal volumes, while the criteria require **every** volume to read ShuttingDown. If the second run's suspension
    /// only writes a mark for its own volume, the first run's volume is left on the "empty mark wiped at adoption time" and the criteria can never be met again —
    /// and the way it breaks is completely silent: the first restart was picked up, so the feature looks fine.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_shutdown_cycles_in_a_row_are_both_picked_up()
    {
        var store = Store();

        await RunAndSuspendAsync(store, "run-1", SuspendReason.ShuttingDown);
        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default));

        // Second round: the run that resumed, adopted run-1's volume, and was then stopped halfway by another shutdown.
        await RunAndSuspendAsync(store, "run-2", SuspendReason.ShuttingDown);
        Assert.Equal(2, (await store.PeekAsync(1, "photos", default)).Count);
        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default));

        // Third round likewise — after the second there should be no such thing as "it stops working from the Nth time on".
        await RunAndSuspendAsync(store, "run-3", SuspendReason.ShuttingDown);
        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default));

        AssertNoOrphanMarks();
    }

    /// <summary>
    /// The moment a volume is adopted, its old owner's mark should retire: the run that wrote that reason has been superseded.
    /// <para>
    /// Adoption only takes the old volume on read-only; the old volume itself is not deleted until the new run **finishes successfully** — and "finishes successfully" is exactly
    /// the step a long-running config finds hardest to reach. If the mark is not cleared at adoption time it sticks around, answering "what state did you stop in"
    /// for the current run with a stale reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Adopting_a_volume_retires_the_mark_of_the_run_it_took_over()
    {
        var store = Store();
        await RunAndSuspendAsync(store, "run-old", SuspendReason.AutoSuspended);

        await using var control = new BackupRunControl(store, 7, "run-new");
        await control.OpenJournalAsync(1, "photos", 0, "/src", "plain", DateTimeOffset.UtcNow, default);

        // The old volume was **adopted**, not voided and deleted — otherwise "the mark is gone" would prove nothing.
        Assert.Equal(2, (await store.PeekAsync(1, "photos", default)).Count);
        Assert.Null(store.ReadSuspendMark(1, "photos", "run-old"));
    }

    /// <summary>
    /// Pressing Run is the operator overruling his own (or the gate's) earlier pause — a planned restart after that should still be resumed.
    /// <para>
    /// The criteria require every volume to be ShuttingDown, and an old volume's mark is only deleted when "some run really does succeed". Without
    /// the two rules "adoption retires the mark" and "suspension writes for the adopted volumes too", a single AutoSuspended / UserRequested would
    /// veto every restart from then on, and **permanently** — until some run finishes. And the ones stuck in that state
    /// are precisely the long-running configs that never get through a whole run, which is the very reason this feature exists.
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

        // The operator pressed Run, and this run then ran into a planned restart.
        await RunAndSuspendAsync(store, "run-after-run", SuspendReason.ShuttingDown);

        Assert.Equal([7], await AutoResumeService.PickResumableAsync(store, OneConfig, default));
        AssertNoOrphanMarks();
    }

    /// <summary>
    /// A declined config must leave a line behind. The deployment shape here is an appliance on a NAS: the operator has no shell, no tool
    /// for looking at mark files, and the switch in the UI is sitting there plainly on. Without that line, "why was the backup not picked up after the restart"
    /// leaves him with nowhere at all to start.
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

    /// <summary>The no-mark class has to be spelled out too, and it has to say "none" rather than name some reason.</summary>
    [Fact]
    public async Task A_declined_config_reports_a_missing_mark_as_none()
    {
        var store = Store();
        await SeedJournalAsync(store, "run-crash");

        var log = new RecordingLogger();
        Assert.Empty(await AutoResumeService.PickResumableAsync(store, OneConfig, default, log));
        Assert.Contains("none", Assert.Single(log.Messages));
    }

    /// <summary>A config that was picked up should not log this line: it is about "why it was not picked up".</summary>
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
    /// The case above only covers **fresh** installs (the CLR default). An upgraded install gets the <c>defaultValue</c> from the
    /// migration, and what the scaffolding generates for <c>AddColumn&lt;bool&gt;</c> is <c>false</c> — without editing that by hand,
    /// old users default to off and new users default to on, which writing only one of the two tests would never reveal.
    /// <para>
    /// And the way that difference gets discovered is particularly bad: no error, no warning, just a human guessing why
    /// one day a backup was not picked up after a planned restart. So this pins the migration step's default value down directly.
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
    /// Automatic resume belongs to the same class as the scheduler, "starts work with nobody pressing anything", so it follows the same switch.
    /// <para>
    /// The immediate benefit is on test hosts: <see cref="TestWebAppFactory"/> starts every hosted service, so with
    /// unconditional registration any integration test that runs long enough could have it start a real backup out of the blue.
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
    /// The registration order matters, for the same reason as the note on <c>GracefulSuspendService</c>: the host stops services in **reverse** registration order,
    /// so shutdown suspension has to be registered **after** automatic resume in order to stop before it — the other way round, automatic resume would still be
    /// awake once shutdown suspension is done, and it could perfectly well start another run right as services are being torn down, with nobody left to suspend that one.
    /// </summary>
    [Fact]
    public void Auto_resume_stops_after_graceful_suspend()
    {
        using var factory = new SchedulerOnFactory();
        _ = factory.Services;   // forces the host to be built; only then is the registration list complete

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
        /// <summary>Record the level along with it: half the meaning of this service's logging is in the **level** (a declined config has to be able to speak up,
        /// a failed auto-resume has to be one notch louder than a successful one), and comparing only the text pins none of that half down.</summary>
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
/// The switch itself: **turn it off and nothing whatsoever starts.**
/// <para>
/// This is the one sentence about this feature that most needs pinning down, because the way it breaks is silent: the operator unticks the box, the UI still reports saved successfully,
/// and then one day after a restart a backup he did not want starts by itself, takes the output lock and burns a night's bandwidth. And this path happens to have two places
/// that would fail silently in exactly the same way — the assignment line in <c>GlobalSettingsService.UpsertAsync</c> (miss it and the value never gets
/// stored), and the early return in <c>ExecuteAsync</c> (miss it and the stored value is never looked at).
/// </para>
/// <para>
/// No Azurite is needed, and no real backup either: the config points at an **account that does not exist**,
/// <c>BackupRunner.RunCoreAsync</c> falls over at the account lookup, and it still registers the run and still releases
/// <c>Completion</c> — so the serializing await is exercised along with it.
/// </para>
/// </summary>
public sealed class AutoResumeSwitchTests : IDisposable
{
    private const string Container = "autoresume-switch";
    private const int AccountId = 987654;   // deliberately an account that does not exist

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
            // Save twice: the first creates the row through the Add branch (the whole object goes into the database as is), and only the second takes the **field-by-field assignment**
            // path — and a missing assignment line there is exactly one of the failure modes this test guards against.
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

        // The scene on disk: one journal volume + one ShuttingDown mark = the picker is certain to pick it.
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
                    // It started: the run registry has it. Wait a moment — after StartAsync there is still the 50 ms delay to get through.
                    var started = await SpinAsync(() => runner.Get(configId) is not null);
                    Assert.True(started, "the setting is on, so the interrupted backup should have started");

                    // This run is bound to fail (no such account), and **a failed auto-resume has to be one notch louder than a successful one**:
                    // nobody is watching this run's outcome, so logging it as Information would bury it in the normal stream.
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
                    // Off: give it plenty of time to do the wrong thing, then confirm it did nothing at all.
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
/// Walk the whole loop for real: run once → shutdown suspension (leaving a journal + a ShuttingDown mark on disk) → the picker picks it
/// → the resuming run **adopts** the old volume instead of voiding it.
/// <para>
/// The unit tests above only prove the predicate; they cannot prove that "resuming really does save what was already uploaded" — and the latter is this feature's
/// entire point. The two cases here verify it against a real Azurite.
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

    /// <summary>Each file's content differs from the others, otherwise the three files would dedup into one blob and the upload count would prove nothing.</summary>
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
            // An upload budget of 1 = only one volume in flight at any moment, which is what makes the moment "stop after the 1st upload" is issued accurate.
            UploadConcurrency = 1,
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    /// <summary>Counts uploads; calls back once on the <c>stopAt</c>-th (the stop comes only after that one is done,
    /// because what we want is "stopped halfway", not "an upload failed").</summary>
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
    /// The full loop: a shutdown stops a run halfway → disk holds a journal + ShuttingDown → the picker picks this config →
    /// the resuming run **adopts** the old volume and re-uploads not one byte of what was already confirmed.
    /// <para>
    /// That last clause is where this test's real weight lies. "Adopted" and "voided" look no different at all to the picker — in both cases
    /// <c>PickResumableAsync</c> picks the config out just the same, the backup finishes just the same, and the cloud ends up correct just the same;
    /// the only difference is **how much was re-uploaded**. So this does not look at whether it finished, it looks at the upload count and at what those index entries point to.
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

            // --- Shutdown: suspend as ShuttingDown once one item has been uploaded ---
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

            // The scene on disk: one journal volume with a ShuttingDown mark beside it.
            var done = (await _journals.ListAsync(account.Id, name, default))[0].Content.Records;
            Assert.NotEmpty(done);
            Assert.True(done.Count < 3, $"the run was supposed to be interrupted, it did all {done.Count}");
            Assert.Equal(
                SuspendReason.ShuttingDown, _journals.ReadSuspendMark(account.Id, name, "run-shutdown"));

            // --- Restart: the picker recognizes this scene ---
            Assert.Equal(
                [11],
                await AutoResumeService.PickResumableAsync(
                    _journals, [(11, account.Id, name)], default));

            // --- Resume: a new runId (BackupRunner generates a fresh one per run), adopting the old volume when opening its own ---
            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming);
            await using (var c2 = new BackupRunControl(_journals, 11, "run-resumed"))
            {
                var result = await o2.RunAsync(Request(account, name), null, default, c2);
                Assert.Equal(1, result.Version);
                Assert.False(c2.Resume.IsEmpty, "the suspended run's journal was voided, not adopted");
                Assert.Equal(done.Count, c2.Resume.RecordCount);
            }

            // Proof of adoption: not one of the items the previous run completed was re-uploaded. Had it been voided, this would be 3.
            Assert.Equal(3 - done.Count, resuming.Uploads);

            // And they really did land in the index, pointing at exactly the blob the previous run uploaded — the re-uploads we saved did not turn into gaps.
            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(
                account, name, info!.Versions[^1].IndexBlob, null, default);
            Assert.Equal(3, index.Entries.Count(e => e.Storage is not null));
            foreach (var r in done)
                Assert.Equal(r.Ref, index.Entries.Single(e => e.Path == r.Path).Storage!.Ref);

            // Wrap-up: both journal volumes retire together, and the mark should not be left behind either — if it were, the next restart would resume another run on the strength of it.
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
            Assert.Null(_journals.ReadSuspendMark(account.Id, name, "run-shutdown"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The cancel path really does **write no mark** — this is the factual basis for the "leave unmarked volumes alone" criterion, not a guess.
    /// <para>
    /// If that ever stopped holding (say someone casually made cancel write a mark too), the unit test
    /// <c>A_cancelled_run_looks_exactly_like_a_crash_and_is_left_alone</c> would be saying nothing at all,
    /// and it would fall away without a sound: the criteria would still be "right", only the person they protect would be gone.
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

            // The journal was flushed (cancel flushes too) and there is no mark — exactly the scene a crash leaves.
            Assert.NotEmpty(await _journals.ListAsync(account.Id, name, default));
            Assert.Null(_journals.ReadSuspendMark(account.Id, name, "run-cancelled"));

            // So a restart should not start it up again on his behalf.
            Assert.Empty(await AutoResumeService.PickResumableAsync(
                _journals, [(12, account.Id, name)], default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
