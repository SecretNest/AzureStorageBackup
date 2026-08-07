using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class StagingAreaClearStaleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-clearstale-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Clears_leftover_subdirectories_and_files()
    {
        var compress = Path.Combine(_root, "compress");
        var staged = Path.Combine(_root, "staged");
        Directory.CreateDirectory(Path.Combine(compress, "abc"));
        Directory.CreateDirectory(Path.Combine(staged, "def"));
        File.WriteAllText(Path.Combine(compress, "abc", "part.7z.001"), "x");
        File.WriteAllText(Path.Combine(staged, "def", "part.7z"), "y");
        File.WriteAllText(Path.Combine(staged, "loose.tmp"), "z");

        StagingArea.ClearStale(compress, staged);

        Assert.Empty(Directory.EnumerateFileSystemEntries(compress));
        Assert.Empty(Directory.EnumerateFileSystemEntries(staged));
    }

    [Fact]
    public void Missing_directories_are_not_an_error()
    {
        var ex = Record.Exception(() => StagingArea.ClearStale(
            Path.Combine(_root, "nope-a"), Path.Combine(_root, "nope-b")));
        Assert.Null(ex);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}
