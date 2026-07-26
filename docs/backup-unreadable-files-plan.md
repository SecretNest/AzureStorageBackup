# 备份遇到读不开的文件 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让单个读不开的文件只产生一条告警并被跳过，而不是终止整轮备份。

**Architecture:** 在 `BackupDiffer` 引入 `ChangeKind.Unreadable`，把读失败在 diff 阶段就收敛成一种变更分类；索引构建时对该分类沿用上一版本条目并打 `UnreadableAt` 标记；分组与压缩路径把读失败并入既有的「成员需排除」路径。

**Tech Stack:** .NET 10 / xUnit。

设计文档：[backup-unreadable-files-design.md](backup-unreadable-files-design.md)

## 计划期发现的设计缺口

设计 §3 只列了 `IFileHasher.FullHashAsync` 的四个调用点。写计划时核对代码发现**压缩与上传同样要读文件**：7z 打包成员、以及原样存储的单文件上传，都在 diff 之后再次打开源文件。一个在 diff 时可读、在压缩时已被锁住的文件仍会终止备份。Task 5 覆盖这一路径。这是设计未覆盖的情形，不是实现自由发挥。

## Global Constraints

- **捕获 `IOException` 与 `UnauthorizedAccessException`，不得捕获 `OperationCanceledException`。** 后者不派生自前两者，所以精确捕获即可；但**不得**写成 `catch (Exception)`，那会把取消和真正的缺陷一并吞掉。
- **不可读绝不能被当作删除。** 否则保留策略滚过几轮后，一个仅是长期被占用的文件会从所有版本里静默消失。
- 索引格式**可自由修改，不考虑兼容**（产品测试阶段，用户确认）。
- 分组的不变量：**绝不上传一个内含已知过期或不可读成员的包**。现有的「排除后重压」已满足，沿用它，不要改写。
- 用户可见文案英文；代码注释中文。
- 无新增 NuGet 包。
- 后端测试：`cd backend && dotnet test`。基线 **577 passed, 0 failed, 0 skipped**。
- 每个任务结束提交一次。

---

### Task 1: 在 diff 阶段把读失败收敛为一种变更分类

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupDiffer.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupDifferUnreadableTests.cs`

**Interfaces:**
- Consumes: 现有 `IFileHasher.HeadHashAsync(string, int, CancellationToken)` 与 `FullHashAsync(string, CancellationToken)`
- Produces:
  - `ChangeKind.Unreadable` 枚举值
  - 对读失败的文件产出 `FileChange`，其 `Kind = ChangeKind.Unreadable`，`Current` 为扫描到的条目，`Previous` 为上一版本条目（新文件时为 `null`），三个 hash 字段与 `CarriedStorage` 均为 `null`

- [ ] **Step 1: 写失败测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/BackupDifferUnreadableTests.cs`。先读 `BackupDifferTests.cs` 了解该文件如何构造 differ、扫描条目与假 hasher，并复用同样的构造方式；下面的测试按那套写法落地：

```csharp
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 一个文件读不开，不该让其余几万个文件的备份一起作废。
/// diff 阶段就把读失败收敛成 Unreadable，后续阶段不必各自 try/catch。
/// </summary>
public class BackupDifferUnreadableTests
{
    /// <summary>指定路径抛 IOException，其余照常算 hash。</summary>
    private sealed class ThrowingHasher(string lockedPath, Exception toThrow) : IFileHasher
    {
        public Task<string> HeadHashAsync(string path, int bytes, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("head-" + Path.GetFileName(path));

        public Task<string> FullHashAsync(string path, CancellationToken ct = default) =>
            path.EndsWith(lockedPath, StringComparison.Ordinal)
                ? throw toThrow
                : Task.FromResult("full-" + Path.GetFileName(path));
    }

    [Fact]
    public async Task An_Unreadable_New_File_Is_Classified_Unreadable_And_Others_Still_Diff()
    {
        // 期望：locked.mdf 分类为 Unreadable，其余文件照常得到 Added；整个 diff 不抛。
    }

    [Fact]
    public async Task An_Unreadable_Modified_File_Keeps_Its_Previous_Entry_Reference()
    {
        // 期望：Kind == Unreadable 且 Previous 指向上一版本条目（供索引沿用）。
    }

    [Fact]
    public async Task UnauthorizedAccess_Is_Treated_The_Same_As_IOException()
    {
        // 期望：与上一条同样分类为 Unreadable。
    }

    [Fact]
    public async Task Cancellation_Still_Aborts_The_Diff()
    {
        // 期望：hasher 抛 OperationCanceledException 时 diff 照常上抛，不被当成 Unreadable。
        // 这条是护栏：捕获写宽成 catch(Exception) 会让取消变成「跳过一个文件」。
    }
}
```

