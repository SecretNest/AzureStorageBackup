namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 备份范围的边界规则集（设计 docs/backup-scope-selection-design.md）。每条规则是一个
/// 「路径 → 包含/排除」，判定取**最长匹配前缀**那一条；一条都不匹配则包含（根的隐含默认）。
/// <para>
/// 与 <see cref="IgnoreRuleSet"/> 刻意**不复用**：那套是 glob 匹配 + 最后规则胜出，
/// 这套是精确路径 + 最长前缀胜出。混在一起只会让两边都变复杂。
/// </para>
/// <para>
/// 两条写入不变式（由 <see cref="With"/> 与 <see cref="Parse"/> 共同维护），规则集因此
/// 永远最小、永远不失控增长：
/// 1) 每条规则的判定必须与它最近的祖先规则**相反**——相同即冗余，不落盘；
/// 2) 写入一条规则时，删除所有以它为严格前缀的更深规则——它们已被覆盖。
/// </para>
/// <para>
/// 不变式 1 的推论是三态显示能在**不加载任何子节点**的前提下算出来（见
/// <see cref="IsPartial"/>），这正是懒加载与三态能同时成立的原因。
/// </para>
/// </summary>
public sealed class ScopeRuleSet
{
    // Ordinal 序下祖先必排在后代之前（严格前缀恒小于其扩展），规范化因此能一遍顺序遍历完成。
    private readonly SortedDictionary<string, bool> _rules;

    private ScopeRuleSet(SortedDictionary<string, bool> rules) => _rules = rules;

    private static SortedDictionary<string, bool> Empty() => new(StringComparer.Ordinal);

    /// <summary>空规则集：全部包含。这是没有配置范围时的默认。</summary>
    public static ScopeRuleSet All { get; } = new(Empty());

    /// <summary>是否「全部包含」（没有任何规则）。</summary>
    public bool IsAll => _rules.Count == 0;

    /// <summary>
    /// 解析规则文本。null/空 → <see cref="All"/>。无法识别的行**跳过而不抛**，与
    /// <see cref="IgnoreRuleSet"/> 对空行/注释的处置一致：这段文本理论上只由 UI 生成，
    /// 但它落在库里，手工改坏不该让备份直接崩掉。解析后立即规范化（清掉冗余规则）。
    /// </summary>
    public static ScopeRuleSet Parse(string? text)
    {
        var rules = Empty();
        foreach (var raw in (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var included = raw[0] switch { '+' => true, '-' => false, _ => (bool?)null };
            if (included is null)
                continue;

            var path = Normalize(raw[1..]);
            // `..` 段在最长前缀匹配下本来就命中不了任何真实相对路径（扫描器给出的路径不含它），
            // 但留着只会让人以为它有意义。直接丢掉。
            if (path.Split('/').Any(seg => seg is ".." or "."))
                continue;

            rules[path] = included.Value;
        }

        Normalize(rules);
        return new ScopeRuleSet(rules);
    }

    /// <summary>某路径是否在范围内：最长前缀匹配，无匹配则为「包含」。O(路径深度)。</summary>
    public bool IsInScope(string relativePath)
    {
        var path = Normalize(relativePath);
        while (true)
        {
            if (_rules.TryGetValue(path, out var included))
                return included;
            if (path.Length == 0)
                return true; // 连根规则都没有 → 默认包含
            var slash = path.LastIndexOf('/');
            path = slash < 0 ? "" : path[..slash];
        }
    }

    /// <summary>
    /// 这个目录的子树里还有没有需要备份的东西：自身在范围内，**或**存在以它为前缀的 `+` 规则。
    /// <para>
    /// 扫描器必须用这个而不是 <see cref="IsInScope"/> 来决定要不要下降：一个被排除的目录
    /// 下面可能还有重新包含的子目录，只判 IsInScope 会把它们一起剪掉。
    /// </para>
    /// </summary>
    public bool MayContainIncluded(string dirPath)
    {
        if (IsInScope(dirPath))
            return true;

        var under = Under(dirPath);
        foreach (var (key, included) in _rules)
            if (included && IsUnder(key, under))
                return true;

        return false;
    }

    /// <summary>
    /// 三态里的「灰选」：规则集里存在以这个目录为严格前缀的规则，说明子树内部有分歧。
    /// <para>
    /// 这是**单向**的：`- docs` + `+ docs/a` + `+ docs/b` 而 docs 下恰好只有 a、b 时，
    /// 实际效果是全选，这里仍报灰选。不加载子节点就无从知道两条规则是否穷尽了目录——
    /// 这是懒加载的固有代价。灰选是保守且诚实的一侧：它如实反映「这里有明确规则在起作用」，
    /// 而不会把「部分选中」错报成「全选」。备份结果不受影响，只是显示。
    /// </para>
    /// </summary>
    public bool IsPartial(string dirPath)
    {
        var under = Under(dirPath);
        foreach (var key in _rules.Keys)
            if (IsUnder(key, under))
                return true;

        return false;
    }

    /// <summary>
    /// 写入一条规则，维护两条不变式，返回**新实例**（原实例不变——前端那份镜像实现同样是
    /// 不可变的，React 靠引用变化触发重渲）。
    /// </summary>
    public ScopeRuleSet With(string path, bool included)
    {
        var key = Normalize(path);
        var next = new SortedDictionary<string, bool>(_rules, StringComparer.Ordinal);

        // 不变式 2：清掉被这条覆盖的更深规则。
        var under = Under(key);
        foreach (var deeper in next.Keys.Where(k => IsUnder(k, under)).ToList())
            next.Remove(deeper);

        // 不变式 1：与最近祖先判定相同则不落盘。先摘掉自身这条，剩下的最近匹配就是祖先判定。
        next.Remove(key);
        if (new ScopeRuleSet(next).IsInScope(key) != included)
            next[key] = included;

        return new ScopeRuleSet(next);
    }

    /// <summary>规范化文本，每行一条。空规则集 → 空串。</summary>
    public override string ToString() =>
        string.Join('\n', _rules.Select(r =>
            r.Key.Length == 0
                ? (r.Value ? "+" : "-")
                : $"{(r.Value ? '+' : '-')} {r.Key}"));

    /// <summary>就地清掉冗余规则（判定与最近祖先相同者）。祖先必先于后代被访问，因此一遍即可：
    /// 被删的那条与其祖先判定相同，删掉不改变任何后代看到的祖先判定。</summary>
    private static void Normalize(SortedDictionary<string, bool> rules)
    {
        foreach (var key in rules.Keys.ToList())
        {
            var self = rules[key];
            rules.Remove(key);
            if (new ScopeRuleSet(rules).IsInScope(key) != self)
                rules[key] = self;
        }
    }

    /// <summary>该目录下所有后代共有的前缀（根为空串，其余为 "dir/"）。</summary>
    private static string Under(string dirPath)
    {
        var p = Normalize(dirPath);
        return p.Length == 0 ? "" : p + "/";
    }

    /// <summary>key 是否严格位于 under 之下（不含 under 所指的目录本身）。</summary>
    private static bool IsUnder(string key, string under) =>
        key.Length > under.Length && key.StartsWith(under, StringComparison.Ordinal);

    /// <summary>规范化路径：反斜杠转正斜杠、去掉空段（首尾斜杠与连续斜杠一并解决）。</summary>
    private static string Normalize(string path) =>
        string.Join('/', path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
