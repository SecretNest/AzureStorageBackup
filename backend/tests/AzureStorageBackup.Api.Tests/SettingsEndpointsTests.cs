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

        // Save again, this time turning auto-resume off. The second pass is not redundant: the **first**
        // PUT of the settings row may be an insert (UpsertAsync's Add branch stores the whole object as-is),
        // and only an existing row takes the field-by-field assignment path — which is exactly where this
        // switch fails silently. One missing assignment and unticking still reports a successful save, while
        // after a restart an unwanted backup starts itself. This is a switch that **starts work on its own**,
        // and being unable to turn it off is its worst failure mode.
        back.AutoResumeInterruptedRuns = false;
        (await _client.PutAsJsonAsync("/api/settings", back)).EnsureSuccessStatusCode();

        var again = await _client.GetFromJsonAsync<GlobalSettings>("/api/settings");
        Assert.False(again!.AutoResumeInterruptedRuns);
        Assert.Equal(42, again.DefaultMaxVersions);
    }
}
