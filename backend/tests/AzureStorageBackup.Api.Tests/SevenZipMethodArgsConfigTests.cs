using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// `Backup__SevenZipMethodArgs` 让运维按自己机器的 CPU/内存换压缩算法、缩字典、限线程。
/// 光测 <see cref="SevenZipCompressor"/> 这个类不够——真正会坏的是**配置绑定那一环**：
/// 键名写错就静默回到 -mx9，设置看着生效其实没有。这里从 Program.cs 的实际装配里取实例来断言。
/// </summary>
public sealed class SevenZipMethodArgsConfigTests
{
    private sealed class Factory(string? methodArgs) : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection.Open();
            builder.UseSetting("Scheduler:Enabled", "false");
            builder.UseSetting("Backup:TempPath",
                Path.Combine(Path.GetTempPath(), "asb-7zcfg-" + Guid.NewGuid().ToString("N")));
            if (methodArgs is not null)
                builder.UseSetting("Backup:SevenZipMethodArgs", methodArgs);

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

    private static IReadOnlyList<string> ArgsFor(string? setting)
    {
        using var factory = new Factory(setting);
        using var scope = factory.Services.CreateScope();
        var compressor = Assert.IsType<SevenZipCompressor>(scope.ServiceProvider.GetRequiredService<IFileCompressor>());
        return compressor.ConfiguredMethodArgs;
    }

    [SkippableFact]
    public void Unset_Keeps_Maximum_Compression()
    {
        Skip.IfNot(SevenZipCli.TryResolveExecutable() is not null, "7z not found");
        Assert.Equal(["-mx9"], ArgsFor(null));
    }

    [SkippableFact]
    public void Configured_Switches_Reach_The_Compressor()
    {
        Skip.IfNot(SevenZipCli.TryResolveExecutable() is not null, "7z not found");
        Assert.Equal(["-mx1", "-md=1m", "-mmt=2"], ArgsFor("-mx1 -md=1m -mmt=2"));
    }

    /// <summary>
    /// 写错的值必须**在启动时**就把应用打下来。DI 工厂是懒的，若拖到第一次备份才炸，
    /// 用户看到的是「装好了、能用」，直到某天夜里的计划备份失败——而这台机器在 NAS 上，
    /// 用户拿不到命令行去看是哪儿错了。
    /// </summary>
    [SkippableTheory]
    [InlineData("-o/tmp/evil")] // 不是方法开关：会改写解压输出位置
    [InlineData("-mx9 -y")]     // 混进了非 -m 开关
    [InlineData("-m")]          // 光一个 -m，没有内容
    public void A_Bad_Value_Fails_At_Startup_Not_Mid_Backup(string bad)
    {
        Skip.IfNot(SevenZipCli.TryResolveExecutable() is not null, "7z not found");
        Assert.Throws<ArgumentException>(() => ArgsFor(bad));
    }
}
