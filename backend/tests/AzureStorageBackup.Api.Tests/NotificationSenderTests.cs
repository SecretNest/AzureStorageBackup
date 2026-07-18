using System.Net;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class NotificationSenderTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Sends_Post_With_Substituted_Body()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? method = null;
        string? body = null;
        var serverTask = System.Threading.Tasks.Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            method = ctx.Request.HttpMethod;
            using (var reader = new StreamReader(ctx.Request.InputStream))
                body = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        var cfg = new NotificationConfig
        {
            Url = $"http://127.0.0.1:{port}/hook",
            Method = NotificationMethod.Post,
            BodyTemplate = "{Title}::{Body}",
            ContentType = "text/plain",
        };

        await new NotificationSender().SendAsync(cfg, "Backup OK", "v1");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("POST", method);
        Assert.Equal("Backup OK::v1", body);
    }

    [Fact]
    public async Task Post_With_Charset_Content_Type_Does_Not_Throw()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverTask = System.Threading.Tasks.Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
        });

        var cfg = new NotificationConfig
        {
            Url = $"http://127.0.0.1:{port}/hook",
            Method = NotificationMethod.Post,
            BodyTemplate = "{}",
            ContentType = "application/json; charset=utf-8",
        };

        var ex = await Record.ExceptionAsync(() => new NotificationSender().SendAsync(cfg, "t", "b"));

        Assert.Null(ex);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Throws_On_Server_Error()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverTask = System.Threading.Tasks.Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
        });

        var cfg = new NotificationConfig { Url = $"http://127.0.0.1:{port}/hook", Method = NotificationMethod.Get };

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => new NotificationSender().SendAsync(cfg, "t", "b"));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
