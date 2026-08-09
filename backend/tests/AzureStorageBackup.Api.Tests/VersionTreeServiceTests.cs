using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class VersionTreeServiceTests
{
    [Fact]
    public void Children_Returns_Direct_Children_With_HasChildren_Flag()
    {
        var index = new VersionIndex
        {
            Version = 1,
            Entries =
            [
                new IndexEntry { Path = "a/b/c.txt", Kind = "file", Length = 10, Permissions = "0644",
                    Storage = new StorageRef { Kind = "pack", Ref = "1", EntryName = "a/b/c.txt", VolumeSizes = [50] } },
                new IndexEntry { Path = "a/d.txt", Kind = "file", Length = 20, Permissions = "0644",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/h", VolumeSizes = [30] } },
                new IndexEntry { Path = "top.txt", Kind = "file", Length = 5, Permissions = "0644",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/t", VolumeSizes = [8] } },
            ],
            EmptyDirs = ["a/empty"],
        };

        var root = VersionTreeService.Children(index, null);
        Assert.Equal(new[] { "a", "top.txt" }, root.Select(n => n.Name).OrderBy(x => x).ToArray());
        Assert.True(root.Single(n => n.Name == "a").IsDir);
        Assert.True(root.Single(n => n.Name == "a").HasChildren);

        var a = VersionTreeService.Children(index, "a");
        Assert.Equal(new[] { "b", "d.txt", "empty" }, a.Select(n => n.Name).OrderBy(x => x).ToArray());
        Assert.True(a.Single(n => n.Name == "empty").IsDir);           // an empty directory is still an expandable node
        Assert.False(a.Single(n => n.Name == "d.txt").IsDir);
    }

    /// <summary>The restore tree has to surface "this entry was carried over from an earlier version". The
    /// moment of choosing what to restore is when the operator most needs to know that restoring this
    /// version does not give the content as of its timestamp — information that was in the index but never
    /// reached the UI.</summary>
    [Fact]
    public void Children_Carries_The_Unreadable_Marker_To_File_Nodes()
    {
        var since = new DateTimeOffset(2026, 7, 20, 8, 30, 0, TimeSpan.Zero);
        var index = new VersionIndex
        {
            Version = 3,
            Entries =
            [
                new IndexEntry { Path = "vault/stale.txt", Kind = "file", Length = 10, Permissions = "0644",
                    UnreadableAt = since,
                    Storage = new StorageRef { Kind = "blob", Ref = "data/s", VolumeSizes = [20] } },
                new IndexEntry { Path = "vault/fresh.txt", Kind = "file", Length = 20, Permissions = "0644",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/f", VolumeSizes = [30] } },
            ],
        };

        var vault = VersionTreeService.Children(index, "vault");

        Assert.Equal(since, vault.Single(n => n.Name == "stale.txt").UnreadableAt);
        Assert.Null(vault.Single(n => n.Name == "fresh.txt").UnreadableAt); // a normal entry must not be flagged by mistake
    }
}
