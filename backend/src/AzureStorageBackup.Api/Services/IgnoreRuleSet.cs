using System.Text;
using System.Text.RegularExpressions;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// A gitignore-style rule set. Supports negation (!), directory-only (trailing /), anchoring (leading / or an internal /),
/// and the * ** ? wildcards. The last matching rule decides the result. Reused in three places: ignore / don't-compress / don't-group (PRD 3.3).
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

    /// <summary>Whether a file matches: it matches when judged as a file itself, or any ancestor directory matches when judged as a directory
    /// (which makes directory rules like `logs/` take effect on the files beneath them, matching how the ignore list behaves when walking by directory).</summary>
    public bool MatchesFileOrAncestorDir(string relativePath)
    {
        if (IsIgnored(relativePath, isDirectory: false))
            return true;

        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = "";
        for (var i = 0; i < parts.Length - 1; i++) // Ancestor directories, level by level
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
        // Anchored means match from the root; otherwise allow a prefix at any level (gitignore: a pattern with no internal slash matches at any depth)
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
                        sb.Append("(?:.*/)?"); // **/ spans zero or more directory levels
                        i++;
                    }
                    else
                    {
                        sb.Append(".*"); // ** spans directories
                    }
                }
                else
                {
                    sb.Append("[^/]*"); // * does not span directories
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
