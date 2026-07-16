using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class BackupDiscoveryTests
{
    [Fact]
    public void Neither_Present_Returns_None() =>
        Assert.Equal(BackupPresence.None, BackupDiscovery.Determine(false, false));

    [Fact]
    public void Plain_Only_Returns_Plain() =>
        Assert.Equal(BackupPresence.Plain, BackupDiscovery.Determine(true, false));

    [Fact]
    public void Encrypted_Only_Returns_Encrypted() =>
        Assert.Equal(BackupPresence.Encrypted, BackupDiscovery.Determine(false, true));

    [Fact]
    public void Both_Present_Prefers_Plain() =>
        Assert.Equal(BackupPresence.Plain, BackupDiscovery.Determine(true, true));
}
