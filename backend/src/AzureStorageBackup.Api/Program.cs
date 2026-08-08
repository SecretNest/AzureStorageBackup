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

// --- 数据层：SQLite ---
var sqliteConn = builder.Configuration.GetConnectionString("Sqlite");
if (string.IsNullOrWhiteSpace(sqliteConn))
    sqliteConn = "Data Source=data/app.db";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(sqliteConn));

// --- Data Protection（敏感信息可逆加密），密钥环持久化到本地卷 ---
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(keysPath))
    keysPath = "keys";
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath));
// 单例：EncryptionService 无状态、IDataProtector 线程安全；且单例 BlobClientFactory 依赖 ISecretReader，
// 注入 Scoped 会在启动时触发作用域校验异常。
builder.Services.AddSingleton<IEncryptionService, EncryptionService>();
builder.Services.AddSingleton<ISecretReader, SecretReader>();

// --- 业务服务 ---
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddSingleton<IBlobClientFactory, BlobClientFactory>();
builder.Services.AddScoped<IContainerService, ContainerService>();
builder.Services.AddScoped<IGroupService, GroupService>();
builder.Services.AddScoped<IScheduledTaskService, ScheduledTaskService>();
builder.Services.AddScoped<IBackupInventoryService, BackupInventoryService>();
builder.Services.AddScoped<IBackupConfigService, BackupConfigService>();

// 每个 7z 进程的 CPU 优先级，实时从 GlobalSettings 读（带 scope，短读一次），与下面 StagingArea
// 的上限同款：界面上改完保存，下一个 7z 进程就按新档跑，不必重启容器。
// 读设置失败绝不能把压缩带下水——这只是个性能偏好，读不到就按默认的"最低"走。
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

// 备份引擎（M4）：7z 编解码 + 信息文件/索引读写。codec 按需构造（首次解析时探测 7z）。
builder.Services.AddSingleton<IArchiveCodec>(sp => new SevenZipArchiveCodec(priority: SevenZipPriority(sp)));
builder.Services.AddScoped<IBackupInfoStore, BackupInfoStore>();
builder.Services.AddScoped<ILocalIndexCache, LocalIndexCache>();
// 反序列化后的版本索引缓存（单例，跨请求）。默认 2 项＝偏响应速度：还原对话框的树浏览与
// 版本对比都命中同一份索引，免去每次点击都重建整份索引（50 万条目实测约 0.9 s / 350 MB）。
// 代价是常驻内存（约 190 MB/份 @ 50 万条目），小内存机器设 Backup__IndexCacheSize=0 完全关闭。
builder.Services.AddSingleton(new VersionIndexMemoryCache(
    int.TryParse(builder.Configuration["Backup:IndexCacheSize"], out var indexCacheSize) && indexCacheSize >= 0
        ? indexCacheSize
        : 2));
builder.Services.AddScoped<ILocalBackupStateStore, LocalBackupStateStore>();
builder.Services.AddScoped<TrackedInfoStore>();

// 引擎组件（无状态用单例；StagingArea 单例以保证压缩全局非并发，跨备份也不并发）。
var tempPath = builder.Configuration["Backup:TempPath"];
if (string.IsNullOrWhiteSpace(tempPath))
    tempPath = Path.Combine(Path.GetTempPath(), "azurestoragebackup");
builder.Services.AddSingleton(sp =>
{
    var compress = Path.Combine(tempPath, "compress");
    var staged = Path.Combine(tempPath, "staged");
    // 上限实时从 GlobalSettings 读（带 scope，短读一次），决策 4：Settings 改动立即生效。
    long Limit()
    {
        using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IGlobalSettingsService>().GetAsync().GetAwaiter().GetResult();
        return settings.StagedLimitBytes > 0 ? settings.StagedLimitBytes : 2L * 1024 * 1024 * 1024;
    }
    return new StagingArea(compress, staged, Limit);
});
// verbose 逐文件 debug 日志的文件后端（按备份+按日期文本文件，PRD 3.6）。
builder.Services.AddSingleton(new VerboseFileLog(Path.Combine(tempPath, "verbose-logs")));

// journal 放在**库文件旁边**，不放 tempPath 下：后者没配 Backup:TempPath 时是 /tmp，容器重建
// 就没了——而 journal 存在的全部理由正是"容器重建之后还认得出上一轮传到哪了"。跟着库走，
// 它自然落在同一个持久卷上，用户不必为了让崩溃恢复生效而额外配一个环境变量。
// 另注意**不能**在启动时清它：它记的正是"云上已有、索引还没有"的内容，清了等于让恢复白跑。
var journalRoot = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(sqliteConn).DataSource)) ?? ".",
    "journal");
builder.Services.AddSingleton(new BackupJournalStore(journalRoot));