每个测试体按 `BackupDifferTests.cs` 既有的构造方式补全：构造扫描条目集合、上一版本索引、调用 differ、断言 `Changes` 中该路径的 `Kind` 与 `Previous`。**不要留空方法体**——空测试恒绿，比没有测试更糟。

- [ ] **Step 2: 跑测试确认失败**

Run: `cd backend && dotnet test --filter FullyQualifiedName~BackupDifferUnreadableTests`
Expected: 编译失败，`'ChangeKind' does not contain a definition for 'Unreadable'`

- [ ] **Step 3: 加枚举值**

在 `BackupDiffer.cs` 的 `ChangeKind` 中，`MetadataOnly` 之后加入：

```csharp
    /// <summary>本轮读不开（被占用/无权限/读错误）。既不是变更也不是删除：
    /// 索引沿用上一版本条目并打 UnreadableAt，绝不能被当成删除。</summary>
    Unreadable,
```

- [ ] **Step 4: 在三个产出点包住 hash 调用**

在 `BackupDiffer` 中加入一个私有助手，并用它替换 `AddedAsync`、`ModifiedAsync`、以及 `CompareAsync` 中两处直接调用 hasher 的地方：

```csharp
    /// <summary>
    /// 读失败（被占用/无权限/读到一半设备错误）不该终止整轮备份。
    /// 精确捕获这两类，**不要**写成 catch(Exception)：OperationCanceledException 不派生自它们，
    /// 写宽了会把取消也变成「跳过一个文件」，备份看起来成功、实际没跑完。
    /// </summary>
    private static async Task<FileChange?> TryReadAsync(
        Func<Task<FileChange>> build, ScannedEntry entry, IndexEntry? prev)
    {
        try
        {
            return await build();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileChange(entry.Path, ChangeKind.Unreadable, entry, prev, null, null, null);
        }
    }
```

调用方把原有的构造逻辑传进 `build`。返回类型用 `FileChange?` 是为了让签名显式表达「可能没读成」；若助手总能返回值，可去掉 `?`——以实现时的实际形状为准，但**不要**把异常吞掉后返回 `null` 让调用方去猜。

读失败的原因文本需要保留到 Task 3 使用。在 `FileChange` 上增加一个可空 `string? UnreadableReason`，由助手填入 `ex.Message`；其余分类为 `null`。

- [ ] **Step 5: 跑测试确认通过**

Run: `cd backend && dotnet test --filter FullyQualifiedName~BackupDifferUnreadableTests`
Expected: PASS，4 条全绿

- [ ] **Step 6: 跑全量**

Run: `cd backend && dotnet test`
Expected: 全绿。`BackupDifferTests` 尤其不得回归。

- [ ] **Step 7: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupDiffer.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupDifferUnreadableTests.cs
git commit -m "feat: classify an unreadable file instead of aborting the diff"
```

---

### Task 2: 索引沿用旧条目并标记

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Models/BackupIndex.cs`（`IndexEntry`）
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`（`BuildEntries`，约 698-723 行）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/UnreadableIndexEntryTests.cs`

**Interfaces:**
- Consumes: `ChangeKind.Unreadable` 与其 `FileChange.Previous`（Task 1）
- Produces: `IndexEntry.UnreadableAt`（`DateTimeOffset?`）

