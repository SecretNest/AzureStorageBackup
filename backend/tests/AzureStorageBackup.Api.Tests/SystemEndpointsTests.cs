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
    public async Task Scheduler_Reports_The_Clock_Cron_Is_Read_In()
    {
        var res = await _client.GetAsync("/api/system/scheduler");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<SchedulerInfo>();
        Assert.NotNull(body);
        // The test host sets no Scheduler:TimeZone, so the default applies and is reported as such — not as a fallback.
        Assert.Equal("UTC", body!.TimeZone);
        Assert.Equal(0, body.UtcOffsetMinutes);
        Assert.Null(body.ConfiguredTimeZone);
        Assert.True(body.Recognised);
        // TestWebAppFactory switches the scheduler off; the page has to be able to say so.
        Assert.False(body.Enabled);
    }

    private sealed record SchedulerInfo(
        string TimeZone, int UtcOffsetMinutes, string? ConfiguredTimeZone, bool Recognised, bool Enabled);

    [Fact]
    public async Task Version_Returns_NonEmpty()
    {
        var res = await _client.GetAsync("/api/system/version");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.False(string.IsNullOrWhiteSpace(body!["version"]));
    }
}
