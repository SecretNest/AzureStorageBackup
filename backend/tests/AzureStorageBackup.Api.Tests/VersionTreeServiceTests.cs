using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class VersionTreeServiceTests
{
    private static IndexEntry Entry(string path, long length, DateTimeOffset? unreadableAt = null, StorageRef? storage = null) => new()
    {
        Path = path,
        Kind = "file",
        Length = length,
        Mtime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)),
        Permissions = "0644",
        UnreadableAt = unreadableAt,
        Storage = storage,
    };

    /// <summary>The children of one directory as the catalog hands them over: subdirectories already told whether they
    /// are worth expanding, files carrying their whole entry.</summary>
    [Fact]
    public void Children_Returns_Direct_Children_With_HasChildren_Flag()
    {
        var d = Entry("a/d.txt", 20, storage: new StorageRef { Kind = "blob", Ref = "data/h", VolumeSizes = [30] });

        var root = VersionTreeService.Children(
        [
            new CatalogChild("a", IsDir: true, HasChildren: true, Entry: null),
            new CatalogChild("top.txt", IsDir: false, HasChildren: false,
                Entry("top.txt", 5, storage: new StorageRef { Kind = "blob", Ref = "data/t", VolumeSizes = [8] })),
        ], null);

        Assert.Equal(new[] { "a", "top.txt" }, root.Select(n => n.Name).OrderBy(x => x).ToArray());
        Assert.True(root.Single(n => n.Name == "a").IsDir);
        Assert.True(root.Single(n => n.Name == "a").HasChildren);
        Assert.Equal("top.txt", root.Single(n => n.Name == "top.txt").Path);   // at the root the path is the bare name

        var a = VersionTreeService.Children(
        [
            new CatalogChild("b", IsDir: true, HasChildren: true, Entry: null),
            new CatalogChild("empty", IsDir: true, HasChildren: false, Entry: null),
            new CatalogChild("d.txt", IsDir: false, HasChildren: false, d),
        ], "a");

        Assert.Equal(new[] { "b", "d.txt", "empty" }, a.Select(n => n.Name).OrderBy(x => x).ToArray());
        Assert.True(a.Single(n => n.Name == "empty").IsDir);           // an empty directory is still an expandable node
        Assert.False(a.Single(n => n.Name == "empty").HasChildren);
        var file = a.Single(n => n.Name == "d.txt");
        Assert.False(file.IsDir);
        Assert.Equal("a/d.txt", file.Path);                            // below the root the child path is prefixed
        Assert.Equal(20, file.Length);
        Assert.Equal(d.Mtime, file.Mtime);
        Assert.Equal("blob", file.StorageKind);
        Assert.Equal("data/h", file.StorageRef);
        Assert.Null(a.Single(n => n.Name == "b").Length);              // a directory node carries no file metadata
    }

    /// <summary>The restore tree has to surface "this entry was carried over from an earlier version". The
    /// moment of choosing what to restore is when the operator most needs to know that restoring this
    /// version does not give the content as of its timestamp — information that was in the index but never
    /// reached the UI.</summary>
    [Fact]
    public void Children_Carries_The_Unreadable_Marker_To_File_Nodes()
    {
        var since = new DateTimeOffset(2026, 7, 20, 8, 30, 0, TimeSpan.Zero);

        var vault = VersionTreeService.Children(
        [
            new CatalogChild("stale.txt", IsDir: false, HasChildren: false, Entry("vault/stale.txt", 10, unreadableAt: since)),
            new CatalogChild("fresh.txt", IsDir: false, HasChildren: false, Entry("vault/fresh.txt", 20)),
        ], "vault");

        Assert.Equal(since, vault.Single(n => n.Name == "stale.txt").UnreadableAt);
        Assert.Null(vault.Single(n => n.Name == "fresh.txt").UnreadableAt); // a normal entry must not be flagged by mistake
    }

    /// <summary>
    /// The move from "walk the whole index in memory" to "ask the catalog for one directory" has to be invisible to
    /// the restore dialog. This runs both implementations over the same version — the old one against the
    /// <see cref="VersionIndex"/> object (kept verbatim as <see cref="LegacyVersionTreeService"/>), the new one
    /// against a real imported catalog — and demands the same nodes for the root and for two subdirectories.
    /// Comparison is order-insensitive because the two get there in different orders (a dictionary's insertion order
    /// versus two ORDER BY clauses) and the UI sorts for display anyway; everything else about a node, down to the
    /// storage ref and the carried-over marker, has to match exactly.
    /// </summary>
    [Fact]
    public async Task Children_From_The_Catalog_Equals_What_The_Whole_Index_Produced()
    {
        var index = new VersionIndex
        {
            Version = 4,
            Entries =
            [
                Entry("top.txt", 5, storage: new StorageRef { Kind = "blob", Ref = "data/t", Volumes = 2, VolumeSizes = [4, 4] }),
                Entry("a/d.txt", 20, storage: new StorageRef { Kind = "blob", Ref = "data/h", VolumeSizes = [30] }),
                Entry("a/b/c.txt", 10, storage: new StorageRef { Kind = "pack", Ref = "p1", EntryName = "a/b/c.txt" }),
                Entry("a/b/stale.txt", 7, unreadableAt: new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero),
                    storage: new StorageRef { Kind = "pack", Ref = "p1", EntryName = "a/b/stale.txt" }),
                new IndexEntry { Path = "a/link", Kind = "symlink", Length = 0, Mtime = DateTimeOffset.UnixEpoch, Permissions = "0777", Target = "../x" },
                Entry("nostorage.bin", 3),
            ],
            EmptyDirs = ["a/empty", "z"],
        };

        await using var catalog = await TestCatalogs.ImportAsync(index);

        foreach (var dir in new string?[] { null, "a", "a/b" })
        {
            var fromCatalog = VersionTreeService.Children(
                await catalog.ChildrenAsync(4, VersionTreeService.NormalizePrefix(dir), CancellationToken.None), dir);
            var fromIndex = LegacyVersionTreeService.Children(index, dir);

            Assert.Equal(
                fromIndex.OrderBy(n => n.Path, StringComparer.Ordinal),
                fromCatalog.OrderBy(n => n.Path, StringComparer.Ordinal));
        }
    }
}

