using AzureStorageBackup.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 集成测试用工厂：把 AppDbContext 指向一个**每个主机独占的临时文件** SQLite 库，
/// 与真实数据库隔离。可被各端点测试复用。
/// </summary>
public class TestWebAppFactory : WebApplicationFactory<Program>
{
    // 每个测试主机独立的压缩临时区（否则 xUnit 跨类并行时，多主机的 StagingArea 单例共享
    // 默认 /tmp/azurestoragebackup 的 compress/staged，同内容→同压缩输出名→跨主机撞车，导致集成测试并发 flaky）。
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), "asb-test-" + Guid.NewGuid().ToString("N"));

    // 文件库而非 `DataSource=:memory:`。内存库只活在开着它的那条连接上，于是所有 DbContext 都得
    // 共用**同一个** SqliteConnection——而 EF 每建一个 DbContext 都会往连接上注册用户函数，
    // 那条连接上只要还有别人的语句在跑，注册就会摔在 SQLite Error 5
    // （'unable to delete/modify user-function due to active statements'）。
    // 只要有后台 job 与测试主线程同时用库就会撞上，表现为随机失败的集成测试。
    // 文件库让每个 DbContext 开自己的连接，谁也不挡谁，也更贴近生产（生产就是文件 SQLite）。
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "asb-test-" + Guid.NewGuid().ToString("N") + ".db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 测试中不启动常驻调度器，避免后台触发干扰
        builder.UseSetting("Scheduler:Enabled", "false");
        // 隔离压缩临时区（跨并行测试主机不共享磁盘）
        builder.UseSetting("Backup:TempPath", _tempPath);

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null)
                services.Remove(descriptor);

            services.AddDbContext<AppDbContext>(o => o.UseSqlite($"DataSource={_dbPath}"));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            // 连接池攥着文件句柄不放，直接删会在 Windows 上失败，在 Linux 上留下 -wal/-shm。
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
                try { File.Delete(f); } catch { /* best effort */ }
            try { Directory.Delete(_tempPath, recursive: true); } catch { /* best effort */ }
        }
    }
}
