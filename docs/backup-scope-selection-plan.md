# 备份范围选择 — 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让一个备份配置只备份根目录下选中的子集，选择可在建好之后继续增删。

**Architecture:** 范围存成一组「边界规则」（`+ path` / `- path` 每行一条），判定用**最长
前缀匹配**，不存文件清单——因此勾选的目录下日后新增的文件自动纳入。规则集逻辑在后端
（`ScopeRuleSet.cs`）与前端（`scopeRules.ts`）各实现一份，用同一份 JSON 夹具钉住一致性。
前端树的三态**从规则集现算、不存**，所以勾选没有父子传播回路，不可能死循环。

**Tech Stack:** .NET 9 + xUnit（后端）、EF Core + SQLite（迁移）、React 19 + TypeScript +
Vite（前端）、vitest（本计划新引入，仅用于前端纯逻辑测试）。

设计文档：`docs/backup-scope-selection-design.md`。

## Global Constraints

- **界面文案一律英文**。代码注释与本文档用中文，UI 上出现的字符串必须是英文。
- **不可继承**：`ScopeRules` 不进 `ResolvedBackupSettings`，`null`/空 = 「全部包含」，
  **不是**「继承全局默认」。其它规则字段的 `null` 含义与它不同，不要顺手对齐。
- **路径格式**：规则里的路径相对 `LocalRoot`，`/` 分隔，无首尾斜杠，根为空串。
- **`LocalRoot` 创建后锁定**（`BackupConfigService.cs:46`），本计划不改这条。
- 后端测试：`cd backend && dotnet test`。前端类型检查：`cd frontend && npm run build`。
- 提交信息用英文，与仓库现有风格一致。

---

### Task 1: `ScopeRuleSet` 与共享夹具

规则集的判定与写入逻辑，纯逻辑无 IO。这是整个功能的地基，先把它和它的测试夹具立起来，
后面前端那份实现直接读同一份夹具。

**Files:**
- Create: `shared/scope-rule-cases.json`
- Create: `backend/src/AzureStorageBackup.Api/Services/ScopeRuleSet.cs`
- Create: `backend/tests/AzureStorageBackup.Api.Tests/ScopeRuleSetTests.cs`
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj`

**Interfaces:**
- Consumes: 无（本计划第一个任务）
- Produces:
  - `ScopeRuleSet.All` → `ScopeRuleSet`（静态属性，空规则集）
  - `ScopeRuleSet.Parse(string? text)` → `ScopeRuleSet`
  - `.IsAll` → `bool`
  - `.IsInScope(string relativePath)` → `bool`
  - `.MayContainIncluded(string dirPath)` → `bool`
  - `.IsPartial(string dirPath)` → `bool`
  - `.With(string path, bool included)` → `ScopeRuleSet`（返回新实例，原实例不变）
  - `.ToString()` → `string`（规范化文本，行序为 Ordinal 序）

- [ ] **Step 1: 写共享夹具**

创建 `shared/scope-rule-cases.json`。前后端测试都读这一份；改它等于同时改两边的期望。

```json
{
  "comment": "Shared cases for ScopeRuleSet (C#) and scopeRules.ts. Both sides must agree.",
  "queries": [
    {
      "name": "empty rule set includes everything",
      "rules": [],
      "inScope": ["", "a", "a/b/c.txt"],
      "outOfScope": [],
      "partial": [],
      "notPartial": ["", "a"],
      "mayContain": ["", "a", "a/b"],
      "mayNotContain": []
    },
    {
      "name": "root excluded with two included subtrees",
      "rules": ["-", "+ photos", "+ docs/2026"],
      "inScope": ["photos", "photos/a.jpg", "photos/x/y.jpg", "docs/2026", "docs/2026/q1.pdf"],
      "outOfScope": ["", "music", "docs", "docs/2025", "docs/2025/old.pdf"],
      "partial": ["", "docs"],
      "notPartial": ["photos", "docs/2026", "music"],
      "mayContain": ["", "docs", "photos", "docs/2026"],
      "mayNotContain": ["music", "docs/2025"]
    },
    {
      "name": "alternating include and exclude down one branch",
      "rules": ["- docs", "+ docs/2026", "- docs/2026/tmp"],
      "inScope": ["", "music", "docs/2026", "docs/2026/q1.pdf"],
      "outOfScope": ["docs", "docs/2025", "docs/2026/tmp", "docs/2026/tmp/a.log"],
      "partial": ["docs", "docs/2026"],
      "notPartial": ["docs/2026/tmp", "music", "docs/2025"],
      "mayContain": ["", "docs", "docs/2026"],
      "mayNotContain": ["docs/2026/tmp", "docs/2025"]
    },
    {
      "name": "paths are normalized on the way in",
      "rules": ["+ /photos/", "- docs//2026"],
      "inScope": ["photos/a.jpg"],
      "outOfScope": ["docs/2026/q1.pdf"],
      "partial": ["docs"],
      "notPartial": ["photos"],
      "mayContain": ["photos"],
      "mayNotContain": ["docs/2026"]
    },
    {
      "name": "malformed lines are skipped, not fatal",
      "rules": ["nonsense", "", "   ", "+ photos", "- ../escape", "# comment"],
      "inScope": ["photos/a.jpg", "music"],
      "outOfScope": [],
      "partial": [],
      "notPartial": ["photos"],
      "mayContain": ["photos", "music"],
      "mayNotContain": []
    }
  ],
  "writes": [
    {
      "name": "unchecking the root stores a single minus",
      "start": [],
      "ops": [{ "path": "", "included": false }],
      "expect": ["-"]
    },
    {
      "name": "checking the root is redundant and stores nothing",
      "start": [],
      "ops": [{ "path": "", "included": true }],
      "expect": []
    },
    {
      "name": "re-including a subtree under an excluded root",
      "start": [],
      "ops": [
        { "path": "", "included": false },
        { "path": "photos", "included": true }
      ],
      "expect": ["-", "+ photos"]
    },
    {
      "name": "writing a rule clears the deeper rules it covers",
      "start": ["- docs", "+ docs/2026", "- docs/2026/tmp"],
      "ops": [{ "path": "docs", "included": true }],
      "expect": []
    },
    {
      "name": "a rule agreeing with its nearest ancestor is not stored",
      "start": ["-"],
      "ops": [{ "path": "music", "included": false }],
      "expect": ["-"]
    },
    {
      "name": "unchecking then re-checking a folder returns to the original set",
      "start": [],
      "ops": [
        { "path": "photos", "included": false },
        { "path": "photos", "included": true }
      ],
      "expect": []
    },
    {
      "name": "excluding every child does not collapse into the parent",
      "start": [],
      "ops": [
        { "path": "docs/a", "included": false },
        { "path": "docs/b", "included": false }
      ],
      "expect": ["- docs/a", "- docs/b"]
    }
  ]
}
```

最后一条用例是有意的：规则集不知道 `docs` 下是否只有 `a`、`b`，所以不会自作主张地把
两条子规则折叠成 `- docs`。折叠了就等于替用户决定「以后 `docs` 下新增的东西也不要」，
而那与子树语义相反。

- [ ] **Step 2: 让测试项目能读到夹具**

在 `backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj` 的
`</Project>` 之前插入（与已有的 `ItemGroup` 平级）：

```xml
  <ItemGroup>
    <None Include="..\..\..\shared\scope-rule-cases.json" Link="scope-rule-cases.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
```

- [ ] **Step 3: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/ScopeRuleSetTests.cs`：

