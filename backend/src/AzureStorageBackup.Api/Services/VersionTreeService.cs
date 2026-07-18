using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>一个目录节点的懒加载条目：文件或子目录（§4.1a）。</summary>
public sealed record TreeNode(
    string Name,
    string Path,
    bool IsDir,
    bool HasChildren,
    long? Length,
    DateTimeOffset? Mtime,
    string? StorageKind,
    string? StorageRef);

/// <summary>
/// 从版本索引懒加载目录树（M4 §4.1a，决策 1）：给定目录路径，返回其直接子节点（子目录 + 文件），
/// 不递归展开。目录节点标注 HasChildren 供前端决定是否可展开；空目录（EmptyDirs）也作为可展开目录节点纳入。
/// 纯逻辑，不做任何 IO，供端点复用。
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
                // 直接子项是文件本身
                var childPath = prefix.Length == 0 ? entry.Path : $"{prefix}/{rest}";
                nodes[rest] = new TreeNode(rest, childPath, IsDir: false, HasChildren: false,
                    entry.Length, entry.Mtime, entry.Storage?.Kind, entry.Storage?.Ref);
            }
            else
            {
                // 直接子项是一个目录（该文件在更深处）
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
                // 空目录本身是直接子项：可展开（自身无子项，除非其它 EmptyDirs/Entries 在其下方补齐）
                var childPath = prefix.Length == 0 ? rest : $"{prefix}/{rest}";
                if (!nodes.ContainsKey(rest))
                    nodes[rest] = new TreeNode(rest, childPath, IsDir: true, HasChildren: false,
                        Length: null, Mtime: null, StorageKind: null, StorageRef: null);
            }
            else
            {
                // 空目录在更深处 → 直接子项是中间目录，必有子内容
                var name = rest[..slash];
                var childPath = prefix.Length == 0 ? name : $"{prefix}/{name}";
                nodes[name] = new TreeNode(name, childPath, IsDir: true, HasChildren: true,
                    Length: null, Mtime: null, StorageKind: null, StorageRef: null);
            }
        }

        return nodes.Values.ToList();
    }

    /// <summary>去除前后 '/'，规范化为空串（根）或不带首尾斜杠的路径。</summary>
    private static string NormalizePrefix(string? dirPath) =>
        string.IsNullOrEmpty(dirPath) ? string.Empty : dirPath.Trim('/');

    /// <summary>路径是否在 prefix 目录下，是则输出其相对于 prefix 的剩余部分。</summary>
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
