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
builder.Services.AddScoped<IEncryptionService, EncryptionService>();

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

// 引擎组件（无状态用单例；StagingArea 单例以保证压缩全局非并发，跨备份也不并发）。
var tempPath = builder.Configuration["Backup:TempPath"];
if (string.IsNullOrWhiteSpace(tempPath))
    tempPath = Path.Combine(Path.GetTempPath(), "azurestoragebackup");
var stagedLimit = builder.Configuration.GetValue<long?>("Backup:StagedLimitBytes") ?? 1_073_741_824L; // 1GB
builder.Services.AddSingleton(new StagingArea(
    Path.Combine(tempPath, "compress"), Path.Combine(tempPath, "staged"), stagedLimit));

builder.Services.AddSingleton<LocalFileScanner>();
builder.Services.AddSingleton<IFileHasher, FileHasher>();
builder.Services.AddSingleton<BackupDiffer>();
builder.Services.AddSingleton<GroupingPlanner>();
builder.Services.AddSingleton<RetentionEvaluator>();
builder.Services.AddScoped<RetentionCleaner>();
builder.Services.AddSingleton<IFileCompressor>(_ => new SevenZipCompressor());
builder.Services.AddSingleton<IBlobUploader, BlobUploader>();
builder.Services.AddScoped<BackupOrchestrator>();
builder.Services.AddSingleton<BackupRunner>();
builder.Services.AddScoped(sp => new RestoreOrchestrator(
    sp.GetRequiredService<IBlobClientFactory>(),
    sp.GetRequiredService<IBackupInfoStore>(),
    sp.GetRequiredService<IFileCompressor>(),
    sp.GetRequiredService<IFileHasher>(),
    Path.Combine(tempPath, "restore"),
    sp.GetRequiredService<INotifier>()));
builder.Services.AddSingleton<RestoreRunner>();
builder.Services.AddScoped<BackupChecker>();

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

// 骨架阶段用 EnsureCreated 建库；接入 EF 迁移后改为 db.Database.Migrate()。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors(CorsPolicy);

app.MapHealthEndpoints();
app.MapAccountEndpoints();
app.MapContainerEndpoints();
app.MapGroupEndpoints();
app.MapTaskEndpoints();
app.MapBackupsEndpoints();
app.MapBackupConfigEndpoints();
app.MapNotificationEndpoints();
app.MapSystemEndpoints();

app.Run();

// 供集成测试通过 WebApplicationFactory<Program> 引用。
public partial class Program;