```csharp
using System.Text.Json;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 规则集的判定与写入。用例来自 shared/scope-rule-cases.json——前端 scopeRules.ts 的测试
/// 读的是同一份文件，两份实现行为分叉时两边同时红。
/// </summary>
public sealed class ScopeRuleSetTests
{
    private sealed record QueryCase(
        string Name, string[] Rules,
        string[] InScope, string[] OutOfScope,
        string[] Partial, string[] NotPartial,
        string[] MayContain, string[] MayNotContain);

    private sealed record WriteOp(string Path, bool Included);

    private sealed record WriteCase(string Name, string[] Start, WriteOp[] Ops, string[] Expect);

    private sealed record Fixture(QueryCase[] Queries, WriteCase[] Writes);

    private static Fixture LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scope-rule-cases.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Fixture>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public static TheoryData<string> QueryNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in LoadFixture().Queries)
            data.Add(c.Name);
        return data;
    }

    public static TheoryData<string> WriteNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in LoadFixture().Writes)
            data.Add(c.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(QueryNames))]
    public void Query_Cases_From_Shared_Fixture(string name)
    {
        var c = LoadFixture().Queries.Single(x => x.Name == name);
        var set = ScopeRuleSet.Parse(string.Join('\n', c.Rules));

        foreach (var p in c.InScope)
            Assert.True(set.IsInScope(p), $"expected in scope: '{p}'");
        foreach (var p in c.OutOfScope)
            Assert.False(set.IsInScope(p), $"expected out of scope: '{p}'");
        foreach (var p in c.Partial)
            Assert.True(set.IsPartial(p), $"expected partial: '{p}'");
        foreach (var p in c.NotPartial)
            Assert.False(set.IsPartial(p), $"expected not partial: '{p}'");
        foreach (var p in c.MayContain)
            Assert.True(set.MayContainIncluded(p), $"expected may contain included: '{p}'");
        foreach (var p in c.MayNotContain)
            Assert.False(set.MayContainIncluded(p), $"expected may not contain included: '{p}'");
    }

    [Theory]
    [MemberData(nameof(WriteNames))]
    public void Write_Cases_From_Shared_Fixture(string name)
    {
        var c = LoadFixture().Writes.Single(x => x.Name == name);
        var set = ScopeRuleSet.Parse(string.Join('\n', c.Start));

        foreach (var op in c.Ops)
            set = set.With(op.Path, op.Included);

        Assert.Equal(string.Join('\n', c.Expect), set.ToString());
    }

    [Fact]
    public void Parse_Of_Null_Is_The_All_Set()
    {
        Assert.True(ScopeRuleSet.Parse(null).IsAll);
        Assert.True(ScopeRuleSet.Parse("").IsAll);
        Assert.True(ScopeRuleSet.Parse("   \n  ").IsAll);
    }

    [Fact]
    public void Parse_Then_ToString_Drops_Redundant_Rules()
    {
        // 手工编辑出来的冗余规则（与最近祖先判定相同）在解析时就被清掉，
        // 否则 IsPartial 会把一个实际上全同的子树报成灰选。
        var set = ScopeRuleSet.Parse("-\n- music\n+ photos");

        Assert.Equal("-\n+ photos", set.ToString());
        Assert.False(set.IsPartial("music"));
    }

    [Fact]
    public void With_Does_Not_Mutate_The_Original_Set()
    {
        var original = ScopeRuleSet.Parse("");
        var changed = original.With("photos", false);

        Assert.True(original.IsAll);
        Assert.False(changed.IsInScope("photos"));
    }
}
```

- [ ] **Step 4: 跑测试确认它失败**

```bash
cd backend && dotnet test --filter FullyQualifiedName~ScopeRuleSetTests
```

Expected: 编译失败，`The type or namespace name 'ScopeRuleSet' could not be found`。

- [ ] **Step 5: 实现 `ScopeRuleSet`**

创建 `backend/src/AzureStorageBackup.Api/Services/ScopeRuleSet.cs`：

```csharp
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
```

- [ ] **Step 6: 跑测试确认它通过**

```bash
cd backend && dotnet test --filter FullyQualifiedName~ScopeRuleSetTests
```

Expected: PASS，共 16 条（5 条 query 用例 + 7 条 write 用例 + 4 条独立 `[Fact]`）。

- [ ] **Step 7: 提交**

```bash
git add shared/scope-rule-cases.json \
  backend/src/AzureStorageBackup.Api/Services/ScopeRuleSet.cs \
  backend/tests/AzureStorageBackup.Api.Tests/ScopeRuleSetTests.cs \
  backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
git commit -m "feat(backup): add ScopeRuleSet, the boundary rules behind backup scope"
```

---

### Task 2: 扫描器按范围剪枝

把范围接进 `LocalFileScanner`。这是唯一动到备份主链路的地方，改动小但有一个必须钉死的坑：
只是「路过」的目录不能被记成空目录。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/LocalFileScanner.cs`
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/LocalFileScannerTests.cs`

**Interfaces:**
- Consumes: `ScopeRuleSet.Parse` / `.IsInScope` / `.MayContainIncluded`（Task 1）
- Produces: `ScanOptions.Scope`（`ScopeRuleSet`，默认 `ScopeRuleSet.All`）——Task 3 的
  `BackupRequestMapper` 会填它

- [ ] **Step 1: 写失败的测试**

在 `backend/tests/AzureStorageBackup.Api.Tests/LocalFileScannerTests.cs` 的最后一个 `}`
之前追加：

```csharp
    [Fact]
    public async Task Scope_Prunes_Whole_Subtrees()
    {
        WriteText("photos/a.jpg", "x");
        WriteText("music/b.mp3", "y");

        var scope = ScopeRuleSet.Parse("-\n+ photos");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet([]), new ScanOptions { Scope = scope });

        Assert.Equal(["photos/a.jpg"], result.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task Scope_Descends_Into_An_Excluded_Directory_To_Reach_A_Re_Included_One()
    {
        WriteText("docs/2025/old.pdf", "x");
        WriteText("docs/2026/q1.pdf", "y");

        // 只判 IsInScope 会在 docs 处就把整棵剪掉，2026 永远到不了。
        var scope = ScopeRuleSet.Parse("- docs\n+ docs/2026");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet([]), new ScanOptions { Scope = scope });

        Assert.Equal(["docs/2026/q1.pdf"], result.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task A_Directory_Only_Passed_Through_Is_Not_Recorded_As_Empty()
    {
        WriteText("docs/2026/q1.pdf", "y");
        Directory.CreateDirectory(Path.Combine(_root, "docs", "scratch"));

        // docs 自身被排除，只是为了下降到 docs/2026 才走进去。它绝不能进 EmptyDirs——
        // 那会让还原凭空重建出一个用户明确排除掉的目录。docs/scratch 同理。
        var scope = ScopeRuleSet.Parse("- docs\n+ docs/2026");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet([]), new ScanOptions { Scope = scope });

        Assert.DoesNotContain("docs", result.EmptyDirs);
        Assert.DoesNotContain("docs/scratch", result.EmptyDirs);
    }

    [Fact]
    public async Task An_In_Scope_Empty_Directory_Is_Still_Recorded()
    {
        Directory.CreateDirectory(Path.Combine(_root, "photos", "empty"));

        var scope = ScopeRuleSet.Parse("-\n+ photos");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet([]), new ScanOptions { Scope = scope });

        Assert.Contains("photos/empty", result.EmptyDirs);
    }

    [Fact]
    public async Task Scope_And_Ignore_Apply_Independently()
    {
        WriteText("photos/a.jpg", "x");
        WriteText("photos/debug.log", "y");
        WriteText("music/c.mp3", "z");

        var scope = ScopeRuleSet.Parse("-\n+ photos");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet(["*.log"]), new ScanOptions { Scope = scope });

        // 范围留下 photos，忽略规则再从中剔掉 .log —— 两层串联，互不干扰。
        Assert.Equal(["photos/a.jpg"], result.Entries.Select(e => e.Path));
    }
```

- [ ] **Step 2: 跑测试确认它失败**

```bash
cd backend && dotnet test --filter FullyQualifiedName~LocalFileScannerTests
```

Expected: 编译失败，`'ScanOptions' does not contain a definition for 'Scope'`。

- [ ] **Step 3: 给 `ScanOptions` 加字段**

`backend/src/AzureStorageBackup.Api/Services/LocalFileScanner.cs`，在 `ScanOptions` 的
`IncludeSymlinks` 之后加：

```csharp
    /// <summary>备份范围（设计 docs/backup-scope-selection-design.md）。默认全部包含。</summary>
    public ScopeRuleSet Scope { get; init; } = ScopeRuleSet.All;
```

- [ ] **Step 4: 让 `ScanDirectory` 返回「子树里是否真的留下了东西」**

同一文件。`ScanDirectory` 的签名由 `private void` 改为 `private bool`，并按下面三处修改。

第一处，签名与顶部的读不出来分支——目录列不出内容时返回 `true`：它记进了 `unreadable`，
是这一轮**真实产生的结果**，不能被当成「什么都没留下」而让父目录忽略它。

```csharp
    /// <returns>这棵子树是否真的留下了东西（条目 / 空目录 / 读不出来的路径）。
    /// 父目录据此决定要不要把自己算作「有保留的子项」——一个只是为了下降到深处某个
    /// 重新包含的目录而被路过的目录，自身并没有留下任何东西，绝不能进 EmptyDirs。</returns>
    private bool ScanDirectory(
        string dir,
        string root,
        IgnoreRuleSet ignore,
        ScanOptions options,
        List<ScannedEntry> entries,
        List<string> emptyDirs,
        List<UnreadablePath> unreadable,
        CancellationToken ct,
        StageTracker? tracker)
    {
```

同一方法内，把两处 `unreadable.Add(...); return;` 改为 `unreadable.Add(...); return true;`
（一处在取迭代器失败的 catch 里，一处在 `MoveNext` 中途失败的 catch 里）。

