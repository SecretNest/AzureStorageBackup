# 本地路径根边界与目录浏览 —— 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 网页上可浏览目录/文件树来选择本地路径；由 `Backup__Root` 指定一道边界，所有本地路径操作不得越过它。

**Architecture:** 一个 `PathBoundary` 服务承担全部判定：逐段展开符号链接得到真实路径，再按路径段边界与真实根比较。所有执行点（端点 + 调度器）调用它，越界返回 409。还原路径穿越单独修复，不依赖是否配置了根。

**Tech Stack:** .NET 10 / ASP.NET Core Minimal API、React + TypeScript（Vite）、xUnit + `Microsoft.AspNetCore.Mvc.Testing`。

设计依据：[local-path-root-design.md](local-path-root-design.md)。实施前请通读该文件第 1 节的 8 条决策与 §3.1（为什么必须逐段解析）。

## Global Constraints

- 界面文案一律英文（含 API 返回给用户的文案）；代码注释与文档用中文，与现有代码保持一致。
- 配置键 `Backup:Root`，环境变量形式 `Backup__Root`。镜像**不得**为它设默认值。
- 未设置或为空 = 无边界，行为与本轮之前完全一致。
- 根**只做安全过滤**：不改写、不截断路径，不作为相对路径基准。存储、显示、日志一律完整原始路径。
- 越界响应 **409** + 错误码 `path_outside_root`。
- 还原绝不能写到 `TargetRoot` 之外，**未设根时同样成立**。
- 不得产生 schema 变更。
- 后端全量测试命令：`dotnet test backend/AzureStorageBackup.slnx`，须全绿且 `dotnet build` 0 warnings。
- 前端：`cd frontend && npm run build` 与 `npm run lint` 均须干净。
- 提交信息用英文，`type: subject` 格式。

---

### Task 1: `PathBoundary` —— 真实路径解析与边界判定

这是本计划唯一的技术难点。**先读设计 §3.1**：`Directory.ResolveLinkTarget(path, returnFinalTarget: true)` 只展开**最后一段**，若 `/nas/link` 是指向 `/etc` 的软链，查询 `/nas/link/passwd` 时它返回 `null`（因为 `passwd` 自身不是链接）。仅用它做判定会漏掉「中间段是软链」的全部情形。

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/PathBoundary.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/PathBoundaryTests.cs`

**Interfaces:**
- Produces: `PathBoundary(IConfiguration)`；`bool Enabled { get; }`；`string? Root { get; }`；`bool IsInside(string path)`；`static string ResolveReal(string path)`；`static bool IsWithin(string root, string candidate)`

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/PathBoundaryTests.cs`：

