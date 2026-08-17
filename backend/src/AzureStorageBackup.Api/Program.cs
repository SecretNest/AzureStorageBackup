using System.Diagnostics;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Endpoints;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicy = "frontend";

// --- Data layer: SQLite ---
var sqliteConn = builder.Configuration.GetConnectionString("Sqlite");
if (string.IsNullOrWhiteSpace(sqliteConn))
    sqliteConn = "Data Source=data/app.db";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(sqliteConn));

// --- Data Protection (reversible encryption of sensitive values), keyring persisted to a local volume ---
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(keysPath))
    keysPath = "keys";
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath));
// Singletons: EncryptionService is stateless and IDataProtector is thread-safe; besides, the singleton BlobClientFactory depends on ISecretReader,
// and injecting a scoped one would trip the scope validation exception at startup.
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
builder.Services.AddSingleton<ISecretReader, SecretReader>();

// --- Business services ---
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddSingleton<IBlobClientFactory, BlobClientFactory>();
builder.Services.AddScoped<IContainerService, ContainerService>();
builder.Services.AddScoped<IGroupService, GroupService>();
builder.Services.AddScoped<IScheduledTaskService, ScheduledTaskService>();
builder.Services.AddScoped<IBackupInventoryService, BackupInventoryService>();
builder.Services.AddScoped<IBackupConfigService, BackupConfigService>();

// CPU priority of each 7z process, read live from GlobalSettings (one short read inside a scope), the same shape as the StagingArea
// limit below: save the change in the UI and the next 7z process runs at the new level, no container restart needed.
// A failure to read the settings must never drag compression down with it — this is only a performance preference, so if it cannot be read, go with the default "lowest".
static Func<ProcessPriorityClass> SevenZipPriority(IServiceProvider sp) => () =>
{
    try
    {
        using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IGlobalSettingsService>()
            .GetAsync().GetAwaiter().GetResult();
        return settings.SevenZipPriority.ToProcessPriorityClass();
    }
    catch
    {
        return SevenZipCpuPriority.Lowest.ToProcessPriorityClass();
    }
};

// Backup engine (M4): 7z codec + info file/index reading and writing. The codec is constructed on demand (7z is probed on the first resolve).
builder.Services.AddSingleton<IArchiveCodec>(sp => new SevenZipArchiveCodec(priority: SevenZipPriority(sp)));
builder.Services.AddScoped<IBackupInfoStore, BackupInfoStore>();
builder.Services.AddScoped<ILocalIndexCache, LocalIndexCache>();
// Cache of deserialized version indexes (singleton, shared across requests). The default of 2 entries favours responsiveness: tree browsing in the
// restore dialog and version comparison hit the same index, so a click no longer rebuilds the whole index (measured at about 0.9 s / 350 MB for 500k entries).
// The cost is resident memory (about 190 MB per index @ 500k entries); on a low-memory machine set Backup__IndexCacheSize=0 to turn it off entirely.
builder.Services.AddSingleton(new VersionIndexMemoryCache(
    int.TryParse(builder.Configuration["Backup:IndexCacheSize"], out var indexCacheSize) && indexCacheSize >= 0
        ? indexCacheSize
        : 2));
builder.Services.AddScoped<ILocalBackupStateStore, LocalBackupStateStore>();
builder.Services.AddScoped<TrackedInfoStore>();

// Engine components (singletons where stateless; StagingArea is a singleton so that compression is globally non-concurrent, across backups too).
var tempPath = builder.Configuration["Backup:TempPath"];
if (string.IsNullOrWhiteSpace(tempPath))
    tempPath = Path.Combine(Path.GetTempPath(), "azurestoragebackup");
builder.Services.AddSingleton(sp =>
{
    var compress = Path.Combine(tempPath, "compress");
    var staged = Path.Combine(tempPath, "staged");
    // The limit is read live from GlobalSettings (one short read inside a scope), decision 4: a Settings change takes effect immediately.
    long Limit()
    {
        using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IGlobalSettingsService>().GetAsync().GetAwaiter().GetResult();
        return settings.StagedLimitBytes > 0 ? settings.StagedLimitBytes : 2L * 1024 * 1024 * 1024;
    }
    return new StagingArea(compress, staged, Limit);
});
// File backend for the verbose per-file debug log (text files per backup and per date, PRD 3.6).
builder.Services.AddSingleton(new VerboseFileLog(Path.Combine(tempPath, "verbose-logs")));