第二处，目录分支——先问 `MayContainIncluded`，再按下降结果决定是否计数：

```csharp
            if (isDirectory && !isSymlink)
            {
                // 目录被排除、且子树里也没有任何重新包含的规则 → 整棵剪掉，不下降。
                // 只判 IsInScope 是不够的：被排除的目录下面可能还有 + 规则（设计 §2）。
                if (!options.Scope.MayContainIncluded(relative))
                    continue;

                // keptChildren 只在子树**真的**留下了东西时才 ++。路过的目录不算——
                // 否则 `- docs` + `+ docs/2026` 会让 docs 被写成空目录，还原时凭空重建出来。
                if (ScanDirectory(info.FullName, root, ignore, options, entries, emptyDirs, unreadable, ct, tracker))
                    keptChildren++;
                continue;
            }

            if (!options.Scope.IsInScope(relative))
                continue;
```

第三处，方法末尾——空目录只在自身也在范围内时才记，并返回是否留下了东西：

```csharp
        // 空文件夹：应用忽略与范围后既无保留文件也无保留子目录（根自身不记录）。
        var self = RelativePath(root, dir);
        if (keptChildren == 0 && !string.IsNullOrEmpty(self))
        {
            // 自身不在范围内的目录（只是被路过）不算空目录，也不算「留下了东西」。
            if (!options.Scope.IsInScope(self))
                return false;
            emptyDirs.Add(self);
        }

        return true;
```

第四处，`ScanAsync` 里的调用点丢弃返回值：

```csharp
        _ = ScanDirectory(root, root, ignore, options, entries, emptyDirs, unreadable, ct, tracker);
```

- [ ] **Step 5: 跑测试确认它通过**

```bash
cd backend && dotnet test --filter FullyQualifiedName~LocalFileScannerTests
```

Expected: PASS，含新加的 5 条与全部既有用例（既有用例都不传 `Scope`，走 `All`，行为不变）。

- [ ] **Step 6: 跑一遍全量后端测试**

```bash
cd backend && dotnet test
```

Expected: 全绿。`ScanDirectory` 改了返回类型，这一步确认没有别的调用方受影响。

- [ ] **Step 7: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/LocalFileScanner.cs \
  backend/tests/AzureStorageBackup.Api.Tests/LocalFileScannerTests.cs
git commit -m "feat(backup): prune the scan by the configured scope"
```

---

### Task 3: 持久化与接线

把范围存进配置、传到引擎。纯接线，但迁移与 DTO 一处不落。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Models/BackupConfig.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupConfigService.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRequestMapper.cs`
- Create: `backend/src/AzureStorageBackup.Api/Migrations/<timestamp>_AddBackupScopeRules.cs`（由 EF 生成）
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/BackupRequestMapperTests.cs`
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/BackupConfigServiceTests.cs`

**Interfaces:**
- Consumes: `ScopeRuleSet.Parse`（Task 1）、`ScanOptions.Scope`（Task 2）
- Produces:
  - `BackupConfig.ScopeRules`（`string?`）
  - `BackupConfigRequest.ScopeRules`（`string?`，可选参数，默认 `null`）
  - `BackupConfigResponse.ScopeRules`（`string?`，可选参数，默认 `null`）

- [ ] **Step 1: 写失败的测试**

在 `backend/tests/AzureStorageBackup.Api.Tests/BackupRequestMapperTests.cs` 最后一个 `}`
之前追加：

```csharp
    [Fact]
    public void Maps_Scope_Rules_Into_Scan_Options()
    {
        var config = Config();
        config.ScopeRules = "-\n+ photos";

        var request = BackupRequestMapper.From(config, Account(), password: null);

        Assert.True(request.Options.Scan.Scope.IsInScope("photos/a.jpg"));
        Assert.False(request.Options.Scan.Scope.IsInScope("music/b.mp3"));
    }

    [Fact]
    public void Scope_Rules_Are_Not_Inheritable_So_Null_Means_Everything()
    {
        // 其它规则字段的 null = 「继承全局默认」，这个字段的 null = 「全部包含」。
        // 这处不同是故意的（设计 §1），别顺手把它塞进 ResolvedBackupSettings。
        var config = Config();
        config.ScopeRules = null;

        var request = BackupRequestMapper.From(
            config, Account(), password: null,
            settings: new GlobalSettings { DefaultIgnoreRules = "*.tmp" });

        Assert.True(request.Options.Scan.Scope.IsAll);
    }
```

`Config()` 与 `Account()` 是 `BackupRequestMapperTests` 里已有的私有辅助（文件顶部），
直接用即可。`BackupConfig` 是 class 不是 record，所以上面用赋值而不是 `with`。

在 `backend/tests/AzureStorageBackup.Api.Tests/BackupConfigServiceTests.cs` 最后一个 `}`
之前追加。`_sut`、`Sample()`、`Clone()` 都是该文件已有的成员：

```csharp
    [Fact]
    public async Task Update_Can_Change_Scope_Rules()
    {
        var created = await _sut.CreateAsync(Sample());

        var update = Clone(created);
        update.ScopeRules = "-\n+ photos";

        var result = await _sut.UpdateAsync(created.Id, update);

        Assert.Equal("-\n+ photos", result!.ScopeRules);
        Assert.Equal("-\n+ photos", (await _sut.GetAsync(created.Id))!.ScopeRules);
    }
```

同时给该文件的 `Clone` 辅助补一行，让它名副其实地逐字段克隆（在 `ContainerName` 附近）：

```csharp
        ScopeRules = c.ScopeRules,
```

- [ ] **Step 2: 跑测试确认它失败**

```bash
cd backend && dotnet test --filter "FullyQualifiedName~BackupRequestMapperTests|FullyQualifiedName~BackupConfigServiceTests"
```

Expected: 编译失败，`'BackupConfig' does not contain a definition for 'ScopeRules'`。

- [ ] **Step 3: 加实体字段**

`backend/src/AzureStorageBackup.Api/Models/BackupConfig.cs`，在 `CrossDirGroupRules` 之后：

```csharp
    /// <summary>
    /// 备份范围（设计 docs/backup-scope-selection-design.md）：每行一条 `+ path` / `- path`，
    /// 判定取最长前缀匹配。null/空 = 根下**全部内容**。
    /// <para>
    /// 注意它与上面几个规则字段的 null 含义**不同**：那些是「继承全局默认」，这个是
    /// 「全部包含」。范围是这个备份自己的事，全局默认没有意义，因此它不进
    /// <see cref="ResolvedBackupSettings"/>。
    /// </para>
    /// </summary>
    public string? ScopeRules { get; set; }
```

- [ ] **Step 4: 加 DTO 字段**

`backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs`：

`BackupConfigResponse` 的参数列表末尾（在 `string? CrossDirGroupRules = null` 之后）加：

```csharp
    string? ScopeRules = null)
```

`From(...)` 的实参列表末尾（`c.CrossDirGroupRules` 之后）加 `, c.ScopeRules`。

`BackupConfigRequest` 的参数列表末尾（`string? CrossDirGroupRules = null` 之后）加：

```csharp
    string? ScopeRules = null)
```

`ToConfig(...)` 的对象初始化器里加：

```csharp
        ScopeRules = ScopeRules,
```

- [ ] **Step 5: 让更新写入该字段**

`backend/src/AzureStorageBackup.Api/Services/BackupConfigService.cs`，在
`existing.CrossDirGroupRules = update.CrossDirGroupRules;` 之后加：

```csharp
        // 范围可改（不属于锁定的基础字段），改后下次备份生效。
        existing.ScopeRules = update.ScopeRules;
```

- [ ] **Step 6: 接进引擎请求**

`backend/src/AzureStorageBackup.Api/Services/BackupRequestMapper.cs`，把

```csharp
                Scan = new ScanOptions { IncludeSymlinks = r.IncludeSymlinks },
```

改成

```csharp
                // ScopeRules 不可继承，直接从 config 取而不是从 r（ResolvedBackupSettings）。
                Scan = new ScanOptions
                {
                    IncludeSymlinks = r.IncludeSymlinks,
                    Scope = ScopeRuleSet.Parse(config.ScopeRules),
                },
```

- [ ] **Step 7: 生成迁移**

```bash
cd backend/src/AzureStorageBackup.Api && dotnet ef migrations add AddBackupScopeRules
```

Expected: 生成 `Migrations/<timestamp>_AddBackupScopeRules.cs`，`Up` 里是一句
`AddColumn<string>(name: "ScopeRules", table: "BackupConfigs", nullable: true)`。
打开确认它**只**加这一列，没有捎带别的改动。

- [ ] **Step 8: 跑测试确认它通过**

