using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>A lazily loaded entry under one directory node: a file or a subdirectory (§4.1a).</summary>
public sealed record TreeNode(
    string Name,
    string Path,
    bool IsDir,
    bool HasChildren,
    long? Length,
    DateTimeOffset? Mtime,
    string? StorageKind,
    string? StorageRef,
    /// <summary>Non-null means this record was carried over from an earlier version (the source file has been unreadable ever since),
    /// and the value says "since when it could no longer be updated". At the moment of choosing what content to restore this is what
    /// the operator most needs to know: restoring this version does not give you the content as of this version's point in time.</summary>
    DateTimeOffset? UnreadableAt = null);

/// <summary>
/// Renders one directory of the restore tree (M4 §4.1a, decision 1) out of what the catalog answered for it: direct
/// children only, never a recursion, with directory nodes tagged <c>HasChildren</c> so the frontend can decide whether
/// they are expandable before anyone clicks.
/// <para>
/// It used to take the whole <see cref="VersionIndex"/> and walk every path in it to find the handful under one
/// prefix — which meant every click in the restore dialog paid for the entire version. The catalog answers the same
/// question with two indexed lookups (<see cref="VersionCatalog.ChildrenAsync"/>), so what is left here is the pure
/// shaping: names into paths, entries into nodes. Still no IO whatsoever, and still reusable by the endpoints.
/// </para>
/// </summary>
public static class VersionTreeService
{
    /// <param name="children">One directory's direct children, straight from <see cref="VersionCatalog.ChildrenAsync"/>.</param>
    /// <param name="dirPath">The directory those children belong to, exactly as the request spelled it; it is what the
    /// returned paths are prefixed with, so it is normalized here the same way <see cref="NormalizePrefix"/> normalized
    /// it for the query.</param>
    public static IReadOnlyList<TreeNode> Children(IReadOnlyList<CatalogChild> children, string? dirPath)
    {
        var prefix = NormalizePrefix(dirPath);
        var nodes = new List<TreeNode>(children.Count);

        foreach (var child in children)
        {
            var path = prefix.Length == 0 ? child.Name : $"{prefix}/{child.Name}";
            // A directory node carries no file metadata: it has no length, no mtime and no storage of its own, and
            // inventing zeros for them would render as "an empty file" in the tree.
            nodes.Add(child is { IsDir: false, Entry: { } entry }
                ? new TreeNode(child.Name, path, IsDir: false, HasChildren: false,
                    entry.Length, entry.Mtime, entry.Storage?.Kind, entry.Storage?.Ref, entry.UnreadableAt)
                : new TreeNode(child.Name, path, IsDir: true, child.HasChildren,
                    Length: null, Mtime: null, StorageKind: null, StorageRef: null));
        }

        return nodes;
    }

    /// <summary>Strips leading and trailing '/', normalizing to the empty string (root) or a path with no slash at
    /// either end — which is exactly the shape the catalog stores in <c>entries.parent</c> and <c>dirs.parent</c>, so
    /// the endpoint passes the request's raw path through this before asking for a directory's children.</summary>
    public static string NormalizePrefix(string? dirPath) =>
        string.IsNullOrEmpty(dirPath) ? string.Empty : dirPath.Trim('/');
}
