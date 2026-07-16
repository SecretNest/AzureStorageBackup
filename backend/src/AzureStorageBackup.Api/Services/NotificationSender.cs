using System.Net;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>发送一次通知 HTTP 请求。</summary>
public interface INotificationSender
{
    Task SendAsync(NotificationConfig config, string title, string body, CancellationToken ct = default);
}

/// <summary>按配置发送一次通知 HTTP 请求（PRD 4.2），支持代理。失败抛异常（由调用方决定吞或报）。</summary>
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
