using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class SchedulerTimeZoneTests
{
    [Fact]
    public void Null_Or_Blank_Falls_Back_To_Utc()
    {
        Assert.Equal(TimeZoneInfo.Utc, SchedulerService.ResolveTimeZone(null));
        Assert.Equal(TimeZoneInfo.Utc, SchedulerService.ResolveTimeZone("  "));
    }

    [Fact]
    public void Invalid_Id_Falls_Back_To_Utc()
    {
        Assert.Equal(TimeZoneInfo.Utc, SchedulerService.ResolveTimeZone("Not/A/Zone"));
    }

    [Fact]
    public void Valid_Iana_Id_Resolves()
    {
        var tz = SchedulerService.ResolveTimeZone("America/New_York");
        Assert.NotEqual(TimeZoneInfo.Utc, tz);
        Assert.NotEqual(TimeSpan.Zero, tz.BaseUtcOffset); // 非零基准偏移
    }
}