```csharp
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.Configuration;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 符号链接相关用例一律在临时目录里构造**真实**软链——本功能的全部意义就是处理
/// 文件系统的真实行为，mock 掉就等于什么都没测。
/// </summary>
public class PathBoundaryTests : IDisposable
{
    private readonly string _base;

    public PathBoundaryTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Dir(string name)
    {
        var p = Path.Combine(_base, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static PathBoundary Boundary(string? root)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(root is null
                ? []
                : new Dictionary<string, string?> { ["Backup:Root"] = root })
            .Build();
        return new PathBoundary(config);
    }

    [Fact]
    public void Disabled_When_Root_Is_Absent()
    {
        var sut = Boundary(null);
        Assert.False(sut.Enabled);
        Assert.True(sut.IsInside("/anywhere/at/all"));
    }

    [Fact]
    public void Disabled_When_Root_Is_Empty()
    {
        var sut = Boundary("");
        Assert.False(sut.Enabled);
        Assert.True(sut.IsInside("/anywhere/at/all"));
    }

    [Fact]
    public void Accepts_The_Root_Itself_And_Its_Descendants()
    {
        var root = Dir("nas");
        var sut = Boundary(root);

        Assert.True(sut.IsInside(root));
        Assert.True(sut.IsInside(Path.Combine(root, "photos")));
        Assert.True(sut.IsInside(Path.Combine(root, "photos", "2024", "a.jpg")));
    }

    [Fact]
    public void Rejects_A_Sibling_Sharing_The_Root_Name_Prefix()
    {
        // /nasty 不得因为字符串前缀匹配 /nas 而通过
        var root = Dir("nas");
        Dir("nasty");
        var sut = Boundary(root);

        Assert.False(sut.IsInside(Path.Combine(_base, "nasty")));
        Assert.False(sut.IsInside(Path.Combine(_base, "nasty", "x")));
    }

    [Fact]
    public void Rejects_Dot_Dot_Escape()
    {
        var root = Dir("nas");
        Dir("outside");
        var sut = Boundary(root);

        Assert.False(sut.IsInside(Path.Combine(root, "..", "outside")));
    }

    [Fact]
    public void Accepts_Paths_Under_A_Root_That_Is_Itself_A_Symlink()
    {
        // 根自身是软链时，必须先把根解析成真实路径，否则一切合法路径都会被误拒
        var real = Dir("real-storage");
        var link = Path.Combine(_base, "nas-link");
        Directory.CreateSymbolicLink(link, real);
        var sut = Boundary(link);

        Assert.True(sut.IsInside(Path.Combine(link, "photos")));
        Assert.True(sut.IsInside(Path.Combine(real, "photos")));
    }

    [Fact]
    public void Rejects_When_The_Final_Segment_Is_A_Symlink_Pointing_Outside()
    {
        var root = Dir("nas");
        var outside = Dir("outside");
        var link = Path.Combine(root, "escape");
        Directory.CreateSymbolicLink(link, outside);
        var sut = Boundary(root);

        Assert.False(sut.IsInside(link));
    }

    [Fact]
    public void Rejects_When_A_MIDDLE_Segment_Is_A_Symlink_Pointing_Outside()
    {
        // ResolveLinkTarget 单独使用会漏掉这一条：a.jpg 自身不是链接，
        // 但它的父目录 escape 是，逐段展开才能发现越界。
        var root = Dir("nas");
        var outside = Dir("outside");
        Directory.CreateDirectory(Path.Combine(outside, "photos"));
        var link = Path.Combine(root, "escape");
        Directory.CreateSymbolicLink(link, outside);
        var sut = Boundary(root);

        Assert.False(sut.IsInside(Path.Combine(link, "photos", "a.jpg")));
    }

    [Fact]
    public void Accepts_A_Symlink_That_Stays_Inside_The_Root()
    {
        // 「用软链把散落各处的目录聚到一处」是本功能面向的正当用法
        var root = Dir("nas");
        var real = Path.Combine(root, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(root, "alias");
        Directory.CreateSymbolicLink(link, real);
        var sut = Boundary(root);

        Assert.True(sut.IsInside(Path.Combine(link, "a.jpg")));
    }

    [Fact]
    public void Rejects_Rather_Than_Hanging_On_A_Symlink_Cycle()
    {
        var root = Dir("nas");
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);
        var sut = Boundary(root);

        Assert.False(sut.IsInside(Path.Combine(a, "x")));
    }

    [Fact]
    public void Accepts_A_Path_That_Does_Not_Exist_Yet_Inside_The_Root()
    {
        // 还原目标常常是尚未创建的目录，不能因为「还不存在」就拒绝
        var root = Dir("nas");
        var sut = Boundary(root);

        Assert.True(sut.IsInside(Path.Combine(root, "not", "created", "yet")));
    }

    [Fact]
    public void Rejects_A_Nonexistent_Path_Behind_An_Escaping_Symlink()
    {
        var root = Dir("nas");
        var outside = Dir("outside");
        var link = Path.Combine(root, "escape");
        Directory.CreateSymbolicLink(link, outside);
        var sut = Boundary(root);

        Assert.False(sut.IsInside(Path.Combine(link, "not-created-yet")));
    }

    [Fact]
    public void IsWithin_Compares_On_Segment_Boundaries_Without_Resolving_Links()
    {
        // 还原写入用这个纯词法版本：它防的是索引数据里的 ..，不解析本地软链
        Assert.True(PathBoundary.IsWithin("/target", "/target"));
        Assert.True(PathBoundary.IsWithin("/target", "/target/a/b.txt"));
        Assert.False(PathBoundary.IsWithin("/target", "/targetx/b.txt"));
        Assert.False(PathBoundary.IsWithin("/target", "/target/../etc/passwd"));
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~PathBoundaryTests`
Expected: 编译失败，`The type or namespace name 'PathBoundary' could not be found`

- [ ] **Step 3: 实现**

创建 `backend/src/AzureStorageBackup.Api/Services/PathBoundary.cs`：

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 本地路径边界（设计 §3）。根来自 <c>Backup:Root</c>，**只做准入过滤**：
/// 不改写、不截断路径，也不作为相对路径基准。未配置时无边界，全部放行。
/// 单例：构造时解析一次真实根，之后不再变。
/// </summary>
public sealed class PathBoundary
{
    /// <summary>符号链接展开深度上限。超限判定为越界，而不是抛异常或死循环。</summary>
    private const int MaxLinkDepth = 40;

    private readonly string? _realRoot;

