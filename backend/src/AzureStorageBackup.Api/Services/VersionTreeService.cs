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
/// Lazily loads the directory tree out of a version index (M4 §4.1a, decision 1): given a directory path, returns its direct children
/// (subdirectories + files) without recursing. Directory nodes are tagged with HasChildren so the frontend can decide whether they are expandable; empty directories (EmptyDirs) are included as expandable directory nodes too.
/// Pure logic, no IO whatsoever, reusable by the endpoints.
/// </summary>
public static class VersionTreeService
{
    public static IReadOnlyList<TreeNode> Children(VersionIndex index, string? dirPath)
    {
        var prefix = NormalizePrefix(dirPath);
        var nodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);

        foreach (var entry in index.Entries)
        {
            if (!TryGetRelative(entry.Path, prefix, out var rest))
                continue;

            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                // The direct child is the file itself
                var childPath = prefix.Length == 0 ? entry.Path : $"{prefix}/{rest}";
                nodes[rest] = new TreeNode(rest, childPath, IsDir: false, HasChildren: false,
                    entry.Length, entry.Mtime, entry.Storage?.Kind, entry.Storage?.Ref, entry.UnreadableAt);
            }
            else
            {
                // The direct child is a directory (the file sits deeper down)
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
                // The empty directory itself is the direct child: expandable (with no children of its own, unless other EmptyDirs/Entries fill in underneath it)
                var childPath = prefix.Length == 0 ? rest : $"{prefix}/{rest}";
                if (!nodes.ContainsKey(rest))
                    nodes[rest] = new TreeNode(rest, childPath, IsDir: true, HasChildren: false,
                        Length: null, Mtime: null, StorageKind: null, StorageRef: null);
            }
            else
            {
                // The empty directory sits deeper down → the direct child is an intermediate directory and must have content beneath it
                var name = rest[..slash];
                var childPath = prefix.Length == 0 ? name : $"{prefix}/{name}";
                nodes[name] = new TreeNode(name, childPath, IsDir: true, HasChildren: true,
                    Length: null, Mtime: null, StorageKind: null, StorageRef: null);
            }
        }

        return nodes.Values.ToList();
    }

    /// <summary>Strips leading and trailing '/', normalizing to the empty string (root) or a path with no slash at either end.</summary>
    private static string NormalizePrefix(string? dirPath) =>
        string.IsNullOrEmpty(dirPath) ? string.Empty : dirPath.Trim('/');

    /// <summary>Whether the path lies under the prefix directory; if so, outputs the remainder relative to prefix.</summary>
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