```bash
cd backend && dotnet test
```

Expected: 全绿。

- [ ] **Step 9: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Models/BackupConfig.cs \
  backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs \
  backend/src/AzureStorageBackup.Api/Services/BackupConfigService.cs \
  backend/src/AzureStorageBackup.Api/Services/BackupRequestMapper.cs \
  backend/src/AzureStorageBackup.Api/Migrations \
  backend/tests/AzureStorageBackup.Api.Tests/BackupRequestMapperTests.cs \
  backend/tests/AzureStorageBackup.Api.Tests/BackupConfigServiceTests.cs
git commit -m "feat(backup): persist the scope on a config and hand it to the engine"
```

---

### Task 4: 范围清空时直接失败

范围把所有文件都剔光时，diff 会判成「全部删除」并写出一个空版本。旧版本还在，不是数据
丢失，但这一定是误操作，不该安静地发生。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs:362`
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/BackupOrchestratorTests.cs`

**Interfaces:**
- Consumes: `ScanOptions.Scope`（Task 2）、`ScopeRuleSet.IsAll`（Task 1）
- Produces: 无新公开 API

- [ ] **Step 1: 写失败的测试**

在 `backend/tests/AzureStorageBackup.Api.Tests/BackupOrchestratorTests.cs` 最后一个 `}`
之前追加。该文件的用例**全是 `[SkippableFact]`**（要跑 Azurite 与 7z），新加的两条照此办理；
`Build()`、`AzuriteAccount()`、`RandomName()`、`WriteText()`、`Request()` 都是文件里已有的
私有辅助，`BackupRequest` 是 record，可以用 `with`：

```csharp
    [SkippableFact]
    public async Task Backup_Fails_Loudly_When_The_Scope_Leaves_Nothing()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("scope-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            WriteText("photos/a.jpg", "x");

            var request = Request(account, name) with
            {
                // 全部排除，一个文件都不剩。
                Options = new BackupEngineOptions
                {
                    Scan = new ScanOptions { Scope = ScopeRuleSet.Parse("-") },
                },
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => orchestrator.RunAsync(request));

            Assert.Contains("scope", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }

    [SkippableFact]
    public async Task An_Empty_Root_Without_A_Scope_Is_Still_Allowed()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var (orchestrator, _, factory) = Build();
        var account = AzuriteAccount();
        var name = RandomName("scope-empty-");
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();

        try
        {
            // 没配范围时的空根是正常情况（比如刚建好还没往里放东西），不该被这条兜底拦下。
            // _root 此刻是空的——这条用例刻意什么都不写进去。
            var result = await orchestrator.RunAsync(Request(account, name));

            Assert.Equal(1, result.Version);
        }
        finally
        {
            await container.DeleteIfExistsAsync();
        }
    }
```

- [ ] **Step 2: 跑测试确认它失败**

```bash
cd backend && dotnet test --filter FullyQualifiedName~BackupOrchestratorTests
```

Expected: `Backup_Fails_Loudly_When_The_Scope_Leaves_Nothing` FAIL —— 没有抛异常，备份
正常跑完写出了一个空版本。

**若结果是 Skipped 而不是 Failed**，说明本机没跑 Azurite 或没有 7z，这一条就没有被真正
验证过。先把 Azurite 起起来（`docker run -p 10000:10000 mcr.microsoft.com/azure-storage/azurite`）
再跑，不要在 Skipped 的状态下往下走——那等于没测。

- [ ] **Step 3: 加兜底**

`backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`，在
`scanTracker.Complete();`（第 362 行那句 `ScanAsync` 的下一行）之后插入：

```csharp
        // 范围把所有文件都剔光了：diff 会把上一版本的一切判成删除，写出一个空版本。
        // 旧版本还在，不是数据丢失，但这一定是误操作（比如勾错了一层目录），不能安静地发生。
        // 没配范围时的空根是正常情况，不在此列。
        if (scan.Entries.Count == 0 && scan.EmptyDirs.Count == 0 && !opts.Scan.Scope.IsAll)
            throw new InvalidOperationException(
                "The configured scope selects no files under the local root. "
                + "Nothing would be backed up, so this run was stopped. "
                + "Check the scope selection on this backup.");
```

- [ ] **Step 4: 跑测试确认它通过**

```bash
cd backend && dotnet test --filter FullyQualifiedName~BackupOrchestratorTests
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
  backend/tests/AzureStorageBackup.Api.Tests/BackupOrchestratorTests.cs
git commit -m "feat(backup): stop a run whose scope selects nothing"
```

---

### Task 5: browse 接口分页

现有实现先收集再排序，截断发生在收集阶段，所以截断之后的排序是错的，也没法分页。改成
目录与文件分别枚举（`isDir` 因此免费得到，不用 stat），各自排序后拼接切片，只对当前页
做 stat。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/SystemEndpoints.cs:85-206`
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/BrowseEndpointTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `GET /api/system/browse?path=&offset=&limit=` → `BrowseResponse` 增加两个字段
  `int Total`、`int Offset`（放在 `Skipped` 之后、`Entries` 之前）

- [ ] **Step 1: 写失败的测试**

在 `backend/tests/AzureStorageBackup.Api.Tests/BrowseEndpointTests.cs` 里，先把顶部的
`BrowseDto` 记录改成带新字段的形状：

```csharp
    private sealed record BrowseDto(
        string Path, string? Parent, bool Truncated, int Skipped,
        int Total, int Offset, List<BrowseEntryDto> Entries);
```

然后在最后一个 `}` 之前追加：

```csharp
    [Fact]
    public async Task Pages_Through_A_Directory_With_A_Stable_Order()
    {
        for (var i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(_root, $"f{i:D2}.txt"), "x");

        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var first = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}&offset=0&limit=5");
        var second = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}&offset=5&limit=5");

        // 2 个目录（photos/docs，来自构造函数）+ 13 个文件（readme.txt + f00..f11）
        Assert.Equal(15, first!.Total);
        Assert.Equal(5, first.Entries.Count);
        Assert.Equal(0, first.Offset);
        Assert.Equal(5, second!.Offset);

        // 目录在前，之后按名称排序；两页不重叠、不漏项。
        Assert.Equal(["docs", "photos"], first.Entries.Take(2).Select(e => e.Name));
        Assert.Empty(first.Entries.Select(e => e.Name).Intersect(second.Entries.Select(e => e.Name)));
    }

    [Fact]
    public async Task Paged_Requests_Are_Not_Marked_Truncated()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}&offset=0&limit=1");

        // Truncated 的意思是「还有东西但拿不到了」。分页请求拿得到，Total 已经说明了全貌。
        Assert.False(body!.Truncated);
        Assert.Equal(3, body.Total);
    }

    [Fact]
    public async Task Offset_Past_The_End_Returns_An_Empty_Page_Not_An_Error()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}&offset=999&limit=10");

        Assert.Empty(body!.Entries);
        Assert.Equal(3, body.Total);
    }
```

- [ ] **Step 2: 跑测试确认它失败**

```bash
cd backend && dotnet test --filter FullyQualifiedName~BrowseEndpointTests
```

Expected: 新加的三条 FAIL（`Total` 反序列化为 0）。

- [ ] **Step 3: 替换 handler**

`backend/src/AzureStorageBackup.Api/Endpoints/SystemEndpoints.cs`。把
`app.MapGet("/api/system/browse", (string? path, PathBoundary boundary) => { ... })` 整个
lambda 的**签名与前半部分**（从 `(string? path, ...` 到目录排序那一段，即原第 85–189 行）
替换为下面这段。`parent` 的算法（原第 191–204 行）与 `return Results.Ok(...)` 之外的部分
原样保留，只改 `BrowseResponse` 的构造。

