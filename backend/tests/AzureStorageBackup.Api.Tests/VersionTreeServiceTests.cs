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
        Assert.True(a.Single(n => n.Name == "empty").IsDir);           // 空目录也作可展开节点
        Assert.False(a.Single(n => n.Name == "d.txt").IsDir);
    }
}
