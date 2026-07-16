using System.Net;
using System.Net.Http.Json;

namespace AzureStorageBackup.Api.Tests;

public class SystemEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Paths_Returns_KeysAndData_Paths()
    {
        var res = await _client.GetAsync("/api/system/paths");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(body);
        Assert.True(body!.ContainsKey("keysPath"));
        Assert.True(body.ContainsKey("dataPath"));
        Assert.False(string.IsNullOrWhiteSpace(body["keysPath"]));
    }

    [Fact]
    public async Task Version_Returns_NonEmpty()
    {
        var res = await _client.GetAsync("/api/system/version");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.False(string.IsNullOrWhiteSpace(body!["version"]));
    }
}
