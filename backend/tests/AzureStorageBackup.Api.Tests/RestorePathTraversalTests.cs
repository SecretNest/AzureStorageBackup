using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 还原目标外写入的防护（设计 §5）。此处直接测判定函数与拼接结果，
/// 完整还原链路的端到端覆盖在 BackupLifecycleTests 中。
/// </summary>
public class RestorePathTraversalTests
{
    [Theory]
    [InlineData("photos/a.jpg", true)]
    [InlineData("a.jpg", true)]
    [InlineData("nested/deep/b.txt", true)]
    [InlineData("../escape.txt", false)]
    [InlineData("../../etc/cron.d/x", false)]
    [InlineData("photos/../../escape.txt", false)]
    public void Only_Paths_Staying_Inside_The_Target_Are_Accepted(string entryPath, bool expected)
    {
        var target = Path.Combine(Path.GetTempPath(), "asb-restore-target");
        var dest = Path.Combine(target, entryPath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected, PathBoundary.IsWithin(target, dest));
    }

    [Fact]
    public void A_Path_Escaping_Sideways_Into_A_Prefix_Sibling_Is_Rejected()
    {
        var target = Path.Combine(Path.GetTempPath(), "asb-target");
        var dest = Path.Combine(target + "x", "b.txt");

        Assert.False(PathBoundary.IsWithin(target, dest));
    }
}