// diff→上传那条队列的溢出区。写侧永不阻塞：内存装不下就落到这里，diff 因此能一路跑到底——
// 这是上传阶段能显示剩余时间的前提（分母 SetTotal 只有 diff 收工才确定，见 StageProgress.Eta）。
var spillDir = Path.Combine(tempPath, "diff-spill");
// 上一次非正常退出（容器被 kill、断电）留下的溢出文件在这里清掉。
// 必须在**进程启动**时清，不能在每次备份开始时清：多个备份可以同时在跑，
// 按运行清会把别人正在写的文件删掉。正常收尾各删各的（DiffWorkQueue.Dispose）。
DiffWorkQueue.ClearStale(spillDir);
// 同理：上次非正常退出留下的压缩中间产物与暂存分卷也在这里清掉。
// 恢复靠的是 journal（云端已确认的内容），不靠这些本地半成品。
StagingArea.ClearStale(Path.Combine(tempPath, "compress"), Path.Combine(tempPath, "staged"));
// 装箱的两条**按机器**定的界。GroupCapBytes 是每个备份自己的设置，不在这里——
// 这两条约束的是这台机器上 7z 进程的内存与 argv 上限，换台机器合适的值就不一样。
builder.Services.AddSingleton(new PackLimits(
    // 每箱成员数。实测 7z 的成员元数据约 1.3 KB/个（与压缩级别无关），加上我们自己的
    // PlannedFile 约 0.4 KB，2 万 ≈ 51 MB/箱。平均 ≥ 5 KB 的文件用不到这条（100 MB 先到）。
    int.TryParse(builder.Configuration["Backup:MaxPackMembers"], out var packMembers) && packMembers > 0
        ? packMembers
        : 20_000,
    // 每箱成员路径在 7z 命令行上的字节数。成员是逐个作为 argv 传的，超了内核直接 E2BIG。
    // 实测单次 exec 的 argv 上限 1.73 MB（ARG_MAX 2 MB / stack 8 MB），这里留了约 40% 余量。
    long.TryParse(builder.Configuration["Backup:MaxPackPathBytes"], out var packPathBytes) && packPathBytes > 0
        ? packPathBytes
        : 1_000_000));

int DiffQueueInt(string key, int fallback) =>
    int.TryParse(builder.Configuration[$"Backup:DiffQueue{key}"], out var v) && v > 0 ? v : fallback;
long DiffQueueLong(string key, long fallback) =>
    long.TryParse(builder.Configuration[$"Backup:DiffQueue{key}"], out var v) && v > 0 ? v : fallback;

builder.Services.AddSingleton(new DiffWorkQueueFactory(spillDir, new DiffQueueLimits(
    // r 段（内存里等着被领走的）：件数是主旋钮，字节是兜底，谁先到算谁。
    // 2000 件——20~50 万文件的备份打包后总件数就在几千，多数情况下只落一点点盘。
    // 64 MB——小文件那一格的保险：一箱 100 MB 装 5 KB 的文件就是两万个成员，
    // 光按件数记，2000 件能到几十 GB。
    MaxCachedItems: DiffQueueInt("MaxItems", 2_000),
    MaxCachedBytes: DiffQueueLong("MemoryBytes", 64L * 1024 * 1024),
    // w 段（等着成批写盘的）：r 段的 1/8。它同样在内存里，不给它设界就等于给 r 段的额度开后门。
    WriteBatchItems: DiffQueueInt("WriteBatchItems", 200),
    WriteBatchBytes: DiffQueueLong("WriteBatchBytes", 8L * 1024 * 1024),
    // 一次从盘上捞多少件。回读在消费侧的关键路径上，成批捞是为了摊平锁与 Flush。
    RefillBatchItems: DiffQueueInt("RefillBatch", 1_000),
    // 临时文件的流缓冲。写侧真正的批量化主要发生在这里。
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
// 压缩方法参数可调（Backup__SevenZipMethodArgs，例如 "-mx7 -md=32m -mmt=2"）：默认 -mx9 最省空间，
// 但 NAS 上 CPU 和内存都是稀缺资源，换算法/缩字典/限线程是很实际的诉求。只收 -m 开头的参数——
// 其余开关决定的是我们怎么和 7z 对话，可配等于让一次手滑毁掉输出解析。
// 归档自描述，改了不影响已有版本的还原。
// 在这里先验一次：DI 工厂是懒的，不验的话一个写错的值要等到第一次备份跑起来才炸。
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
    sp.GetRequiredService<TrackedInfoStore>())); // 元数据漂移比对用本地权威缓存
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
    sp.GetRequiredService<ILocalIndexCache>())); // 修复经本地权威状态机，避免下次备份 412（§3.2）

// 操作日志（M8）+ 全局设置
builder.Services.AddScoped<IOperationLog, OperationLogService>();
builder.Services.AddScoped<IGlobalSettingsService, GlobalSettingsService>();

// 通知（M7）
builder.Services.AddScoped<INotificationConfigService, NotificationConfigService>();
builder.Services.AddSingleton<INotificationSender, NotificationSender>();
builder.Services.AddScoped<INotifier, NotificationService>();

