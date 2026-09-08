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
        Assert.Equal(20, s.CheckHeadConcurrency);
    }

    /// <summary>Two resources, one row: each page reads and writes its own half, and a save on one never carries
    /// the other page's fields — which is what the old whole-object PUT did, and why it is gone.</summary>
    [Fact]
    public async Task Each_Half_Round_Trips_On_Its_Own_And_Leaves_The_Other_Alone()
    {
        var d = await _client.GetFromJsonAsync<BackupDefaultsSettings>("/api/settings/defaults");
        d = d! with { DefaultMaxVersions = 42, DefaultIncludeSymlinks = true, DefaultDataTier = StorageTier.Cool };
        (await _client.PutAsJsonAsync("/api/settings/defaults", d)).EnsureSuccessStatusCode();

        var p = await _client.GetFromJsonAsync<PerformanceSettings>("/api/settings/performance");
        // AutoResumeInterruptedRuns is a switch that **starts work on its own**; being unable to turn it off is its
        // worst failure mode, and the field-by-field assignment on an existing row is exactly where a missing line
        // fails silently — so the second save exercises that path with it.
        p = p! with { LogEphemeralMaxAgeDays = 7, AutoResumeInterruptedRuns = false, CheckHeadConcurrency = 8 };
        (await _client.PutAsJsonAsync("/api/settings/performance", p)).EnsureSuccessStatusCode();

        var back = await _client.GetFromJsonAsync<GlobalSettings>("/api/settings");
        Assert.Equal(42, back!.DefaultMaxVersions);
        Assert.True(back.DefaultIncludeSymlinks);
        Assert.Equal(StorageTier.Cool, back.DefaultDataTier);
        Assert.Equal(7, back.LogEphemeralMaxAgeDays);
        Assert.False(back.AutoResumeInterruptedRuns);
        Assert.Equal(8, back.CheckHeadConcurrency);

        var dAgain = await _client.GetFromJsonAsync<BackupDefaultsSettings>("/api/settings/defaults");
        Assert.Equal(42, dAgain!.DefaultMaxVersions);
        var pAgain = await _client.GetFromJsonAsync<PerformanceSettings>("/api/settings/performance");
        Assert.Equal(7, pAgain!.LogEphemeralMaxAgeDays);
    }

    [Fact]
    public async Task The_Whole_Object_Can_Be_Read_But_No_Longer_Written()
    {
        var s = await _client.GetFromJsonAsync<GlobalSettings>("/api/settings");
        var response = await _client.PutAsJsonAsync("/api/settings", s);
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
