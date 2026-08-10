using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
        // A JSON template puts the placeholders inside string literals, so the values have to be escaped for JSON
        // or the payload stops being JSON at the first newline or quote. That is not a guess about intent: the
        // configured content type is the statement that this is JSON, and honouring it is the only reading under
        // which such a template works at all.
        var payload = Substitute(
            config.BodyTemplate ?? "", title, body, urlEncode: false, jsonEscape: IsJson(config.ContentType));
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

    private static bool IsJson(string? contentType) =>
        contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false;

    /// <summary>
    /// Escapes only what JSON requires — quotes, backslashes and control characters, newlines among them. Non-ASCII
    /// is deliberately left alone (<see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>): the arrows and
    /// non-Latin text in a run summary stay readable on the receiving side instead of arriving as \uXXXX runs, and
    /// they were never the problem. "Unsafe" there refers to HTML escaping, which a JSON request body does not want.
    /// </summary>
    private static string JsonEscape(string value) =>
        JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();

    private static string Substitute(
        string template, string title, string body, bool urlEncode, bool jsonEscape = false)
    {
        var t = urlEncode ? Uri.EscapeDataString(title) : jsonEscape ? JsonEscape(title) : title;
        var b = urlEncode ? Uri.EscapeDataString(body) : jsonEscape ? JsonEscape(body) : body;
        return template
            // The Raw pair is the way out for a template where the placeholder *is* a piece of JSON structure
            // rather than a value inside a string — escaping would break that, and nothing about the content type
            // can tell the two apart. Replaced first so {Title} cannot consume part of one.
            .Replace("{TitleRaw}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{BodyRaw}", body, StringComparison.OrdinalIgnoreCase)
            .Replace("{Title}", t, StringComparison.OrdinalIgnoreCase)
            .Replace("{Body}", b, StringComparison.OrdinalIgnoreCase);
    }
}
