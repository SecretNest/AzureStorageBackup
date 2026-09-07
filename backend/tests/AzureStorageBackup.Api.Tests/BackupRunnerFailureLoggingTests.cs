using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// A backup started from the UI that fails used to leave nothing but its message behind: the run state and the
/// config's LastError both carry <c>ex.Message</c>, and neither carries the stack. For an exception raised by the
/// pipeline's own code the message names the cause well enough, but the failure this was written for did not —
/// <c>SQLite Error 5: 'not an error'</c> is the wording SQLite produces when two threads share one connection, and
/// which call was on the wire when it happened is exactly what the message does not say. A scheduled run gets its
/// trace from TaskDispatcher's catch; a run pressed by hand had no equivalent, so the container log — the one
/// place an operator on a NAS can read — was empty at the moment it mattered.
/// </summary>
public sealed class BackupRunnerFailureLoggingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-runfail-" + Guid.NewGuid().ToString("N"));

    public BackupRunnerFailureLoggingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task A_failed_run_reaches_the_log_with_its_exception_attached()
    {
        var log = new CapturingLoggerProvider();
        using var factory = new LoggedFactory(log);

        // An account id nothing answers to: the run passes the sentinel gate (the root exists) and dies on the
        // account lookup, before any blob or 7z work — the same path a config orphaned by a deleted account takes.
        const int missingAccount = 987_654;
        int configId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var config = new BackupConfig
            {
                AccountId = missingAccount, ContainerName = "orphaned", Name = "Orphaned", LocalRoot = _root,
            };
            db.BackupConfigs.Add(config);
            await db.SaveChangesAsync();
            configId = config.Id;
        }

        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var state = await runner.StartAsync(configId);
        await state.Completion.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(RunStatus.Failed, state.Status);
        Assert.Contains($"Account {missingAccount} not found", state.Error);

        // The trace, not just the words: the entry must carry the exception object, which is what puts the stack
        // into the container log. Matching on the message alone would pass for a line that logged ex.Message only.
        var entry = Assert.Single(log.Entries, e =>
            e.Exception is not null
            && e.Exception.Message.Contains($"Account {missingAccount} not found", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(configId.ToString(), entry.Message);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

        private readonly List<Entry> _entries = [];
        public IReadOnlyList<Entry> Entries { get { lock (_entries) return [.. _entries]; } }

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
                lock (owner._entries) owner._entries.Add(new Entry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed class LoggedFactory(CapturingLoggerProvider provider) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddLogging(b => b.AddProvider(provider)));
        }
    }
}
