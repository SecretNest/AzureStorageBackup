using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>Pure filesystem-logic unit tests (no Azurite dependency): the "does this filesystem fold case" probe.</summary>
public sealed class PathCaseSensitivityTests : IDisposable
{
    private readonly List<string> _dirs = [];

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "asb-case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Cross-checks the probe against what the filesystem actually does, so the test states the truth on
    /// every platform instead of hard-coding one — which is the entire reason the probe asks the filesystem rather
    /// than the OS in the first place.</summary>
    [Fact]
    public void Probe_Agrees_With_What_The_Filesystem_Actually_Does()
    {
        var dir = NewTempDir();
        File.WriteAllText(Path.Combine(dir, "witness"), "x");
        var folds = File.Exists(Path.Combine(dir, "WITNESS"));

        Assert.Equal(folds, PathCaseSensitivity.IsCaseInsensitive(dir));
    }

    [SkippableFact]
    public void Probe_Reports_Case_Sensitive_On_Linux()
    {
        // ext4/tmpfs, which is what CI and every Docker deployment run on: the branch that must stay a no-op.
        Skip.IfNot(OperatingSystem.IsLinux(), "case-folding behaviour is filesystem-specific outside Linux");

        Assert.False(PathCaseSensitivity.IsCaseInsensitive(NewTempDir()));
    }

    [Fact]
    public void Probe_Leaves_Nothing_Behind()
    {
        var dir = NewTempDir();

        PathCaseSensitivity.IsCaseInsensitive(dir);

        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    [Fact]
    public void Probe_Assumes_Folding_When_It_Cannot_Write()
    {
        // An unusable probe must not answer "case-sensitive": that answer silently re-enables the overwrite path the
        // probe exists to block, and the resulting data loss is invisible. Assuming folding costs at worst a visible,
        // explained failure on the handful of paths that collide.
        var missing = Path.Combine(NewTempDir(), "no-such-directory");

        Assert.True(PathCaseSensitivity.IsCaseInsensitive(missing));
    }
}
