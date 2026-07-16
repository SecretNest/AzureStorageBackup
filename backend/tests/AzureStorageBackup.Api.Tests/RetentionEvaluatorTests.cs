using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class RetentionEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    private static VersionRef V(int n, DateTimeOffset created) => new(n, created);
    private static DateTimeOffset Days(int daysAgo) => Now.AddDays(-daysAgo);

    private static IReadOnlyList<int> Evaluate(IReadOnlyList<VersionRef> versions, RetentionPolicy policy) =>
        new RetentionEvaluator().VersionsToDelete(versions, policy, Now);

    [Fact]
    public void Version_Only_Deletes_Oldest_Beyond_Max()
    {
        var versions = new[] { V(1, Days(50)), V(2, Days(40)), V(3, Days(30)), V(4, Days(20)), V(5, Days(10)) };

        var deleted = Evaluate(versions, new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 3 });

        Assert.Equal([1, 2], deleted);
    }

    [Fact]
    public void Time_Only_Deletes_Older_Than_Max_Age()
    {
        var versions = new[] { V(1, Days(365)), V(2, Days(45)), V(3, Days(5)) };

        var deleted = Evaluate(versions, new RetentionPolicy { Mode = RetentionMode.TimeOnly, MaxAgeDays = 180, MaxVersions = 100 });

        Assert.Equal([1], deleted);
    }

    [Fact]
    public void Either_Triggers_Deletes_Excess_Or_Too_Old()
    {
        var versions = new[] { V(1, Days(900)), V(2, Days(400)), V(3, Days(5)) };

        var deleted = Evaluate(versions,
            new RetentionPolicy { Mode = RetentionMode.EitherTriggers, MaxVersions = 2, MaxAgeDays = 180 });

        Assert.Equal([1, 2], deleted); // v1 excess+old, v2 old
    }

    [Fact]
    public void Both_Required_Deletes_Only_Excess_And_Too_Old()
    {
        var versions = new[] { V(1, Days(900)), V(2, Days(400)), V(3, Days(5)) };

        var deleted = Evaluate(versions,
            new RetentionPolicy { Mode = RetentionMode.BothRequired, MaxVersions = 2, MaxAgeDays = 180 });

        Assert.Equal([1], deleted); // v1 excess&old; v2 old but not excess
    }

    [Fact]
    public void Newest_Version_Is_Never_Deleted()
    {
        var versions = new[] { V(1, Days(900)), V(2, Days(800)) };

        var deleted = Evaluate(versions, new RetentionPolicy { Mode = RetentionMode.TimeOnly, MaxAgeDays = 180 });

        Assert.Equal([1], deleted);
        Assert.DoesNotContain(2, deleted);
    }

    [Fact]
    public void Nothing_Deleted_When_Within_Limits()
    {
        var versions = new[] { V(1, Days(30)), V(2, Days(10)) };

        var deleted = Evaluate(versions, new RetentionPolicy { MaxVersions = 100, MaxAgeDays = 180 });

        Assert.Empty(deleted);
    }
}