// The journal lives **next to the database file**, not under tempPath: without Backup:TempPath the latter is /tmp, which is gone
// as soon as the container is recreated — and the entire reason the journal exists is to "still know where the last run got to after the container is recreated". Following the
// database, it naturally lands on the same persistent volume, and the user does not have to set an extra environment variable just to make crash recovery work.
// Also note it must **not** be cleared at startup: what it records is exactly the content that is "already in the cloud but not yet in the index", and clearing it makes recovery pointless.
var journalRoot = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(sqliteConn).DataSource)) ?? ".",
    "journal");
builder.Services.AddSingleton(new BackupJournalStore(journalRoot));

// Spill area for the diff→upload queue. The write side never blocks: whatever memory cannot hold spills here, so diff can run all the way to the end —
// which is the precondition for showing a remaining time during the upload stage (the denominator, SetTotal, is only fixed once diff finishes, see StageProgress.Eta).
var spillDir = Path.Combine(tempPath, "diff-spill");
// Spill files left behind by the last abnormal exit (container killed, power loss) are cleared here.
// This has to happen at **process startup**, not at the start of every backup: several backups can be running at once,
// and clearing per run would delete files someone else is writing. A normal finish deletes its own (DiffWorkQueue.Dispose).
DiffWorkQueue.ClearStale(spillDir);
// Same reasoning: compression intermediates and staged volumes left by the last abnormal exit are cleared here too.
// Recovery leans on the journal (content confirmed in the cloud), not on these local half-products.
StagingArea.ClearStale(Path.Combine(tempPath, "compress"), Path.Combine(tempPath, "staged"));
// The two packing limits that are set **per machine**. GroupCapBytes is each backup's own setting and does not belong here —
// these two constrain the memory and the argv ceiling of the 7z process on this machine, and a different machine wants different values.
builder.Services.AddSingleton(new PackLimits(
    // Members per pack. Measured: 7z's member metadata is about 1.3 KB each (independent of the compression level), plus our own
    // PlannedFile at about 0.4 KB, so 20k ≈ 51 MB per pack. Files averaging ≥ 5 KB never reach this limit (100 MB arrives first).
    int.TryParse(builder.Configuration["Backup:MaxPackMembers"], out var packMembers) && packMembers > 0
        ? packMembers
        : 20_000,
    // Bytes of member paths on the 7z command line, per pack. Members are passed one by one as argv, and going over gets a flat E2BIG from the kernel.
    // Measured argv ceiling for a single exec: 1.73 MB (ARG_MAX 2 MB / stack 8 MB), so this leaves about 40% headroom.
    long.TryParse(builder.Configuration["Backup:MaxPackPathBytes"], out var packPathBytes) && packPathBytes > 0
        ? packPathBytes
        : 1_000_000));

int DiffQueueInt(string key, int fallback) =>
    int.TryParse(builder.Configuration[$"Backup:DiffQueue{key}"], out var v) && v > 0 ? v : fallback;
long DiffQueueLong(string key, long fallback) =>
    long.TryParse(builder.Configuration[$"Backup:DiffQueue{key}"], out var v) && v > 0 ? v : fallback;

builder.Services.AddSingleton(new DiffWorkQueueFactory(spillDir, new DiffQueueLimits(
    // The r stage (in memory, waiting to be picked up): the item count is the main dial, the bytes are the backstop, whichever hits first wins.
    // 2000 items — a backup of 200k~500k files comes to a few thousand items once packed, so in most cases only a little touches disk.
    // 64 MB — the insurance for the small-file case: a 100 MB pack of 5 KB files is twenty thousand members,
    // and counting items alone, 2000 of them could reach tens of GB.
    MaxCachedItems: DiffQueueInt("MaxItems", 2_000),
    MaxCachedBytes: DiffQueueLong("MemoryBytes", 64L * 1024 * 1024),
    // The w stage (waiting to be written to disk in batches): 1/8 of the r stage. It is in memory too, so leaving it unbounded is a back door around the r stage's budget.
    WriteBatchItems: DiffQueueInt("WriteBatchItems", 200),
    WriteBatchBytes: DiffQueueLong("WriteBatchBytes", 8L * 1024 * 1024),
    // How many items to fetch from disk at a time. The read-back sits on the consumer's critical path, so batching amortizes the lock and the Flush.
    RefillBatchItems: DiffQueueInt("RefillBatch", 1_000),
    // Stream buffer for the temp file. This is where the write side's real batching mostly happens.
    FileBufferBytes: DiffQueueInt("FileBufferBytes", 256 * 1024))));

