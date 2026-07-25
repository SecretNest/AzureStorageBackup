using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 进程内的密钥环状态位（单例）。此前只被端点/恢复流程间接覆盖，
/// 而它恰恰是「默认必须是 Healthy」的唯一持有者：默认若是 Lost，全新安装会开机即锁死
/// （/api/health/ready 恒 503、调度器全跳过、一切动作 409）。
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

    /// <summary>写极少、读频繁，实现用 volatile int 承载枚举——多次写后读到的必须是最后一次写入的值。</summary>
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