    public PathBoundary(IConfiguration config)
    {
        var configured = config["Backup:Root"];
        // 根自身可能是软链：必须先解析成真实路径，否则后续比较全部基于一个假地址，
        // 会把所有合法路径都误拒。
        _realRoot = string.IsNullOrWhiteSpace(configured) ? null : ResolveReal(configured);
    }

    /// <summary>是否启用边界。未配置根时为 false，一切放行。</summary>
    public bool Enabled => _realRoot is not null;

    /// <summary>解析后的真实根；未启用时为 null。用于错误消息。</summary>
    public string? Root => _realRoot;

    /// <summary>路径是否在边界之内。未启用边界时恒为 true。</summary>
    public bool IsInside(string path)
    {
        if (_realRoot is null)
            return true;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var real = ResolveReal(path);
        return real is not null && IsWithin(_realRoot, real);
    }

    /// <summary>
    /// 逐段展开符号链接，得到真实路径。链接成环（超过 <see cref="MaxLinkDepth"/>）时返回 null。
    /// <para>
    /// 不能用 <c>Directory.ResolveLinkTarget(p, returnFinalTarget: true)</c> 代替：它只展开
    /// **最后一段**。若 <c>/nas/link</c> 是指向 <c>/etc</c> 的软链，查询 <c>/nas/link/passwd</c>
    /// 时它返回 null（passwd 自身不是链接），中间段的越界就被漏掉了。
    /// </para>
    /// <para>
    /// 路径不存在的段不是链接，直接拼接即可——这自然实现了「按最近已存在祖先判定」，
    /// 使尚未创建的还原目标可以通过。
    /// </para>
    /// </summary>
    public static string? ResolveReal(string path)
    {
        var full = Path.GetFullPath(path);
        var sep = Path.DirectorySeparatorChar;
        var root = Path.GetPathRoot(full) ?? sep.ToString();
        var segments = full[root.Length..].Split(sep, StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        var depth = 0;

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            // 一段可能连环指向另一个链接，循环展开到不再是链接为止
            while (true)
            {
                if (++depth > MaxLinkDepth)
                    return null;

                // FileSystemInfo.LinkTarget 底层是 lstat，不关心目标是文件还是目录；
                // 路径不存在时返回 null，正是我们要的（不存在的段不是链接）。
                var target = new FileInfo(current).LinkTarget;
                if (target is null)
                    break;

                current = Path.IsPathRooted(target)
                    ? Path.GetFullPath(target)
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? root, target));
            }
        }

        return current;
    }

    /// <summary>
    /// 纯词法的包含判定：规范化后按**路径段边界**比较，不解析符号链接。
    /// <c>/target</c> 不包含 <c>/targetx</c>。还原写入用它防索引数据里的 <c>..</c>。
    /// </summary>
    public static bool IsWithin(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(full, fullRoot, StringComparison.Ordinal))
            return true;

        return full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~PathBoundaryTests`
Expected: PASS，13 passed

- [ ] **Step 5: 证明中间段那条用例真的在守护**

把 `ResolveReal` 临时改成只展开最后一段（用 `Directory.ResolveLinkTarget(full, true)` 的返回值，为 null 就用 `full`），重跑：

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~PathBoundaryTests`
Expected: FAIL —— `Rejects_When_A_MIDDLE_Segment_Is_A_Symlink_Pointing_Outside` 失败。恢复实现后重跑应回到 13 passed。把两次输出记进报告。

- [ ] **Step 6: 注册为单例并提交**

`backend/src/AzureStorageBackup.Api/Program.cs`，在 `AuthGate` 注册附近加：

```csharp
builder.Services.AddSingleton<PathBoundary>();
```

Run: `dotnet build backend/AzureStorageBackup.slnx`
Expected: 0 warnings

```bash
git add backend/src/AzureStorageBackup.Api/Services/PathBoundary.cs \
        backend/src/AzureStorageBackup.Api/Program.cs \
        backend/tests/AzureStorageBackup.Api.Tests/PathBoundaryTests.cs
git commit -m "feat: add PathBoundary with segment-wise symlink resolution"
```

---

### Task 2: 修还原路径穿越（独立于边界）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/RestoreOrchestrator.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/RestorePathTraversalTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `PathBoundary.IsWithin(string root, string candidate)`

**背景**：`RestoreOrchestrator.cs:304` 拼接来自**云端索引**的 `entry.Path`，而 `ToLocal`（`:396`）只替换分隔符、不校验 `..`。含 `../../etc/cron.d/x` 的条目会写到 `TargetRoot` 之外。`/import` 端点允许导入任意容器的备份，所以这条路径是实际可达的。

