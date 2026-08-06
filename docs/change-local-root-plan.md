# 迁移备份源路径（Change Local Root）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 `BackupConfig.LocalRoot` 开一条带防呆的专用变更通道，让挂载点搬家后的备份配置能指回自己的数据。

**Architecture:** 新增静态类 `LocalRootMigration` 承载全部判定逻辑（纯计算 + 只读文件系统，不碰数据库、不连云、不解密）；两个端点 `preview` / apply 做编排，apply 自己重跑一遍校验不信任前端结果；`UpdateAsync` 上原有的基础字段锁定**一行不改**——新通道是另开一道门，不是撬开旧锁。

**Tech Stack:** .NET 9 Minimal API + EF Core (SQLite) + xUnit；React 19 + TypeScript + Vitest。

设计文档：`docs/change-local-root-design.md`（本计划的每条判定都可回溯到它）。

> **本计划已实施完毕，此后按历史存档读。** 实施期评审推翻了其中两处，代码与设计文档才是现状：
> `Inspect` 不再收 `currentRoot` 参数（下文各处仍是三参数的旧签名，连同「`LocalRoot` 为空直接
> `NoBaseline`」的短路体——那正是被移除的反模式，见设计 §2 第 3 步）；另外多出一档
> `BaselineUnreadable`（见设计 §5）。

## Global Constraints

- 界面文案一律英文（既有约定）。代码注释用中文，与仓库现有风格一致。
- 后端测试 `cd backend && dotnet test`；前端测试 `cd frontend && npm test`；前端 lint `npm run lint`。
- 越界路径一律 409 + `code: "path_outside_root"`，走既有的 `PathBoundaryGuard.Blocked`，不另立一套。
- 匹配判定只看「存在 + size」；mtime 只统计、不参与判定。
- 分档阈值区间左闭右开：`[95%,100%]` → `Ok`，`[5%,95%)` → `NeedsConfirm`，`[0,5%)` → `Rejected`。
- 抽样上限 200 条；四档 size 分层；档内等距取样。
- 落库只改 `LocalRoot` 一个字段，`ScopeRules` 原文保留不改写。
- 每个任务结束时提交，提交信息用英文正文（仓库现有风格），不加 `Co-Authored-By` 之外的尾注。

---

### Task 1: 判定结果的数据形状与分层抽样

抽样是纯函数，先把它和它的输出类型钉死，后面两个任务都依赖这里的名字。

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/LocalRootMigration.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs`（文件末尾追加）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/LocalRootMigrationTests.cs`

**Interfaces:**
- Consumes: `AzureStorageBackup.Api.Models.IndexEntry`、`VersionIndex`（`Models/BackupIndex.cs`，已存在，勿改）
- Produces:
  - `enum LocalRootVerdict { Ok, NeedsConfirm, Rejected, NoBaseline }`
  - `record LocalRootPreviewResponse(string Verdict, int Sampled, int Matched, int Missing, int SizeMismatch, int MtimeDiffers, double MatchRate, string? Reason, IReadOnlyList<string> Examples)`
  - `record LocalRootChangeRequest(string NewRoot, bool Force = false)`
  - `static IReadOnlyList<IndexEntry> LocalRootMigration.Sample(IReadOnlyList<IndexEntry> entries, int max = 200)`

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/LocalRootMigrationTests.cs`：

```csharp
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class LocalRootMigrationSampleTests
{
    private static IndexEntry Entry(string path, long length, string kind = "file",
        DateTimeOffset? unreadableAt = null) => new()
    {
        Path = path,
        Kind = kind,
        Length = length,
        Mtime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Permissions = "644",
        UnreadableAt = unreadableAt,
    };

    [Fact]
    public void Sample_Takes_Everything_When_Below_The_Cap()
    {
        var entries = Enumerable.Range(0, 30).Select(i => Entry($"f{i}", i)).ToList();

        var sample = LocalRootMigration.Sample(entries, max: 200);

        Assert.Equal(30, sample.Count);
        Assert.Equal(entries.Select(e => e.Path).OrderBy(p => p), sample.Select(e => e.Path).OrderBy(p => p));
    }

    [Fact]
    public void Sample_Never_Exceeds_The_Cap()
    {
        var entries = Enumerable.Range(0, 5000).Select(i => Entry($"f{i}", i * 1000L)).ToList();

        var sample = LocalRootMigration.Sample(entries, max: 200);

        Assert.Equal(200, sample.Count);
        Assert.Equal(200, sample.Select(e => e.Path).Distinct().Count());
    }

    /// <summary>
    /// 四档都要有代表。全压在一档上，就检不出"只有大文件那个子目录挂对了"这种半错迁移。
    /// </summary>
    [Fact]
    public void Sample_Covers_All_Four_Size_Buckets()
    {
        var entries = new List<IndexEntry>();
        for (var i = 0; i < 300; i++) entries.Add(Entry($"empty/{i}", 0));
        for (var i = 0; i < 300; i++) entries.Add(Entry($"small/{i}", 1024));
        for (var i = 0; i < 300; i++) entries.Add(Entry($"medium/{i}", 50L * 1024 * 1024));
        for (var i = 0; i < 300; i++) entries.Add(Entry($"large/{i}", 500L * 1024 * 1024));

        var sample = LocalRootMigration.Sample(entries, max: 200);

        Assert.Contains(sample, e => e.Path.StartsWith("empty/"));
        Assert.Contains(sample, e => e.Path.StartsWith("small/"));
        Assert.Contains(sample, e => e.Path.StartsWith("medium/"));
        Assert.Contains(sample, e => e.Path.StartsWith("large/"));
    }

    /// <summary>
    /// 索引顺序近似目录序：取头部会把样本全压在第一个子目录里，
    /// 于是"只挂上了其中一个子目录"恰好检不出来。必须等距铺开。
    /// </summary>
    [Fact]
    public void Sample_Spreads_Across_The_Index_Instead_Of_Taking_The_Head()
    {
        var entries = Enumerable.Range(0, 1000).Select(i => Entry($"dir{i / 100}/f{i}", 1024)).ToList();

        var sample = LocalRootMigration.Sample(entries, max: 200);

        var dirs = sample.Select(e => e.Path.Split('/')[0]).Distinct().ToList();
        Assert.Equal(10, dirs.Count);
    }

    /// <summary>
    /// UnreadableAt 条目的 size/mtime 沿用上一版本，本就不保证与磁盘一致，
    /// 拿来判定只会制造假不匹配。
    /// </summary>
    [Fact]
    public void Sample_Excludes_Entries_Carrying_UnreadableAt()
    {
        var entries = new List<IndexEntry>
        {
            Entry("good", 100),
            Entry("stale", 100, unreadableAt: DateTimeOffset.UtcNow),
        };

        var sample = LocalRootMigration.Sample(entries, max: 200);

        Assert.Single(sample);
        Assert.Equal("good", sample[0].Path);
    }

    /// <summary>某档条目数少于分配名额时，剩余名额让给其它档，不白白浪费样本。</summary>
    [Fact]
    public void Sample_Reallocates_Quota_From_Underfilled_Buckets()
    {
        var entries = new List<IndexEntry> { Entry("only-big", 500L * 1024 * 1024) };
        for (var i = 0; i < 500; i++) entries.Add(Entry($"small/{i}", 1024));

        var sample = LocalRootMigration.Sample(entries, max: 200);

        Assert.Equal(200, sample.Count);
        Assert.Contains(sample, e => e.Path == "only-big");
    }
}
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `cd backend && dotnet test --filter LocalRootMigrationSampleTests`
Expected: 编译失败，`error CS0103: The name 'LocalRootMigration' does not exist`

- [ ] **Step 3: 加 DTO**

在 `backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs` **文件末尾**追加：

