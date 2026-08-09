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
/// **Who** asks for an orphan sweep: the one boolean at each of the two call sites.
/// <para>
/// <see cref="RetentionCleaner"/>'s own tests are pinned term by term over in <c>RetentionCleanerJournalTests</c>,
/// and those cases all hand it <c>sweepOrphans: true</c> directly — so "does anything in production ever pass true" was not
/// pinned down by a single word: flip the <see cref="TaskDispatcher"/> line to <c>false</c>, turn the orchestrator's tail line
/// into a constant, and the whole suite stays green while in the field the blocks left by a cancel/crash are never collected.
/// These cases all go through the real call sites and never touch the <c>sweepOrphans</c> parameter itself.
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

    /// <summary>Plant a data blob nobody references — exactly the kind of thing a cancel/crash leaves behind in a container.</summary>
    private static async Task PlantOrphanAsync(BlobContainerClient container)
        => await container.GetBlobClient("data/orphan").UploadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("nobody references me")), overwrite: true);

    private static async Task<bool> OrphanExistsAsync(BlobContainerClient container)
        => (await container.GetBlobClient("data/orphan").ExistsAsync()).Value;

    /// <summary>
    /// One orchestrator. <paramref name="authority"/> is held by the caller so that one and the same "local authoritative state"
    /// carries across runs — build a fresh one per run and every run gets treated as the first (see <see cref="A_first_run_sweeps_what_the_deleted_config_left_behind"/>).
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
        // Default retention policy (100 versions / 180 days): none of these runs retires a single version, so the only reason
        // left for the cleaner to act is "did anybody ask for a sweep" — precisely what these cases are testing.
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
    /// The scheduled Cleanup task always sweeps orphans. This path is the **only** backstop for those blocks: the backup's own
    /// tail cleanup sweeps only on adoption/voiding/a first run, so if the user never touches that backup again this is the only thing left to collect them.
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

            // Get one version in first: on a container with no committed version at all, the standalone cleanup path returns straight away (half of its test cannot even be read).
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

            // Not a single version retired (default retention policy), and the orphan was still collected.
            Assert.False(await OrphanExistsAsync(container), "the scheduled cleanup must sweep orphans");
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// The backup's tail cleanup does what <see cref="BackupRunControl.SweepNeeded"/> says — both directions have to be pinned:
    /// sweep when a journal was voided, do not sweep when nothing happened.
    /// <para>
    /// Pinning only "it swept when it should have" is not enough: turning that line into the constant <c>true</c> passes just as
    /// well, and that means every backup's tail lists the data/ and packs/ prefixes twice more, a bill paid on every single backup on a container with hundreds of thousands of objects.
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

        // One local authoritative state runs through all three rounds: rounds two and three are therefore not "the first run", and SweepNeeded is decided by the journal alone.
        var authority = new TestLocalAuthority(store);
        try
        {
            Write("big.bin", new string('a', 60_000));
            await using (var c1 = new BackupRunControl(_journals, ConfigId, "run-1"))
                await BuildOrchestrator(authority, store).RunAsync(Request(account, name), null, default, c1);

            // Round two: plant a journal on disk whose terms do not match (a different configId = residue of a config deleted
            // and recreated) → voided when opened → SweepNeeded.
            await PlantOrphanAsync(container);
            await PlantStaleJournalAsync(account.Id, name);
            Write("big.bin", new string('b', 60_000));
            await using (var c2 = new BackupRunControl(_journals, ConfigId, "run-2"))
            {
                await BuildOrchestrator(authority, store).RunAsync(Request(account, name), null, default, c2);
                Assert.True(c2.SweepNeeded);
            }
            Assert.False(await OrphanExistsAsync(container), "a voided journal must trigger the tail sweep");

            // Round three: nothing happened (nothing adopted, nothing voided, and not a first run) → no sweep.
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
    /// Delete the config (keeping the container) and then recreate it on the same container: the first backup's tail must collect the orphans the old config left behind.
    /// <para>
    /// Deleting the config throws away every journal for this container (<c>BackupConfigEndpoints</c>), and from then on those
    /// "in the cloud but not yet in the index" blocks have no protection; the promise the endpoint writes down is "once this
    /// container has a config again, the first cleanup will use the full test to sweep the real orphans away". And the first
    /// cleanup after recreation is precisely the **backup tail** one: at that point the journal directory has just been emptied,
    /// so nothing was adopted and nothing voided — without the "a first run always sweeps" term, that promise is empty.
    /// </para>
    /// <para>
    /// Here "swap in a different local authoritative state" stands in for deleting the config: the delete-config endpoint deletes
    /// exactly that local state (<c>localState.RemoveAsync</c>), and it is exactly what the orchestrator looks at to decide "is this a first run".
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

            // Config deleted: local state gone, journals gone, the container and this block still there.
            await PlantOrphanAsync(container);
            _journals.DeleteAll(account.Id, name);

            // The first run after the config is recreated. Not one journal volume on disk, so the sweep can only be demanded by the "first run" term.
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

    /// <summary>Plant a journal on disk whose terms do not match: opening it voids and deletes it on the spot.</summary>
    private async Task PlantStaleJournalAsync(int accountId, string container)
    {
        await using var j = await _journals.CreateAsync(accountId, container, "run-stale", new JournalHeader
        {
            RunId = "run-stale",
            ConfigId = ConfigId + 1,          // residue left by another config → voided
            StartedAt = DateTimeOffset.UnixEpoch,
            BaselineVersion = 0,
            LocalRoot = _root,
            EncryptionIdentity = "plain",
        }, default);
        await j.AppendAsync(
            new JournalRecord { Kind = "blob", Ref = "data/stale", Path = "stale.bin", FullHash = "stale" }, default);
    }
}
