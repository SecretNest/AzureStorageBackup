using System.Net.Http.Headers;
using System.Text;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Build an HTTP request from the notification configuration plus Title/Body (PRD 4.2). Placeholders are case-insensitive.</summary>
public static class NotificationRequestBuilder
{
    public static HttpRequestMessage Build(NotificationConfig config, string title, string body)
    {
        // Placeholders inside the URL are URL-encoded
        var url = Substitute(config.Url, title, body, urlEncode: true);

        if (config.Method == NotificationMethod.Get)
            return new HttpRequestMessage(HttpMethod.Get, url);

        var req = new HttpRequestMessage(HttpMethod.Post, url);
        var payload = Substitute(config.BodyTemplate ?? "", title, body, urlEncode: false);
        var contentType = string.IsNullOrWhiteSpace(config.ContentType) ? "text/plain" : config.ContentType;
        // Use the 2-arg StringContent ctor + MediaTypeHeaderValue.Parse rather than the 3-arg ctor,
        // because StringContent's mediaType parameter rejects parameterized values (e.g. "; charset=utf-8")
        // with a FormatException. MediaTypeHeaderValue.Parse handles the full media-type grammar.
        var mediaType = MediaTypeHeaderValue.Parse(contentType);
        // Preserve prior wire behavior: StringContent used to always declare "; charset=utf-8"
        // for content-types that didn't specify one. Only default it in when the configured
        // content-type omits a charset param; an explicitly-configured charset is left untouched.
        if (string.IsNullOrEmpty(mediaType.CharSet))
            mediaType.CharSet = "utf-8";
        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = mediaType;
        req.Content = content;
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
