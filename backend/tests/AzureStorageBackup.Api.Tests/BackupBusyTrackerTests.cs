using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class BackupBusyTrackerTests
{
    [Fact]
    public void Acquire_Then_Second_Acquire_Fails_Until_Released()
    {
        var t = new BackupBusyTracker();

        Assert.True(t.TryAcquire(1, "c"));    // first acquire succeeds
        Assert.False(t.TryAcquire(1, "c"));   // already busy → fails
        Assert.True(t.IsBusy(1, "c"));

        t.Release(1, "c");
        Assert.False(t.IsBusy(1, "c"));
        Assert.True(t.TryAcquire(1, "c"));    // can be acquired again after release
    }

    [Fact]
    public void Different_Backups_Are_Independent()
    {
        var t = new BackupBusyTracker();

        Assert.True(t.TryAcquire(1, "c"));
        Assert.True(t.TryAcquire(1, "other")); // a different container does not interfere
        Assert.True(t.TryAcquire(2, "c"));     // a different account does not interfere
    }

    [Fact]
    public void CurrentActivity_Reflects_Acquire_Label_And_Clears_On_Release()
    {
        var t = new BackupBusyTracker();

        Assert.Null(t.CurrentActivity(1, "c"));       // idle → null

        t.TryAcquire(1, "c", "CleaningUp");            // scheduled cleanup must not be mislabeled as Checking
        Assert.Equal("CleaningUp", t.CurrentActivity(1, "c"));

        t.Release(1, "c");
        Assert.Null(t.CurrentActivity(1, "c"));

        Assert.True(t.TryAcquire(1, "c"));             // default label = Checking (e.g. a manual /check)
        Assert.Equal("Checking", t.CurrentActivity(1, "c"));
    }
}