本任务**不依赖** `Backup__Root`：即使未设根，还原也绝不能越出 `TargetRoot`。

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/RestorePathTraversalTests.cs`：

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 还原目标外写入的防护（设计 §5）。此处直接测判定函数与拼接结果，
/// 完整还原链路的端到端覆盖在 BackupLifecycleTests 中。
/// </summary>
public class RestorePathTraversalTests
{
    [Theory]
    [InlineData("photos/a.jpg", true)]
    [InlineData("a.jpg", true)]
    [InlineData("nested/deep/b.txt", true)]
    [InlineData("../escape.txt", false)]
    [InlineData("../../etc/cron.d/x", false)]
    [InlineData("photos/../../escape.txt", false)]
    public void Only_Paths_Staying_Inside_The_Target_Are_Accepted(string entryPath, bool expected)
    {
        var target = Path.Combine(Path.GetTempPath(), "asb-restore-target");
        var dest = Path.Combine(target, entryPath.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected, PathBoundary.IsWithin(target, dest));
    }

    [Fact]
    public void A_Path_Escaping_Sideways_Into_A_Prefix_Sibling_Is_Rejected()
    {
        var target = Path.Combine(Path.GetTempPath(), "asb-target");
        var dest = Path.Combine(target + "x", "b.txt");

        Assert.False(PathBoundary.IsWithin(target, dest));
    }
}
```

