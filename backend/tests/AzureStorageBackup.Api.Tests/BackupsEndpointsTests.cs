using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class BackupsEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Backups_Empty_When_No_Accounts()
    {
        // 该 fixture 的库无账户，聚合应返回空且不触及 Azure
        var res = await _client.GetAsync("/api/backups");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var list = await res.Content.ReadFromJsonAsync<List<DiscoveredBackup>>();
        Assert.NotNull(list);
        Assert.Empty(list!);
    }
}