```csharp
        app.MapGet("/api/system/browse", (string? path, int? offset, int? limit, PathBoundary boundary) =>
        {
            var start = string.IsNullOrWhiteSpace(path) ? DefaultBrowseStart(boundary) : path!;

            if (PathBoundaryGuard.Blocked(boundary, start) is { } outside)
                return outside;

            if (!Directory.Exists(start))
                return Results.NotFound(new { error = $"Directory '{start}' does not exist." });

            // 分页请求（传了 offset 或 limit）与老的一次性请求走同一段代码，区别只在切片。
            // 老调用方（PathBrowser）不传这两个参数，行为与从前完全一致：最多 MaxBrowseEntries 项，
            // 超出则 Truncated。
            var paged = offset is not null || limit is not null;
            var skip = Math.Max(0, offset ?? 0);
            var take = paged ? Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize) : MaxBrowseEntries;

            // 目录与文件分开枚举：isDir 因此免费得到，不必对每一项 stat。名字先全部收上来
            // （20 万个字符串是可以接受的），排完序再只对**当前页**取属性——原先那版先收集
            // 再排序，截断发生在收集阶段，于是截断之后的顺序是随机的，也就没法分页。
            List<string> dirs;
            List<string> files;
            try
            {
                dirs = Directory.EnumerateDirectories(start).ToList();
                files = Directory.EnumerateFiles(start).ToList();
            }
            // DirectoryNotFoundException 派生自 IOException，必须先于更宽的分支单独捕获，
            // 否则 Directory.Exists 与这里之间的 TOCTOU 窗口里目录被删会报成 403 而不是 404。
            catch (DirectoryNotFoundException)
            {
                return Results.NotFound(new { error = $"Directory '{start}' does not exist." });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 目录不可读（权限不足）或读取失败（挂载点掉线）：docker 卷挂载场景下是常态，
                // 给一个干净的 403 而不是裸 500。
                return Results.Json(
                    new { error = $"Directory '{start}' could not be read." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            dirs.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
            files.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));

            var total = dirs.Count + files.Count;
            var ordered = dirs.Select(d => (Full: d, IsDir: true))
                .Concat(files.Select(f => (Full: f, IsDir: false)))
                .Skip(skip)
                .Take(take)
                .ToList();

            var truncated = !paged && total > MaxBrowseEntries;
            var entries = new List<BrowseEntry>(ordered.Count);
            var skipped = 0;

            foreach (var (full, isDir) in ordered)
            {
                try
                {
                    var info = new FileInfo(full);
                    entries.Add(new BrowseEntry(
                        Path.GetFileName(full),
                        // 绝对路径，原样可作为下一次 `?path=` 或 localRoot 送回。
                        full,
                        isDir,
                        // 软链的 Length 是 lstat 值（链接自身的字节数），不是目标文件的大小。
                        isDir ? null : info.Length,
                        info.LastWriteTimeUtc,
                        // 软链可能指向根外：返回但标记，前端灰显不可点。
                        !boundary.IsInside(full)));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // 单项 stat 失败（目录 mode 为 r--：可 readdir、不可 stat 子项）跳过该项，
                    // 但要计数并随响应返回——静默跳过会让这种目录渲染成「空目录」，用户看不出差别。
                    skipped++;
                }
            }
```

紧接着保留原有的 `parent` 计算段（`var real = PathBoundary.ResolveReal(start);` 起三行），
然后把返回改成：

```csharp
            return Results.Ok(new BrowseResponse(start, parent, truncated, skipped, total, skip, entries));
        })
        .WithTags("System");
```

在类顶部 `MaxBrowseEntries` 常量旁边加两个常量：

```csharp
    private const int DefaultPageSize = 500;
    private const int MaxPageSize = 2000;
```

最后把 `BrowseResponse` 记录改成：

```csharp
/// <summary>
/// 浏览结果。Parent 为 null 表示已在根（或边界）处，不能再往上。
/// <para><c>Skipped</c>：读不出属性因而未列出的子项数（典型成因是目录 mode 为 <c>r--</c>——
/// 可 readdir、不可 stat 子项）。与 <c>Truncated</c> 同一用途：少给了东西必须说出来。</para>
/// <para><c>Total</c>：该目录的子项总数（不受分页影响）；<c>Offset</c>：本页起始位置。
/// 分页请求恒不置 <c>Truncated</c>——它的意思是「还有东西但拿不到了」，而分页拿得到。</para>
/// </summary>
public record BrowseResponse(
    string Path, string? Parent, bool Truncated, int Skipped, int Total, int Offset,
    IReadOnlyList<BrowseEntry> Entries);
```

- [ ] **Step 4: 跑测试确认它通过**

```bash
cd backend && dotnet test --filter FullyQualifiedName~BrowseEndpointTests
```

Expected: PASS，含既有全部用例（老调用方不传分页参数，行为不变）。

- [ ] **Step 5: 更新前端的类型与调用**

`frontend/src/api/browse.ts` 全文替换为：

```typescript
import { api } from './client'

export interface BrowseEntry {
  name: string
  fullPath: string
  isDirectory: boolean
  length: number | null
  modifiedAt: string
  outsideRoot: boolean
}

export interface BrowseResult {
  path: string
  parent: string | null
  truncated: boolean
  /** 属性读不出来因而未列出的子项数（例如目录 mode 为 r--：可 readdir、不可 stat 子项）。 */
  skipped: number
  /** 该目录的子项总数，不受分页影响。 */
  total: number
  /** 本页起始位置。 */
  offset: number
  entries: BrowseEntry[]
}

export const browseApi = {
  // signal：调用方（PathBrowser）在目录快速切换时用它取消上一次尚未完成的请求，
  // 避免慢的旧响应后到达反而覆盖了新目录的数据。
  // offset/limit：ScopeTree 用来分页拉大目录；都不传时是老行为（一次性，超量则 truncated）。
  list: (path?: string, signal?: AbortSignal, page?: { offset: number; limit: number }) => {
    const params = new URLSearchParams()
    if (path) params.set('path', path)
    if (page) {
      params.set('offset', String(page.offset))
      params.set('limit', String(page.limit))
    }
    const qs = params.toString()
    return api.get<BrowseResult>(`/system/browse${qs ? `?${qs}` : ''}`, { signal })
  },
}
```

- [ ] **Step 6: 确认前端仍能编译**

```bash
cd frontend && npm run build
```

Expected: 成功。`PathBrowser` 只传 `path` 与 `signal`，第三个参数可选，不受影响。

- [ ] **Step 7: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Endpoints/SystemEndpoints.cs \
  backend/tests/AzureStorageBackup.Api.Tests/BrowseEndpointTests.cs \
  frontend/src/api/browse.ts
git commit -m "feat(api): page through browse listings with a stable order"
```

---

### Task 6: 前端规则集与 vitest

后端那份 `ScopeRuleSet` 在 TypeScript 里再实现一遍。**有意的重复**：走 API 意味着每点一个
复选框就要一次往返，一棵树点几十下就是几十次请求。代价用同一份夹具偿付。

**Files:**
- Create: `frontend/src/lib/scopeRules.ts`
- Create: `frontend/src/lib/scopeRules.test.ts`
- Create: `frontend/vitest.config.ts`
- Modify: `frontend/package.json`

**Interfaces:**
- Consumes: `shared/scope-rule-cases.json`（Task 1）
- Produces（供 Task 7 使用）：
  - `type ScopeRules`（不透明的不可变值）
  - `parseScope(text: string | null): ScopeRules`
  - `isAll(rules: ScopeRules): boolean`
  - `isInScope(rules: ScopeRules, path: string): boolean`
  - `isPartial(rules: ScopeRules, dirPath: string): boolean`
  - `withRule(rules: ScopeRules, path: string, included: boolean): ScopeRules`
  - `scopeToText(rules: ScopeRules): string`
  - `scopeState(rules: ScopeRules, path: string): 'checked' | 'indeterminate' | 'unchecked'`

  注意前端**不需要** `mayContainIncluded` —— 那是扫描器决定要不要下降用的，前端的下降由
  用户点展开决定。

- [ ] **Step 1: 装 vitest**

```bash
cd frontend && npm install --save-dev vitest@^3
```

- [ ] **Step 2: 加配置与脚本**

创建 `frontend/vitest.config.ts`：

```typescript
import { defineConfig } from 'vitest/config'

// 只跑纯逻辑测试（scopeRules），不需要 DOM 环境，因此不引 jsdom。
export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
})
```

在 `frontend/package.json` 的 `scripts` 里加一行（放在 `lint` 之后）：

```json
    "test": "vitest run",
```

- [ ] **Step 3: 写失败的测试**

创建 `frontend/src/lib/scopeRules.test.ts`：

```typescript
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { isAll, isInScope, isPartial, parseScope, scopeState, scopeToText, withRule } from './scopeRules'

// 与后端 ScopeRuleSetTests 读的是同一份文件。两份实现行为分叉时，两边同时红。
const fixture = JSON.parse(
  readFileSync(new URL('../../../shared/scope-rule-cases.json', import.meta.url), 'utf8'),
) as {
  queries: {
    name: string
    rules: string[]
    inScope: string[]
    outOfScope: string[]
    partial: string[]
    notPartial: string[]
  }[]
  writes: { name: string; start: string[]; ops: { path: string; included: boolean }[]; expect: string[] }[]
}

describe('shared fixture — queries', () => {
  for (const c of fixture.queries) {
    it(c.name, () => {
      const rules = parseScope(c.rules.join('\n'))
      for (const p of c.inScope) expect(isInScope(rules, p), `in scope: '${p}'`).toBe(true)
      for (const p of c.outOfScope) expect(isInScope(rules, p), `out of scope: '${p}'`).toBe(false)
      for (const p of c.partial) expect(isPartial(rules, p), `partial: '${p}'`).toBe(true)
      for (const p of c.notPartial) expect(isPartial(rules, p), `not partial: '${p}'`).toBe(false)
    })
  }
})

