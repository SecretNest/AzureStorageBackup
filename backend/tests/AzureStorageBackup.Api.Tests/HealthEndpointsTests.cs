using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Smoke 测试：验证应用能启动、DI 管道成立、存活探针返回 200。
/// 业务测试随需求逐步补充。
/// </summary>
public class HealthEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    [Fact]
    public async Task Health_Returns_Ok()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