/// <summary>
/// The pre-catalog <c>VersionTreeService.Children(VersionIndex, string?)</c>, kept verbatim as the reference
/// implementation the catalog-backed one is measured against
/// (<see cref="VersionTreeServiceTests.Children_From_The_Catalog_Equals_What_The_Whole_Index_Produced"/>). It lives in
/// the test project because production has no use for it any more — reading a whole index to render one directory is
/// exactly what the catalog exists to stop — but deleting it would leave the replacement with nothing but its own
/// output to agree with.
/// </summary>
internal static class LegacyVersionTreeService
{
    public static IReadOnlyList<TreeNode> Children(VersionIndex index, string? dirPath)
    {
        var prefix = string.IsNullOrEmpty(dirPath) ? string.Empty : dirPath.Trim('/');
        var nodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);

        foreach (var entry in index.Entries)
        {
            if (!TryGetRelative(entry.Path, prefix, out var rest))
                continue;

            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                var childPath = prefix.Length == 0 ? entry.Path : $"{prefix}/{rest}";
                nodes[rest] = new TreeNode(rest, childPath, IsDir: false, HasChildren: false,
                    entry.Length, entry.Mtime, entry.Storage?.Kind, entry.Storage?.Ref, entry.UnreadableAt);
            }
            else
            {
                var name = rest[..slash];
                var childPath = prefix.Length == 0 ? name : $"{prefix}/{name}";
                nodes[name] = new TreeNode(name, childPath, IsDir: true, HasChildren: true,
                    Length: null, Mtime: null, StorageKind: null, StorageRef: null);
            }
        }

        foreach (var emptyDir in index.EmptyDirs)
        {
            if (!TryGetRelative(emptyDir, prefix, out var rest))
                continue;

            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                var childPath = prefix.Length == 0 ? rest : $"{prefix}/{rest}";
                if (!nodes.ContainsKey(rest))
                    nodes[rest] = new TreeNode(rest, childPath, IsDir: true, HasChildren: false,
                        Length: null, Mtime: null, StorageKind: null, StorageRef: null);
            }
            else
            {
                var name = rest[..slash];
                var childPath = prefix.Length == 0 ? name : $"{prefix}/{name}";
                nodes[name] = new TreeNode(name, childPath, IsDir: true, HasChildren: true,
                    Length: null, Mtime: null, StorageKind: null, StorageRef: null);
            }
        }

        return nodes.Values.ToList();
    }

    private static bool TryGetRelative(string path, string prefix, out string rest)
    {
        if (prefix.Length == 0)
        {
            rest = path;
            return rest.Length > 0;
        }

        if (path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal) && path[prefix.Length] == '/')
        {
            rest = path[(prefix.Length + 1)..];
            return rest.Length > 0;
        }

        rest = string.Empty;
        return false;
    }
}
