using System.Text;
using System.Text.RegularExpressions;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// gitignore 风格的规则集。支持否定(!)、仅目录(/后缀)、锚定(/前缀或含内部/)、
/// * ** ? 通配。最后匹配的规则决定结果。三处复用：忽略/不压缩/不分组（PRD 3.3）。
/// </summary>
public sealed class IgnoreRuleSet
{
    private sealed record Rule(Regex Regex, bool Negate, bool DirOnly);

    private readonly List<Rule> _rules = [];

    public IgnoreRuleSet(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('#'))
                continue;

            var p = raw.Trim();

            var negate = false;
            if (p.StartsWith('!'))
            {
                negate = true;
                p = p[1..];
            }

            var dirOnly = false;
            if (p.EndsWith('/'))
            {
                dirOnly = true;
                p = p[..^1];
            }

            var anchored = false;
            if (p.StartsWith('/'))
            {
                anchored = true;
                p = p[1..];
            }
            if (p.Contains('/'))
                anchored = true;

            if (p.Length == 0)
                continue;

            _rules.Add(new Rule(new Regex(GlobToRegex(p, anchored), RegexOptions.Compiled), negate, dirOnly));
        }
    }

    public bool IsIgnored(string relativePath, bool isDirectory = false)
    {
        var path = relativePath.Replace('\\', '/').TrimStart('/');

        bool? decision = null;
        foreach (var rule in _rules)
        {
            if (rule.DirOnly && !isDirectory)
                continue;
            if (rule.Regex.IsMatch(path))
                decision = !rule.Negate;
        }

        return decision ?? false;
    }

    /// <summary>文件是否命中：自身以文件判定命中，或任一祖先目录以目录判定命中
    /// （使 `logs/` 这类目录规则对其下文件生效，与忽略列表按目录遍历的行为一致）。</summary>
    public bool MatchesFileOrAncestorDir(string relativePath)
    {
        if (IsIgnored(relativePath, isDirectory: false))
            return true;

        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = "";
        for (var i = 0; i < parts.Length - 1; i++) // 逐级祖先目录
        {
            prefix = prefix.Length == 0 ? parts[i] : prefix + "/" + parts[i];
            if (IsIgnored(prefix, isDirectory: true))
                return true;
        }

        return false;
    }

    private static string GlobToRegex(string glob, bool anchored)
    {
        var sb = new StringBuilder();
        // 锚定则从根匹配；否则允许任意层级前缀（gitignore：无内部斜杠的模式匹配任意深度）
        sb.Append(anchored ? "^" : "^(?:.*/)?");

        var i = 0;
        while (i < glob.Length)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i += 2;
                    if (i < glob.Length && glob[i] == '/')
                    {
                        sb.Append("(?:.*/)?"); // **/ 跨零或多层目录
                        i++;
                    }
                    else
                    {
                        sb.Append(".*"); // ** 跨目录
                    }
                }
                else
                {
                    sb.Append("[^/]*"); // * 不跨目录
                    i++;
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }

        sb.Append('$');
        return sb.ToString();
    }
}
