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
}
