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

        // 再存一次，这一次关掉自动接着跑。第二趟不是多余的：设置行**第一次** PUT 时可能是新建
        // （UpsertAsync 的 Add 分支把整个对象原样落库），只有已经有行时才走逐字段赋值那条路，
        // 而那正是这个开关会静默失效的地方——少写一行赋值，取消勾选照样显示保存成功，
        // 而重启之后一轮没人要的备份自己跑起来。这是个会**主动开工**的开关，关不掉是它最坏的坏法。
        back.AutoResumeInterruptedRuns = false;
        (await _client.PutAsJsonAsync("/api/settings", back)).EnsureSuccessStatusCode();

        var again = await _client.GetFromJsonAsync<GlobalSettings>("/api/settings");
        Assert.False(again!.AutoResumeInterruptedRuns);
        Assert.Equal(42, again.DefaultMaxVersions);
    }
}