// 密钥环健康判定（设计 §3.2）
builder.Services.AddSingleton<IKeyringHealth, KeyringHealth>();
builder.Services.AddScoped<KeyringProbe>();
builder.Services.AddScoped<KeyringRecovery>();

// 调度器（M6）：常驻后台按 cron 触发计划任务。测试环境用 Scheduler:Enabled=false 关闭。
builder.Services.AddSingleton<TaskDispatcher>();
if (builder.Configuration.GetValue("Scheduler:Enabled", true))
    builder.Services.AddHostedService<SchedulerService>();

// 启动后把上次被计划内退出打断的备份接着跑（Task 15）。**跟着调度器那个开关**，而不是像
// GracefulSuspendService 那样无条件注册——两者不是一回事：关机挂起只是在停机路径上保住现场，
// 而这个会**主动开一轮真备份**，与调度器同属"没人按就自己开工"这一类，该由同一个开关管。
// 直接的好处在测试主机上：TestWebAppFactory 把所有 hosted service 都起起来，无条件注册的话，
// 任何跑够久的集成用例都可能被它冷不丁开一轮真备份，撞上产出锁与容器。
//
// 排在调度器之后、GracefulSuspendService 之前：宿主按注册的逆序停服务，关机挂起因此停在它前面，
// 会把它刚起的那一轮挂起落盘——而它正等着那一轮的终态，等待也就随之结束。
if (builder.Configuration.GetValue("Scheduler:Enabled", true))
    builder.Services.AddHostedService<AutoResumeService>();

// 计划内退出（docker stop / 升级重启）时把在跑的备份挂起落盘。
// **无条件注册**，且必须排在调度器之后：宿主按注册的逆序停服务，排在后面才停在前面——
// 不然调度器可能在挂起进行到一半时又起一轮。调度器关着时这条路径同样要生效，所以不跟着那个 if。
builder.Services.AddHostedService<GracefulSuspendService>();

// 默认 5 秒不够：挂起本身只写几十字节，但要先等每个工作者从当前这步退出来。
// 给到 30 秒，配合 docker-compose 的 stop_grace_period —— 两边都得放宽，短的那个说了算。
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

// 本地路径边界（设计 §3）：Backup:Root 未配置时无边界，行为不变。
// 这里**立即构造**（而不是交给容器懒加载），让「配了根但解析不出来」在启动期就炸掉，
// 而不是拖到第一个请求——边界失效是配置错误，越早暴露越好。
builder.Services.AddSingleton(new PathBoundary(builder.Configuration));

// --- 预置密码访问控制（设计 §2/§3）---
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
            // 镜像默认监听 HTTP；硬编码 Always 会让浏览器根本不回传 cookie，
            // 症状是「登录成功但立刻又被要求登录」。跟随请求协议才对。
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            // SPA + fetch：未认证返回 401，重定向只会让 fetch 拿到一份 HTML。
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

    // 默认全保护：将来新增端点自动受保护，漏加的后果是「多挡一个」而非「漏开一个洞」。
    builder.Services.AddAuthorization(options =>
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());
}

// --- CORS（开发时前端 dev server 直连用；生产走 nginx 反代同源，不需要 CORS）---
// dev-server 地址只写在 appsettings.Development.json 里（唯一真源）；未配置就是空列表——
// 总不能让生产环境默认放行一个本地地址的带凭据跨域请求。
var configuredOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
// 通配来源 + AllowCredentials() 是 CORS 协议禁止的组合，留着它会让任何跨域请求 500
// （策略是惰性构建的，启动时不报错，只在第一个带 Origin 的请求上炸）。
// 本轮加 AllowCredentials() 之前 "*" 是合法配置，所以只能丢弃它并告警，不能让老配置直接坏掉。
var hasWildcardOrigin = configuredOrigins.Contains("*");
var allowedOrigins = hasWildcardOrigin
    ? configuredOrigins.Where(o => o != "*").ToArray()
    : configuredOrigins;
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddOpenApi();

var app = builder.Build();

// 确保 SQLite 文件所在目录存在（连接串形如 "Data Source=data/app.db"）。
var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(sqliteConn).DataSource;
var dataDir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
if (!string.IsNullOrEmpty(dataDir))
    Directory.CreateDirectory(dataDir);

// 启动时按 EF 迁移建/升级库（含迁移历史）。旧的 EnsureCreated 建的库无迁移历史，需删库重建（当前无部署）。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // 密钥环健康判定（设计 §3.2）：纯本地，不访问云端。
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

// 托管构建后的前端静态资源（Docker 镜像里位于 wwwroot）；开发时 wwwroot 为空，前端走 Vite。
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

// 深度防御（设计 §3.1）：漏网的 SecretUnavailableException 统一映射为 409 keyring_lost，
// 而不是裸 500。必须在端点之前入列才能包住端点执行。
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

// 前端客户端路由回退到 index.html（非 /api 的未匹配路径）；wwwroot 无 index.html 时返回 404（开发无害）。
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// 供集成测试通过 WebApplicationFactory<Program> 引用。
public partial class Program;