builder.Services.AddSingleton<LocalFileScanner>();
builder.Services.AddSingleton<IFileHasher, FileHasher>();
builder.Services.AddSingleton<BackupDiffer>();
builder.Services.AddSingleton<GroupingPlanner>();
builder.Services.AddSingleton<RetentionEvaluator>();
builder.Services.AddSingleton(sp => new DeadWeightCompactor(
    sp.GetRequiredService<IBlobUploader>(),
    sp.GetRequiredService<IFileCompressor>(),
    sp.GetRequiredService<IFileHasher>(),
    Path.Combine(tempPath, "compact"),
    sp.GetRequiredService<StagingArea>(),
    sp.GetService<ILogger<DeadWeightCompactor>>()));
builder.Services.AddScoped<RetentionCleaner>();
// The compression method arguments are tunable (Backup__SevenZipMethodArgs, e.g. "-mx7 -md=32m -mmt=2"): the default -mx9 is the most space-efficient,
// but CPU and memory are both scarce on a NAS, so switching algorithm / shrinking the dictionary / capping threads is a very real need. Only arguments starting with -m are accepted —
// the other switches decide how we talk to 7z, and making them configurable would let one slip of the hand wreck our output parsing.
// Archives are self-describing, so a change here does not affect restoring existing versions.
// Validate right here: the DI factory is lazy, and without this a mistyped value would not blow up until the first backup actually runs.
var sevenZipMethodArgs = builder.Configuration["Backup:SevenZipMethodArgs"];
SevenZipCompressor.ValidateMethodArgs(sevenZipMethodArgs);
builder.Services.AddSingleton<IFileCompressor>(sp =>
    new SevenZipCompressor(methodArgs: sevenZipMethodArgs, priority: SevenZipPriority(sp)));
builder.Services.AddSingleton<IBlobUploader, BlobUploader>();
builder.Services.AddScoped<BackupOrchestrator>();
builder.Services.AddSingleton<BackupBusyTracker>();
builder.Services.AddSingleton<BackupRunner>();
builder.Services.AddScoped(sp => new RestoreOrchestrator(
    sp.GetRequiredService<IBlobClientFactory>(),
    sp.GetRequiredService<IBackupInfoStore>(),
    sp.GetRequiredService<IFileCompressor>(),
    sp.GetRequiredService<IFileHasher>(),
    Path.Combine(tempPath, "restore"),
    sp.GetRequiredService<INotifier>(),
    sp.GetRequiredService<IOperationLog>()));
builder.Services.AddSingleton<RestoreRunner>();
builder.Services.AddSingleton<RepairRunner>();
builder.Services.AddSingleton<CheckRunner>();
builder.Services.AddScoped(sp => new BackupChecker(
    sp.GetRequiredService<IBlobClientFactory>(),
    sp.GetRequiredService<IBackupInfoStore>(),
    sp.GetRequiredService<IFileCompressor>(),
    sp.GetRequiredService<IFileHasher>(),
    Path.Combine(tempPath, "check"),
    sp.GetRequiredService<INotifier>(),
    sp.GetRequiredService<IOperationLog>(),
    sp.GetRequiredService<TrackedInfoStore>())); // metadata drift comparison uses the local-authority cache
builder.Services.AddScoped(sp => new BackupRepairer(
    sp.GetRequiredService<IBlobClientFactory>(),
    sp.GetRequiredService<IBackupInfoStore>(),
    sp.GetRequiredService<IFileCompressor>(),
    sp.GetRequiredService<IFileHasher>(),
    sp.GetRequiredService<IBlobUploader>(),
    Path.Combine(tempPath, "repair"),
    sp.GetRequiredService<StagingArea>(),
    sp.GetRequiredService<INotifier>(),
    sp.GetRequiredService<IOperationLog>(),
    sp.GetRequiredService<BackupChecker>(),
    sp.GetRequiredService<TrackedInfoStore>(),
    sp.GetRequiredService<ILocalIndexCache>())); // repair goes through the local-authority state machine, so the next backup does not hit a 412 (§3.2)

