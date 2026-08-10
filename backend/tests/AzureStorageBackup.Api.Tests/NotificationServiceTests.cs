using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzureStorageBackup.Api.Tests;

public sealed class NotificationServiceTests
{
    private sealed class FakeConfigService(NotificationConfig config) : INotificationConfigService
    {
        public Task<NotificationConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(config);
        public Task<NotificationConfig> UpsertAsync(NotificationConfig c, CancellationToken ct = default) => Task.FromResult(c);
    }

    private sealed class CountingSender : INotificationSender
    {
        public int Sends;
        public string? LastTitle;
        public Task SendAsync(NotificationConfig config, string title, string body, CancellationToken ct = default)
        {
            Sends++;
            LastTitle = title;
            return Task.CompletedTask;
        }
    }

    private static (NotificationService Svc, CountingSender Sender) Build(NotificationConfig config)
    {
        var sender = new CountingSender();
        return (new NotificationService(new FakeConfigService(config), sender, NullLogger<NotificationService>.Instance), sender);
    }

    private sealed class RecordingLog : IOperationLog
    {
        public List<(OperationLogLevel Level, string Source, string Message)> Entries { get; } = [];

        public Task AppendAsync(OperationLogLevel level, string source, string message, CancellationToken ct = default, bool? durable = null)
        {
            Entries.Add((level, source, message));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LogEntry>> QueryAsync(
            OperationLogLevel? minLevel, string? source, DateTimeOffset? from, DateTimeOffset? to, int limit,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LogEntry>>([]);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteForContainerAsync(int accountId, string container, CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.CompletedTask;
        public Task TrimAsync(int? maxAgeDays, DateTimeOffset now, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// A rejected notification has to leave a trace the operator can actually see. It used to go only to the
    /// container log — and on a NAS nobody has a shell for that — so "the receiver refused it every time" and
    /// "it was never sent" looked exactly alike from the UI. That is what made a broken JSON payload present
    /// itself as "the success notification does not work".
    /// </summary>
    [Fact]
    public async Task A_rejected_notification_is_recorded_where_the_operator_can_see_it()
    {
        var log = new RecordingLog();
        var svc = new NotificationService(
            new FakeConfigService(new NotificationConfig
            {
                Enabled = true, Url = "https://h/x", Events = NotificationEvents.BackupSuccess,
            }),
            new ThrowingSender(), NullLogger<NotificationService>.Instance, log);

        // Still must not throw: a notification problem cannot be allowed to fail the backup that triggered it.
        await svc.NotifyAsync(NotificationEvents.BackupSuccess, "T", "B");

        var entry = Assert.Single(log.Entries);
        Assert.Equal(OperationLogLevel.Warning, entry.Level);
        Assert.Contains("BackupSuccess", entry.Message, StringComparison.Ordinal);
        Assert.Contains("boom", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sends_When_Enabled_And_Subscribed()
    {
        var (svc, sender) = Build(new NotificationConfig
        {
            Enabled = true, Url = "https://h/x", Events = NotificationEvents.BackupSuccess,
        });

        await svc.NotifyAsync(NotificationEvents.BackupSuccess, "T", "B");

        Assert.Equal(1, sender.Sends);
        Assert.Equal("T", sender.LastTitle);
    }

    [Fact]
    public async Task Does_Not_Send_For_Unsubscribed_Event()
    {
        var (svc, sender) = Build(new NotificationConfig
        {
            Enabled = true, Url = "https://h/x", Events = NotificationEvents.BackupSuccess,
        });

        await svc.NotifyAsync(NotificationEvents.BackupFailure, "T", "B");

        Assert.Equal(0, sender.Sends);
    }

    [Fact]
    public async Task Does_Not_Send_When_Disabled()
    {
        var (svc, sender) = Build(new NotificationConfig
        {
            Enabled = false, Url = "https://h/x", Events = NotificationEvents.BackupSuccess,
        });

        await svc.NotifyAsync(NotificationEvents.BackupSuccess, "T", "B");

        Assert.Equal(0, sender.Sends);
    }

    [Fact]
    public async Task Send_Failure_Is_Swallowed()
    {
        var throwing = new ThrowingSender();
        var svc = new NotificationService(
            new FakeConfigService(new NotificationConfig { Enabled = true, Url = "https://h/x", Events = NotificationEvents.BackupSuccess }),
            throwing, NullLogger<NotificationService>.Instance);

        // Does not throw (a failed notification must not affect the backup)
        await svc.NotifyAsync(NotificationEvents.BackupSuccess, "T", "B");
    }

    private sealed class ThrowingSender : INotificationSender
    {
        public Task SendAsync(NotificationConfig config, string title, string body, CancellationToken ct = default)
            => throw new HttpRequestException("boom");
    }
}
