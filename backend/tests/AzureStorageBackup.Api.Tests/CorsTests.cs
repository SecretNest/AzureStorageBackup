using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 生产环境不该默认放行任何跨域来源——AllowCredentials() 已开启，
/// 放行 dev-server 地址就是让攻击者的页面能带着活会话读接口（复审 Finding 3）。
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
    /// 通配来源 + AllowCredentials() 是 CORS 协议禁止的组合，策略惰性构建，
    /// 于是启动不报错、第一个带 Origin 的跨域请求直接 500。
    /// "*" 在本轮加 AllowCredentials() 之前是合法配置，绝不能因此把老部署炸掉。
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

    /// <summary>"*" 与显式来源并存：修复不能把整张白名单一起丢掉。</summary>
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
        // 丢弃通配后该来源不再被放行——但请求本身照常完成，而不是 500
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
