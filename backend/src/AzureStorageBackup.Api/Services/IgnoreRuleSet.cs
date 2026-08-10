using System.Text;
using System.Text.RegularExpressions;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// A gitignore-style rule set. Supports negation (!), directory-only (trailing /), anchoring (leading / or an internal /),
/// and the * ** ? wildcards. The last matching rule decides the result. Reused in three places: ignore / don't-compress / don't-group (PRD 3.3).
/// <para>
/// Case sensitivity is <b>per rule</b>, not per set. Every rule list comes in a sensitive and an insensitive
/// half, and the two are concatenated into one set here — because "the last matching rule decides" has to keep
/// holding across the pair, or a negation in one half could never override a match in the other. Two independent
/// sets OR-ed together would silently break exactly that.
/// </para>
/// <para>
/// There is no character-class support (<c>[wW]</c>): everything that is not <c>*</c> or <c>?</c> goes through
/// <see cref="Regex.Escape(string)"/>, so brackets match themselves. Insensitivity is therefore something the
/// caller has to ask for; it cannot be spelled out in the pattern.
/// </para>
/// </summary>
public sealed class IgnoreRuleSet
{
    private sealed record Rule(Regex Regex, bool Negate, bool DirOnly);

    private readonly List<Rule> _rules = [];

    /// <summary>All patterns matched case-sensitively, which is what a path on Linux means literally.</summary>
    public IgnoreRuleSet(IEnumerable<string> patterns) : this(patterns.Select(p => (p, false)), tagged: true) { }

    /// <summary>
    /// Patterns paired with whether each is matched ignoring case. Order matters: the caller concatenates its
    /// sensitive list before its insensitive one, and the last rule that matches still wins.
    /// <para>
    /// A factory rather than a second constructor: an empty collection literal cannot choose between
    /// <c>IEnumerable&lt;string&gt;</c> and <c>IEnumerable&lt;(string, bool)&gt;</c>, and existing callers pass one.
    /// </para>
    /// </summary>
    public static IgnoreRuleSet FromTagged(IEnumerable<(string Pattern, bool IgnoreCase)> patterns)
        => new(patterns, tagged: true);

    private IgnoreRuleSet(IEnumerable<(string Pattern, bool IgnoreCase)> patterns, bool tagged)
    {
        _ = tagged;
        foreach (var (raw, ignoreCase) in patterns)
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

            var opts = ignoreCase ? RegexOptions.Compiled | RegexOptions.IgnoreCase : RegexOptions.Compiled;
            _rules.Add(new Rule(new Regex(GlobToRegex(p, anchored), opts), negate, dirOnly));
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
