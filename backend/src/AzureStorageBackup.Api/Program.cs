using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Endpoints;
using AzureStorageBackup.Api.Services;
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

// --- Azure Blob Storage ---
// 连接串来自配置 / 环境变量；未配置时回退到本地 Azurite 开发存储，保证进程可启动。
var storageConn = builder.Configuration.GetConnectionString("AzureStorage");
if (string.IsNullOrWhiteSpace(storageConn))
    storageConn = builder.Configuration["Azure:Storage:ConnectionString"];
if (string.IsNullOrWhiteSpace(storageConn))
    storageConn = "UseDevelopmentStorage=true";
builder.Services.AddSingleton(_ => new BlobServiceClient(storageConn));

// --- 业务服务 ---
builder.Services.AddScoped<IAzureStorageService, AzureStorageService>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddSingleton<IBlobClientFactory, BlobClientFactory>();
builder.Services.AddScoped<IContainerService, ContainerService>();
builder.Services.AddScoped<IGroupService, GroupService>();
builder.Services.AddScoped<IScheduledTaskService, ScheduledTaskService>();
builder.Services.AddScoped<IBackupInventoryService, BackupInventoryService>();
builder.Services.AddScoped<IBackupConfigService, BackupConfigService>();

// 备份引擎（M4）：7z 编解码 + 信息文件/索引读写。codec 按需构造（首次解析时探测 7z）。
builder.Services.AddSingleton<IArchiveCodec>(_ => new SevenZipArchiveCodec());
builder.Services.AddScoped<IBackupInfoStore, BackupInfoStore>();
builder.Services.AddScoped<ILocalIndexCache, LocalIndexCache>();
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
    sp.GetService<ILogger<DeadWeightCompactor>>()));
builder.Services.AddScoped<RetentionCleaner>();
builder.Services.AddSingleton<IFileCompressor>(_ => new SevenZipCompressor());
builder.Services.AddSingleton<IBlobUploader, BlobUploader>();
builder.Services.AddSingleton<ProcessingVerifier>();
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

// 调度器（M6）：常驻后台按 cron 触发计划任务。测试环境用 Scheduler:Enabled=false 关闭。
builder.Services.AddSingleton<TaskDispatcher>();
if (builder.Configuration.GetValue("Scheduler:Enabled", true))
    builder.Services.AddHostedService<SchedulerService>();

// --- CORS（开发时前端 dev server 直连用；生产走 nginx 反代同源）---
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173"];
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));

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
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(CorsPolicy);

// 托管构建后的前端静态资源（Docker 镜像里位于 wwwroot）；开发时 wwwroot 为空，前端走 Vite。
app.UseDefaultFiles();
app.UseStaticFiles();

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
app.MapFallbackToFile("index.html");

app.Run();

// 供集成测试通过 WebApplicationFactory<Program> 引用。
public partial class Program;