describe('shared fixture — writes', () => {
  for (const c of fixture.writes) {
    it(c.name, () => {
      let rules = parseScope(c.start.join('\n'))
      for (const op of c.ops) rules = withRule(rules, op.path, op.included)
      expect(scopeToText(rules)).toBe(c.expect.join('\n'))
    })
  }
})

describe('scopeState', () => {
  it('reports the three states off the rule set alone', () => {
    const rules = parseScope('-\n+ photos\n- photos/raw')

    expect(scopeState(rules, 'photos')).toBe('indeterminate')
    expect(scopeState(rules, 'photos/edited')).toBe('checked')
    expect(scopeState(rules, 'photos/raw')).toBe('unchecked')
    expect(scopeState(rules, 'music')).toBe('unchecked')
  })

  it('needs no knowledge of the file system', () => {
    // 整个功能的承重点：没有加载任何子节点，三态照样算得出来。
    const rules = parseScope('- docs\n+ docs/2026')

    expect(scopeState(rules, 'docs')).toBe('indeterminate')
    expect(scopeState(rules, 'never/loaded/anything')).toBe('checked')
  })
})

describe('parseScope', () => {
  it('treats null and empty text as "everything"', () => {
    expect(isAll(parseScope(null))).toBe(true)
    expect(isAll(parseScope(''))).toBe(true)
  })

  it('does not mutate the set it is given', () => {
    const original = parseScope('')
    const changed = withRule(original, 'photos', false)

    expect(isAll(original)).toBe(true)
    expect(isInScope(changed, 'photos')).toBe(false)
  })
})
```

- [ ] **Step 4: 跑测试确认它失败**

```bash
cd frontend && npm test
```

Expected: FAIL，`Failed to resolve import "./scopeRules"`。

- [ ] **Step 5: 实现 `scopeRules.ts`**

创建 `frontend/src/lib/scopeRules.ts`：

```typescript
/**
 * 备份范围的边界规则集 —— 后端 ScopeRuleSet.cs 的镜像实现。
 *
 * 为什么要重复一遍：走 API 意味着每点一个复选框就要一次往返，一棵树点几十下就是几十次
 * 请求。代价是两份实现必须行为一致 —— 由 shared/scope-rule-cases.json 这份共享夹具钉住，
 * 两边的测试读同一个文件。**改这里的行为就要同时改后端**，反之亦然。
 *
 * 规则语义：每条是「路径 → 包含/排除」，判定取最长匹配前缀那一条；一条都不匹配则包含。
 * 两条写入不变式让规则集永远最小：
 *   1) 每条规则的判定必须与最近祖先相反，相同即冗余、不落盘；
 *   2) 写入一条规则时删除所有以它为严格前缀的更深规则。
 */

/** 不可变。所有操作都返回新值，React 靠引用变化触发重渲。 */
export type ScopeRules = ReadonlyMap<string, boolean>

const normalize = (path: string): string =>
  path
    .replace(/\\/g, '/')
    .split('/')
    .map((s) => s.trim())
    .filter((s) => s.length > 0)
    .join('/')

/** 该目录下所有后代共有的前缀（根为空串，其余为 "dir/"）。 */
const under = (dirPath: string): string => {
  const p = normalize(dirPath)
  return p.length === 0 ? '' : `${p}/`
}

/** key 是否严格位于 prefix 之下（不含 prefix 所指的目录本身）。 */
const isUnder = (key: string, prefix: string): boolean =>
  key.length > prefix.length && key.startsWith(prefix)

/** Ordinal 序：祖先必排在后代之前（严格前缀恒小于其扩展），规范化因此一遍即可。 */
const ordinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0)

const sorted = (rules: Map<string, boolean>): Map<string, boolean> =>
  new Map([...rules.entries()].sort((a, b) => ordinal(a[0], b[0])))

const lookup = (rules: ReadonlyMap<string, boolean>, path: string): boolean => {
  let p = normalize(path)
  for (;;) {
    const hit = rules.get(p)
    if (hit !== undefined) return hit
    if (p.length === 0) return true // 连根规则都没有 → 默认包含
    const slash = p.lastIndexOf('/')
    p = slash < 0 ? '' : p.slice(0, slash)
  }
}

/** 就地清掉冗余规则（判定与最近祖先相同者）。 */
const dropRedundant = (rules: Map<string, boolean>): Map<string, boolean> => {
  for (const key of [...rules.keys()].sort(ordinal)) {
    const self = rules.get(key)!
    rules.delete(key)
    if (lookup(rules, key) !== self) rules.set(key, self)
  }
  return sorted(rules)
}

/** 解析规则文本。null/空 → 全部包含。无法识别的行跳过而不抛。 */
export function parseScope(text: string | null | undefined): ScopeRules {
  const rules = new Map<string, boolean>()
  for (const raw of (text ?? '').split('\n')) {
    const line = raw.trim()
    if (line.length === 0) continue

    const included = line[0] === '+' ? true : line[0] === '-' ? false : null
    if (included === null) continue

    const path = normalize(line.slice(1))
    // `..` 段命中不了任何真实相对路径，留着只会让人以为它有意义。
    if (path.split('/').some((seg) => seg === '..' || seg === '.')) continue

    rules.set(path, included)
  }
  return dropRedundant(rules)
}

/** 是否「全部包含」（没有任何规则）。 */
export const isAll = (rules: ScopeRules): boolean => rules.size === 0

/** 某路径是否在范围内：最长前缀匹配。 */
export const isInScope = (rules: ScopeRules, path: string): boolean => lookup(rules, path)

/**
 * 三态里的「灰选」：存在以这个目录为严格前缀的规则，说明子树内部有分歧。
 *
 * 这是单向的：`- docs` + `+ docs/a` + `+ docs/b` 而 docs 下恰好只有 a、b 时，实际是全选，
 * 这里仍报灰选。不加载子节点就无从知道两条规则是否穷尽了目录 —— 懒加载的固有代价。
 * 灰选是保守且诚实的一侧，备份结果不受影响，只是显示。
 */
export function isPartial(rules: ScopeRules, dirPath: string): boolean {
  const prefix = under(dirPath)
  for (const key of rules.keys()) if (isUnder(key, prefix)) return true
  return false
}

/** 复选框的三态。**从规则集现算，不存** —— 因此没有父子传播回路，不可能死循环。 */
export const scopeState = (
  rules: ScopeRules,
  path: string,
): 'checked' | 'indeterminate' | 'unchecked' =>
  isPartial(rules, path) ? 'indeterminate' : isInScope(rules, path) ? 'checked' : 'unchecked'

/** 写入一条规则，维护两条不变式，返回新值。 */
export function withRule(rules: ScopeRules, path: string, included: boolean): ScopeRules {
  const key = normalize(path)
  const next = new Map(rules)

  // 不变式 2：清掉被这条覆盖的更深规则。
  const prefix = under(key)
  for (const deeper of [...next.keys()]) if (isUnder(deeper, prefix)) next.delete(deeper)

  // 不变式 1：与最近祖先判定相同则不落盘。先摘掉自身，剩下的最近匹配就是祖先判定。
  next.delete(key)
  if (lookup(next, key) !== included) next.set(key, included)

  return sorted(next)
}

/** 规范化文本，每行一条。空规则集 → 空串（存库时即 null，表示「全部」）。 */
export const scopeToText = (rules: ScopeRules): string =>
  [...rules.entries()]
    .map(([key, included]) => (key.length === 0 ? (included ? '+' : '-') : `${included ? '+' : '-'} ${key}`))
    .join('\n')
```

- [ ] **Step 6: 跑测试确认它通过**

```bash
cd frontend && npm test
```

Expected: PASS，共 16 条（5 条 query + 7 条 write + 4 条独立用例）。这个数字应与后端
`ScopeRuleSetTests` 的夹具部分一一对应。

- [ ] **Step 7: 确认类型检查通过**

```bash
cd frontend && npm run build
```

Expected: 成功。

- [ ] **Step 8: 提交**

```bash
git add frontend/package.json frontend/package-lock.json frontend/vitest.config.ts \
  frontend/src/lib/scopeRules.ts frontend/src/lib/scopeRules.test.ts