- [ ] **Step 1: 写失败测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/UnreadableIndexEntryTests.cs`。先读 `BackupOrchestratorTests.cs` 了解如何构造一次备份运行与上一版本索引，复用其构造方式：

```csharp
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class UnreadableIndexEntryTests
{
    [Fact]
    public async Task A_Previously_Backed_Up_File_Carries_Its_Old_Entry_Forward()
    {
        // 期望：新版本条目的 Storage/FullHash/Length 与上一版本一致（指向同一份已上传内容），
        // 且 UnreadableAt 非 null。
    }

    /// <summary>决策 5 的护栏。不可读被当成删除的话，保留策略滚过几轮就会
    /// 把一个仅是长期被占用的文件从所有版本里抹掉——每轮告警看起来都只是「跳过一个文件」。</summary>
    [Fact]
    public async Task An_Unreadable_File_Is_Never_Recorded_As_Deleted()
    {
        // 期望：该路径出现在新版本索引中，且不在任何「已删除」判定里。
    }

    [Fact]
    public async Task A_Brand_New_Unreadable_File_Is_Absent_From_The_Version()
    {
        // 期望：上一版本没有该文件时，新版本索引不含它——没有内容可指向，编造条目是撒谎。
    }
}
```

同样，**测试体必须写实**，不得留空。

- [ ] **Step 2: 跑测试确认失败**

Run: `cd backend && dotnet test --filter FullyQualifiedName~UnreadableIndexEntryTests`
Expected: 编译失败，`'IndexEntry' does not contain a definition for 'UnreadableAt'`

- [ ] **Step 3: 加索引字段**

在 `Models/BackupIndex.cs` 的 `IndexEntry` 中，`Storage` 之前加入：

```csharp
    /// <summary>本轮未能重读该文件（被占用/无权限/读错误），条目内容沿用上一版本。
    /// null = 本版本正常读取。值为发生时刻，便于操作员判断这份旧内容有多旧。</summary>
    public DateTimeOffset? UnreadableAt { get; init; }
```

- [ ] **Step 4: 在 BuildEntries 中分支处理**

`BackupOrchestrator.BuildEntries` 目前对每个非 `Deleted` 且 `Current` 非空的变更走同一条构造路径。`Unreadable` 不能走它——那条路径会用 `c.HeadHash`/`c.FullHash`/`storageByPath`，对不可读文件全是 `null`，产出一个内容指向为空的坏条目。

在 `if (c.Kind == ChangeKind.Deleted || c.Current is null) continue;` 之后插入：

```csharp
            // 读不开：沿用上一版本条目（含 Storage，因此不重传任何内容、不影响去重），
            // 仅追加 UnreadableAt。上一版本没有该文件时整条跳过——没有内容可指向。
            if (c.Kind == ChangeKind.Unreadable)
            {
                if (c.Previous is not null)
                    entries.Add(c.Previous with { UnreadableAt = DateTimeOffset.UtcNow });
                continue;
            }
```

`IndexEntry` 是 record，`with` 表达式整体沿用旧值，因此 `Length`/`Mtime`/`Permissions`/三段 hash/`Storage` 全部照抄，无需逐字段列举——逐字段列举正是日后加了新字段却忘记同步的地方。

- [ ] **Step 5: 确认计划阶段本就不会处理它**

Run: `cd backend && grep -n "ChangeKind.Added or ChangeKind.Modified" backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`
Expected: 命中约 204 行的 `.Where(...)`。`Unreadable` 不在该集合中，因此不会进入上传计划——**无需改动**，确认即可。若此处已被改动成别的形状，停下来报告。

- [ ] **Step 6: 跑测试确认通过，再跑全量**

Run: `cd backend && dotnet test --filter FullyQualifiedName~UnreadableIndexEntryTests`
Expected: PASS

Run: `cd backend && dotnet test`
Expected: 全绿

- [ ] **Step 7: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Models/BackupIndex.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/tests/AzureStorageBackup.Api.Tests/UnreadableIndexEntryTests.cs
git commit -m "feat: carry an unreadable file's previous entry forward instead of dropping it"
```

---

### Task 3: 告警与计数

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/UnreadableWarningTests.cs`

**Interfaces:**
- Consumes: `ChangeKind.Unreadable` 与 `FileChange.UnreadableReason`（Task 1）；编排层既有的 `Record(...)` 告警助手
- Produces: `BackupRunResult` 增加 `int UnreadableFiles`

- [ ] **Step 1: 写失败测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/UnreadableWarningTests.cs`：

