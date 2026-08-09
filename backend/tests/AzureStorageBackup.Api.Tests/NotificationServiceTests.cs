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
