using System.Text;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>把通知配置 + Title/Body 构建成 HTTP 请求（PRD 4.2）。占位符大小写不敏感。</summary>
public static class NotificationRequestBuilder
{
    public static HttpRequestMessage Build(NotificationConfig config, string title, string body)
    {
        // URL 中的占位符做 URL 编码
        var url = Substitute(config.Url, title, body, urlEncode: true);

        if (config.Method == NotificationMethod.Get)
            return new HttpRequestMessage(HttpMethod.Get, url);

        var req = new HttpRequestMessage(HttpMethod.Post, url);
        var payload = Substitute(config.BodyTemplate ?? "", title, body, urlEncode: false);
        req.Content = new StringContent(payload, Encoding.UTF8,
            string.IsNullOrWhiteSpace(config.ContentType) ? "text/plain" : config.ContentType);
        return req;
    }

    private static string Substitute(string template, string title, string body, bool urlEncode)
    {
        var t = urlEncode ? Uri.EscapeDataString(title) : title;
        var b = urlEncode ? Uri.EscapeDataString(body) : body;
        return template
            .Replace("{Title}", t, StringComparison.OrdinalIgnoreCase)
            .Replace("{Body}", b, StringComparison.OrdinalIgnoreCase);
    }
}
