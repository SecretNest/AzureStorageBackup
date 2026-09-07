using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// <c>Backup__IndexCacheSize</c> used to size the in-process <see cref="VersionIndexMemoryCache"/> that sat in
/// front of the on-disk version index. That index is gone: <see cref="IVersionCatalogs"/> reads versions from the
/// SQLite catalog on demand, so there is nothing left for a deserialized-object cache to shortcut. The setting is
/// retired, not removed from configuration binding — an operator's existing environment variable must not turn
/// into a startup failure, so this asserts the host still starts and logs one line saying the value no longer
/// does anything, rather than silently ignoring it or throwing on an unrecognised key.
/// </summary>
public sealed class IndexCacheSizeConfigTests
{
    /// <summary>Same technique as <c>BackupRunnerFailureLoggingTests.CapturingLoggerProvider</c>: attach a provider
    /// to the host's own logging pipeline rather than parsing container output, so the assertion is on the actual
    /// log record (level and text), not a side channel.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];
        public IReadOnlyList<(LogLevel Level, string Message)> Entries { get { lock (_entries) return [.. _entries]; } }

        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._entries) owner._entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class Factory(string? indexCacheSize, CapturingLoggerProvider log) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (indexCacheSize is not null)
                builder.UseSetting("Backup:IndexCacheSize", indexCacheSize);
            builder.ConfigureServices(services => services.AddLogging(b => b.AddProvider(log)));
        }
    }

    /// <summary>The low-memory setting an operator would have used to disable the old cache. The host must still
    /// start with it present, and <see cref="IVersionCatalogs"/> — the service that replaced the cache's reason
    /// for existing — must resolve normally out of a request scope.</summary>
    [Fact]
    public void Setting_Present_App_Starts_And_Catalogs_Resolve()
    {
        var log = new CapturingLoggerProvider();
        using var factory = new Factory("0", log);
        using var scope = factory.Services.CreateScope();

        var catalogs = scope.ServiceProvider.GetRequiredService<IVersionCatalogs>();
        Assert.NotNull(catalogs);
    }

    [Fact]
    public void Setting_Present_Logs_That_It_No_Longer_Does_Anything()
    {
        var log = new CapturingLoggerProvider();
        using var factory = new Factory("0", log);
        // Force the host to actually start (WebApplicationFactory builds it lazily on first access to Server/Services).
        _ = factory.Server;

        var entry = Assert.Single(log.Entries, e => e.Message.Contains("no longer used", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("Backup__IndexCacheSize=0", entry.Message, StringComparison.Ordinal);
    }

    /// <summary>The common case — the variable was never set — must stay silent: logging the note unconditionally
    /// would tell every operator about a setting they never touched.</summary>
    [Fact]
    public void Setting_Absent_Nothing_Is_Logged()
    {
        var log = new CapturingLoggerProvider();
        using var factory = new Factory(indexCacheSize: null, log);
        _ = factory.Server;

        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("IndexCacheSize", StringComparison.Ordinal));
    }
}