// Operation log (M8) + global settings
builder.Services.AddScoped<IOperationLog, OperationLogService>();
builder.Services.AddScoped<IGlobalSettingsService, GlobalSettingsService>();

// Notifications (M7)
builder.Services.AddScoped<INotificationConfigService, NotificationConfigService>();
builder.Services.AddSingleton<INotificationSender, NotificationSender>();
builder.Services.AddScoped<INotifier, NotificationService>();

// Keyring health assessment (design §3.2)
builder.Services.AddSingleton<IKeyringHealth, KeyringHealth>();
builder.Services.AddScoped<KeyringProbe>();
builder.Services.AddScoped<KeyringRecovery>();

// Scheduler (M6): a resident background service that fires scheduled tasks by cron. Test environments turn it off with Scheduler:Enabled=false.
builder.Services.AddSingleton<TaskDispatcher>();
if (builder.Configuration.GetValue("Scheduler:Enabled", true))
    builder.Services.AddHostedService<SchedulerService>();

// After startup, continue the backups that a planned exit interrupted last time (Task 15). It **follows the scheduler's switch** rather than being
// registered unconditionally the way GracefulSuspendService is — the two are not the same thing: shutdown suspension merely preserves the scene on the way down,
// while this one **actively starts a real backup run**, which puts it in the same class as the scheduler, "starts work with nobody pressing anything", and it should answer to the same switch.
// The immediate benefit is on test hosts: TestWebAppFactory starts every hosted service, so with unconditional registration
// any integration test that runs long enough could have it start a real backup out of the blue, colliding with the output lock and the container.
//
// Registered after the scheduler and before GracefulSuspendService: the host stops services in reverse registration order, so shutdown suspension stops before this one
// and suspends and flushes the run this one has just started — and since it is waiting for that run's terminal state, the wait ends with it.
var autoResumeRegistered = builder.Configuration.GetValue("Scheduler:Enabled", true);
if (autoResumeRegistered)
    builder.Services.AddHostedService<AutoResumeService>();

// On a planned exit (docker stop / upgrade restart), suspend the running backup and flush it to disk.
// **Registered unconditionally**, and it has to come after the scheduler: the host stops services in reverse registration order, so registering later means stopping earlier —
// otherwise the scheduler could start another run halfway through the suspension. This path must work with the scheduler off as well, so it does not sit inside that if.
builder.Services.AddHostedService<GracefulSuspendService>();

// The default 5 seconds is not enough: the suspension itself only writes a few dozen bytes, but first every worker has to come out of its current step.
// Raised to 30 seconds, to go with docker-compose's stop_grace_period — both sides have to be widened, because the shorter one gets the say.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

// Local path boundary (design §3): with Backup:Root unconfigured there is no boundary and behavior is unchanged.
// It is constructed **eagerly** here (rather than left to the container's lazy resolution) so that "a root was configured but cannot be resolved" blows up at startup
// instead of waiting for the first request — a dead boundary is a configuration error, and the sooner it surfaces the better.
builder.Services.AddSingleton(new PathBoundary(builder.Configuration));

// --- Preset-password access control (design §2/§3) ---
var authGate = new AuthGate(builder.Configuration);
builder.Services.AddSingleton(authGate);

if (authGate.Required)
{
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "asb_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // The image listens on HTTP by default; hardcoding Always would stop the browser from sending the cookie back at all,
            // with the symptom "login succeeds and immediately asks you to log in again". Following the request's scheme is the right call.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            // SPA + fetch: return 401 when unauthenticated; a redirect would just hand fetch a page of HTML.
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

    // Protected by default: endpoints added later are covered automatically, so the cost of forgetting one is "one extra thing blocked" rather than "a hole left open".
    builder.Services.AddAuthorization(options =>
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());
}

// --- CORS (for the frontend dev server connecting directly during development; in production the backend serves the built SPA out of wwwroot, so it is same-origin and needs no CORS) ---
// The dev-server origin is written only in appsettings.Development.json (the single source of truth); unconfigured means an empty list —
// we are certainly not going to have production allow a credentialed cross-origin request from a localhost address by default.
var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
// A wildcard origin plus AllowCredentials() is a combination the CORS protocol forbids, and keeping it would make every cross-origin request a 500
// (the policy is built lazily, so it does not fail at startup, only on the first request carrying an Origin).
// Before AllowCredentials() was added in this round, "*" was a legal configuration, so all we can do is drop it and warn — an existing configuration must not simply break.
var hasWildcardOrigin = configuredOrigins.Contains("*");
var allowedOrigins = hasWildcardOrigin
    ? configuredOrigins.Where(o => o != "*").ToArray()
    : configuredOrigins;
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddOpenApi();

