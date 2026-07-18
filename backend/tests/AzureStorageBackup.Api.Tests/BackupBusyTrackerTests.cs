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

    [Fact]
    public void CurrentActivity_Reflects_Acquire_Label_And_Clears_On_Release()
    {
        var t = new BackupBusyTracker();

        Assert.Null(t.CurrentActivity(1, "c"));       // 空闲 → null

        t.TryAcquire(1, "c", "CleaningUp");            // 计划清理不应被误标为 Checking
        Assert.Equal("CleaningUp", t.CurrentActivity(1, "c"));

        t.Release(1, "c");
        Assert.Null(t.CurrentActivity(1, "c"));

        Assert.True(t.TryAcquire(1, "c"));             // 默认标签 = Checking（如手动 /check）
        Assert.Equal("Checking", t.CurrentActivity(1, "c"));
    }
}
