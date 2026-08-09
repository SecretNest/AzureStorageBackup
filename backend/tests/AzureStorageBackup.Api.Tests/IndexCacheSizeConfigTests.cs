using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AzureStorageBackup.Api.Data;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// `Backup__IndexCacheSize` is the switch that lets a low-memory machine turn the in-process index cache
/// off (the README suggests values).
/// Testing <see cref="VersionIndexMemoryCache"/> alone is not enough — what actually breaks is the
/// **configuration binding**: a mistyped key, or a parse failure silently falling back to the default,
/// leaves the class itself correct and useless. This takes the instance from Program.cs's real wiring.
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

    /// <summary>The low-memory setting: 0 disables it entirely, restoring the behaviour from before this cache existed.</summary>
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

    /// <summary>Unparseable or negative → fall back to the default rather than silently disabling the cache (which would look like the setting took effect).</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-1")]
    public void Invalid_Values_Fall_Back_To_The_Default(string value)
        => Assert.Equal(2, CapacityFor(value));
}