- [ ] **Step 2: 运行测试，确认通过**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~RestorePathTraversalTests`
Expected: PASS，7 passed（`IsWithin` 来自 Task 1，本步只是钉住语义）

- [ ] **Step 3: 在还原写入处加防护**

`backend/src/AzureStorageBackup.Api/Services/RestoreOrchestrator.cs` —— 在 `WriteRestoredFile`（`:302`）开头加：

```csharp
    private static void WriteRestoredFile(RestoreRequest request, IndexEntry entry, string sourceFile)
    {
        var dest = Path.Combine(request.TargetRoot, ToLocal(entry.Path));

        // 索引来自云端（可能是 /import 导入的任意容器）：条目路径含 .. 时会写到目标根之外。
        // 跳过该条目而不是中断整次还原——与既有的逐组容错语义一致。
        if (!PathBoundary.IsWithin(request.TargetRoot, dest))
            throw new UnsafeRestorePathException(entry.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (request.Conflict == RestoreConflictMode.RenameKeep && File.Exists(dest))
            RestoreConflict.RenameExisting(dest, DateTimeOffset.UtcNow);
        File.Copy(sourceFile, dest, overwrite: true);
        ApplyMetadata(dest, entry);
    }
```

在同文件末尾（类外）加异常类型：

```csharp
/// <summary>还原条目的目标路径逃出了 TargetRoot（索引被篡改或来自不可信容器）。</summary>
public sealed class UnsafeRestorePathException(string entryPath)
    : Exception($"Restore entry path escapes the target root: {entryPath}");
```

`RestoreSymlink`（`:312`）同样处理——在计算出链接创建位置后、创建之前加同一道校验，越界则返回 `false`（该方法已用返回值表示是否还原成功）。

`EmptyDirs` 的创建（`:143`）同样：越界的目录条目跳过，不创建。

- [ ] **Step 4: 让越界条目计入 FailedFiles 而非中断**

计数是**组级聚合**的：`RestoreGroupAsync` 返回 `(Restored, Skipped, Failed)` 元组，外层在 `:180-183` 用 `counts.Sum(...)` 汇总进 `RestoreResult` 的 `FailedFiles`。所以越界跳过必须计进**该组自己**的 Failed，不能去改外层那个 `failed` 局部变量（它在另一个作用域，且组是并发执行的）。

在 `RestoreGroupAsync` 内 `foreach (var e in needed)`（`:268` 一带）用 try/catch 包住单个条目，并让该方法的失败计数加一：

```csharp
                foreach (var e in needed)
                {
                    var source = Path.Combine(extractDir, ToLocal(e.Path));
                    try
                    {
                        WriteRestoredFile(request, e, source);
                        restored++;
                    }
                    catch (UnsafeRestorePathException ex)
                    {
                        phase?.Report(ex.Message);
                        skippedUnsafe++;
                    }
                }
```

在该方法开头声明 `var skippedUnsafe = 0;`，并把它加进该方法返回元组的 `Failed` 分量。`phase` 是该方法既有的进度报告参数（见 `:176` 的 `phase?.Report(...)` 用法）。

- [ ] **Step 5: 全量测试**

Run: `dotnet test backend/AzureStorageBackup.slnx`
Expected: 全绿，0 warnings

- [ ] **Step 6: 提交**

```bash
git add -A backend/
git commit -m "fix: restore never writes outside the target root"
```

---

### Task 3: 边界接入所有执行点

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Endpoints/PathBoundaryGuard.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`（`:92`、`:171`、`:190`、`:361`、`:410`）
- Modify: `backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/PathBoundaryEnforcementTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `PathBoundary.Enabled` / `IsInside` / `Root`
- Produces: `PathBoundaryGuard.Blocked(PathBoundary, string path) -> IResult?`

**调度器不能漏**：`TaskDispatcher.cs:87`（备份）、`:100`（检查）、`:110`（清理）不经过端点。只在端点校验，计划任务就整个绕过了边界。

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/PathBoundaryEnforcementTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using Microsoft.AspNetCore.Hosting;

namespace AzureStorageBackup.Api.Tests;

public class PathBoundaryEnforcementTests
{
    private sealed class RootedFactory(string root) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Backup:Root", root);
        }
    }

    private static string TempRoot()
    {
        var p = Path.Combine(Path.GetTempPath(), "asb-enforce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static AccountRequest SampleAccount() => new(
        Name: "acct", Description: null,
        BlobEndpoint: "https://x.blob.core.windows.net",
        Region: AzureRegion.Global, AccountKey: "dGVzdA==",
        UseProxy: false, ProxyMode: ProxyMode.Independent,
        ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null);

    [Fact]
    public async Task Creating_A_Config_Outside_The_Root_Is_Rejected()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var res = await client.PostAsJsonAsync("/api/backup-configs", new
        {
            accountId = acct!.Id,
            containerName = "c",
            name = "outside",
            localRoot = "/definitely/outside/the/root",
        });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("path_outside_root", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Creating_A_Config_Inside_The_Root_Is_Accepted()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var res = await client.PostAsJsonAsync("/api/backup-configs", new
        {
            accountId = acct!.Id,
            containerName = "c",
            name = "inside",
            localRoot = Path.Combine(root, "photos"),
        });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Without_A_Root_Any_Local_Path_Is_Accepted()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var res = await client.PostAsJsonAsync("/api/backup-configs", new
        {
            accountId = acct!.Id,
            containerName = "c",
            name = "anywhere",
            localRoot = "/anywhere/at/all",
        });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~PathBoundaryEnforcementTests`
Expected: FAIL —— `Creating_A_Config_Outside_The_Root_Is_Rejected` 得到 201 而非 409

- [ ] **Step 3: 实现闸门**

创建 `backend/src/AzureStorageBackup.Api/Endpoints/PathBoundaryGuard.cs`：

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// 本地路径边界闸门（设计 §4）。越界返回 409 + <c>path_outside_root</c>。
/// 每次操作都校验，不只在设置时——配置可能来自旧版本、手工改库或 /import。
/// </summary>
public static class PathBoundaryGuard
{
    public static IResult? Blocked(PathBoundary boundary, string path) =>
        boundary.IsInside(path)
            ? null
            : Results.Json(
                new
                {
                    error = $"Path '{path}' is outside the configured root '{boundary.Root}'.",
                    code = "path_outside_root",
                },
                statusCode: StatusCodes.Status409Conflict);
}
```

- [ ] **Step 4: 接到五个端点**

`backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs` —— 以下五个 handler 各加参数 `PathBoundary boundary`，并在既有的 `KeyringGuard.Blocked(...)` 那一行**之后**加一行校验：

| 行 | 端点 | 校验的路径 |
|---|---|---|
| `:92` | `POST /` 创建配置 | `req.LocalRoot` |
| `:171` | `POST /{id}/run` | `config.LocalRoot`（取到 config 之后） |
| `:190` | `POST /{id}/restore` | `target`（即 `:198` 算出的那个值，之后） |
| `:361` | `POST /{id}/repair` | `config.LocalRoot`（取到 config 之后） |
| `:410` | `POST /{id}/check` | `config.LocalRoot`（取到 config 之后） |

形如：

```csharp
            if (PathBoundaryGuard.Blocked(boundary, config.LocalRoot) is { } outside) return outside;
```

创建端点（`:92`）在取 config 之前，直接校验 `req.LocalRoot`。还原端点（`:190`）必须放在 `:198` 那行**之后**，因为 `TargetRoot` 为空时会回落到 `config.LocalRoot`，两种来源都要校验。

- [ ] **Step 5: 接到调度器**

`backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs` —— 构造参数加入 `PathBoundary boundary`。在取到 `config` 之后、分派到备份/检查/清理之前加：

```csharp
        if (!boundary.IsInside(config.LocalRoot))
        {
            logger.LogError(
                "Scheduled task skipped: local root '{Root}' is outside the configured Backup__Root.",
                config.LocalRoot);
            return;
        }
```

用该文件既有的 logger 字段名。返回位置需在实际执行前——三处分派（`:87` 备份、`:100` 检查、`:110` 清理）共用同一个 `config`，因此放在它们之前一处即可。

- [ ] **Step 6: 运行测试并提交**

Run: `dotnet test backend/AzureStorageBackup.slnx`
Expected: 全绿，0 warnings

```bash
git add -A backend/
git commit -m "feat: enforce the local path boundary at every entry point"
```

---

### Task 4: 目录浏览 API

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/SystemEndpoints.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BrowseEndpointTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `PathBoundary`；Task 3 的 `PathBoundaryGuard.Blocked`
- Produces: `GET /api/system/browse?path=...` → `BrowseResponse(string Path, string? Parent, bool Truncated, IReadOnlyList<BrowseEntry> Entries)`；`BrowseEntry(string Name, string FullPath, bool IsDirectory, long? Length, DateTimeOffset ModifiedAt, bool OutsideRoot)`

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/BrowseEndpointTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace AzureStorageBackup.Api.Tests;

public class BrowseEndpointTests : IDisposable
{
    private readonly string _root;

    public BrowseEndpointTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-browse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "photos"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "hello");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed record BrowseEntryDto(
        string Name, string FullPath, bool IsDirectory, long? Length,
        DateTimeOffset ModifiedAt, bool OutsideRoot);

    private sealed record BrowseDto(
        string Path, string? Parent, bool Truncated, List<BrowseEntryDto> Entries);

    private sealed class RootedFactory(string root) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Backup:Root", root);
        }
    }

    [Fact]
    public async Task Lists_Directories_And_Files_With_Full_Paths()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}");

        Assert.NotNull(body);
        Assert.Contains(body!.Entries, e => e.Name == "photos" && e.IsDirectory);
        Assert.Contains(body.Entries, e => e.Name == "readme.txt" && !e.IsDirectory);
        // 完整路径，不因为设了根就截断
        Assert.Contains(body.Entries, e => e.FullPath == Path.Combine(_root, "photos"));
    }

    [Fact]
    public async Task Defaults_To_The_Configured_Root()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>("/api/system/browse");

        Assert.NotNull(body);
        Assert.Contains(body!.Entries, e => e.Name == "photos");
    }

    [Fact]
    public async Task Rejects_A_Path_Outside_The_Root()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var res = await client.GetAsync("/api/system/browse?path=%2Fdefinitely%2Foutside");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("path_outside_root", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Parent_Stops_At_The_Root()
    {
        using var factory = new RootedFactory(_root);
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}");

        Assert.Null(body!.Parent);
    }

    [Fact]
    public async Task Marks_A_Symlink_Escaping_The_Root_As_Outside()
    {
        var outside = Path.Combine(Path.GetTempPath(), "asb-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(_root, "escape"), outside);
        try
        {
            using var factory = new RootedFactory(_root);
            var client = factory.CreateClient();

            var body = await client.GetFromJsonAsync<BrowseDto>(
                $"/api/system/browse?path={Uri.EscapeDataString(_root)}");

            // 返回而不是过滤掉——否则用户会困惑「目录里明明有这个东西」
            var escape = Assert.Single(body!.Entries, e => e.Name == "escape");
            Assert.True(escape.OutsideRoot);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Without_A_Root_Nothing_Is_Marked_Outside()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<BrowseDto>(
            $"/api/system/browse?path={Uri.EscapeDataString(_root)}");

        Assert.All(body!.Entries, e => Assert.False(e.OutsideRoot));
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~BrowseEndpointTests`
Expected: FAIL，404 —— 端点尚不存在

- [ ] **Step 3: 实现端点**

`backend/src/AzureStorageBackup.Api/Endpoints/SystemEndpoints.cs` —— 在 `return app;`（`:81`）之前追加：

```csharp
        // 本地目录浏览（设计 §6）。懒加载，只返回直接子项。
        app.MapGet("/api/system/browse", (string? path, PathBoundary boundary) =>
        {
            var start = string.IsNullOrWhiteSpace(path)
                ? boundary.Root ?? Path.GetPathRoot(Path.GetFullPath("/")) ?? "/"
                : path;

            if (PathBoundaryGuard.Blocked(boundary, start) is { } outside)
                return outside;

            if (!Directory.Exists(start))
                return Results.NotFound(new { error = $"Directory '{start}' does not exist." });

            var entries = new List<BrowseEntry>();
            var truncated = false;

            foreach (var item in Directory.EnumerateFileSystemEntries(start))
            {
                if (entries.Count >= MaxBrowseEntries)
                {
                    truncated = true;
                    break;
                }

                try
                {
                    var info = new FileInfo(item);
                    var isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    entries.Add(new BrowseEntry(
                        Path.GetFileName(item),
                        item,
                        isDir,
                        isDir ? null : info.Length,
                        info.LastWriteTimeUtc,
                        // 软链可能指向根外：返回但标记，前端灰显不可点
                        !boundary.IsInside(item)));
                }
                catch (Exception)
                {
                    // 单项读取失败（权限不足等）跳过该项，不让整个请求失败
                }
            }

            // 目录在前，各自按名称排序
            entries.Sort((a, b) => a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // 上级到根为止
            var parent = Path.GetDirectoryName(Path.GetFullPath(start).TrimEnd(Path.DirectorySeparatorChar));
            if (parent is not null && !boundary.IsInside(parent))
                parent = null;

            return Results.Ok(new BrowseResponse(start, parent, truncated, entries));
        })
        .WithTags("System");
```

在文件顶部的 `SystemEndpoints` 类内加常量：

```csharp
    /// <summary>单次浏览返回的条目上限。超出即截断并在响应里标明，不静默少给。</summary>
    private const int MaxBrowseEntries = 2000;
```

在文件末尾（类外）加 DTO：

```csharp
/// <summary>浏览结果。Parent 为 null 表示已在根（或边界）处，不能再往上。</summary>
public record BrowseResponse(
    string Path, string? Parent, bool Truncated, IReadOnlyList<BrowseEntry> Entries);

/// <summary>OutsideRoot=true 表示该项（通常是指向根外的软链）不可选，但仍列出以免用户困惑。</summary>
public record BrowseEntry(
    string Name, string FullPath, bool IsDirectory,
    long? Length, DateTimeOffset ModifiedAt, bool OutsideRoot);
```

文件顶部补 `using AzureStorageBackup.Api.Services;`（若尚未引入）。

- [ ] **Step 4: 运行测试并提交**

Run: `dotnet test backend/AzureStorageBackup.slnx`
Expected: 全绿，0 warnings

```bash
git add -A backend/
git commit -m "feat: add a local directory browse endpoint"
```

---

### Task 5: 前端路径浏览器

**Files:**
- Create: `frontend/src/api/browse.ts`
- Create: `frontend/src/components/PathBrowser.tsx`
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`（local root 输入框附近，`:445` 一带）
- Modify: `frontend/src/components/RestoreDialog.tsx`（target 输入框附近，`:40` 一带）

**Interfaces:**
- Consumes: Task 4 的 `GET /api/system/browse`

- [ ] **Step 1: 新增 API 模块**

创建 `frontend/src/api/browse.ts`：

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
  entries: BrowseEntry[]
}

export const browseApi = {
  list: (path?: string) =>
    api.get<BrowseResult>(`/system/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`),
}
```

- [ ] **Step 2: 新增浏览器组件**

创建 `frontend/src/components/PathBrowser.tsx`：

```tsx
import { useEffect, useState } from 'react'
import { browseApi, type BrowseResult } from '../api/browse'
import { overlayStyle, panelStyle } from './modalStyles'

/**
 * 本地目录选择器（设计 §7）。只有目录可选；文件列出但不可选，
 * 以便确认选对了位置。越界项（通常是指向根外的软链）灰显不可点。
 */
export function PathBrowser({
  initialPath,
  onPick,
  onClose,
}: {
  initialPath?: string
  onPick: (path: string) => void
  onClose: () => void
}) {
  const [data, setData] = useState<BrowseResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [path, setPath] = useState<string | undefined>(initialPath)

  useEffect(() => {
    setError(null)
    browseApi
      .list(path)
      .then(setData)
      .catch((e) => setError(String(e)))
  }, [path])

  return (
    <div style={overlayStyle}>
      <div style={panelStyle}>
        <h3 style={{ marginTop: 0 }}>Choose a folder</h3>

        <p style={{ fontFamily: 'monospace', fontSize: '0.85rem', wordBreak: 'break-all' }}>
          {data?.path ?? path ?? ''}
        </p>

        {error && <p style={{ color: '#b91c1c' }}>{error}</p>}

        <div style={{ maxHeight: 320, overflowY: 'auto', border: '1px solid #ddd', padding: '0.5rem' }}>
          {data?.parent && (
            <div>
              <button type="button" onClick={() => setPath(data.parent!)}>
                .. (up)
              </button>
            </div>
          )}
          {data?.entries.map((e) => (
            <div key={e.fullPath} style={{ padding: '0.15rem 0' }}>
              {e.isDirectory ? (
                <button
                  type="button"
                  disabled={e.outsideRoot}
                  title={e.outsideRoot ? 'Outside the configured root' : undefined}
                  onClick={() => setPath(e.fullPath)}
                >
                  {e.name}/
                </button>
              ) : (
                <span style={{ color: '#888' }}>{e.name}</span>
              )}
            </div>
          ))}
          {data?.truncated && (
            <p style={{ color: '#b45309' }}>Too many entries — this listing was truncated.</p>
          )}
        </div>

        <div style={{ marginTop: '1rem', display: 'flex', gap: '0.5rem' }}>
          <button type="button" onClick={() => data && onPick(data.path)} disabled={!data}>
            Use this folder
          </button>
          <button type="button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  )
}
```

`components/modalStyles.ts` 导出的正是 `overlayStyle` 与 `panelStyle`（`RestoreDialog` 已在用），直接引入即可。

- [ ] **Step 3: 接到备份配置页**

`frontend/src/pages/BackupConfigsPage.tsx` —— 引入组件与状态：

```tsx
import { PathBrowser } from '../components/PathBrowser'
```

在组件内加状态：

```tsx
  const [browsing, setBrowsing] = useState(false)
```

在 local root 输入框（`:445` 一带）之后加按钮：

```tsx
                <button type="button" onClick={() => setBrowsing(true)} disabled={!!editing}>
                  Browse
                </button>
```

在该表单的 JSX 末尾加弹窗：

```tsx
      {browsing && (
        <PathBrowser
          initialPath={form.localRoot || undefined}
          onPick={(p) => {
            set('localRoot', p)
            setBrowsing(false)
          }}
          onClose={() => setBrowsing(false)}
        />
      )}
```

`disabled={!!editing}` 是因为 local root 属于创建后不可改的基础字段（`BackupConfigService` 会拒绝修改）。

- [ ] **Step 4: 接到还原对话框**

`frontend/src/components/RestoreDialog.tsx` —— 同样引入 `PathBrowser`，加 `const [browsing, setBrowsing] = useState(false)`，在 target 输入框旁加 `Browse` 按钮，选中后 `setTarget(p)`。还原目标可改，故此处不加 `disabled`。

- [ ] **Step 5: 构建与 lint**

```bash
cd frontend && npm run build && npm run lint
```

Expected: 构建成功、无 TypeScript 报错；oxlint 干净

- [ ] **Step 6: 提交**

```bash
git add -A frontend/
git commit -m "feat: add a folder picker for local paths"
```

---

### Task 6: 文档

**Files:**
- Modify: `README.md`（环境变量表与其下方的注记）

- [ ] **Step 1: 增加环境变量条目**

在环境变量表中 `Backup__TempPath` 一行之后插入：

```markdown
| `Backup__Root` | Confines every local path — backup source, restore target, and the folder picker — to this directory. Unset = no limit. | *(unset)* |
```

- [ ] **Step 2: 增加说明段**

在表下方已有的 `>` 注记之后追加：

```markdown
> `Backup__Root` is a **safety filter only**: it never rewrites or shortens a path, and it is not a base for relative paths. Paths are stored and displayed in full — with a root of `/nas`, a backup source still reads `/nas/photos/2024`. It constrains paths **inside the container**, so use it together with your volume mounts: mount everything you want to back up beneath that one directory.
>
> Symbolic links are resolved before the check, so a link inside the root that points outside it is rejected — including when the link is a middle segment of the path. Backup configurations whose local root falls outside the root are kept but refuse to run, so setting this on an existing install tells you which ones need attention instead of silently dropping them.
```

- [ ] **Step 3: 提交**

```bash
git add README.md
git commit -m "docs: document Backup__Root"
```

---

## 完成后的验证

- [ ] `dotnet test backend/AzureStorageBackup.slnx` 全绿、`dotnet build` 0 warnings
- [ ] `cd frontend && npm run build && npm run lint` 干净
- [ ] 手工验证：不设 `Backup__Root` 启动 → 路径框可填任意路径、Browse 从 `/` 开始；设 `-e Backup__Root=/nas` 启动 → Browse 从 `/nas` 开始且上不去，填 `/etc` 得 409，`/nas` 内的软链若指向外部则灰显不可点
