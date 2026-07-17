using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Tests;

public class SettingsEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Get_Returns_Defaults()
    {
        var s = await _client.GetFromJsonAsync<GlobalSettings>("/api/settings");
        Assert.NotNull(s);
        Assert.Equal(100, s!.DefaultMaxVersions);
        Assert.Equal(14, s.LogEphemeralMaxAgeDays);
    }

    [Fact]
    public async Task Put_Then_Get_RoundTrips()
    {
        var s = await _client.GetFromJsonAsync<GlobalSettings>("/api/settings");
        s!.DefaultMaxVersions = 42;
        s.DefaultIncludeSymlinks = true;
        s.LogEphemeralMaxAgeDays = 7;
        s.DefaultDataTier = StorageTier.Cool;

        (await _client.PutAsJsonAsync("/api/settings", s)).EnsureSuccessStatusCode();

        var back = await _client.GetFromJsonAsync<GlobalSettings>("/api/settings");
        Assert.Equal(42, back!.DefaultMaxVersions);
        Assert.True(back.DefaultIncludeSymlinks);
        Assert.Equal(7, back.LogEphemeralMaxAgeDays);
        Assert.Equal(StorageTier.Cool, back.DefaultDataTier);
    }
}