var app = builder.Build();

// Make sure the directory holding the SQLite file exists (the connection string looks like "Data Source=data/app.db").
var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(sqliteConn).DataSource;
var dataDir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
if (!string.IsNullOrEmpty(dataDir))
    Directory.CreateDirectory(dataDir);

// Write-ahead logging, before anything else opens the database. A backup writes to it for hours on end
// while the UI polls and the scheduler reads; under SQLite's default journal those two shut each other out
// and the backup dies with "database is locked" (see SqliteJournalMode). The mode is persisted in the file
// header, so this one call covers every connection the process will open.
// The result is logged rather than assumed: WAL needs a local filesystem, and on a network share the PRAGMA
// quietly leaves the old mode in place — better to say so on line one of the log than to leave the operator
// wondering why the locking never went away.
var journalMode = SqliteJournalMode.Enable(sqliteConn);
app.Logger.Log(
    journalMode == "wal" ? LogLevel.Information : LogLevel.Warning,
    "SQLite journal mode is {JournalMode}", journalMode);

// Create/upgrade the database from the EF migrations at startup (migration history included). A database created by the old EnsureCreated has no migration history and must be deleted and rebuilt (there are no deployments yet).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // Keyring health assessment (design §3.2): purely local, no cloud access.
    var status = await scope.ServiceProvider.GetRequiredService<KeyringProbe>().EvaluateAsync();
    app.Services.GetRequiredService<IKeyringHealth>().Set(status);
    if (status == KeyringStatus.Lost)
        app.Services.GetRequiredService<ILogger<Program>>().LogError(
            "Data protection keyring cannot decrypt stored secrets; entering recovery mode.");
}

if (hasWildcardOrigin)
    app.Logger.LogWarning(
        "Cors:AllowedOrigins contains \"*\", which cannot be combined with credentials; the wildcard entry was ignored. "
            + "List every allowed origin explicitly.");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(CorsPolicy);

// Serve the built frontend's static assets (located at wwwroot in the Docker image); during development wwwroot is empty and the frontend goes through Vite.
app.UseDefaultFiles();
app.UseStaticFiles();

if (authGate.Required)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
else
{
    app.Logger.LogWarning("Authentication is disabled: Auth__Password is not set.");
}

// With the scheduler off, the "Resume interrupted backups on startup" switch on the Settings page is **dead** —
// automatic resume follows the same switch (see the registration above). The UI still draws it as on, because it reads that bit out of the
// database and cannot see the deployment-side Scheduler__Enabled. Say so out loud, otherwise the difference only surfaces the day a restart fails to pick a backup up,
// and by then the first thing anyone suspects is the switch that is plainly on.
if (!autoResumeRegistered)
{
    app.Logger.LogInformation(
        "Automatic resume of interrupted backups is off because the scheduler is disabled "
        + "(Scheduler__Enabled=false), whatever the setting on the Settings page says. "
        + "Interrupted backups wait for you to press Run.");
}

// Defense in depth (design §3.1): any SecretUnavailableException that slips through is mapped uniformly to 409 keyring_lost
// rather than a bare 500. It has to be queued before the endpoints in order to wrap their execution.
app.UseSecretUnavailableMapping();

app.MapAuthEndpoints();
app.MapHealthEndpoints();
app.MapAccountEndpoints();
app.MapContainerEndpoints();
app.MapGroupEndpoints();
app.MapTaskEndpoints();
app.MapBackupsEndpoints();
app.MapBackupConfigEndpoints();
app.MapNotificationEndpoints();
app.MapLogEndpoints();
app.MapSettingsEndpoints();
app.MapSystemEndpoints();

// Client-side frontend routes fall back to index.html (unmatched non-/api paths); with no index.html in wwwroot this returns 404 (harmless during development).
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Exposed so that integration tests can reference it through WebApplicationFactory<Program>.
public partial class Program;