```csharp
public class UnreadableWarningTests
{
    [Fact]
    public async Task Each_Unreadable_File_Produces_One_Warning_Carrying_The_System_Reason()
    {
        // 期望：操作日志中有一条 Warning，源为 backup:{accountId}/{container}，
        // 消息含完整路径，且含系统给出的原因原文（如 "being used by another process"）。
        // 原因必须原样保留：「被哪个进程占用」「权限不足」「设备读错误」需要不同的处理方式，
        // 压成一句「无法读取」等于让操作员无从下手。
    }

    [Fact]
    public async Task The_Run_Result_Counts_Unreadable_Files()
    {
        // 期望：BackupRunResult.UnreadableFiles 等于不可读文件数，且备份本身成功完成。
    }

    /// <summary>决策 8：长期被占用的文件每轮都告警。这是有意的——它确实没被备起来。
    /// 若第二轮静默，操作员会以为问题自己好了。</summary>
    [Fact]
    public async Task Two_Consecutive_Runs_Each_Warn_About_The_Same_File()
    {
        // 期望：连续跑两轮，同一路径产生两条 Warning，且第二轮的备份同样成功完成。
    }
}
```

测试体按 `BackupOrchestratorTests.cs` 的构造方式写实。

- [ ] **Step 2: 跑测试确认失败**

Run: `cd backend && dotnet test --filter FullyQualifiedName~UnreadableWarningTests`
Expected: FAIL 或编译失败（`UnreadableFiles` 不存在）

- [ ] **Step 3: 实现**

先读 `BackupOrchestrator` 中既有的 `Record(...)` 调用（例如「File kept changing during backup」那一处），照同样形状为每个 `ChangeKind.Unreadable` 的变更记一条 **Warning** 级操作日志，源 `backup:{request.Account.Id}/{request.Container}`，消息包含 `c.Path` 与 `c.UnreadableReason`。

在 `BackupRunResult` 上增加 `int UnreadableFiles`，取值为 `diff.Changes.Count(c => c.Kind == ChangeKind.Unreadable)`。

告警的记录位置应在索引构建之前或之后均可，但必须在**每轮都执行**的路径上——不可放在只有变更文件才走到的分支里，否则一个全程不可读的备份会一条告警都不产生。

- [ ] **Step 4: 跑测试确认通过，再跑全量**

Run: `cd backend && dotnet test --filter FullyQualifiedName~UnreadableWarningTests`
Expected: PASS

Run: `cd backend && dotnet test`
Expected: 全绿

- [ ] **Step 5: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/tests/AzureStorageBackup.Api.Tests/UnreadableWarningTests.cs
git commit -m "feat: warn once per unreadable file and count them in the run result"
```

---

### Task 4: 分组成员在重校验时读不开

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`（`ProcessDirectoryAsync` 中压缩后的成员重校验，约 546-556 行）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/UnreadablePackMemberTests.cs`

**Interfaces:**
- Consumes: 无新接口
- Produces: 无新接口

- [ ] **Step 1: 写失败测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/UnreadablePackMemberTests.cs`：

```csharp
public class UnreadablePackMemberTests
{
    /// <summary>不变量：绝不上传一个内含已知不可读成员的包。</summary>
    [Fact]
    public async Task A_Member_That_Becomes_Unreadable_Is_Excluded_And_The_Pack_Is_Recompressed()
    {
        // 期望：上传的 pack 不含该成员；其余成员照常成包并可还原。
    }
}
```

按 `BackupOrchestratorTests.cs` 中已有的分组用例构造方式写实。

- [ ] **Step 2: 跑测试确认失败**

Run: `cd backend && dotnet test --filter FullyQualifiedName~UnreadablePackMemberTests`
Expected: FAIL——重校验里的 `hasher.FullHashAsync` 抛出，整轮备份终止

- [ ] **Step 3: 实现**

压缩后的成员重校验目前是：

```csharp
                if (Stat(local) != before[m.Path] && await hasher.FullHashAsync(local, ct) != m.FullHash)
                    changed.Add(m);
```

