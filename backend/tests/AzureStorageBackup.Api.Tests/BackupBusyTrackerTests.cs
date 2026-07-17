using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class BackupBusyTrackerTests
{
    [Fact]
    public void Acquire_Then_Second_Acquire_Fails_Until_Released()
    {
        var t = new BackupBusyTracker();

        Assert.True(t.TryAcquire(1, "c"));    // 首次获取成功
        Assert.False(t.TryAcquire(1, "c"));   // 已忙碌 → 失败
        Assert.True(t.IsBusy(1, "c"));

        t.Release(1, "c");
        Assert.False(t.IsBusy(1, "c"));
        Assert.True(t.TryAcquire(1, "c"));    // 释放后可再获取
    }

    [Fact]
    public void Different_Backups_Are_Independent()
    {
        var t = new BackupBusyTracker();

        Assert.True(t.TryAcquire(1, "c"));
        Assert.True(t.TryAcquire(1, "other")); // 不同 container 互不影响
        Assert.True(t.TryAcquire(2, "c"));     // 不同账户互不影响
    }
}
