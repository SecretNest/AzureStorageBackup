using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Tests;

public class NotificationEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Get_Returns_A_Config()
    {
        // A singleton configuration: before anything is saved the service returns a default object rather than 404
        var res = await _client.GetAsync("/api/notifications");
        res.EnsureSuccessStatusCode();
        Assert.NotNull(await res.Content.ReadFromJsonAsync<NotificationResponse>());
    }

    [Fact]
    public async Task Put_Then_Get_RoundTrips()
    {
        var req = new NotificationRequest(
            Enabled: true,
            Url: "https://hook.example/notify?t={Title}",
            Method: NotificationMethod.Post,
            BodyTemplate: "{Title}: {Body}",
            ContentType: "application/json",
            Events: NotificationEvents.BackupSuccess | NotificationEvents.BackupFailure,
            ProxyUrl: null);

        var put = await _client.PutAsJsonAsync("/api/notifications", req);
        put.EnsureSuccessStatusCode();

        var cfg = await _client.GetFromJsonAsync<NotificationResponse>("/api/notifications");
        Assert.True(cfg!.Enabled);
        Assert.Equal(NotificationMethod.Post, cfg.Method);
        Assert.Equal("application/json", cfg.ContentType);
        Assert.True(cfg.Events.HasFlag(NotificationEvents.BackupSuccess));
        Assert.True(cfg.Events.HasFlag(NotificationEvents.BackupFailure));
        Assert.False(cfg.Events.HasFlag(NotificationEvents.RestoreStart));
    }
}