git commit -m "feat(ui): mirror the scope rule set in TypeScript, pinned by a shared fixture"
```

---

### Task 7: `ScopeTree` 组件

树本身。数据来自 browse（活的文件系统），勾选状态来自规则集（现算）。

**Files:**
- Create: `frontend/src/components/ScopeTree.tsx`

**Interfaces:**
- Consumes: `browseApi.list`（Task 5）、`scopeRules.ts` 的全部导出（Task 6）
- Produces:
  ```typescript
  export function ScopeTree(props: {
    localRoot: string
    rules: ScopeRules
    onChange: (next: ScopeRules) => void
    ignoreRules: string        // 只用于给命中的行打 ignored 徽标，不影响可勾选性
  }): JSX.Element
  ```

- [ ] **Step 1: 写组件**

创建 `frontend/src/components/ScopeTree.tsx`：

```tsx
import { useEffect, useState } from 'react'
import { browseApi, type BrowseEntry } from '../api/browse'
import { isInScope, scopeState, withRule, type ScopeRules } from '../lib/scopeRules'

const PAGE_SIZE = 500

interface Loaded {
  entries: BrowseEntry[]
  total: number
  loading: boolean
  error: string | null
}

/**
 * 备份范围选择树（设计 docs/backup-scope-selection-design.md §8）。
 *
 * 刻意**不复用** RestoreDialog 那棵树：那棵的数据源是云端版本索引（有限已知全集，三态靠
 * 数已加载的后代文件算），这棵是活的文件系统（无限、会变，三态靠规则集算）。两者只有外观
 * 像，内核相反，合并只会让两边都变脆。
 *
 * 状态只有三份，真相只有一份：rules 是唯一真相，children/expanded 纯展示。
 * 节点的勾选状态**永远从 rules 现算、不存** —— 因此点击只写一条规则就结束，没有
 * 「子改父 → 父改子」的传播回路，不可能死循环。
 */
export function ScopeTree({
  localRoot,
  rules,
  onChange,
  ignoreRules,
}: {
  localRoot: string
  rules: ScopeRules
  onChange: (next: ScopeRules) => void
  ignoreRules: string
}) {
  // key 是**相对 localRoot** 的路径（根为空串），与规则集同一套坐标。
  const [children, setChildren] = useState<Record<string, Loaded>>({})
  const [expanded, setExpanded] = useState<Set<string>>(new Set([''])) 

  const absolute = (relative: string) =>
    relative.length === 0 ? localRoot : `${localRoot.replace(/\/+$/, '')}/${relative}`

  const load = async (relative: string, offset: number) => {
    setChildren((c) => ({
      ...c,
      [relative]: { entries: c[relative]?.entries ?? [], total: c[relative]?.total ?? 0, loading: true, error: null },
    }))
    try {
      const page = await browseApi.list(absolute(relative), undefined, { offset, limit: PAGE_SIZE })
      setChildren((c) => ({
        ...c,
        [relative]: {
          // 追加而不是替换：Load more 不能把已经看到的项抖掉。
          entries: offset === 0 ? page.entries : [...(c[relative]?.entries ?? []), ...page.entries],
          total: page.total,
          loading: false,
          error: null,
        },
      }))
    } catch (e) {
      setChildren((c) => ({
        ...c,
        [relative]: {
          entries: c[relative]?.entries ?? [],
          total: c[relative]?.total ?? 0,
          loading: false,
          error: e instanceof Error ? e.message : String(e),
        },
      }))
    }
  }

  // 根目录一进来就展开：第一层只有一个节点，就是本地根自己。
  useEffect(() => {
    if (localRoot) void load('', 0)
    // localRoot 创建后锁定，实际不会变；列出来是为了让依赖诚实。
  }, [localRoot])

  const toggleExpand = (relative: string) => {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(relative)) {
        next.delete(relative)
      } else {
        next.add(relative)
        if (!children[relative]) void load(relative, 0)
      }
      return next
    })
  }

  return (
    <div style={{ border: '1px solid var(--border)', padding: 'var(--sp-2)', maxHeight: '22rem', overflowY: 'auto' }}>
      <Row
        name={localRoot || '(local root)'}
        relative=""
        isDir
        rules={rules}
        onChange={onChange}
        expanded={expanded}
        onToggleExpand={toggleExpand}
        depth={0}
        ignored={false}
        outsideRoot={false}
        length={null}
      />
      {expanded.has('') && (
        <Level
          relative=""
          depth={1}
          children_={children}
          expanded={expanded}
          onToggleExpand={toggleExpand}
          onLoadMore={load}
          rules={rules}
          onChange={onChange}
          ignoreRules={ignoreRules}
        />
      )}
    </div>
  )
}

function Level({
  relative,
  depth,
  children_,
  expanded,
  onToggleExpand,
  onLoadMore,
  rules,
  onChange,
  ignoreRules,
}: {
  relative: string
  depth: number
  children_: Record<string, Loaded>
  expanded: Set<string>
  onToggleExpand: (relative: string) => void
  onLoadMore: (relative: string, offset: number) => void
  rules: ScopeRules
  onChange: (next: ScopeRules) => void
  ignoreRules: string
}) {
  const state = children_[relative]
  const pad = { paddingLeft: depth * 16 }

  if (!state) return null
  if (state.error) {
    return (
      <div className="text-warn text-sm" style={pad}>
        Could not be read — {state.error}
      </div>
    )
  }
  if (state.loading && state.entries.length === 0) {
    return (
      <div className="text-faint text-sm" style={pad}>
        Loading…
      </div>
    )
  }
  if (state.entries.length === 0) {
    return (
      <div className="text-faint text-sm" style={pad}>
        Empty
      </div>
    )
  }

  return (
    <>
      {state.entries.map((e) => {
        const childRelative = relative.length === 0 ? e.name : `${relative}/${e.name}`
        return (
          <div key={e.fullPath}>
            <Row
              name={e.name}
              relative={childRelative}
              isDir={e.isDirectory}
              rules={rules}
              onChange={onChange}
              expanded={expanded}
              onToggleExpand={onToggleExpand}
              depth={depth}
              ignored={matchesIgnore(childRelative, e.isDirectory, ignoreRules)}
              outsideRoot={e.outsideRoot}
              length={e.length}
            />
            {e.isDirectory && expanded.has(childRelative) && (
              <Level
                relative={childRelative}
                depth={depth + 1}
                children_={children_}
                expanded={expanded}
                onToggleExpand={onToggleExpand}
                onLoadMore={onLoadMore}
                rules={rules}
                onChange={onChange}
                ignoreRules={ignoreRules}
              />
            )}
          </div>
        )
      })}
      {state.entries.length < state.total && (
        <div style={pad}>
          <button
            type="button"
            className="text-sm"
            disabled={state.loading}
            onClick={() => onLoadMore(relative, state.entries.length)}
          >
            {state.loading
              ? 'Loading…'
              : `Load more (showing ${state.entries.length.toLocaleString()} of ${state.total.toLocaleString()})`}
          </button>
        </div>
      )}
    </>
  )
}

function Row({
  name,
  relative,
  isDir,
  rules,
  onChange,
  expanded,
  onToggleExpand,
  depth,
  ignored,
  outsideRoot,
  length,
}: {
  name: string
  relative: string
  isDir: boolean
  rules: ScopeRules
  onChange: (next: ScopeRules) => void
  expanded: Set<string>
  onToggleExpand: (relative: string) => void
  depth: number
  ignored: boolean
  outsideRoot: boolean
  length: number | null
}) {
  // 三态现算。这一行就是「不会死循环」的全部理由：读规则集，不读也不写任何兄弟/父子状态。
  const state = isDir ? scopeState(rules, relative) : isInScope(rules, relative) ? 'checked' : 'unchecked'

  return (
    <div className="row text-sm" style={{ paddingLeft: depth * 16 }}>
      {isDir ? (
        <button
          type="button"
          className="icon-btn hit-target"
          style={{ width: 18 }}
          onClick={() => onToggleExpand(relative)}
        >
          {expanded.has(relative) ? '▾' : '▸'}
        </button>
      ) : (
        <span style={{ width: 18, display: 'inline-block' }} />
      )}
      <input
        type="checkbox"
        checked={state === 'checked'}
        ref={(el) => {
          if (el) el.indeterminate = state === 'indeterminate'
        }}
        disabled={outsideRoot}
        // 点击只做一件事：写一条规则。父子状态在下一次渲染时各自现算。
        onChange={() => onChange(withRule(rules, relative, state !== 'checked'))}
      />
      <span>
        {isDir ? <strong>{name}</strong> : name}
        {isDir && '/'}
      </span>
      {length != null && (
        <span className="text-muted" style={{ marginLeft: 6 }}>
          {formatBytes(length)}
        </span>
      )}
      {ignored && (
        <span
          className="text-muted"
          style={{ marginLeft: 6 }}
          title="Matches this backup's ignore rules. It stays selectable and your choice is saved, but ignore rules are applied separately and will still leave it out of the backup."
        >
          ignored
        </span>
      )}
      {outsideRoot && (
        <span className="text-warn" style={{ marginLeft: 6 }}>
          outside root
        </span>
      )}
    </div>
  )
}

