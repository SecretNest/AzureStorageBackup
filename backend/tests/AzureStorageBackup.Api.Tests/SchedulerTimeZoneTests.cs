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
    public void TryResolve_Tells_Unset_From_Unrecognised()
    {
        // Unset is the default, not an error: UTC and true.
        Assert.True(SchedulerService.TryResolveTimeZone(null, out var unset));
        Assert.Equal(TimeZoneInfo.Utc, unset);
        Assert.True(SchedulerService.TryResolveTimeZone(" ", out _));

        // An id the system does not know still yields UTC, but says so.
        Assert.False(SchedulerService.TryResolveTimeZone("Not/A/Zone", out var unknown));
        Assert.Equal(TimeZoneInfo.Utc, unknown);

        Assert.True(SchedulerService.TryResolveTimeZone("America/New_York", out var known));
        Assert.NotEqual(TimeZoneInfo.Utc, known);
    }

    [Fact]
    public void Valid_Iana_Id_Resolves()
    {
        var tz = SchedulerService.ResolveTimeZone("America/New_York");
        Assert.NotEqual(TimeZoneInfo.Utc, tz);
        Assert.NotEqual(TimeSpan.Zero, tz.BaseUtcOffset); // a non-zero base offset
    }
}
