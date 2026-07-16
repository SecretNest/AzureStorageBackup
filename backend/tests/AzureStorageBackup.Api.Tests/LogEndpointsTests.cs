using System.Net;
using System.Net.Http.Json;

namespace AzureStorageBackup.Api.Tests;

public class LogEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Get_Returns_List()
    {
        var res = await _client.GetAsync("/api/logs");
        res.EnsureSuccessStatusCode();
        Assert.NotNull(await res.Content.ReadFromJsonAsync<object[]>());
    }

    [Fact]
    public async Task Delete_Clears()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/logs")).StatusCode);
    }
}
