using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>Pure filesystem-logic unit tests (no Azurite dependency): the restore conflict mode "rename and keep" (decision 3).</summary>
public sealed class RestoreConflictTests : IDisposable
{
    private readonly List<string> _dirs = [];

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "asb-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void RenameKeep_Renames_Existing_To_Bak_Timestamp()
    {
        var dir = NewTempDir();
        var dest = Path.Combine(dir, "file.txt");
        File.WriteAllText(dest, "OLD");
        var now = new DateTimeOffset(2026, 7, 18, 14, 30, 22, TimeSpan.Zero);

        var bak = RestoreConflict.RenameExisting(dest, now);   // returns the path of the renamed backup
        Assert.Equal(Path.Combine(dir, "file.txt.bak-20260718-143022"), bak);
        Assert.False(File.Exists(dest));                        // the original name is freed
        Assert.Equal("OLD", File.ReadAllText(bak));             // the old content is preserved
    }

    [Fact]
    public void RenameKeep_Appends_Counter_On_Collision()
    {
        var dir = NewTempDir();
        var dest = Path.Combine(dir, "file.txt");
        File.WriteAllText(dest, "OLD");
        var now = new DateTimeOffset(2026, 7, 18, 14, 30, 22, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(dir, "file.txt.bak-20260718-143022"), "PREV"); // already exists

        var bak = RestoreConflict.RenameExisting(dest, now);
        Assert.Equal(Path.Combine(dir, "file.txt.bak-20260718-143022-1"), bak);
        Assert.Equal("PREV", File.ReadAllText(Path.Combine(dir, "file.txt.bak-20260718-143022"))); // the earlier backup was not overwritten
        Assert.Equal("OLD", File.ReadAllText(bak));
    }

    [Fact]
    public void RenameKeep_Appends_Incrementing_Counter_On_Repeated_Collision()
    {
        var dir = NewTempDir();
        var dest = Path.Combine(dir, "file.txt");
        var now = new DateTimeOffset(2026, 7, 18, 14, 30, 22, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(dir, "file.txt.bak-20260718-143022"), "P0");
        File.WriteAllText(Path.Combine(dir, "file.txt.bak-20260718-143022-1"), "P1");
        File.WriteAllText(dest, "OLD");

        var bak = RestoreConflict.RenameExisting(dest, now);
        Assert.Equal(Path.Combine(dir, "file.txt.bak-20260718-143022-2"), bak);
    }
}
