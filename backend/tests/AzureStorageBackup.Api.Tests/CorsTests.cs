using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Production must not allow any cross-origin source by default — AllowCredentials() is on, so allowing the
/// dev-server address means an attacker's page can read the API carrying a live session (review Finding 3).
/// </summary>
public class CorsTests
{
    private sealed class ProductionWebAppFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
        }
    }

    [Fact]
    public async Task Outside_Development_The_Dev_Server_Origin_Is_Not_Allowed()
    {
        using var factory = new ProductionWebAppFactory();
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "http://localhost:5173");
        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// A wildcard origin plus AllowCredentials() is a combination the CORS spec forbids, and the policy is built lazily,
    /// so startup succeeds and the first cross-origin request carrying an Origin blows up with a 500.
    /// "*" was a legal configuration before AllowCredentials() was added this round, and existing deployments must not be broken because of it.
    /// </summary>
    private sealed class WildcardOriginWebAppFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
            builder.UseSetting("Cors:AllowedOrigins:0", "*");
        }
    }

    /// <summary>"*" alongside explicit origins: the fix must not throw away the whole allowlist with it.</summary>
    private sealed class WildcardPlusExplicitOriginWebAppFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
            builder.UseSetting("Cors:AllowedOrigins:0", "*");
            builder.UseSetting("Cors:AllowedOrigins:1", "http://frontend.test");
        }
    }

    [Fact]
    public async Task A_Wildcard_Origin_Is_Dropped_Instead_Of_Failing_The_Request()
    {
        using var factory = new WildcardOriginWebAppFactory();
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "http://somewhere.test");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // With the wildcard dropped this origin is no longer allowed — but the request itself completes as usual instead of 500ing
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Explicit_Origins_Alongside_A_Wildcard_Still_Work()
    {
        using var factory = new WildcardPlusExplicitOriginWebAppFactory();
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "http://frontend.test");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "http://frontend.test",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }
}
