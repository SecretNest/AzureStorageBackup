using System.Text.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class NotificationRequestBuilderTests
{
    /// <summary>
    /// A backup's closing notification carries the run summary, which is several lines. Substituted raw into a
    /// JSON template those newlines are control characters inside a string literal, which JSON forbids — the
    /// receiver answers 4xx, EnsureSuccessStatusCode throws, and NotificationService swallows it into a log line
    /// nobody reads. From the outside it looks exactly like "the success notification is never sent", while the
    /// opening one — a single-line container name — keeps working and proves the wiring is fine.
    /// </summary>
    [Fact]
    public async Task Post_Json_Survives_A_Multi_Line_Body()
    {
        var cfg = new NotificationConfig
        {
            Url = "https://hook.example/notify",
            Method = NotificationMethod.Post,
            // The shape a push service actually wants, and the one this was reported against.
            BodyTemplate = """{"title":"NASBackup - {Title}","body":"{Body}"}""",
            ContentType = "application/json",
        };

        using var req = NotificationRequestBuilder.Build(
            cfg, "Backup succeeded: Public", "Version 7\nFiles: 12 new, 3 modified\nData: 4.1 GB changed at source → 1.2 GB uploaded");

        var payload = await req.Content!.ReadAsStringAsync();

        // The point of the test: it has to parse. Asserting on the escaped text instead would just re-state
        // whichever escaping we happened to implement.
        using var parsed = JsonDocument.Parse(payload);
        Assert.Equal("NASBackup - Backup succeeded: Public", parsed.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "Version 7\nFiles: 12 new, 3 modified\nData: 4.1 GB changed at source → 1.2 GB uploaded",
            parsed.RootElement.GetProperty("body").GetString());
    }

    /// <summary>A quote in the payload is the same failure with a shorter fuse — one backup named `My "Photos"` is enough.</summary>
    [Fact]
    public async Task Post_Json_Survives_Quotes_And_Backslashes()
    {
        var cfg = new NotificationConfig
        {
            Url = "https://hook.example/notify",
            Method = NotificationMethod.Post,
            BodyTemplate = """{"title":"{Title}","body":"{Body}"}""",
            ContentType = "application/json",
        };

        using var req = NotificationRequestBuilder.Build(cfg, """A "quoted" name""", @"path\to\thing");

        using var parsed = JsonDocument.Parse(await req.Content!.ReadAsStringAsync());
        Assert.Equal("""A "quoted" name""", parsed.RootElement.GetProperty("title").GetString());
        Assert.Equal(@"path\to\thing", parsed.RootElement.GetProperty("body").GetString());
    }

    /// <summary>text/plain must stay untouched: escaping there would put literal backslash-n into the message.</summary>
    [Fact]
    public async Task Post_Plain_Text_Is_Not_Escaped()
    {
        var cfg = new NotificationConfig
        {
            Url = "https://hook.example/notify",
            Method = NotificationMethod.Post,
            BodyTemplate = "{Title}\n{Body}",
            ContentType = "text/plain",
        };

        using var req = NotificationRequestBuilder.Build(cfg, "Title", "line one\nline two");

        Assert.Equal("Title\nline one\nline two", await req.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_Substitutes_UrlEncoded_Placeholders()
    {
        var cfg = new NotificationConfig
        {
            Url = "https://hook.example/notify?t={Title}&b={Body}",
            Method = NotificationMethod.Get,
        };

        using var req = NotificationRequestBuilder.Build(cfg, "Backup OK", "v1 done");

        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://hook.example/notify?t=Backup%20OK&b=v1%20done", req.RequestUri!.AbsoluteUri);
        Assert.Null(req.Content);
    }

    [Fact]
    public async Task Post_Substitutes_Body_And_Sets_ContentType()
    {
        var cfg = new NotificationConfig
        {
            Url = "https://hook.example/notify",
            Method = NotificationMethod.Post,
            BodyTemplate = "{\"title\":\"{Title}\",\"body\":\"{Body}\"}",
            ContentType = "application/json",
        };

        using var req = NotificationRequestBuilder.Build(cfg, "Backup OK", "v1 done");

        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{\"title\":\"Backup OK\",\"body\":\"v1 done\"}", await req.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Placeholders_Are_Case_Insensitive()
    {
        var cfg = new NotificationConfig { Url = "https://h/x?m={title}-{BODY}", Method = NotificationMethod.Get };

        using var req = NotificationRequestBuilder.Build(cfg, "A", "B");

        Assert.Equal("https://h/x?m=A-B", req.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Post_Defaults_ContentType_To_Text_Plain()
    {
        var cfg = new NotificationConfig
        {
            Url = "https://h/x", Method = NotificationMethod.Post, BodyTemplate = "{Title}",
        };

        using var req = NotificationRequestBuilder.Build(cfg, "hi", "b");

        Assert.Equal("text/plain", req.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Post_With_Plain_ContentType_Defaults_CharSet_To_Utf8()
    {
        // Backward-compat: StringContent used to always declare "; charset=utf-8" on the wire.
        // Configuring a bare content-type (no charset param) must still yield that on the header.
        var cfg = new NotificationConfig
        {
            Url = "https://hook.example/notify",
            Method = NotificationMethod.Post,
            BodyTemplate = "{}",
            ContentType = "application/json",
        };

        using var req = NotificationRequestBuilder.Build(cfg, "t", "b");

        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", req.Content.Headers.ContentType!.CharSet);
    }

    [Fact]
    public async Task Post_With_Explicit_CharSet_Is_Preserved()
    {
        // An explicitly-configured charset must not be silently overwritten with utf-8.
        var cfg = new NotificationConfig
        {
            Url = "https://hook.example/notify",
            Method = NotificationMethod.Post,
            BodyTemplate = "{}",
            ContentType = "application/json; charset=utf-16",
        };

        using var req = NotificationRequestBuilder.Build(cfg, "t", "b");

        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("utf-16", req.Content.Headers.ContentType!.CharSet);
    }
}