`FullHashAsync` 在此处可能抛出。改为读不开即视为「需排除」，并入既有路径：

```csharp
                bool exclude;
                try
                {
                    // 读不开与内容变了，对这个包而言后果相同：都不能把它留在归档里上传。
                    exclude = Stat(local) != before[m.Path] && await hasher.FullHashAsync(local, ct) != m.FullHash;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    exclude = true;
                }
                if (exclude)
                    changed.Add(m);
```

被排除的成员随后走 `overrides` 那条「以新 hash 单独处理」的路径，而该路径本身也会读文件——若那里同样读不开，由 Task 5 覆盖。

- [ ] **Step 3b: ProcessingVerifier 内部的重算 hash**

设计 §3 列出的四个调用点中，还有一处在 `Services/ProcessingVerifier.cs`：元数据变化后它会 `await hasher.FullHashAsync(path, ct)` 重算内容 hash。文件在处理期间被锁住时，这一处同样会抛，且它在 `BackupRunner` 的执行体里，抛出即终止整轮。

处置：读不开视同「内容不再可确认」，直接以 `ProcessingOutcome.Alarmed` 收场——不要继续重试。重试的前提是「文件可能稳定下来」，而读不开时连是否稳定都无法判断，反复重处理只是空耗。

在 `ProcessingVerifier.RunAsync` 的重算处包上：

```csharp
            string current;
            try
            {
                current = await hasher.FullHashAsync(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不开就无法确认内容是否稳定，继续重试没有意义：直接报警收场。
                return new ProcessingResult(ProcessingOutcome.Alarmed, attempts, expected);
            }
```

为它加一条测试到 `backend/tests/AzureStorageBackup.Api.Tests/ProcessingVerifierTests.cs`（该文件已存在，沿用其构造方式）：读取抛 `IOException` 时返回 `Alarmed` 而非上抛。

- [ ] **Step 4: 跑测试确认通过，再跑全量**

Run: `cd backend && dotnet test --filter FullyQualifiedName~UnreadablePackMemberTests`
Expected: PASS

Run: `cd backend && dotnet test`
Expected: 全绿

- [ ] **Step 5: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/tests/AzureStorageBackup.Api.Tests/UnreadablePackMemberTests.cs
git commit -m "fix: exclude a pack member that became unreadable rather than aborting"
```

---

### Task 5: 压缩与上传阶段读不开（设计未覆盖的路径）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/UnreadableDuringUploadTests.cs`

**Interfaces:**
- Consumes: `ChangeKind.Unreadable`、`IndexEntry.UnreadableAt`（Task 1、2）
- Produces: 无新接口

**背景**：diff 通过之后，源文件还会被再次打开——7z 压缩打包成员，以及原样存储的单文件上传。一个在 diff 时可读、随后被锁住的文件仍会终止整轮备份。设计 §3 只数了 hash 的四个调用点，漏了这一段。

- [ ] **Step 1: 先摸清失败形状**

Run: `cd backend && grep -n "CompressPackAsync\|CompressAsync\|OpenRead" backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs backend/src/AzureStorageBackup.Api/Services/SevenZipArchiveCodec.cs | head -20`

读这两处，判定：7z 读不到某个成员时是**整个归档失败**，还是**跳过该成员并以非零退出码报告**。两者处置不同：

- 整归档失败 → 必须先排除该成员再重压，与 Task 4 同路径。
- 部分失败 → 必须能识别是哪个成员失败，否则无法排除。

把结论写进报告。**若无法从代码判定，就写一个最小用例实测**，不要靠猜。

