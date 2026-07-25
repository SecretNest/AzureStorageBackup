using System.Net;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Smoke 测试：验证应用能启动、DI 管道成立、存活/就绪探针行为符合预期。
/// 业务测试随需求逐步补充。
/// </summary>
public class HealthEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private IKeyringHealth Keyring => factory.Services.GetRequiredService<IKeyringHealth>();

    [Fact]
    public async Task Health_Returns_Ok()
    {
        var response = await _client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_Returns_Ok_Without_Any_Cloud_Configuration()
    {
        // 就绪探针必须是纯本地的：无任何 Azure 连接串时依然 200（设计决策 10）
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_Returns_ServiceUnavailable_When_Keyring_Lost()
    {
        // 密钥环丢失时就绪探针必须报 503：判定全程本地（IKeyringHealth 缓存值），不访问云端。
        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var response = await _client.GetAsync("/api/health/ready");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }
}
