using AzureStorageBackup.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 集成测试用工厂：把 AppDbContext 覆盖为 in-memory SQLite（连接保持打开），
/// 与真实文件数据库隔离。可被各端点测试复用。
/// </summary>
public class TestWebAppFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    // 每个测试主机独立的压缩临时区（否则 xUnit 跨类并行时，多主机的 StagingArea 单例共享
    // 默认 /tmp/azurestoragebackup 的 compress/staged，同内容→同压缩输出名→跨主机撞车，导致集成测试并发 flaky）。
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), "asb-test-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

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

            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            try { Directory.Delete(_tempPath, recursive: true); } catch { /* best effort */ }
        }
    }
}
