using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The in-process key ring status flag (a singleton). It used to be covered only indirectly through the
/// endpoints and the recovery flow, yet it is the sole holder of "the default must be Healthy": were the
/// default Lost, a fresh installation would lock itself at first boot (/api/health/ready permanently 503,
/// the scheduler skipping everything, every action 409).
/// </summary>
public class KeyringHealthTests
{
    [Fact]
    public void Defaults_To_Healthy()
    {
        Assert.Equal(KeyringStatus.Healthy, new KeyringHealth().Status);
    }

    [Fact]
    public void Set_Flips_The_Status_Both_Ways()
    {
        var sut = new KeyringHealth();

        sut.Set(KeyringStatus.Lost);
        Assert.Equal(KeyringStatus.Lost, sut.Status);

        sut.Set(KeyringStatus.Healthy);
        Assert.Equal(KeyringStatus.Healthy, sut.Status);
    }

    /// <summary>Rarely written and often read, the implementation carries the enum in a volatile int — after several writes, a read must see the last one.</summary>
    [Fact]
    public void Last_Write_Wins()
    {
        var sut = new KeyringHealth();

        sut.Set(KeyringStatus.Lost);
        sut.Set(KeyringStatus.Lost);
        sut.Set(KeyringStatus.Healthy);
        sut.Set(KeyringStatus.Lost);

        Assert.Equal(KeyringStatus.Lost, sut.Status);
    }
}
