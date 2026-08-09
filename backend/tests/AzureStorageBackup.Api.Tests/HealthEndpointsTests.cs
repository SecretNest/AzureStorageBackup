using System.Net;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Smoke tests: the app starts, the DI pipeline holds together, and the liveness and readiness probes
/// behave as expected. Behavioural tests are added alongside the features.
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
        // The readiness probe must be purely local: still 200 with no Azure connection string at all (design decision 10)
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Ready_Returns_ServiceUnavailable_When_Keyring_Lost()
    {
        // With the key ring lost the readiness probe must report 503, judged entirely locally (the cached IKeyringHealth value) without touching the cloud.
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