```csharp
/// <summary>迁移本地根路径的判定结论（设计 docs/change-local-root-design.md §5）。</summary>
public enum LocalRootVerdict
{
    /// <summary>抽样匹配率 ≥95%，直接放行。</summary>
    Ok = 0,

    /// <summary>匹配率落在 [5%, 95%)，需要用户确认（Force）。</summary>
    NeedsConfirm = 1,

    /// <summary>匹配率 &lt;5%（含一个都找不到），默认拒绝，仍可 Force 越过。</summary>
    Rejected = 2,

    /// <summary>没有可比对的基线（当前根为空、无任何版本、或索引读不出来），只校验了路径本身。</summary>
    NoBaseline = 3,
}

/// <summary>
/// 迁移本地根路径的校验报告。<c>MtimeDiffers</c> 仅供参考、**不参与判定**——跨文件系统搬迁时
/// mtime 的精度与保留情况经常不一致，拿它当判据会大面积误伤，而它对不上的真实后果只是
/// 下次备份重传这些文件。
/// </summary>
/// <param name="Examples">最多 10 条不匹配的相对路径。这不是装饰：用户在 NAS 上拿不到命令行，
/// 界面必须把「到底哪些文件对不上」直接摆出来，否则一个 68% 的匹配率无从判断该不该强制。</param>
public record LocalRootPreviewResponse(
    string Verdict,
    int Sampled,
    int Matched,
    int Missing,
    int SizeMismatch,
    int MtimeDiffers,
    double MatchRate,
    string? Reason,
    IReadOnlyList<string> Examples);

/// <summary>迁移本地根路径请求。<c>Force</c> 用于越过 NeedsConfirm / Rejected。</summary>
public record LocalRootChangeRequest(string NewRoot, bool Force = false);

/// <summary>preview 端点请求体。</summary>
public record LocalRootPreviewRequest(string NewRoot);
```

- [ ] **Step 4: 实现分层抽样**

创建 `backend/src/AzureStorageBackup.Api/Services/LocalRootMigration.cs`：

```csharp
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 迁移本地根路径的判定逻辑（设计 docs/change-local-root-design.md）。
///
/// **静态、无依赖**是刻意的：它只做纯计算加只读文件系统访问，不碰数据库、不连云、不解密。
/// 取索引要用的账户/密码/云端信息由端点备好后把 baseline 传进来。这样整套分档逻辑
/// 能脱离 HTTP、EF 与 Azure 单测——喂一个假索引加一个临时目录就验得完。
/// </summary>
public static class LocalRootMigration
{
    /// <summary>抽样上限。200 条足够把「填错目录」摁住，又不至于让一次 preview 变成全量扫描。</summary>
    public const int DefaultSampleSize = 200;

    private const long SmallCeiling = 1L * 1024 * 1024;          // <1MB
    private const long MediumCeiling = 100L * 1024 * 1024;       // 1–100MB

    /// <summary>
    /// 从索引条目里分层抽样。按 Length 分四档（0 / &lt;1MB / 1–100MB / &gt;100MB），
    /// 每档按档内条目数占比分名额，**档内等距取样**而非取头部——索引顺序近似目录序，
    /// 取头部会把样本全压在第一个子目录里，那样「只挂上了其中一个子目录」这种半对半错的
    /// 迁移就恰好检不出来。
    ///
    /// 带 UnreadableAt 的条目排除在外：它们的 size/mtime 沿用上一版本，本就不保证与磁盘一致。
    /// </summary>
    public static IReadOnlyList<IndexEntry> Sample(IReadOnlyList<IndexEntry> entries, int max = DefaultSampleSize)
    {
        var pool = entries.Where(e => e.UnreadableAt is null).ToList();
        if (pool.Count <= max)
            return pool;

        var buckets = new List<IndexEntry>[4];
        for (var i = 0; i < buckets.Length; i++) buckets[i] = [];
        foreach (var e in pool)
            buckets[BucketOf(e.Length)].Add(e);

        // 按占比分名额，然后把空档/不足档的余额还给还装得下的档，避免样本白白浪费。
        //
        // **非空档保底 1 个**：纯按占比算，一个「500 个小文件 + 1 个大文件」的索引里，
        // 大文件那档四舍五入下来是 0 个名额，于是唯一那个大文件永远抽不到——而大文件恰恰是
        // 最值得看一眼的（挂错盘时它们往往就是缺的那批）。四档最多占用 4 个保底名额，
        // 对 200 的上限无足轻重。
        var quota = new int[buckets.Length];
        for (var i = 0; i < buckets.Length; i++)
            quota[i] = buckets[i].Count == 0
                ? 0
                : Math.Clamp((int)((long)max * buckets[i].Count / pool.Count), 1, buckets[i].Count);

        var assigned = quota.Sum();

        // 保底可能把总额顶过上限（max 小于非空档数时）。从名额最多的档往回收，
        // 保底的那 1 个不动——收成 0 就等于把整档丢掉，正是保底要防的事。
        while (assigned > max)
        {
            var fattest = -1;
            for (var i = 0; i < buckets.Length; i++)
                if (quota[i] > 1 && (fattest < 0 || quota[i] > quota[fattest])) fattest = i;
            if (fattest < 0) break;   // 各档都只剩保底，收无可收
            quota[fattest]--;
            assigned--;
        }

        while (assigned < max)
        {
            var grew = false;
            for (var i = 0; i < buckets.Length && assigned < max; i++)
            {
                if (quota[i] >= buckets[i].Count) continue;
                quota[i]++;
                assigned++;
                grew = true;
            }
            if (!grew) break;   // 全部档都装满了（pool.Count > max 时不会发生，保险起见）
        }

        var result = new List<IndexEntry>(max);
        for (var i = 0; i < buckets.Length; i++)
            result.AddRange(TakeEvenly(buckets[i], quota[i]));
        return result;
    }

    private static int BucketOf(long length) => length switch
    {
        0 => 0,
        < SmallCeiling => 1,
        < MediumCeiling => 2,
        _ => 3,
    };

    /// <summary>档内等距取样：把 count 个位置均匀铺在整个列表上，而不是取前 count 个。</summary>
    private static IEnumerable<IndexEntry> TakeEvenly(List<IndexEntry> items, int count)
    {
        if (count <= 0) yield break;
        if (count >= items.Count)
        {
            foreach (var e in items) yield return e;
            yield break;
        }

        for (var i = 0; i < count; i++)
            yield return items[(int)((long)i * items.Count / count)];
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

Run: `cd backend && dotnet test --filter LocalRootMigrationSampleTests`
Expected: PASS，6 个测试全绿

- [ ] **Step 6: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/LocalRootMigration.cs \
        backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs \
        backend/tests/AzureStorageBackup.Api.Tests/LocalRootMigrationTests.cs
git commit -m "feat(local-root): sample a version index in a way that spots a half-right move

Stratify by size and step evenly through each bucket rather than taking
the head: index order tracks directory order, so a head sample lands
entirely in the first subdirectory and a root that only got one of its
subdirectories mounted would read as a clean match. Entries carrying
UnreadableAt stay out of the pool — their size and mtime were inherited
from the previous version and never promised to match the disk."
```

---

