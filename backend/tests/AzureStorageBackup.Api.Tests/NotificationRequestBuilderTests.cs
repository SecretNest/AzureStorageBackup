using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class NotificationRequestBuilderTests
{
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
}