const formatBytes = (n: number): string => {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = n
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${i === 0 ? v : v.toFixed(1)} ${units[i]}`
}

/**
 * 仅用于给行打 `ignored` 徽标 —— 是提示，不是判定。真正的忽略在备份时由后端的
 * IgnoreRuleSet 执行；这里只支持最常见的几种写法（目录后缀 /、`*.ext`、精确路径），
 * 不追求与后端逐字节一致。看不出 gitignore 全部语义是可以接受的，误报/漏报徽标不影响
 * 任何备份结果。
 */
function matchesIgnore(relative: string, isDir: boolean, ignoreRules: string): boolean {
  const name = relative.slice(relative.lastIndexOf('/') + 1)
  for (const raw of ignoreRules.split('\n')) {
    let p = raw.trim()
    if (p.length === 0 || p.startsWith('#') || p.startsWith('!')) continue

    let dirOnly = false
    if (p.endsWith('/')) {
      dirOnly = true
      p = p.slice(0, -1)
    }
    if (dirOnly && !isDir) continue
    p = p.replace(/^\//, '')

    if (p === relative || p === name) return true
    if (p.startsWith('*.') && name.endsWith(p.slice(1))) return true
  }
  return false
}
```

- [ ] **Step 2: 确认类型检查通过**

```bash
cd frontend && npm run build
```

Expected: 成功。

- [ ] **Step 3: 提交**

```bash
git add frontend/src/components/ScopeTree.tsx
git commit -m "feat(ui): add the scope selection tree"
```

---

### Task 8: 接进备份配置表单

把复选框与树放进表单，接上保存与移出范围的警告。

**Files:**
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`
- Modify: `frontend/src/api/backupConfigs.ts`

**Interfaces:**
- Consumes: `ScopeTree`（Task 7）、`scopeRules.ts`（Task 6）、`BackupConfigResponse.ScopeRules`（Task 3）
- Produces: 无（终端任务）

- [ ] **Step 1: 加 API 类型字段**

`frontend/src/api/backupConfigs.ts`：在 `BackupConfig`（响应，含 `localRoot`/`hasPassword`
那个）与 `BackupConfigInput`（请求）两个接口里，各自在 `crossDirGroupRules` 之后加一行：

```typescript
  /** 备份范围。null = 根下全部内容。不可继承，因此不出现在 EffectiveBackupSettings 里。 */
  scopeRules: string | null
```

**只加这两处。** 同文件里的 `EffectiveBackupSettings` 是继承体系解析后的生效值，
`ScopeRules` 不参与继承，不要加进去。

- [ ] **Step 2: 表单初值与回填**

`frontend/src/pages/BackupConfigsPage.tsx`：

`emptyForm` 里（`crossDirGroupRules: null,` 之后）加：

```typescript
  scopeRules: null,
```

`startEdit` 的 `setForm({...})` 里（第 388 行 `crossDirGroupRules: c.crossDirGroupRules,`
之后）加：

```typescript
      scopeRules: c.scopeRules,
```

- [ ] **Step 3: 加 import 与派生状态**

文件顶部 import 区加：

```typescript
import { ScopeTree } from '../components/ScopeTree'
import { isInScope, parseScope, scopeToText } from '../lib/scopeRules'
```

`useMemo` 若尚未从 `react` 导入，一并加上。

范围的**唯一真相是 `form.scopeRules`**（文本），树要的规则集由它现算——不另开一份状态，
免得两者失步。在 `passwordMismatch` 那一行附近加：

```typescript
  // 树要的是规则集，表单存的是文本。现算，不另存一份状态。
  const scope = useMemo(() => parseScope(form.scopeRules), [form.scopeRules])
  // 「全部」与「空规则集」在文本上都是 null，界面上却要分开：勾着复选框是前者，
  // 取消勾选后从全选起步是后者。所以这个开关必须独立于 form。
  const [pickingScope, setPickingScope] = useState(false)
```

`startCreate` 的 `setShowForm(true)` 之前加：

```typescript
    setPickingScope(false)
```

`startEdit` 的 `setShowForm(true)` 之前加：

```typescript
    setPickingScope(!!c.scopeRules)
```

- [ ] **Step 4: 在 Local Root 之下插入复选框与树**

在 `<Field label={editing ? 'Local Root (locked)' : 'Local Root'}>…</Field>`
（第 801–812 行那个 Field）的**结束标签之后**插入：

```tsx
              <Field label="Scope">
                <label className="row" style={{ gap: 'var(--sp-1)' }}>
                  <input
                    type="checkbox"
                    checked={!pickingScope}
                    disabled={!form.localRoot.trim()}
                    onChange={(e) => {
                      setPickingScope(!e.target.checked)
                      // 两个方向都回到「全部」：勾回去是清空范围，取消勾选是从全选起步、
                      // 由用户往下剔除（设计 §10）。
                      set('scopeRules', null)
                    }}
                  />
                  <span>Back up everything in this folder</span>
                </label>
              </Field>
              {pickingScope && !!form.localRoot.trim() && (
                <>
                  <p className="text-muted text-sm" style={{ margin: '0 0 var(--sp-2)' }}>
                    Checking a folder backs up everything inside it, including files added later.
                    Hidden files and files matched by the ignore rules are listed here too — ignore
                    rules are applied separately and still leave those out of the backup.
                  </p>
                  <ScopeTree
                    localRoot={form.localRoot}
                    rules={scope}
                    onChange={(next) => set('scopeRules', scopeToText(next) || null)}
                    ignoreRules={form.ignoreRules ?? editing?.effective.ignoreRules ?? ''}
                  />
                </>
              )}
```

- [ ] **Step 5: 保存时给出移出范围的警告**

`scopeRules` 已经在 `form` 里，`save()` 直接把整个 `form` 交给 API，所以不需要改请求体，
只要在 `save`（第 409 行）开头、`if (passwordMismatch) return` 之后插入警告：

```typescript
    // 范围收窄的警告。移出范围的文件在下次备份时会被当作删除处理——新版本不再包含它们
    // （旧版本仍可还原，直到保留策略把旧版本清掉）。与改忽略规则的行为一致，但用户在树上
    // 点几下就能收窄一大片，所以这里必须说出来。
    if (editing) {
      const before = parseScope(editing.scopeRules)
      const after = parseScope(form.scopeRules)
      // 判断依据是新旧规则集的差异，不扫文件系统：只要两边任一条规则所指的路径从「在范围内」
      // 变成了「不在」，就算收窄。规则所指的路径正是范围发生变化的那些边界点，因此够用。
      const boundaries = new Set<string>([...before.keys(), ...after.keys()])
      const narrowed = [...boundaries].some((p) => isInScope(before, p) && !isInScope(after, p))
      if (
        narrowed &&
        !window.confirm(
          'This narrows the backup scope. Files that are no longer in scope will be treated as '
            + 'deleted on the next backup: new versions will not include them. Older versions keep '
            + 'them until your retention policy removes those versions. Continue?',
        )
      )
        return
    }
```

- [ ] **Step 6: 确认类型检查与前端测试通过**

```bash
cd frontend && npm run build && npm test
```

Expected: 均成功。

- [ ] **Step 7: 全量后端测试**

```bash
cd backend && dotnet test
```

Expected: 全绿。

- [ ] **Step 8: 提交**

```bash
git add frontend/src/pages/BackupConfigsPage.tsx frontend/src/api/backupConfigs.ts
git commit -m "feat(ui): choose a backup's scope from the config form"
```

---

## 不在本计划内

- **`ScopeTree` 的组件测试**。设计文档 §测试 第 4 点列了它，但本计划只引入了 vitest 的
  node 环境（`vitest.config.ts` 里 `environment: 'node'`），没有引入 jsdom 与
  `@testing-library/react`。理由：前端目前零组件测试，为一个组件引入 DOM 测试栈是另一个
  量级的决定，应当单独提出。`ScopeTree` 里真正容易错的是规则集逻辑，那部分已由 Task 6 的
  共享夹具完整覆盖；组件本身只是把 `scopeState` 的结果画出来。
  如果要补，追加一个任务：装 `jsdom` + `@testing-library/react`，`vitest.config.ts` 改
  `environment: 'jsdom'`，测三态渲染、点击后 `onChange` 收到的规则集、Load more 不丢已有勾选。
- 还原与检查不受范围影响 —— 范围只作用于备份扫描。
- 范围不写入云端信息文件，与 `LocalRoot`、忽略规则一样是本地设备配置。
