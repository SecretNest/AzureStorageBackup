using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AzureStorageBackup.Api.Data;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// `Backup__IndexCacheSize` 是让小内存机器能把进程内索引缓存关掉的开关（README 有取值建议）。
/// 光测 <see cref="VersionIndexMemoryCache"/> 这个类不够——真正会坏的是**配置绑定那一环**：
/// 键名写错、解析失败静默变默认值，类本身再正确也没用。这里从 Program.cs 的实际装配里取实例来断言。
/// </summary>
public sealed class IndexCacheSizeConfigTests
{
    private sealed class Factory(string? indexCacheSize) : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseSetting("Scheduler:Enabled", "false");
            builder.UseSetting("Backup:TempPath",
                Path.Combine(Path.GetTempPath(), "asb-cfg-" + Guid.NewGuid().ToString("N")));
            if (indexCacheSize is not null)
                builder.UseSetting("Backup:IndexCacheSize", indexCacheSize);

            builder.ConfigureServices(services =>
            {
                var d = services.SingleOrDefault(x => x.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (d is not null) services.Remove(d);
                services.AddDbContext<AppDbContext>(o => o.UseSqlite(_connection));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _connection.Dispose();
        }
    }

    private static int CapacityFor(string? setting)
    {
        using var factory = new Factory(setting);
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<VersionIndexMemoryCache>().Capacity;
    }

    [Fact]
    public void Unset_Defaults_To_Two_Favouring_Responsiveness()
        => Assert.Equal(2, CapacityFor(null));

    /// <summary>小内存机器的适配值：0 = 完全关闭，行为回到加这层缓存之前。</summary>
    [Fact]
    public void Zero_Disables_The_Cache()
    {
        Assert.Equal(0, CapacityFor("0"));
        using var factory = new Factory("0");
        using var scope = factory.Services.CreateScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<VersionIndexMemoryCache>().Enabled);
    }

    [Fact]
    public void One_Is_The_Half_Memory_Middle_Ground()
        => Assert.Equal(1, CapacityFor("1"));

    /// <summary>无法解析或为负 → 回到默认值，而不是把缓存悄悄关掉（那会让人以为设置生效了）。</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-1")]
    public void Invalid_Values_Fall_Back_To_The_Default(string value)
        => Assert.Equal(2, CapacityFor(value));
}
