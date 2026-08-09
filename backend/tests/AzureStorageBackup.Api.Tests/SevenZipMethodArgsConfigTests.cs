using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// `Backup__SevenZipMethodArgs` lets an operator change the compression algorithm, shrink the dictionary and cap threads to suit their own machine's CPU/memory.
/// Testing the <see cref="SevenZipCompressor"/> class alone is not enough — the link that actually breaks is **configuration binding**:
/// misspell the key and it silently falls back to -mx9, so the setting looks like it took effect when it did not. Here the instance is taken from Program.cs's real wiring to assert on.
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
    /// A malformed value must bring the application down **at startup**. The DI factory is lazy, and if this were put
    /// off until the first backup blew up, what the user sees is "installed and working" until some night's scheduled
    /// backup fails — and this machine is a NAS, where the user has no command line to go and find out what went wrong.
    /// </summary>
    [SkippableTheory]
    [InlineData("-o/tmp/evil")] // not a method switch: it would rewrite the extraction output location
    [InlineData("-mx9 -y")]     // a non -m switch mixed in
    [InlineData("-m")]          // just -m, with nothing after it
    public void A_Bad_Value_Fails_At_Startup_Not_Mid_Backup(string bad)
    {
        Skip.IfNot(SevenZipCli.TryResolveExecutable() is not null, "7z not found");
        Assert.Throws<ArgumentException>(() => ArgsFor(bad));
    }
}
