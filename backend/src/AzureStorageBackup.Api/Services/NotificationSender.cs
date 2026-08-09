using System.Net;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Send one notification HTTP request.</summary>
public interface INotificationSender
{
    Task SendAsync(NotificationConfig config, string title, string body, CancellationToken ct = default);
}

/// <summary>Send one notification HTTP request per configuration (PRD 4.2), with proxy support. Failures throw, leaving the caller to swallow or report.</summary>
public sealed class NotificationSender : INotificationSender
{
    public async Task SendAsync(NotificationConfig config, string title, string body, CancellationToken ct = default)
    {
        using var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(config.ProxyUrl))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(config.ProxyUrl);
        }

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = NotificationRequestBuilder.Build(config, title, body);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