- [ ] **Step 2: 写失败测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/UnreadableDuringUploadTests.cs`，按 Step 1 的结论构造：

```csharp
public class UnreadableDuringUploadTests
{
    [Fact]
    public async Task A_File_Locked_After_The_Diff_Does_Not_Abort_The_Run()
    {
        // 期望：备份完成；该文件不在本版本（或沿用旧条目，视其上一版本是否存在）；
        // 有一条 Warning；其余文件正常上传。
    }
}
```

在 Linux 上模拟「读不开」最可靠的方式是权限：`File.SetUnixFileMode(path, UnixFileMode.None)` 后由非 root 进程读取会抛 `UnauthorizedAccessException`。**注意测试若以 root 运行则权限不生效**——容器里常是 root。若如此，改用一个在读取时抛出的 `IFileHasher`/codec 替身，并在报告中说明为何不用真实权限。

- [ ] **Step 3: 实现**

按 Step 1 的结论，在压缩/上传路径捕获 `IOException` 与 `UnauthorizedAccessException`，把该文件降级为与 `ChangeKind.Unreadable` 相同的处置：不产生 blob、索引沿用旧条目（有则沿用、无则缺席）、记一条 Warning、计入 `UnreadableFiles`。

复用 Task 2 与 Task 3 已建立的机制，不要另起一套并行逻辑。

- [ ] **Step 4: 跑测试确认通过，再跑全量**

Run: `cd backend && dotnet test --filter FullyQualifiedName~UnreadableDuringUploadTests`
Expected: PASS

Run: `cd backend && dotnet test`
Expected: 全绿

- [ ] **Step 5: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/tests/AzureStorageBackup.Api.Tests/UnreadableDuringUploadTests.cs
git commit -m "fix: survive a file that becomes unreadable after the diff"
```

---

### Task 6: 全量验证

- [ ] **Step 1: 后端全量**

Run: `cd backend && dotnet test`
Expected: 全绿。记录数量与 577 基线对比。

`BackupOrchestratorTests.Blobs_Are_Uploaded_Concurrently_Up_To_The_Limit` 断言的是实时并发上限、跑在真实 Azurite 与 7z 上，此前偶发抖动过。若它失败，单独重跑一次并同时报告两次结果，不要把任一次当作定论。

- [ ] **Step 2: 确认没有写宽的捕获**

Run: `cd backend && grep -rn "catch (Exception)" backend/src/AzureStorageBackup.Api/Services/BackupDiffer.cs backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`
Expected: 无输出，或命中的每一处都能说明为何不会吞掉取消。写宽的捕获会让 `OperationCanceledException` 变成「跳过一个文件」——备份看起来成功，实际没跑完。

- [ ] **Step 3: 确认不可读不会被当成删除**

Run: `cd backend && grep -rn "ChangeKind.Unreadable" backend/src/AzureStorageBackup.Api/Services/`
Expected: 出现在 `BackupDiffer`（产出）与 `BackupOrchestrator`（索引构建、告警、计数）。**不得**出现在任何计算删除集合的地方。逐处说明其作用。

- [ ] **Step 4: 端到端手工核对（人工）**

在一个真实容器里，对一个持续被写入且无法共享读取的文件跑一次备份，确认：备份完成而非失败；该文件产生一条 Warning；其余文件正常入库；再跑一轮时该文件仍产生告警而不是消失。

- [ ] **Step 5: 最终提交**

```bash
git add -A
git commit -m "chore: verify unreadable-file handling end to end"
```

若无改动则不提交空提交。

---

## 本计划的已知弱点

**测试体是以「期望」而非成品代码给出的。** 每个新测试文件里，测试方法带有完整的名称、意图注释与断言目标，但方法体需要实现者按该测试文件邻近的既有构造方式补全（`BackupDifferTests.cs`、`BackupOrchestratorTests.cs`、`ProcessingVerifierTests.cs` 各有自己的 fixture 与 helper）。

写计划时没有逐一读完这三个测试文件的构造 helper，因此不敢直接写出调用它们的代码——凭印象编出来的构造代码看起来权威、实则大概率错，比明说「按邻近用例的写法补全」更糟。

对实现者的硬性要求：**不得留空方法体**。空测试恒绿，比没有测试更危险——它会让评审和后来者以为这条路径有覆盖。若某条测试确实写不出来，报告 BLOCKED 并说明卡在哪里，不要留一个空壳。

## 交付说明

Task 5 覆盖的是**设计文档未列出**的路径（压缩与上传阶段的读取）。它是写计划时核对代码发现的，不是实现时的自由发挥；汇报时应说明设计已据此补充或需要补充。

Task 6 Step 4 的端到端核对需要一个真实被占用的文件，无法自动化，必须由人执行。