### Task 2: 校验与分档裁决

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/LocalRootMigration.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/LocalRootMigrationTests.cs`（追加一个测试类）

**Interfaces:**
- Consumes: `LocalRootMigration.Sample`（Task 1）、`LocalRootVerdict`、`LocalRootPreviewResponse`（Task 1）
- Produces: `static LocalRootPreviewResponse LocalRootMigration.Inspect(string? currentRoot, string newRoot, VersionIndex? baseline)`

- [ ] **Step 1: 写失败的测试**

在 `LocalRootMigrationTests.cs` **末尾追加**：

```csharp
/// <summary>
/// 校验与分档。每个用例都在临时目录上真跑一遍文件系统比对——这层逻辑的价值
/// 全在"它到底怎么看待磁盘上的东西"，用假文件系统测等于什么都没测。
/// </summary>
public sealed class LocalRootMigrationInspectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lrm-" + Guid.NewGuid().ToString("N")[..8]);

    public LocalRootMigrationInspectTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WriteFile(string relative, long length)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[length]);
    }

    private static IndexEntry Entry(string path, long length, string kind = "file") => new()
    {
        Path = path,
        Kind = kind,
        Length = length,
        Mtime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Permissions = "644",
    };

    private static VersionIndex Index(params IndexEntry[] entries) =>
        new() { Version = 1, Entries = [.. entries] };

    [Fact]
    public void Everything_Present_And_Same_Size_Is_Ok()
    {
        for (var i = 0; i < 20; i++) WriteFile($"d/f{i}", 10);
        var index = Index([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect("/old/root", _root, index);

        Assert.Equal(nameof(LocalRootVerdict.Ok), r.Verdict);
        Assert.Equal(20, r.Sampled);
        Assert.Equal(20, r.Matched);
        Assert.Equal(1.0, r.MatchRate);
        Assert.Empty(r.Examples);
    }

    [Fact]
    public void Half_The_Files_Missing_Needs_Confirmation()
    {
        for (var i = 0; i < 10; i++) WriteFile($"d/f{i}", 10);
        var index = Index([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect("/old/root", _root, index);

        Assert.Equal(nameof(LocalRootVerdict.NeedsConfirm), r.Verdict);
        Assert.Equal(10, r.Matched);
        Assert.Equal(10, r.Missing);
        Assert.NotEmpty(r.Examples);
        Assert.True(r.Examples.Count <= 10, "examples are capped at 10");
    }

    [Fact]
    public void An_Empty_Directory_Is_Rejected()
    {
        var index = Index([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect("/old/root", _root, index);

        Assert.Equal(nameof(LocalRootVerdict.Rejected), r.Verdict);
        Assert.Equal(0, r.Matched);
        Assert.Equal(0.0, r.MatchRate);
    }

    /// <summary>size 对不上说明多半填错了目录——它和"文件不存在"同等地算作不匹配。</summary>
    [Fact]
    public void Size_Mismatch_Counts_As_A_Miss()
    {
        for (var i = 0; i < 20; i++) WriteFile($"d/f{i}", 99);
        var index = Index([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect("/old/root", _root, index);

        Assert.Equal(20, r.SizeMismatch);
        Assert.Equal(0, r.Matched);
        Assert.Equal(nameof(LocalRootVerdict.Rejected), r.Verdict);
    }

    /// <summary>
    /// mtime 只统计不判定：跨文件系统搬迁时它经常整体偏移，让它参与判定会把一次
    /// 完全正确的迁移判成 Rejected。
    /// </summary>
    [Fact]
    public void Mtime_Differences_Are_Counted_But_Never_Judged()
    {
        for (var i = 0; i < 20; i++) WriteFile($"d/f{i}", 10);
        // 索引里的 mtime 是 2026-01-01，磁盘上的是"刚刚"，20 条全都对不上。
        var index = Index([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect("/old/root", _root, index);

        Assert.Equal(20, r.MtimeDiffers);
        Assert.Equal(nameof(LocalRootVerdict.Ok), r.Verdict);
        Assert.Equal(20, r.Matched);
    }

    /// <summary>symlink 的 IndexEntry.Length 恒为 0（LocalFileScanner.cs:170），不能拿 size 比。</summary>
    [Fact]
    public void Symlinks_Are_Matched_On_Existence_Only()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d"));
        File.WriteAllBytes(Path.Combine(_root, "d", "target"), new byte[123]);
        File.CreateSymbolicLink(Path.Combine(_root, "d", "link"), Path.Combine(_root, "d", "target"));

        var index = Index(Entry("d/link", 0, kind: "symlink"), Entry("d/target", 123));

        var r = LocalRootMigration.Inspect("/old/root", _root, index);

        Assert.Equal(nameof(LocalRootVerdict.Ok), r.Verdict);
        Assert.Equal(2, r.Matched);
    }

    [Fact]
    public void An_Empty_Current_Root_Has_No_Baseline_To_Compare()
    {
        var index = Index(Entry("d/f", 10));

        var r = LocalRootMigration.Inspect(currentRoot: "", _root, index);

        Assert.Equal(nameof(LocalRootVerdict.NoBaseline), r.Verdict);
        Assert.NotNull(r.Reason);
        Assert.Equal(0, r.Sampled);
    }

    [Fact]
    public void A_Null_Baseline_Has_Nothing_To_Compare()
    {
        var r = LocalRootMigration.Inspect("/old/root", _root, baseline: null);

        Assert.Equal(nameof(LocalRootVerdict.NoBaseline), r.Verdict);
        Assert.NotNull(r.Reason);
    }

    /// <summary>索引里一条可比条目都没有（全是 UnreadableAt）也是无基线，不是 0% 匹配。</summary>
    [Fact]
    public void A_Baseline_With_No_Comparable_Entries_Is_NoBaseline()
    {
        var stale = new IndexEntry
        {
            Path = "d/f", Kind = "file", Length = 10,
            Mtime = DateTimeOffset.UnixEpoch, Permissions = "644",
            UnreadableAt = DateTimeOffset.UtcNow,
        };

        var r = LocalRootMigration.Inspect("/old/root", _root, Index(stale));

        Assert.Equal(nameof(LocalRootVerdict.NoBaseline), r.Verdict);
    }

    [Fact]
    public void Inspect_Never_Touches_The_New_Root()
    {
        WriteFile("d/f", 10);
        var before = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();

        LocalRootMigration.Inspect("/old/root", _root, Index(Entry("d/f", 10)));

        var after = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();
        Assert.Equal(before, after);
    }
}
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `cd backend && dotnet test --filter LocalRootMigrationInspectTests`
Expected: 编译失败，`error CS0117: 'LocalRootMigration' does not contain a definition for 'Inspect'`

- [ ] **Step 3: 实现 Inspect**

在 `LocalRootMigration.cs` 的 `Sample` 方法**之前**插入（保持公开方法在前）：

```csharp
    /// <summary>报告里最多列几条不匹配的样例路径。</summary>
    public const int MaxExamples = 10;

    private const double OkThreshold = 0.95;
    private const double RejectThreshold = 0.05;

    /// <summary>
    /// 比对新根与基线索引，给出判定。**纯查询**：只读文件系统，不改任何东西，可安全重入
    /// ——apply 正是靠再跑一遍它来兜住 preview 与 apply 之间的竞态。
    ///
    /// 调用方负责在此之前做完路径校验（存在/是目录/边界内）与忙检查。
    /// </summary>
    /// <param name="currentRoot">配置当前的根。为空表示导入时没拿到 SourceRootHint，无基线可比。</param>
    /// <param name="baseline">最新版本的索引；取不到（无版本/缓存缺失）时传 null。</param>
    public static LocalRootPreviewResponse Inspect(string? currentRoot, string newRoot, VersionIndex? baseline)
    {
        if (string.IsNullOrWhiteSpace(currentRoot))
            return NoBaseline("This backup has no local root recorded yet, so there is nothing to compare against.");
        if (baseline is null)
            return NoBaseline("This backup has no version index available to compare against.");

        var sample = Sample(baseline.Entries);
        if (sample.Count == 0)
            return NoBaseline("The latest version index has no comparable entries.");

        var matched = 0;
        var missing = 0;
        var sizeMismatch = 0;
        var mtimeDiffers = 0;
        var examples = new List<string>();

        foreach (var entry in sample)
        {
            var full = Path.Combine(newRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var outcome = Compare(entry, full, ref mtimeDiffers);
            switch (outcome)
            {
                case Outcome.Matched:
                    matched++;
                    break;
                case Outcome.Missing:
                    missing++;
                    if (examples.Count < MaxExamples) examples.Add(entry.Path);
                    break;
                case Outcome.SizeMismatch:
                    sizeMismatch++;
                    if (examples.Count < MaxExamples) examples.Add(entry.Path);
                    break;
            }
        }

        var rate = (double)matched / sample.Count;
        // 区间左闭右开，边界值归入更宽松的一档。
        var verdict = rate >= OkThreshold
            ? LocalRootVerdict.Ok
            : rate >= RejectThreshold
                ? LocalRootVerdict.NeedsConfirm
                : LocalRootVerdict.Rejected;

        return new LocalRootPreviewResponse(
            verdict.ToString(), sample.Count, matched, missing, sizeMismatch, mtimeDiffers,
            rate, Reason: null, examples);
    }

    private enum Outcome { Matched, Missing, SizeMismatch }

    /// <summary>
    /// 单条比对。判定只看「存在 + size」；mtime 单独计数但**不影响结果**
    /// ——跨文件系统搬迁时它经常整体偏移，让它参与判定会把一次完全正确的迁移判成失败。
    /// </summary>
    private static Outcome Compare(IndexEntry entry, string fullPath, ref int mtimeDiffers)
    {
        // symlink 的 IndexEntry.Length 恒为 0（LocalFileScanner.cs:170），比 size 毫无意义，
        // 只确认它还在、且仍是个链接。
        if (string.Equals(entry.Kind, "symlink", StringComparison.Ordinal))
        {
            var link = new FileInfo(fullPath);
            return link.Exists && link.LinkTarget is not null ? Outcome.Matched : Outcome.Missing;
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists)
            return Outcome.Missing;

        // mtime 只在文件确实存在时才有得比；秒级容差吸收文件系统的时间戳粒度差异。
        if (Math.Abs((info.LastWriteTimeUtc - entry.Mtime.UtcDateTime).TotalSeconds) > 1)
            mtimeDiffers++;

        return info.Length == entry.Length ? Outcome.Matched : Outcome.SizeMismatch;
    }

    private static LocalRootPreviewResponse NoBaseline(string reason) => new(
        nameof(LocalRootVerdict.NoBaseline), Sampled: 0, Matched: 0, Missing: 0,
        SizeMismatch: 0, MtimeDiffers: 0, MatchRate: 0, Reason: reason, Examples: []);
```

- [ ] **Step 4: 跑测试确认通过**

Run: `cd backend && dotnet test --filter LocalRootMigration`
Expected: PASS，Task 1 的 6 个 + 本任务的 10 个全绿

- [ ] **Step 5: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/LocalRootMigration.cs \
        backend/tests/AzureStorageBackup.Api.Tests/LocalRootMigrationTests.cs
git commit -m "feat(local-root): judge a candidate root on existence and size alone

Modification times are counted and reported but kept out of the verdict.
A move across filesystems routinely shifts every timestamp — rsync
without -t, a different timestamp granularity — and a correct move would
read as a total mismatch. What a wrong timestamp actually costs is a
re-upload next run; what a wrong size means is that the root is pointing
at something else entirely."
```

---

### Task 3: 服务层的落库方法，以及旧锁仍然锁着的回归保护

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/IBackupConfigService.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupConfigService.cs:33-53`（只改文档注释与新增方法，**不动**锁定检查那几行）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupConfigServiceTests.cs`（追加）

**Interfaces:**
- Consumes: `BackupConfig`（`Models/BackupConfig.cs`）
- Produces: `Task<BackupConfig?> IBackupConfigService.ChangeLocalRootAsync(int id, string newRoot, CancellationToken ct = default)`

- [ ] **Step 1: 写失败的测试**

在 `BackupConfigServiceTests.cs` 类**末尾**追加：

```csharp
    [Fact]
    public async Task ChangeLocalRoot_Moves_The_Root_And_Leaves_Everything_Else_Alone()
    {
        var created = await _sut.CreateAsync(Sample());
        // 范围规则是相对根的坐标，换根后必须原文保留、一字不改。
        created.ScopeRules = "+ albums\n- albums/tmp";
        await _sut.UpdateAsync(created.Id, created);
        var before = await _sut.GetAsync(created.Id);

        var moved = await _sut.ChangeLocalRootAsync(created.Id, "/mnt/photos");

        Assert.NotNull(moved);
        Assert.Equal("/mnt/photos", moved!.LocalRoot);

        var after = await _sut.GetAsync(created.Id);
        Assert.Equal("/mnt/photos", after!.LocalRoot);
        Assert.Equal(before!.ScopeRules, after.ScopeRules);
        Assert.Equal(before.AccountId, after.AccountId);
        Assert.Equal(before.ContainerName, after.ContainerName);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.PasswordProtected, after.PasswordProtected);
        Assert.Equal(before.IndexTier, after.IndexTier);
        Assert.Equal(before.DataTier, after.DataTier);
        Assert.Equal(before.IgnoreRules, after.IgnoreRules);
        Assert.Equal(before.MaxVersions, after.MaxVersions);
        Assert.Equal(before.RetentionMode, after.RetentionMode);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
    }

    [Fact]
    public async Task ChangeLocalRoot_Returns_Null_For_An_Unknown_Config()
    {
        Assert.Null(await _sut.ChangeLocalRootAsync(999999, "/mnt/photos"));
    }

    /// <summary>
    /// 新通道是另开的一道门，不是把旧锁撬开：常规更新路径必须**依然**拒绝改根，
    /// 否则日后一次顺手的编辑就能悄悄换掉根路径。
    /// </summary>
    [Fact]
    public async Task Update_Still_Refuses_To_Change_The_Local_Root()
    {
        var created = await _sut.CreateAsync(Sample());
        var update = await _sut.GetAsync(created.Id);
        update!.LocalRoot = "/mnt/photos";
        update.PasswordProtected = null;   // 空 = 保留原值，避免撞上密码那条拒绝

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(created.Id, update));
    }
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `cd backend && dotnet test --filter BackupConfigServiceTests`
Expected: 编译失败，`'IBackupConfigService' does not contain a definition for 'ChangeLocalRootAsync'`

- [ ] **Step 3: 加接口方法**

在 `backend/src/AzureStorageBackup.Api/Services/IBackupConfigService.cs` 的 `UpdateAsync` 声明**之后**加：

```csharp
    /// <summary>
    /// 迁移本地根路径（设计 docs/change-local-root-design.md）。只改 LocalRoot 一个字段，
    /// 其余一概不动——ScopeRules 是相对根的坐标，换根后语义不变，必须原文保留。
    /// 校验由调用方（端点）在此之前完成；本方法只负责落库。配置不存在返回 null。
    /// </summary>
    Task<BackupConfig?> ChangeLocalRootAsync(int id, string newRoot, CancellationToken ct = default);
```

- [ ] **Step 4: 实现，并更新那段锁定注释**

在 `BackupConfigService.cs` 里，把 `UpdateAsync` 上方的文档注释（第 33-37 行那段）替换为：

```csharp
    /// <summary>
    /// 更新配置。基础字段（AccountId/ContainerName/LocalRoot/IndexTier/DataTier）与密码创建后锁定
    /// （§4.5）：本地权威状态（TrackedInfoStore/LocalIndexCache）按 账户+container 键控，改这些字段会与云端/本地
    /// 索引失步。检测到变更时抛 <see cref="InvalidOperationException"/>，端点映射为 400。
    ///
    /// <para>
    /// LocalRoot 另有一条带校验的专用通道 <see cref="ChangeLocalRootAsync"/>（挂载点搬家用）。
    /// **这里的检查不因此放松**：常规编辑路径继续拒绝改根，否则一次顺手的改名保存就能悄悄换掉根，
    /// 绕开那条通道的全部防呆。
    /// </para>
    /// </summary>
```

然后在 `UpdateAsync` 方法**之后**、`DeleteAsync` **之前**插入：

```csharp
    /// <inheritdoc />
    public async Task<BackupConfig?> ChangeLocalRootAsync(int id, string newRoot, CancellationToken ct = default)
    {
        var existing = await db.BackupConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (existing is null)
            return null;

        // 只动这一个字段。ScopeRules 尤其不能顺手改写：它是相对根的坐标，
        // 新根下是同一份数据时，规则原样继续正确命中。
        existing.LocalRoot = newRoot;
        await db.SaveChangesAsync(ct);
        return existing;
    }
```

- [ ] **Step 5: 跑测试确认通过**

Run: `cd backend && dotnet test --filter BackupConfigServiceTests`
Expected: PASS

- [ ] **Step 6: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/IBackupConfigService.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupConfigService.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupConfigServiceTests.cs
git commit -m "feat(local-root): move the root through its own door, not the old lock

The ordinary update path keeps refusing to change the root, and a test
now pins that refusal in place. Reopening it would let a routine rename
quietly relocate the root and skip every guard the new channel exists to
run. Scope rules ride along untouched — they are relative to the root, so
the same data under a new path keeps matching them."
```

---

### Task 4: 两个端点

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`（在 `reset-password` 端点之后追加）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/LocalRootEndpointTests.cs`（新建）

**Interfaces:**
- Consumes: `LocalRootMigration.Inspect`（Task 2）、`IBackupConfigService.ChangeLocalRootAsync`（Task 3）、`LocalRootPreviewRequest` / `LocalRootChangeRequest` / `LocalRootPreviewResponse`（Task 1）、既有的 `PathBoundaryGuard.Blocked`、`KeyringGuard.Blocked`、`BackupBusyTracker.IsBusy`、`TrackedInfoStore.LoadAsync`、`ILocalIndexCache.ReadAsync`、`ISecretReader.RevealBackupPassword`、`IOperationLog.AppendAsync`
- Produces: `POST /api/backup-configs/{id}/local-root/preview`、`POST /api/backup-configs/{id}/local-root`

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/LocalRootEndpointTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class LocalRootEndpointTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>, IDisposable
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly IServiceProvider _services = factory.Services;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lre-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task<int> CreateAccountAsync()
    {
        var req = new AccountRequest(
            Name: "acct-" + Guid.NewGuid().ToString("N")[..6],
            Description: null,
            BlobEndpoint: "https://example.blob.core.windows.net",
            Region: AzureRegion.Global,
            AccountKey: "dGVzdGtleQ==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null);
        var res = await _client.PostAsJsonAsync("/api/accounts", req);
        var account = await res.Content.ReadFromJsonAsync<AccountResponse>();
        return account!.Id;
    }

    /// <summary>建一条配置，直接落库（绕开创建端点对本地根存在性的校验）。</summary>
    private async Task<int> CreateConfigAsync(int accountId, string localRoot)
    {
        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var created = await svc.CreateAsync(new BackupConfig
        {
            AccountId = accountId,
            ContainerName = "c" + Guid.NewGuid().ToString("N")[..8],
            Name = "photos",
            LocalRoot = localRoot,
            IndexTier = StorageTier.Hot,
            DataTier = StorageTier.Cool,
        });
        return created.Id;
    }

    [Fact]
    public async Task Preview_Rejects_A_Relative_Path()
    {
        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = "relative/path" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Preview_Rejects_An_Empty_Path()
    {
        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Preview_Rejects_A_Path_That_Does_Not_Exist()
    {
        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview",
            new { newRoot = Path.Combine(_dir, "nope") });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Preview_Rejects_A_Path_That_Is_A_File()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "afile");
        await File.WriteAllTextAsync(file, "x");
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = file });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Preview_Reports_NoBaseline_When_The_Backup_Has_No_Versions()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = target });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<LocalRootPreviewResponse>();
        Assert.Equal(nameof(LocalRootVerdict.NoBaseline), body!.Verdict);
        Assert.NotNull(body.Reason);
    }

    /// <summary>preview 是纯查询：跑完之后配置必须一字未动。</summary>
    [Fact]
    public async Task Preview_Does_Not_Change_Anything()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        await _client.PostAsJsonAsync($"/api/backup-configs/{id}/local-root/preview", new { newRoot = target });

        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var config = await svc.GetAsync(id);
        Assert.Equal(_dir, config!.LocalRoot);
    }

    [Fact]
    public async Task Apply_Moves_The_Root_When_There_Is_No_Baseline()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(target, body!.LocalRoot);
    }

    /// <summary>导入时没拿到 SourceRootHint 的配置，根是空串——它必须能被补上。</summary>
    [Fact]
    public async Task Apply_Fills_In_An_Empty_Root_Left_Behind_By_Import()
    {
        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAccountAsync(), localRoot: "");

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = _dir, force = false });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(_dir, body!.LocalRoot);
    }

    [Fact]
    public async Task Apply_Is_Refused_While_The_Backup_Is_Busy()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var accountId = await CreateAccountAsync();
        var id = await CreateConfigAsync(accountId, _dir);

        string container;
        using (var scope = _services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
            container = (await svc.GetAsync(id))!.ContainerName;
        }

        var busy = _services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(accountId, container, "BackingUp"));
        try
        {
            var res = await _client.PostAsJsonAsync(
                $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

            using var scope = _services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
            Assert.Equal(_dir, (await svc.GetAsync(id))!.LocalRoot);   // 未落库
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }

    [Fact]
    public async Task Unknown_Config_Is_A_404()
    {
        Directory.CreateDirectory(_dir);

        var res = await _client.PostAsJsonAsync(
            "/api/backup-configs/999999/local-root/preview", new { newRoot = _dir });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `cd backend && dotnet test --filter LocalRootEndpointTests`
Expected: FAIL —— 全部返回 404（端点还不存在）

- [ ] **Step 3: 实现两个端点**

在 `BackupConfigEndpoints.cs` 里 `reset-password` 端点的 `});` **之后**插入：

```csharp
        // 迁移本地根路径（设计 docs/change-local-root-design.md）。
        // preview 与 apply 分开：preview 是纯查询、幂等、可反复重试（换个路径再试一次不留痕迹），
        // apply 的确认语义在日志里独立可辨。同形先例是 restore-estimate 与 restore。
        group.MapPost("/{id:int}/local-root/preview", async (
            int id, LocalRootPreviewRequest req, IBackupConfigService svc, IAccountService accounts,
            ILocalIndexCache indexCache, TrackedInfoStore trackedInfo, ISecretReader secrets,
            IKeyringHealth keyring, PathBoundary boundary, BackupBusyTracker busy, CancellationToken ct) =>
        {
            var prepared = await PrepareLocalRootAsync(
                id, req.NewRoot, svc, accounts, indexCache, trackedInfo, secrets, keyring, boundary, busy, ct);
            return prepared.Failure ?? Results.Ok(prepared.Preview);
        });

        group.MapPost("/{id:int}/local-root", async (
            int id, LocalRootChangeRequest req, IBackupConfigService svc, IAccountService accounts,
            ILocalIndexCache indexCache, TrackedInfoStore trackedInfo, ISecretReader secrets,
            IKeyringHealth keyring, PathBoundary boundary, BackupBusyTracker busy, IOperationLog log,
            IGlobalSettingsService settingsSvc, CancellationToken ct) =>
        {
            // 不信任前端传来的 preview 结果，自己重跑一遍完整校验——这正是 Inspect
            // 必须是纯查询、可安全重入的原因。preview 之后新根被拔掉、或备份在两次调用之间
            // 开跑，都由这一遍兜住。
            var prepared = await PrepareLocalRootAsync(
                id, req.NewRoot, svc, accounts, indexCache, trackedInfo, secrets, keyring, boundary, busy, ct);
            if (prepared.Failure is { } failure)
                return failure;

            var preview = prepared.Preview!;
            var needsForce = preview.Verdict is nameof(LocalRootVerdict.NeedsConfirm)
                or nameof(LocalRootVerdict.Rejected);
            if (needsForce && !req.Force)
                return Results.Json(
                    new
                    {
                        error = "The new root does not match this backup's latest version index.",
                        code = "local_root_mismatch",
                        preview,
                    },
                    statusCode: StatusCodes.Status400BadRequest);

            var oldRoot = prepared.Config!.LocalRoot;
            var moved = await svc.ChangeLocalRootAsync(id, prepared.ResolvedRoot!, ct);
            if (moved is null)
                return Results.NotFound();

            await log.AppendAsync(
                OperationLogLevel.Warning, "backup",
                $"Local root of '{moved.Name}' changed from '{(string.IsNullOrEmpty(oldRoot) ? "(none)" : oldRoot)}' " +
                $"to '{moved.LocalRoot}' (verdict {preview.Verdict}, " +
                $"{preview.Matched}/{preview.Sampled} sampled entries matched" +
                $"{(req.Force && needsForce ? ", forced" : "")}).",
                ct);

            var settings = await settingsSvc.GetAsync(ct);
            return Results.Ok(BackupConfigResponse.From(moved, settings));
        });
```

在 `BackupConfigEndpoints` 类的**末尾**（`MapBackupConfigEndpoints` 方法之外）加共用的准备逻辑：

```csharp
    private readonly record struct PreparedLocalRoot(
        IResult? Failure, BackupConfig? Config, string? ResolvedRoot, LocalRootPreviewResponse? Preview);

    /// <summary>
    /// preview 与 apply 共用的前置：取配置 → 忙检查 → 路径校验 → 取基线索引 → Inspect。
    /// 顺序短路，任一步失败就带着对应的 IResult 回去。
    /// </summary>
    private static async Task<PreparedLocalRoot> PrepareLocalRootAsync(
        int id, string newRoot, IBackupConfigService svc, IAccountService accounts,
        ILocalIndexCache indexCache, TrackedInfoStore trackedInfo, ISecretReader secrets,
        IKeyringHealth keyring, PathBoundary boundary, BackupBusyTracker busy, CancellationToken ct)
    {
        if (KeyringGuard.Blocked(keyring) is { } blocked)
            return new PreparedLocalRoot(blocked, null, null, null);

        var config = await svc.GetAsync(id, ct);
        if (config is null)
            return new PreparedLocalRoot(Results.NotFound(), null, null, null);

        // 忙检查在最前面：正在备份/还原/检查时换根，是在给一个正在读的目录抽地毯。
        if (busy.IsBusy(config.AccountId, config.ContainerName))
            return new PreparedLocalRoot(
                Results.Json(
                    new { error = "This backup is busy; try again once the current operation finishes.", code = "backup_busy" },
                    statusCode: StatusCodes.Status409Conflict),
                null, null, null);

        if (string.IsNullOrWhiteSpace(newRoot))
            return new PreparedLocalRoot(
                Results.BadRequest(new { error = "A new local root is required." }), null, null, null);
        if (!Path.IsPathRooted(newRoot))
            return new PreparedLocalRoot(
                Results.BadRequest(new { error = "The new local root must be an absolute path." }), null, null, null);

        // 越界走全仓统一的 409 + path_outside_root，不为本功能另立一套。
        if (PathBoundaryGuard.Blocked(boundary, newRoot) is { } outside)
            return new PreparedLocalRoot(outside, null, null, null);

        if (!Directory.Exists(newRoot))
            return new PreparedLocalRoot(
                Results.BadRequest(new { error = $"'{newRoot}' does not exist or is not a directory." }),
                null, null, null);
        try
        {
            _ = Directory.EnumerateFileSystemEntries(newRoot).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new PreparedLocalRoot(
                Results.BadRequest(new { error = $"'{newRoot}' cannot be listed: {ex.Message}" }), null, null, null);
        }

        var baseline = await LoadBaselineAsync(config, accounts, indexCache, trackedInfo, secrets, ct);
        var preview = LocalRootMigration.Inspect(config.LocalRoot, newRoot, baseline);
        return new PreparedLocalRoot(null, config, newRoot, preview);
    }

    /// <summary>
    /// 取最新版本的索引作为比对基线。走本地权威缓存（与 /tree、/file-versions 同一套依赖），
    /// 取不到就返回 null —— 判定会因此落到 NoBaseline，而不是伪装成 0% 匹配。
    /// 任何异常都吞成"没有基线"：拿不到基线不该让用户连根都改不了。
    /// </summary>
    private static async Task<VersionIndex?> LoadBaselineAsync(
        BackupConfig config, IAccountService accounts, ILocalIndexCache indexCache,
        TrackedInfoStore trackedInfo, ISecretReader secrets, CancellationToken ct)
    {
        try
        {
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return null;

            var password = secrets.RevealBackupPassword(config);
            var info = await trackedInfo.LoadAsync(account, config.ContainerName, password, ct);
            var latest = info?.Versions.OrderByDescending(v => v.Version).FirstOrDefault();
            if (info is null || latest is null)
                return null;

            return await indexCache.ReadAsync(
                account, config.ContainerName, latest.Version,
                info.Backup.CreatedAt.UtcTicks, latest.IndexBlob, password, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run: `cd backend && dotnet test --filter LocalRootEndpointTests`
Expected: PASS，10 个测试全绿

- [ ] **Step 5: 跑全量后端测试，确认没碰坏别的**

Run: `cd backend && dotnet test`
Expected: 全绿（含既有 `AnonymousEndpointInventoryTests` —— 新端点在认证 group 下，不应出现在匿名清单里）

- [ ] **Step 6: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs \
        backend/tests/AzureStorageBackup.Api.Tests/LocalRootEndpointTests.cs
git commit -m "feat(local-root): add preview and apply endpoints for moving a root

Apply re-runs the whole inspection instead of trusting the report the
browser hands back, which covers the window where the new root gets
unmounted between the two calls or a backup starts in between. A missing
baseline resolves to NoBaseline rather than a zero-percent match, so a
config imported without a source root hint can finally be given one."
```

---

### Task 5: 前端 API 层与 verdict 决策纯函数

仓库现有前端测试只覆盖纯逻辑（`lib/scopeRules.test.ts`、`constants/format.test.ts`），没有组件渲染测试的基建，本功能不为此单独引入。所以把 verdict → UI 决策抽成纯函数来测，组件只负责画。

**Files:**
- Create: `frontend/src/lib/localRootVerdict.ts`
- Create: `frontend/src/lib/localRootVerdict.test.ts`
- Modify: `frontend/src/api/backupConfigs.ts`（类型区追加 + `backupConfigsApi` 对象追加两个方法）

**Interfaces:**
- Consumes: 后端 `LocalRootPreviewResponse`（Task 1）的 JSON 形状
- Produces:
  - `interface LocalRootPreview { verdict, sampled, matched, missing, sizeMismatch, mtimeDiffers, matchRate, reason, examples }`
  - `function localRootDecision(preview: LocalRootPreview | null): { canApply: boolean; needsForce: boolean; tone: 'ok' | 'warn' | 'danger' | 'info'; headline: string }`
  - `backupConfigsApi.previewLocalRoot(id, newRoot)`、`backupConfigsApi.changeLocalRoot(id, newRoot, force)`

- [ ] **Step 1: 写失败的测试**

创建 `frontend/src/lib/localRootVerdict.test.ts`：

```ts
import { describe, expect, it } from 'vitest'
import { localRootDecision, type LocalRootPreview } from './localRootVerdict'

const base: LocalRootPreview = {
  verdict: 'Ok',
  sampled: 200,
  matched: 200,
  missing: 0,
  sizeMismatch: 0,
  mtimeDiffers: 0,
  matchRate: 1,
  reason: null,
  examples: [],
}

describe('localRootDecision', () => {
  it('nothing checked yet — cannot apply', () => {
    const d = localRootDecision(null)
    expect(d.canApply).toBe(false)
    expect(d.needsForce).toBe(false)
  })

  it('Ok — applies straight away, no checkbox', () => {
    const d = localRootDecision(base)
    expect(d.canApply).toBe(true)
    expect(d.needsForce).toBe(false)
    expect(d.tone).toBe('ok')
  })

  it('NoBaseline — applies straight away, no checkbox', () => {
    const d = localRootDecision({ ...base, verdict: 'NoBaseline', sampled: 0, matched: 0, reason: 'no versions' })
    expect(d.canApply).toBe(true)
    expect(d.needsForce).toBe(false)
    expect(d.tone).toBe('info')
  })

  it('NeedsConfirm — needs the checkbox before applying', () => {
    const d = localRootDecision({ ...base, verdict: 'NeedsConfirm', matched: 137, matchRate: 0.685 })
    expect(d.canApply).toBe(false)
    expect(d.needsForce).toBe(true)
    expect(d.tone).toBe('warn')
    // 用户看不到命令行，数字必须直接摆在标题上。
    expect(d.headline).toContain('137')
    expect(d.headline).toContain('200')
  })

  it('Rejected — needs the checkbox, strongest tone', () => {
    const d = localRootDecision({ ...base, verdict: 'Rejected', matched: 0, matchRate: 0 })
    expect(d.canApply).toBe(false)
    expect(d.needsForce).toBe(true)
    expect(d.tone).toBe('danger')
  })

  it('BaselineUnreadable — needs the checkbox, and surfaces the underlying reason', () => {
    const d = localRootDecision({
      ...base,
      verdict: 'BaselineUnreadable',
      sampled: 0,
      matched: 0,
      matchRate: 0,
      reason: 'The latest version index could not be read: bad decrypt',
    })
    expect(d.canApply).toBe(false)
    expect(d.needsForce).toBe(true)
    // 「有历史但读不出来」绝不能被当成「没有历史」放行。
    expect(d.headline).toContain('bad decrypt')
  })

  it('an unknown verdict never silently allows the change', () => {
    const d = localRootDecision({ ...base, verdict: 'SomethingNew' })
    expect(d.canApply).toBe(false)
  })
})
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `cd frontend && npm test -- localRootVerdict`
Expected: FAIL —— `Failed to resolve import "./localRootVerdict"`

- [ ] **Step 3: 实现纯函数**

创建 `frontend/src/lib/localRootVerdict.ts`：

```ts
// 迁移本地根路径的判定结果 → 界面决策（设计 docs/change-local-root-design.md）。
// 抽成纯函数是为了能测：仓库没有组件渲染测试的基建，对话框只负责把这里的输出画出来。

export interface LocalRootPreview {
  verdict: string // 'Ok' | 'NeedsConfirm' | 'Rejected' | 'NoBaseline' | 'BaselineUnreadable'
  sampled: number
  matched: number
  missing: number
  sizeMismatch: number
  mtimeDiffers: number
  matchRate: number
  reason: string | null
  examples: string[]
}

export interface LocalRootDecision {
  /** 当前是否已经可以点 Apply（needsForce 为真时还要求用户勾过复选框）。 */
  canApply: boolean
  /** 是否必须先手动勾选 "change anyway" 才能 Apply。 */
  needsForce: boolean
  tone: 'ok' | 'warn' | 'danger' | 'info'
  headline: string
}

export function localRootDecision(preview: LocalRootPreview | null): LocalRootDecision {
  if (!preview) {
    return { canApply: false, needsForce: false, tone: 'info', headline: 'Check the new path first.' }
  }

  switch (preview.verdict) {
    case 'Ok':
      return {
        canApply: true,
        needsForce: false,
        tone: 'ok',
        headline: `${preview.matched} of ${preview.sampled} sampled entries match.`,
      }
    case 'NoBaseline':
      return {
        canApply: true,
        needsForce: false,
        tone: 'info',
        headline: preview.reason ?? 'No previous version to compare against — only the path itself was checked.',
      }
    case 'NeedsConfirm':
      return {
        canApply: false,
        needsForce: true,
        tone: 'warn',
        headline: `Only ${preview.matched} of ${preview.sampled} sampled entries match.`,
      }
    case 'Rejected':
      return {
        canApply: false,
        needsForce: true,
        tone: 'danger',
        headline: `${preview.matched} of ${preview.sampled} sampled entries match — this looks like the wrong directory.`,
      }
    case 'BaselineUnreadable':
      // 有历史却读不出来，和「压根没有历史」是两回事：后者放行，前者必须问一句。
      // reason 里带着底层异常原文——用户在 NAS 上没有命令行，那是他唯一的诊断线索。
      return {
        canApply: false,
        needsForce: true,
        tone: 'danger',
        headline: preview.reason ?? 'This backup has history, but its latest version index could not be read.',
      }
    default:
      // 后端加了新 verdict 而前端还没跟上时，宁可卡住也不放行。
      return { canApply: false, needsForce: false, tone: 'danger', headline: 'Unrecognised check result.' }
  }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `cd frontend && npm test -- localRootVerdict`
Expected: PASS，6 个测试全绿

- [ ] **Step 5: 接上 API 层**

在 `frontend/src/api/backupConfigs.ts` 里，`BackupConfigInput` 接口**之后**加：

```ts
// 迁移本地根路径的校验报告（后端 LocalRootPreviewResponse）。
// 形状定义在 lib/localRootVerdict.ts，与判定逻辑放一起，这里只做转出。
export type { LocalRootPreview } from '../lib/localRootVerdict'
```

在同文件 `backupConfigsApi` 对象里，`resetStatus` **之后**加：

```ts
  // 迁移本地根路径。preview 是纯查询，可反复试；changeLocalRoot 才真的改。
  previewLocalRoot: (id: number, newRoot: string) =>
    api.post<LocalRootPreview>(`/backup-configs/${id}/local-root/preview`, { newRoot }),
  changeLocalRoot: (id: number, newRoot: string, force: boolean) =>
    api.post<BackupConfig>(`/backup-configs/${id}/local-root`, { newRoot, force }),
```

并在该文件顶部的 import 区加：

```ts
import type { LocalRootPreview } from '../lib/localRootVerdict'
```

- [ ] **Step 6: 类型检查与 lint**

Run: `cd frontend && npx tsc -b && npm run lint`
Expected: 无错误

- [ ] **Step 7: 提交**

```bash
git add frontend/src/lib/localRootVerdict.ts frontend/src/lib/localRootVerdict.test.ts \
        frontend/src/api/backupConfigs.ts
git commit -m "feat(local-root): turn a check result into a UI decision, testably

The dialog has no rendering test to lean on, so the part worth pinning —
which verdicts may apply, which demand a deliberate checkbox — lives in a
pure function next to its own tests. An unrecognised verdict blocks
rather than allows, so a future backend verdict cannot quietly slip a
change through an older frontend."
```

---

### Task 6: 迁移对话框与页面接入

**Files:**
- Create: `frontend/src/components/ChangeLocalRootDialog.tsx`
- Modify: `frontend/src/pages/BackupConfigsPage.tsx:857-880`（`Local Root (locked)` 那个 Field）

**Interfaces:**
- Consumes: `localRootDecision` / `LocalRootPreview`（Task 5）、`backupConfigsApi.previewLocalRoot` / `.changeLocalRoot`（Task 5）、既有 `Modal`（`components/Modal.tsx`）、`PathBrowser`（`components/PathBrowser.tsx`，props：`initialPath?` / `onPick` / `onClose`）
- Produces: `<ChangeLocalRootDialog configId currentRoot onDone onClose />`

- [ ] **Step 1: 实现对话框**

创建 `frontend/src/components/ChangeLocalRootDialog.tsx`：

```tsx
import { useState } from 'react'
import { backupConfigsApi } from '../api/backupConfigs'
import { ApiError } from '../api/client'
import { localRootDecision, type LocalRootPreview } from '../lib/localRootVerdict'
import { Modal } from './Modal'
import { PathBrowser } from './PathBrowser'

/**
 * 迁移本地根路径。流程刻意分两步——先 Check 看报告，再 Apply——因为这个操作改错了，
 * 下次备份会把整个备份记成全删全增。
 */
export function ChangeLocalRootDialog({
  configId,
  currentRoot,
  onDone,
  onClose,
}: {
  configId: number
  currentRoot: string
  onDone: () => void
  onClose: () => void
}) {
  const [newRoot, setNewRoot] = useState('')
  const [preview, setPreview] = useState<LocalRootPreview | null>(null)
  const [browsing, setBrowsing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [acknowledged, setAcknowledged] = useState(false)

  const decision = localRootDecision(preview)
  const canApply = (decision.canApply || (decision.needsForce && acknowledged)) && !busy

  async function check() {
    setBusy(true)
    setError(null)
    setPreview(null)
    setAcknowledged(false)
    try {
      setPreview(await backupConfigsApi.previewLocalRoot(configId, newRoot.trim()))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  async function apply() {
    setBusy(true)
    setError(null)
    try {
      await backupConfigsApi.changeLocalRoot(configId, newRoot.trim(), decision.needsForce)
      onDone()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <Modal
        title="Change Local Root"
        onClose={onClose}
        footer={
          <>
            <button type="button" onClick={onClose}>
              Cancel
            </button>
            <button type="button" className="primary" disabled={!canApply} onClick={() => void apply()}>
              Apply
            </button>
          </>
        }
      >
        <div className="col" style={{ gap: 'var(--sp-3)' }}>
          <div>
            <div className="text-faint">Current</div>
            <div className="mono">{currentRoot || '(none)'}</div>
          </div>

          <div className="row" style={{ gap: 'var(--sp-1)' }}>
            <input
              className="w-lg mono"
              placeholder="/mnt/photos"
              value={newRoot}
              onChange={(e) => {
                setNewRoot(e.target.value)
                setPreview(null)
                setAcknowledged(false)
              }}
            />
            <button type="button" onClick={() => setBrowsing(true)}>
              Browse
            </button>
            <button type="button" disabled={!newRoot.trim() || busy} onClick={() => void check()}>
              Check
            </button>
          </div>

          {error && <div className="text-danger">{error}</div>}

          {preview && (
            <div className="col" style={{ gap: 'var(--sp-2)' }}>
              <div className={decision.tone === 'ok' ? 'text-ok' : `text-${decision.tone}`}>
                {decision.headline}
              </div>

              {preview.sampled > 0 && (
                <div className="text-faint">
                  {preview.missing} missing, {preview.sizeMismatch} with a different size
                  {preview.mtimeDiffers > 0 && (
                    <> ({preview.mtimeDiffers} also differ in modification time, which is not counted against the match)</>
                  )}
                </div>
              )}

              {preview.examples.length > 0 && (
                <div>
                  <div className="text-faint">Examples that did not match:</div>
                  <ul className="mono">
                    {preview.examples.map((p) => (
                      <li key={p}>{p}</li>
                    ))}
                  </ul>
                </div>
              )}

              {decision.needsForce && (
                <label className="row" style={{ gap: 'var(--sp-1)' }}>
                  <input
                    type="checkbox"
                    checked={acknowledged}
                    onChange={(e) => setAcknowledged(e.target.checked)}
                  />
                  <span>
                    I understand — change it anyway. The next backup will record every file that no longer
                    matches as deleted and upload the new ones. Scope rules are kept as they are and may no
                    longer match if this directory is laid out differently.
                  </span>
                </label>
              )}
            </div>
          )}
        </div>
      </Modal>

      {browsing && (
        <PathBrowser
          initialPath={newRoot || currentRoot || undefined}
          onPick={(p) => {
            setNewRoot(p)
            setPreview(null)
            setAcknowledged(false)
            setBrowsing(false)
          }}
          onClose={() => setBrowsing(false)}
        />
      )}
    </>
  )
}
```

- [ ] **Step 2: 确认样式类真的存在**

Run: `cd frontend && grep -n "text-ok\|text-warn\|text-danger\|text-faint\|\.col\b\|\.row\b" src/index.css | head -20`
Expected: 列出这些类。**任何一个缺失，就在 `index.css` 里补上对应规则**——补规则前先按项目既有教训逐个核对 specificity（见 `docs/web-ui-modernization-design.md` 的层叠约定），不要指望后写的规则自动获胜。

- [ ] **Step 3: 接入页面**

在 `frontend/src/pages/BackupConfigsPage.tsx` 顶部 import 区加：

```tsx
import { ChangeLocalRootDialog } from '../components/ChangeLocalRootDialog'
```

在该组件的 state 区（与 `browsing`、`pickingScope` 等并列处）加：

```tsx
const [changingRoot, setChangingRoot] = useState(false)
```

把 `Local Root (locked)` 那个 Field（约 857-880 行）整段替换为：

```tsx
              <Field label={editing ? 'Local Root (locked)' : 'Local Root'}>
                <input
                  className="w-lg mono"
                  placeholder="/data/photos"
                  value={form.localRoot}
                  disabled={!!editing}
                  onChange={(e) => set('localRoot', e.target.value)}
                />
                {editing ? (
                  // 常规编辑里根仍然锁着；换根走带校验的专用通道（挂载点搬家用）。
                  <button type="button" onClick={() => setChangingRoot(true)}>
                    Change…
                  </button>
                ) : (
                  <button type="button" onClick={() => setBrowsing(true)}>
                    Browse
                  </button>
                )}
              </Field>
```

在该组件 return 的末尾，与 `PathBrowser` 那段并列处加：

```tsx
          {changingRoot && editing && (
            <ChangeLocalRootDialog
              configId={editing.id}
              currentRoot={form.localRoot}
              onClose={() => setChangingRoot(false)}
              onDone={() => {
                setChangingRoot(false)
                void load()
              }}
            />
          )}
```

- [ ] **Step 4: 核对接入处的实际标识符**

`editing` 与 `load` 是本步假定的既有名字。执行前先确认：

Run: `cd frontend && grep -n "const \[editing\|function load\|const load" src/pages/BackupConfigsPage.tsx | head`
Expected: 列出这两个标识符。**名字不同就照实际的改**，别硬套；`editing` 若不是持有配置对象而只是个 id/布尔，`configId` 与 `currentRoot` 要相应地从别处取。

- [ ] **Step 5: 类型检查、lint、测试**

Run: `cd frontend && npx tsc -b && npm run lint && npm test`
Expected: 全部通过

- [ ] **Step 6: 提交**

```bash
git add frontend/src/components/ChangeLocalRootDialog.tsx frontend/src/pages/BackupConfigsPage.tsx
git commit -m "feat(local-root): add a check-then-apply dialog for moving a root

The check step lists the paths that did not line up, because the person
doing this is on a NAS with no shell and a bare percentage gives them
nothing to judge by. Forcing past a bad result takes a deliberate
checkbox that spells out what the next backup will do."
```

---

### Task 7: 修订那条已失效的锁定前提

`docs/backup-scope-selection-design.md` 的"现状"一节把 `LocalRoot` 的锁定当作范围规则不需要额外防护的依据。本功能推翻了它，留着不改会误导下一个读它的人。

**Files:**
- Modify: `docs/backup-scope-selection-design.md`（"现状"一节里的那条）

- [ ] **Step 1: 定位那一条**

Run: `grep -n "创建后锁定" docs/backup-scope-selection-design.md`
Expected: 命中"`BackupConfig.LocalRoot` 创建后锁定（`BackupConfigService.cs:46`），因此范围规则的相对路径基准永远稳定，不需要额外防护。"

- [ ] **Step 2: 改写**

把该条替换为：

```markdown
- `BackupConfig.LocalRoot` 在常规编辑路径上仍然锁定（`BackupConfigService.cs:46`），但另有一条
  带校验的专用迁移通道（`docs/change-local-root-design.md`，挂载点搬家用）。范围规则的相对
  基准因此**不是绝对稳定**的：换根后规则原文保留、不做改写，新根下是同一份数据时继续正确；
  用户强行迁到结构不同的目录树时，规则可能命中变空或部分失效，后果与手工改窄范围一致
  （见本文语义 4），不损坏数据。
```

- [ ] **Step 3: 提交**

```bash
git add docs/backup-scope-selection-design.md
git commit -m "docs(scope): the root is no longer immovable, so stop leaning on that

The scope design justified skipping extra protection by pointing at a
lock that now has a guarded door through it. Left as it was, the next
person to read it would draw a conclusion the code no longer supports."
```

---

### Task 8: 端到端验证与合并

**Files:** 无改动

- [ ] **Step 1: 全量后端测试**

Run: `cd backend && dotnet test`
Expected: 全绿。测试总数应比动工前多 26 条（Task 1 的 6 + Task 2 的 10 + Task 3 的 3 + Task 4 的 10 减去重叠计数差异；以实际为准，**不得有任何失败或跳过**）

- [ ] **Step 2: 全量前端测试与构建**

Run: `cd frontend && npm run lint && npm test && npm run build`
Expected: 全部通过

- [ ] **Step 3: 核对设计文档的每条判定都已落地**

逐条对照 `docs/change-local-root-design.md` 的"测试"一节，确认每一条都有对应的测试存在且通过。缺哪条补哪条。

- [ ] **Step 4: 合并到 main 并删分支**

```bash
git checkout main
git merge --no-ff feat/change-local-root
git branch -d feat/change-local-root
```

- [ ] **Step 5: 推送**

```bash
git push origin main
```

推送后 `docker-publish` 会自动触发。NAS 上的拉取与重启由用户自己做。
