# Backup Suspend / Resume Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Spec:** `docs/backup-suspend-resume-design.md`

**Goal:** 让备份在网络/云端瞬时故障时挂起而不是失败，支持用户主动暂停、两种取消语义、以及崩溃后从落盘的 journal 恢复，已上传的内容不重传。

**Architecture:** 三块新东西：(1) 一个共享的瞬时错误判据 `TransientErrors`，修掉 `AggregateException` 漏判这个根因；(2) 一个 append-only 的 JSONL journal，记录"这块内容确实已经在云上了"，写在上传确认返回之后，供下次运行恢复；(3) 一个挂起闸门 `PauseGate`，让所有上传工作者在瞬时错误上原地等，自愈重试成功就继续，超过耐心阈值就降级成 `Suspended` 落盘退出。`Pause` 是 `Running` 的子状态（不新增 `RunStatus` 值，否则 19 处后端 `== RunStatus.Running` 与前端轮询循环全会误判成"跑完了"）；`Suspended` 是新增的**终态**。

**Tech Stack:** .NET 9 / ASP.NET Core Minimal API / EF Core + SQLite / Azure.Storage.Blobs / xUnit + `[SkippableFact]` + Azurite / React + TypeScript + Vite。

## Global Constraints

- 代码注释一律**中文**；commit message（标题 + 正文）一律**英文**；UI 文案一律**英文**。
- 不改 `NetworkTimeout`，不引入 `TransferOptions`（用户明确否决："第二条不建议改。100M应该不至于。"）。
- **挂起状态不得被后续计划任务打断**——`Paused` 期间运行仍是 `Running`，调度器看到"在跑"就不会再起一轮；不要新增会让调度器认为它已结束的状态值。
- **有未完成任务时不要清理临时文件夹**，除非明确知道不会干扰。本计划只在**进程启动**时清 `{tempPath}/compress` 与 `{tempPath}/staged`（此刻没有任何运行存活），不在每次备份开始时清。
- **一次性上线，不分批发版。** 13 个任务全部做完再触发 docker-publish。
- 做完直接合并 `main`，不留分支；仓库只保留 `main` 一条线。
- 后端测试：`dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj`
- 集成测试需要 Azurite 在跑，否则 189 条会静悄悄跳过：`npx azurite --skipApiVersionCheck`
- 前端：`cd frontend && npm run lint && npm run test && npm run build`
- 已知的**对 spec 的有意偏离**：journal 路径用 `{accountId}/{container}/{runId}.jsonl` 而不是 spec 写的 `{configId}/...`。理由：`RetentionCleaner` 按 `(account, container)` 定位，手上根本没有 configId，用 configId 当目录名它就找不到本容器的活动 journal。configId 记在 journal 头里。

## File Structure

**新建（后端源码）**

| 文件 | 职责 |
| --- | --- |
| `Services/TransientErrors.cs` | 唯一的瞬时错误判据，上传重试与挂起闸门共用 |
| `Services/BackupJournal.cs` | 单个 journal 文件的读/写/追加，JSONL 格式 |
| `Services/BackupJournalStore.cs` | journal 的目录管理：建、列、删、汇总活动引用 |
| `Services/PauseGate.cs` | 挂起闸门：等待、自愈重试计时、超时降级 |
| `Services/BackupRunControl.cs` | 把 journal / 闸门 / 停止意图 / 恢复查表打包传给编排器 |
| `Services/BackupSuspendedException.cs` | 把"挂起退出"与"失败"区分开的信号异常 |

**修改（后端源码）**

| 文件 | 改什么 |
| --- | --- |
| `Services/BlobUploader.cs:121-127` | `IsTransient` 改调 `TransientErrors` |
| `Services/StagingArea.cs` | 加 `static ClearStale(compressDir, stagedDir)` |
| `Program.cs:60-100` | 启动时调 `StagingArea.ClearStale`；注册 `BackupJournalStore` |
| `Services/BackupOrchestrator.cs` | 接 `BackupRunControl`：写 journal、读 journal 恢复、闸门包装、停止检查 |
| `Services/BackupRunner.cs` | `RunStatus.Suspended`、`BackupRunState.Pause`、`Suspend/Cancel/RetryNow/Resume/Discard` |
| `Services/RetentionCleaner.cs:74-75` | 去掉 `toDelete.Count == 0` 早退；删除判据并上"不被任何活动 journal 引用" |
| `Services/BlobAddressScheme.cs` | 加 `Identity`，供 journal 头做加密身份前置校验 |
| `Endpoints/BackupConfigEndpoints.cs` | 新端点 + 删配置兜底扫 journal |
| `frontend/src/api/backupConfigs.ts` | `RunStatus` 加 `'Suspended'`、`Pause` 类型、新 API |
| `frontend/src/pages/BackupConfigsPage.tsx` | 按钮、暂停横幅、Cancel 对话框、轮询适配 |

**新建（测试）**

`TransientErrorsTests.cs`、`StagingAreaClearStaleTests.cs`、`BackupJournalTests.cs`、`BackupJournalStoreTests.cs`、`BackupJournalWriteTests.cs`、`PauseGateTests.cs`、`BackupPauseGateIntegrationTests.cs`、`BackupSuspendResumeTests.cs`、`BackupCancelModesTests.cs`、`JournalAwareCleanupTests.cs`、`BackupSuspendEndpointsTests.cs`

---

### Task 1: 瞬时错误判据（修根因）

现在 `BlobUploader.IsTransient` 只认 `RequestFailedException` 和 `IOException`。Azure.Core 重试耗尽后抛的是 `AggregateException`（内层是 `TaskCanceledException`），一条都不匹配，于是项目自己那层 `RetryPolicy` 一次都没重试就把错误捅到顶。这是这次线上失败的根因。

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/TransientErrors.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BlobUploader.cs:121-127`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/TransientErrorsTests.cs`

**Interfaces:**
- Produces: `static class TransientErrors { static bool IsTransient(Exception ex, CancellationToken ct = default); }`

- [ ] **Step 1: 写失败的测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/TransientErrorsTests.cs`：

```csharp
using System.Net.Sockets;
using Azure;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class TransientErrorsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void RequestFailed_transient_statuses(int status)
        => Assert.True(TransientErrors.IsTransient(new RequestFailedException(status, "boom")));

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(412)]
    public void RequestFailed_permanent_statuses(int status)
        => Assert.False(TransientErrors.IsTransient(new RequestFailedException(status, "nope")));

    [Fact]
    public void Io_socket_timeout_are_transient()
    {
        Assert.True(TransientErrors.IsTransient(new IOException("disk hiccup")));
        Assert.True(TransientErrors.IsTransient(new SocketException(110)));
        Assert.True(TransientErrors.IsTransient(new TimeoutException("slow")));
    }

    // 这条就是线上那次失败的形状：SDK 重试耗尽 -> AggregateException(TaskCanceledException...)
    [Fact]
    public void Aggregate_of_timeouts_is_transient()
    {
        var agg = new AggregateException(
            "Retry failed after 6 tries.",
            new TaskCanceledException("timeout"), new TaskCanceledException("timeout"));
        Assert.True(TransientErrors.IsTransient(agg));
    }

    [Fact]
    public void Aggregate_with_any_permanent_inner_is_not_transient()
    {
        var agg = new AggregateException(
            new TaskCanceledException("timeout"), new InvalidOperationException("bug"));
        Assert.False(TransientErrors.IsTransient(agg));
    }

    [Fact]
    public void Empty_aggregate_is_not_transient()
        => Assert.False(TransientErrors.IsTransient(new AggregateException()));

    // 用户按了取消 -> 取消令牌已触发 -> 这不是"网络抖了一下"，不能当瞬时错误吞掉。
    [Fact]
    public void Cancellation_by_user_is_not_transient()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(TransientErrors.IsTransient(new OperationCanceledException(cts.Token), cts.Token));
        Assert.False(TransientErrors.IsTransient(
            new AggregateException(new TaskCanceledException()), cts.Token));
    }

    // 取消令牌没触发的 OperationCanceledException = SDK 的网络超时，算瞬时。
    [Fact]
    public void Cancellation_without_user_request_is_transient()
        => Assert.True(TransientErrors.IsTransient(new OperationCanceledException(), CancellationToken.None));

    [Fact]
    public void Plain_bug_is_not_transient()
        => Assert.False(TransientErrors.IsTransient(new InvalidOperationException("bug")));
}
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~TransientErrorsTests
```

Expected: 编译失败，`error CS0103: The name 'TransientErrors' does not exist`。

- [ ] **Step 3: 写实现**

新建 `backend/src/AzureStorageBackup.Api/Services/TransientErrors.cs`：

```csharp
using System.Net.Sockets;
using Azure;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 瞬时（可重试、可挂起）错误的唯一判据。上传重试与挂起闸门共用一套，
/// 免得两边各判各的，出现"重试层认为该重试、闸门层认为该失败"这种自相矛盾。
/// </summary>
public static class TransientErrors
{
    /// <param name="ct">
    /// 调用方的取消令牌。取消是唯一需要上下文才能分辨的情况：同样是
    /// <see cref="OperationCanceledException"/>，令牌已触发说明是**用户按了取消**（必须往上抛），
    /// 没触发说明是 SDK 内部的网络超时（该重试）。判错这一条，取消按钮会静悄悄失效。
    /// </param>
    public static bool IsTransient(Exception ex, CancellationToken ct = default) => ex switch
    {
        RequestFailedException rfe => rfe.Status == 0 || rfe.Status >= 500 || rfe.Status is 408 or 429,
        IOException => true,
        SocketException => true,
        TimeoutException => true,
        OperationCanceledException => !ct.IsCancellationRequested,
        // Azure.Core 重试耗尽时抛的就是这个（内层一串 TaskCanceledException）。
        // 从前这里漏判，导致我们自己那层 RetryPolicy 一次都没重试，直接把运行判死。
        AggregateException agg => agg.InnerExceptions.Count > 0
            && agg.InnerExceptions.All(inner => IsTransient(inner, ct)),
        _ => false,
    };
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~TransientErrorsTests
```

Expected: PASS，11 个用例全绿。

- [ ] **Step 5: 让 BlobUploader 改用它**

编辑 `backend/src/AzureStorageBackup.Api/Services/BlobUploader.cs`，把文件末尾那段私有 `IsTransient` 整个替换成转调：

```csharp
    /// <summary>可重试的瞬时错误。判据集中在 <see cref="TransientErrors"/>，与挂起闸门同源。</summary>
    private static bool IsTransient(Exception ex) => TransientErrors.IsTransient(ex);
```

同时删掉文件顶部因此不再需要的 `using`（若 `Azure`/`System.IO` 还被别处用到就留着，编译器会告诉你）。

- [ ] **Step 6: 跑全量后端测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿（Azurite 没起会有跳过，可接受）。

- [ ] **Step 7: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/TransientErrors.cs \
        backend/src/AzureStorageBackup.Api/Services/BlobUploader.cs \
        backend/tests/AzureStorageBackup.Api.Tests/TransientErrorsTests.cs
git commit -m "fix(upload): treat AggregateException as transient

Azure.Core wraps its exhausted retries in an AggregateException whose
inner exceptions are TaskCanceledException. The old IsTransient matched
neither, so our own RetryPolicy never ran a single retry and a network
hiccup failed the whole run. Extract the predicate into TransientErrors
so the pause gate can share it, and add AggregateException, SocketException,
TimeoutException and non-user OperationCanceledException.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: 启动时清空 compress / staged

`StagingArea.MoveToStaged` 在 `{tempPath}/staged/` 下按 GUID 建子目录；进程被 kill / 断电时这些子目录留着，没人清。这是既有的泄漏。清理只能放在**进程启动**（此刻没有任何运行存活），不能放在每次备份开始（多个备份可以并行，会删掉别人正在写的文件）——`Program.cs:95-99` 的 `DiffWorkQueue.ClearStale` 就是这个先例。

**这不违背"有未完成任务时不要清理临时文件夹"**，因为这两个目录里的东西没有一件是恢复要用的。恢复认的是**云上已经确认存在**的块（journal 记的就是这个），压到一半、或压好了还没传的本地产物一律重来——这是已经定下的取舍："如果不复用就不用保留了"。journal 本身不在这两个目录下（Task 4 放在库文件旁边），也就不会被这一步碰到。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/StagingArea.cs`（加静态方法）
- Modify: `backend/src/AzureStorageBackup.Api/Program.cs`（启动时调用）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/StagingAreaClearStaleTests.cs`

**Interfaces:**
- Produces: `static void StagingArea.ClearStale(string compressTempDir, string stagedTempDir)`

- [ ] **Step 1: 写失败的测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/StagingAreaClearStaleTests.cs`：

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class StagingAreaClearStaleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-clearstale-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Clears_leftover_subdirectories_and_files()
    {
        var compress = Path.Combine(_root, "compress");
        var staged = Path.Combine(_root, "staged");
        Directory.CreateDirectory(Path.Combine(compress, "abc"));
        Directory.CreateDirectory(Path.Combine(staged, "def"));
        File.WriteAllText(Path.Combine(compress, "abc", "part.7z.001"), "x");
        File.WriteAllText(Path.Combine(staged, "def", "part.7z"), "y");
        File.WriteAllText(Path.Combine(staged, "loose.tmp"), "z");

        StagingArea.ClearStale(compress, staged);

        Assert.Empty(Directory.EnumerateFileSystemEntries(compress));
        Assert.Empty(Directory.EnumerateFileSystemEntries(staged));
    }

    [Fact]
    public void Missing_directories_are_not_an_error()
    {
        var ex = Record.Exception(() => StagingArea.ClearStale(
            Path.Combine(_root, "nope-a"), Path.Combine(_root, "nope-b")));
        Assert.Null(ex);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~StagingAreaClearStaleTests
```

Expected: 编译失败，`'StagingArea' does not contain a definition for 'ClearStale'`。

- [ ] **Step 3: 写实现**

在 `backend/src/AzureStorageBackup.Api/Services/StagingArea.cs` 的类里（放在构造函数之后、`AcquireLease` 之前）加：

```csharp
    /// <summary>
    /// 进程启动时清掉上一个进程留下的压缩/暂存残留。
    /// <para>
    /// 必须在**进程启动**时清，不能在每次备份开始时清：多个备份可以同时在跑，
    /// 按运行清会把别人正在写的文件删掉。进程刚起来时没有任何运行存活，
    /// 这里看到的一切都是上次非正常退出（容器被 kill、断电）的垃圾。
    /// </para>
    /// <para>
    /// 恢复时不复用这些暂存文件——重压一遍比校验一堆来路不明的半成品便宜也安全得多。
    /// </para>
    /// </summary>
    public static void ClearStale(string compressTempDir, string stagedTempDir)
    {
        foreach (var dir in new[] { compressTempDir, stagedTempDir })
        {
            try
            {
                if (!Directory.Exists(dir))
                    continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    try { Directory.Delete(sub, recursive: true); } catch { /* 删不掉就算了，下次再说 */ }
                foreach (var file in Directory.EnumerateFiles(dir))
                    try { File.Delete(file); } catch { /* 同上 */ }
            }
            catch { /* 同上 */ }
        }
    }
```

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~StagingAreaClearStaleTests
```

Expected: PASS。

- [ ] **Step 5: 在 Program.cs 启动时调用**

编辑 `backend/src/AzureStorageBackup.Api/Program.cs`，紧挨着 `DiffWorkQueue.ClearStale(spillDir);` 那几行之后加：

```csharp
// 同理：上次非正常退出留下的压缩中间产物与暂存分卷也在这里清掉。
// 恢复靠的是 journal（云端已确认的内容），不靠这些本地半成品。
StagingArea.ClearStale(Path.Combine(tempPath, "compress"), Path.Combine(tempPath, "staged"));
```

- [ ] **Step 6: 跑全量后端测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿。

- [ ] **Step 7: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/StagingArea.cs \
        backend/src/AzureStorageBackup.Api/Program.cs \
        backend/tests/AzureStorageBackup.Api.Tests/StagingAreaClearStaleTests.cs
git commit -m "fix(staging): clear leftover temp dirs at process startup

MoveToStaged creates a GUID subdirectory per staged item. A killed
container left those behind forever. Clear compress/ and staged/ at
process start, where no run is alive and everything present is garbage
from the previous process. Mirrors DiffWorkQueue.ClearStale.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: journal 文件格式

一个 append-only 的 JSONL 文件：第一行是头（含前置校验所需的一切），后面每行一条"这块内容已经在云上确认了"。头里要放加密身份，所以本任务顺带给 `BlobAddressScheme` 加一个 `Identity`。

**不 fsync**：崩溃时最后几行可能是半截的。代价不对称——少记一条 = 下次多传一个文件；为它每条都 fsync = 每个文件多一次磁盘同步。所以读取端必须能容忍**最后一行截断**（跳过解析不了的行），只有头坏了才判整卷作废。

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/BackupJournal.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BlobAddressScheme.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupJournalTests.cs`

**Interfaces:**
- Produces:
  - `string BlobAddressScheme.Identity { get; }`
  - `sealed record JournalHeader { string RunId; int ConfigId; DateTimeOffset StartedAt; int BaselineVersion; string LocalRoot; string EncryptionIdentity; }`
  - `sealed record JournalMember(string Path, string EntryName, string FullHash, long Length)`
  - `sealed record JournalRecord { string Kind; string Ref; string? Path; string? FullHash; string? HeadHash; string? TailHash; long Length; int Volumes; bool Raw; bool StoreOnly; IReadOnlyList<JournalMember> Members; IReadOnlyList<long> VolumeSizes; }`
  - `sealed record JournalContent(JournalHeader Header, IReadOnlyList<JournalRecord> Records)`
  - `sealed class BackupJournal : IAsyncDisposable` — `static Task<BackupJournal> CreateAsync(string path, JournalHeader header, CancellationToken ct)`、`static Task<JournalContent?> ReadAsync(string path, CancellationToken ct)`、`Task AppendAsync(JournalRecord record, CancellationToken ct)`、`Task FlushAsync(bool fsync, CancellationToken ct)`

- [ ] **Step 1: 写失败的测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/BackupJournalTests.cs`：

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class BackupJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "asb-journal-" + Guid.NewGuid().ToString("N"));

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    private static JournalHeader Header() => new()
    {
        RunId = "r1",
        ConfigId = 7,
        StartedAt = DateTimeOffset.UnixEpoch,
        BaselineVersion = 3,
        LocalRoot = "/data/src",
        EncryptionIdentity = "plain",
    };

    public BackupJournalTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Round_trips_header_and_records()
    {
        var file = Path_("a.jsonl");
        await using (var j = await BackupJournal.CreateAsync(file, Header(), default))
        {
            await j.AppendAsync(new JournalRecord
            {
                Kind = "blob", Ref = "data/aaa", Path = "x/y.bin", FullHash = "aaa",
                HeadHash = "h", TailHash = "t", Length = 10, Volumes = 2, Raw = true,
                VolumeSizes = [4, 6],
            }, default);
            await j.AppendAsync(new JournalRecord
            {
                Kind = "pack", Ref = "p123456780001", StoreOnly = true, Volumes = 1,
                VolumeSizes = [99],
                Members = [new JournalMember("a.txt", "0001_a.txt", "hh", 5)],
            }, default);
        }

        var content = await BackupJournal.ReadAsync(file, default);
        Assert.NotNull(content);
        Assert.Equal(7, content!.Header.ConfigId);
        Assert.Equal(3, content.Header.BaselineVersion);
        Assert.Equal(2, content.Records.Count);
        Assert.Equal("data/aaa", content.Records[0].Ref);
        Assert.Equal([4L, 6L], content.Records[0].VolumeSizes);
        Assert.True(content.Records[1].StoreOnly);
        Assert.Equal("0001_a.txt", content.Records[1].Members[0].EntryName);
    }

    // 不 fsync 的代价：崩溃时最后一行可能是半截的。读取端必须扛得住。
    [Fact]
    public async Task Truncated_last_line_is_skipped()
    {
        var file = Path_("b.jsonl");
        await using (var j = await BackupJournal.CreateAsync(file, Header(), default))
            await j.AppendAsync(new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "p", FullHash = "aaa" }, default);
        await File.AppendAllTextAsync(file, "{\"Kind\":\"blob\",\"Ref\":\"data/bb");

        var content = await BackupJournal.ReadAsync(file, default);
        Assert.NotNull(content);
        Assert.Single(content!.Records);
        Assert.Equal("data/aaa", content.Records[0].Ref);
    }

    [Fact]
    public async Task Corrupt_header_voids_the_whole_journal()
    {
        var file = Path_("c.jsonl");
        await File.WriteAllTextAsync(file, "not json at all\n{\"Kind\":\"blob\",\"Ref\":\"data/aaa\"}\n");
        Assert.Null(await BackupJournal.ReadAsync(file, default));
    }

    [Fact]
    public async Task Empty_file_reads_as_null()
    {
        var file = Path_("d.jsonl");
        await File.WriteAllTextAsync(file, "");
        Assert.Null(await BackupJournal.ReadAsync(file, default));
    }

    [Fact]
    public async Task Missing_file_reads_as_null()
        => Assert.Null(await BackupJournal.ReadAsync(Path_("nope.jsonl"), default));

    [Fact]
    public async Task Flush_makes_records_readable_while_still_open()
    {
        var file = Path_("e.jsonl");
        await using var j = await BackupJournal.CreateAsync(file, Header(), default);
        await j.AppendAsync(new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "p", FullHash = "aaa" }, default);
        await j.FlushAsync(fsync: true, default);

        var content = await BackupJournal.ReadAsync(file, default);
        Assert.Single(content!.Records);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}

public class BlobAddressSchemeIdentityTests
{
    [Fact]
    public void Unkeyed_identity_is_plain()
        => Assert.Equal("plain", new BlobAddressScheme(null, null).Identity);

    [Fact]
    public void Same_password_and_salt_give_same_identity()
    {
        var salt = new byte[16];
        Assert.Equal(new BlobAddressScheme("pw", salt).Identity, new BlobAddressScheme("pw", salt).Identity);
    }

    [Fact]
    public void Different_password_gives_different_identity()
    {
        var salt = new byte[16];
        Assert.NotEqual(new BlobAddressScheme("pw", salt).Identity, new BlobAddressScheme("other", salt).Identity);
    }
}
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter "FullyQualifiedName~BackupJournalTests|FullyQualifiedName~BlobAddressSchemeIdentityTests"
```

Expected: 编译失败，`The name 'BackupJournal' does not exist` / `'BlobAddressScheme' does not contain a definition for 'Identity'`。

- [ ] **Step 3: 给 BlobAddressScheme 加 Identity**

编辑 `backend/src/AzureStorageBackup.Api/Services/BlobAddressScheme.cs`，在 `Keyed` 属性旁边加：

```csharp
    /// <summary>
    /// 这套寻址方案的身份指纹，用来在恢复时判定"journal 是不是同一把钥匙写的"。
    /// 换了密码 / 换了 KDF 盐，地址空间就变了，旧 journal 里的引用全都对不上，必须整卷作废。
    /// 从已派生的密钥再 HMAC 一次，泄露的信息不比现有寻址方案更多。
    /// </summary>
    public string Identity => !Keyed
        ? "plain"
        : Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            _key!, "asb-journal-identity"u8.ToArray()))[..16].ToLowerInvariant();
```

（`_key` 的可空性以文件里实际声明为准；若它是非空 `byte[]`，去掉 `!`。）

- [ ] **Step 4: 写 BackupJournal**

新建 `backend/src/AzureStorageBackup.Api/Services/BackupJournal.cs`：

```csharp
using System.Text;
using System.Text.Json;

namespace AzureStorageBackup.Api.Services;

/// <summary>journal 的头一行：恢复前置校验要用的一切都在这。</summary>
public sealed record JournalHeader
{
    public required string RunId { get; init; }
    public required int ConfigId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>本次运行差异比对的基线版本号。基线变了（别人跑完了一轮），这卷 journal 作废。</summary>
    public required int BaselineVersion { get; init; }

    /// <summary>本地源根。改过根目录，路径含义就变了，作废。</summary>
    public required string LocalRoot { get; init; }

    /// <summary>加密身份指纹（<see cref="BlobAddressScheme.Identity"/>）。换了密码，地址空间就变了，作废。</summary>
    public required string EncryptionIdentity { get; init; }
}

/// <summary>pack 里的一个成员。恢复时要靠它重建 <c>PackInfo</c> 与每个成员的 StorageRef。</summary>
public sealed record JournalMember(string Path, string EntryName, string FullHash, long Length);

/// <summary>一条"这块内容已经在云上确认了"。</summary>
public sealed record JournalRecord
{
    /// <summary>"blob" 或 "pack"。</summary>
    public required string Kind { get; init; }

    /// <summary>blob：data blob 的基名（如 <c>data/abc</c>）；pack：packId。</summary>
    public required string Ref { get; init; }

    // 以下 blob 用
    public string? Path { get; init; }
    public string? FullHash { get; init; }
    public string? HeadHash { get; init; }
    public string? TailHash { get; init; }
    public long Length { get; init; }
    public bool Raw { get; init; }

    // 以下 pack 用
    public bool StoreOnly { get; init; }
    public IReadOnlyList<JournalMember> Members { get; init; } = [];

    public int Volumes { get; init; } = 1;
    public IReadOnlyList<long> VolumeSizes { get; init; } = [];
}

/// <summary>读出来的整卷 journal。</summary>
public sealed record JournalContent(JournalHeader Header, IReadOnlyList<JournalRecord> Records);

/// <summary>
/// 一次备份运行的恢复日志：append-only 的 JSONL，头一行是 <see cref="JournalHeader"/>，
/// 后面每行一条 <see cref="JournalRecord"/>。
/// <para>
/// **时序是这个文件的全部意义**：压缩 → 上传 → 上传确认返回 → 才追加一行。
/// 顺序反了就会记下一块其实不在云上的内容，下次恢复直接跳过它 —— 数据丢失。
/// </para>
/// <para>
/// **不逐条 fsync**：代价不对称。少记一条 = 下次多传一个文件；每条都 fsync = 每个文件
/// 多一次磁盘同步。所以崩溃后最后一行可能是半截的，<see cref="ReadAsync"/> 跳过解析不了的行。
/// 只有主动挂起收尾时才真 fsync（那一刻我们承诺"落盘成功再返回"）。
/// </para>
/// </summary>
public sealed class BackupJournal : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly FileStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private BackupJournal(FileStream stream) => _stream = stream;

    /// <summary>建一卷新 journal 并写下头一行。父目录不存在会自动建。</summary>
    public static async Task<BackupJournal> CreateAsync(string path, JournalHeader header, CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var journal = new BackupJournal(stream);
        await journal.WriteLineAsync(JsonSerializer.Serialize(header, Json), ct);
        await journal.FlushAsync(fsync: true, ct);   // 头写不下去，后面全是白搭，这一次同步值得
        return journal;
    }

    /// <summary>追加一条。调用点必须在**上传确认返回之后**。</summary>
    public async Task AppendAsync(JournalRecord record, CancellationToken ct)
        => await WriteLineAsync(JsonSerializer.Serialize(record, Json), ct);

    /// <param name="fsync">true 时连同操作系统缓冲一起刷到盘上（主动挂起收尾用）。</param>
    public async Task FlushAsync(bool fsync, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.FlushAsync(ct);
            if (fsync)
                _stream.Flush(flushToDisk: true);
        }
        finally { _writeLock.Release(); }
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);   // 只刷到 OS，不落盘；见类注释
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>读整卷。文件不在、空的、或头坏了都返回 null（= 这卷作废，当没有恢复点）。</summary>
    public static async Task<JournalContent?> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;

        JournalHeader? header = null;
        var records = new List<JournalRecord>();
        using var reader = new StreamReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
                continue;
            if (header is null)
            {
                try { header = JsonSerializer.Deserialize<JournalHeader>(line, Json); }
                catch (JsonException) { return null; }   // 头坏了，整卷作废
                if (header is null)
                    return null;
                continue;
            }
            try
            {
                if (JsonSerializer.Deserialize<JournalRecord>(line, Json) is { } record)
                    records.Add(record);
            }
            catch (JsonException)
            {
                // 崩溃留下的半截行。正常只可能出现在最后一行；真出现在中间也只是少认几条，
                // 后果是多传几个文件，不是数据丢失。继续读完。
            }
        }

        return header is null ? null : new JournalContent(header, records);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stream.FlushAsync(); } catch { /* 关的时候刷不动就算了 */ }
        await _stream.DisposeAsync();
        _writeLock.Dispose();
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter "FullyQualifiedName~BackupJournalTests|FullyQualifiedName~BlobAddressSchemeIdentityTests"
```

Expected: PASS，9 个用例全绿。

- [ ] **Step 6: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupJournal.cs \
        backend/src/AzureStorageBackup.Api/Services/BlobAddressScheme.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupJournalTests.cs
git commit -m "feat(backup): add append-only resume journal format

One JSONL file per run: a header carrying the resume preconditions
(local root, baseline version, encryption identity) followed by one line
per block confirmed present in the cloud. Reading tolerates a truncated
last line, because we deliberately do not fsync per record.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: journal 目录

journal 存在 **`{dataDir}/journal/{accountId}/{container}/{runId}.jsonl`**，`dataDir` 就是 SQLite 库文件所在的那个目录。

**不能放 tempPath 下。** `Program.cs:74-76`：没配 `Backup:TempPath` 时它是 `/tmp/azurestoragebackup`，容器重建就没了——而 journal 存在的全部理由正是"容器重建之后还认得出上一轮传到哪了"。放在库文件旁边，它跟着同一个持久卷走，用户不必为了让崩溃恢复生效而额外配一个环境变量（而配漏了不会报错，只会在真出事那天悄悄失效）。

按 `(accountId, container)` 分目录，是因为清理器（`RetentionCleaner`）就是按这两样定位的，手上没有 configId——configId 记在头里。

清理器要问的问题只有一个："这个 blob / pack 是不是被某卷**活动** journal 引用着？"所以本任务给它一个 `LoadActiveRefsAsync`。

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/BackupJournalStore.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Program.cs`（DI 注册）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupJournalStoreTests.cs`

**Interfaces:**
- Consumes: `BackupJournal`, `JournalHeader`, `JournalRecord`, `JournalContent`（Task 3）
- Produces:
  - `sealed record ActiveJournalRefs(IReadOnlySet<string> Blobs, IReadOnlySet<string> Packs)` + `static readonly ActiveJournalRefs Empty`
  - `sealed class BackupJournalStore(string rootDir)` — `string PathFor(int accountId, string container, string runId)`、`Task<BackupJournal> CreateAsync(int, string, string, JournalHeader, CancellationToken)`、`Task<IReadOnlyList<(string RunId, JournalContent Content)>> ListAsync(int, string, CancellationToken)`、`Task<ActiveJournalRefs> LoadActiveRefsAsync(int, string, CancellationToken)`、`void Delete(int, string, string)`、`void DeleteAll(int, string)`

- [ ] **Step 1: 写失败的测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/BackupJournalStoreTests.cs`：

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class BackupJournalStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-jstore-" + Guid.NewGuid().ToString("N"));
    private readonly BackupJournalStore _store;

    public BackupJournalStoreTests() => _store = new BackupJournalStore(_root);

    private static JournalHeader Header(string runId) => new()
    {
        RunId = runId, ConfigId = 1, StartedAt = DateTimeOffset.UnixEpoch,
        BaselineVersion = 0, LocalRoot = "/src", EncryptionIdentity = "plain",
    };

    private async Task WriteRunAsync(string runId, params JournalRecord[] records)
    {
        await using var j = await _store.CreateAsync(9, "cont", runId, Header(runId), default);
        foreach (var r in records)
            await j.AppendAsync(r, default);
    }

    [Fact]
    public async Task Lists_journals_for_the_container_only()
    {
        await WriteRunAsync("run-a");
        await using (var other = await _store.CreateAsync(9, "elsewhere", "run-b", Header("run-b"), default)) { }

        var listed = await _store.ListAsync(9, "cont", default);
        Assert.Single(listed);
        Assert.Equal("run-a", listed[0].RunId);
    }

    [Fact]
    public async Task Active_refs_union_blobs_and_packs_across_runs()
    {
        await WriteRunAsync("run-a",
            new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "p1", FullHash = "aaa" },
            new JournalRecord { Kind = "pack", Ref = "p000000010001" });
        await WriteRunAsync("run-b",
            new JournalRecord { Kind = "blob", Ref = "data/bbb", Path = "p2", FullHash = "bbb" });

        var refs = await _store.LoadActiveRefsAsync(9, "cont", default);
        Assert.Equal(["data/aaa", "data/bbb"], refs.Blobs.OrderBy(x => x));
        Assert.Equal(["p000000010001"], refs.Packs);
    }

    [Fact]
    public async Task No_journals_gives_empty_refs()
    {
        var refs = await _store.LoadActiveRefsAsync(9, "cont", default);
        Assert.Empty(refs.Blobs);
        Assert.Empty(refs.Packs);
    }

    [Fact]
    public async Task Delete_removes_one_run()
    {
        await WriteRunAsync("run-a");
        await WriteRunAsync("run-b");
        _store.Delete(9, "cont", "run-a");

        var listed = await _store.ListAsync(9, "cont", default);
        Assert.Single(listed);
        Assert.Equal("run-b", listed[0].RunId);
    }

    [Fact]
    public async Task DeleteAll_removes_the_container_folder()
    {
        await WriteRunAsync("run-a");
        _store.DeleteAll(9, "cont");
        Assert.Empty(await _store.ListAsync(9, "cont", default));
    }

    // 容器名带斜杠这种事不该把 journal 写到目录树外面去。
    [Fact]
    public void PathFor_flattens_container_names()
    {
        var p = _store.PathFor(9, "a/b", "run-a");
        Assert.StartsWith(_root, p);
        Assert.DoesNotContain("a/b", p.Replace(Path.DirectorySeparatorChar, '/'));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~BackupJournalStoreTests
```

Expected: 编译失败，`The name 'BackupJournalStore' does not exist`。

- [ ] **Step 3: 写实现**

新建 `backend/src/AzureStorageBackup.Api/Services/BackupJournalStore.cs`：

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 某个容器上所有**活动** journal 引用到的 blob 基名与 packId。
/// 清理器拿它当"别删我"的名单：这些内容云上有、索引里还没有，
/// 只有 journal 记着它们存在，删了就等于让恢复白跑。
/// </summary>
public sealed record ActiveJournalRefs(IReadOnlySet<string> Blobs, IReadOnlySet<string> Packs)
{
    public static readonly ActiveJournalRefs Empty =
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// journal 的目录：<c>{root}/{accountId}/{container}/{runId}.jsonl</c>。
/// <para>
/// 按 (accountId, container) 分目录而不是按 configId——清理器就是按这两样定位容器的，
/// 手上根本没有 configId。configId 记在 journal 头里，需要时从头读。
/// </para>
/// </summary>
public sealed class BackupJournalStore(string rootDir)
{
    /// <summary>容器名理论上不含斜杠，但别把这条当保证：拼路径前一律做一次扁平化。</summary>
    private static string Safe(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0 || chars[i] is '/' or '\\')
                chars[i] = '_';
        return new string(chars);
    }

    private string DirFor(int accountId, string container)
        => Path.Combine(rootDir, accountId.ToString(), Safe(container));

    public string PathFor(int accountId, string container, string runId)
        => Path.Combine(DirFor(accountId, container), Safe(runId) + ".jsonl");

    public Task<BackupJournal> CreateAsync(
        int accountId, string container, string runId, JournalHeader header, CancellationToken ct)
        => BackupJournal.CreateAsync(PathFor(accountId, container, runId), header, ct);

    /// <summary>列出该容器上所有能读通的 journal。读不通的（头坏了）直接当不存在。</summary>
    public async Task<IReadOnlyList<(string RunId, JournalContent Content)>> ListAsync(
        int accountId, string container, CancellationToken ct)
    {
        var dir = DirFor(accountId, container);
        if (!Directory.Exists(dir))
            return [];

        var result = new List<(string, JournalContent)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            var content = await BackupJournal.ReadAsync(file, ct);
            if (content is not null)
                result.Add((Path.GetFileNameWithoutExtension(file), content));
        }
        return result;
    }

    /// <summary>汇总该容器上所有活动 journal 引用到的内容。清理判据的一半。</summary>
    public async Task<ActiveJournalRefs> LoadActiveRefsAsync(int accountId, string container, CancellationToken ct)
    {
        var blobs = new HashSet<string>(StringComparer.Ordinal);
        var packs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, content) in await ListAsync(accountId, container, ct))
            foreach (var r in content.Records)
                (r.Kind == "pack" ? packs : blobs).Add(r.Ref);
        return blobs.Count == 0 && packs.Count == 0 ? ActiveJournalRefs.Empty : new ActiveJournalRefs(blobs, packs);
    }

    public void Delete(int accountId, string container, string runId)
    {
        try { File.Delete(PathFor(accountId, container, runId)); } catch { /* 删不掉下次再说 */ }
    }

    /// <summary>删配置兜底用：这个容器的 journal 全不要了。</summary>
    public void DeleteAll(int accountId, string container)
    {
        try
        {
            var dir = DirFor(accountId, container);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* 同上 */ }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~BackupJournalStoreTests
```

Expected: PASS，6 个用例全绿。

- [ ] **Step 5: DI 注册**

编辑 `backend/src/AzureStorageBackup.Api/Program.cs`，在 `VerboseFileLog` 那条 `AddSingleton` 附近加：

```csharp
// journal 放在**库文件旁边**，不放 tempPath 下：后者没配 Backup:TempPath 时是 /tmp，容器重建
// 就没了——而 journal 存在的全部理由正是"容器重建之后还认得出上一轮传到哪了"。跟着库走，
// 它自然落在同一个持久卷上，用户不必为了让崩溃恢复生效而额外配一个环境变量。
// 另注意**不能**在启动时清它：它记的正是"云上已有、索引还没有"的内容，清了等于让恢复白跑。
var journalRoot = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(sqliteConn).DataSource)) ?? ".",
    "journal");
builder.Services.AddSingleton(new BackupJournalStore(journalRoot));
```

（`sqliteConn` 在 `Program.cs:16` 就有了，早于这里；`Program.cs:275` 那句 `dataSource` 是在 `builder.Build()` **之后**算的，用不上。）

- [ ] **Step 6: 跑全量后端测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿。

- [ ] **Step 7: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupJournalStore.cs \
        backend/src/AzureStorageBackup.Api/Program.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupJournalStoreTests.cs
git commit -m "feat(backup): add journal store keyed by account and container

Journals live at {temp}/journal/{accountId}/{container}/{runId}.jsonl.
Keyed by account+container rather than config id because the retention
cleaner locates work that way and has no config id; the config id is
recorded in the journal header instead. LoadActiveRefsAsync gives the
cleaner the do-not-delete set.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: 编排器写 journal（只写不读）

这一步**不改变任何行为**：只在"上传确认返回之后"多追加一行。先把时序安全立住，恢复逻辑（Task 10）才有可信的输入。

时序是这里唯一重要的东西：压缩 → 上传 → **上传确认返回** → 才追加 journal 行。顺序反了就会记下一块其实不在云上的内容，下次恢复直接跳过它——数据丢失。

`UploadIfMissingAsync` 返回 `false`（云上已经有了，if-missing 命中）也**必须**记：它同样是"这块内容确实在云上"的确证。现有代码里这条路径走的是 `localResolver` 的去重命中，在 `HandleBlobAsync` 里与真上传汇合到同一个 `storageByPath[...] =` 赋值点，所以一个写入点就覆盖了两者。

跨版本的 pack 成员命中（`OnChangeAsync` 里 `localResolver.TryFindPackMember`）**不记**：它是从保留版本的索引确定性推导出来的，而基线版本本身就是 journal 的前置条件，下次恢复会一模一样地再推一遍。

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`（`RunAsync`/`RunCoreAsync` 签名、`ConsumeAsync` 透传、`HandleBlobAsync`、`RecordPack`）
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs`（造 control 并传进去）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupJournalWriteTests.cs`

**Interfaces:**
- Consumes: `BackupJournalStore`, `BackupJournal`, `JournalHeader`, `JournalRecord`, `JournalMember`（Task 3/4）
- Produces:
  - `sealed class BackupRunControl : IAsyncDisposable` — 构造 `BackupRunControl(BackupJournalStore store, int configId, string runId)`；`string RunId`；`Task OpenJournalAsync(int accountId, string container, int baselineVersion, string localRoot, string encryptionIdentity, DateTimeOffset startedAt, CancellationToken ct)`；`Task RecordBlobAsync(string path, string blobRef, string fullHash, string headHash, string tailHash, long length, int volumes, bool raw, IReadOnlyList<long> volumeSizes, CancellationToken ct)`；`Task RecordPackAsync(string packId, IReadOnlyList<JournalMember> members, IReadOnlyList<long> volumeSizes, bool storeOnly, CancellationToken ct)`；`Task FlushAsync(bool fsync, CancellationToken ct)`；`Task CompleteAsync()`
  - `BackupOrchestrator.RunAsync(BackupRequest, IProgress<BackupProgress>?, CancellationToken, BackupRunControl?)`

- [ ] **Step 1: 写失败的测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/BackupJournalWriteTests.cs`：

```csharp
using System.Net.Sockets;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupJournalWriteTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupJournalWriteTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-jwrite-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
        _journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 41,
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private void WriteText(string rel, string content)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void WriteBytes(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
    }

    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory) Build(IBlobUploader? uploader = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"),
            staging);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
        return (orchestrator, factory);
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 } },
    };

    /// <summary>第 N 次上传起一律抛永久错误，用来把运行卡死在半路。</summary>
    private sealed class FailAfter(IBlobUploader inner, int allowed) : IBlobUploader
    {
        private int _count;

        private void Gate()
        {
            if (Interlocked.Increment(ref _count) > allowed)
                throw new InvalidOperationException("upload refused by test");
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, CancellationToken ct)
        {
            Gate();
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, metadata, options, ct);
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, IProgress<long>? progress, CancellationToken ct)
        {
            Gate();
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, metadata, options, progress, ct);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, CancellationToken ct)
        {
            Gate();
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, metadata, options, ct);
        }
    }

    [SkippableFact]
    public async Task Successful_run_deletes_its_journal()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("jw");
        var (orchestrator, factory) = Build();
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteText("a.txt", "hello");
            await using var control = new BackupRunControl(_journals, configId: 3, runId: "run-ok");
            await orchestrator.RunAsync(Request(account, name), null, default, control);

            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Journal_keeps_what_was_confirmed_before_the_failure()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("jw");
        var factoryOnly = new BlobClientFactory(TestSecrets.Reader);
        // 两个大文件 → 各走单文件 blob 通道；第一个允许传，第二个起就拒。
        var (orchestrator, factory) = Build(new FailAfter(new BlobUploader(factoryOnly), allowed: 1));
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            await using (var control = new BackupRunControl(_journals, configId: 3, runId: "run-boom"))
            {
                await Assert.ThrowsAnyAsync<Exception>(
                    () => orchestrator.RunAsync(Request(account, name), null, default, control));
            }

            var listed = await _journals.ListAsync(account.Id, name, default);
            var journal = Assert.Single(listed);
            Assert.Equal("run-boom", journal.RunId);
            Assert.Equal(3, journal.Content.Header.ConfigId);
            Assert.Equal(0, journal.Content.Header.BaselineVersion);
            Assert.Equal(_root, journal.Content.Header.LocalRoot);
            // 只记下确实传完的那一个；被拒的那个绝不能出现在里面。
            var record = Assert.Single(journal.Content.Records);
            Assert.Equal("blob", record.Kind);
            Assert.False(string.IsNullOrEmpty(record.FullHash));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
npx azurite --skipApiVersionCheck &
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~BackupJournalWriteTests
```

Expected: 编译失败，`The name 'BackupRunControl' does not exist` 以及 `RunAsync` 参数个数不匹配。

- [ ] **Step 3: 写 BackupRunControl**

新建 `backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs`：

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 一次备份运行的"外部把手"：编排器不认识运行注册表，也不该认识；它只认这一个对象。
/// 目前只装 journal，后续任务会往里加挂起闸门与停止意图。
/// </summary>
public sealed class BackupRunControl(BackupJournalStore store, int configId, string runId) : IAsyncDisposable
{
    private BackupJournal? _journal;
    private int _accountId;
    private string _container = "";

    public string RunId => runId;

    /// <summary>
    /// 开卷。必须等编排器算出基线版本与寻址身份之后再调——这两样是恢复的前置条件，
    /// 写不进头里，这卷 journal 就没法安全复用。
    /// </summary>
    public async Task OpenJournalAsync(
        int accountId, string container, int baselineVersion, string localRoot, string encryptionIdentity,
        DateTimeOffset startedAt, CancellationToken ct)
    {
        _accountId = accountId;
        _container = container;
        _journal = await store.CreateAsync(accountId, container, runId, new JournalHeader
        {
            RunId = runId,
            ConfigId = configId,
            StartedAt = startedAt,
            BaselineVersion = baselineVersion,
            LocalRoot = localRoot,
            EncryptionIdentity = encryptionIdentity,
        }, ct);
    }

    /// <summary>记一个单文件 blob。**只能**在上传确认返回之后调。</summary>
    public async Task RecordBlobAsync(
        string path, string blobRef, string fullHash, string headHash, string tailHash, long length,
        int volumes, bool raw, IReadOnlyList<long> volumeSizes, CancellationToken ct)
    {
        if (_journal is null)
            return;
        await _journal.AppendAsync(new JournalRecord
        {
            Kind = "blob", Ref = blobRef, Path = path, FullHash = fullHash, HeadHash = headHash,
            TailHash = tailHash, Length = length, Volumes = volumes, Raw = raw, VolumeSizes = volumeSizes,
        }, ct);
    }

    /// <summary>记一个 pack。同样**只能**在上传确认返回之后调。</summary>
    public async Task RecordPackAsync(
        string packId, IReadOnlyList<JournalMember> members, IReadOnlyList<long> volumeSizes, bool storeOnly,
        CancellationToken ct)
    {
        if (_journal is null)
            return;
        await _journal.AppendAsync(new JournalRecord
        {
            Kind = "pack", Ref = packId, Members = members, VolumeSizes = volumeSizes,
            Volumes = Math.Max(1, volumeSizes.Count), StoreOnly = storeOnly,
        }, ct);
    }

    public async Task FlushAsync(bool fsync, CancellationToken ct)
    {
        if (_journal is not null)
            await _journal.FlushAsync(fsync, ct);
    }

    /// <summary>
    /// 运行成功收尾：索引已提交，journal 就没用了。
    /// 必须在信息文件提交**之后**、保留清理**之前**删——顺序反了，
    /// 清理会看到"既不被索引引用、也不被 journal 引用"的空档，把刚传上去的内容删掉。
    /// </summary>
    public async Task CompleteAsync()
    {
        if (_journal is null)
            return;
        await _journal.DisposeAsync();
        _journal = null;
        store.Delete(_accountId, _container, runId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_journal is not null)
            await _journal.DisposeAsync();
        _journal = null;
    }
}
```

- [ ] **Step 4: 编排器接上 control（签名 + 开卷 + 收尾）**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`：

1) `RunAsync` 与 `RunCoreAsync` 各加一个末位可选参数 `BackupRunControl? control = null`，并在 `RunAsync` 里把它透传给 `RunCoreAsync`。

2) 在 `var localResolver = LocalDedupResolver.Build(addressing, indexes);` 这一行**之后**加开卷：

```csharp
        // journal 开卷：基线版本与寻址身份到这里才齐。恢复时靠这两样判断"这卷还作不作数"。
        if (control is not null)
            await control.OpenJournalAsync(
                request.Account.Id, request.Container, lastVer ?? 0, request.LocalRoot, addressing.Identity,
                startedAt, ct);
```

3) 在第 8 步 `await indexCache.PutAsync(...)` 之后、第 10 步 `cleaner.CleanupAsync(...)` 之前加收尾：

```csharp
        // 索引已提交，journal 使命完成。必须删在清理之前：留着它，清理会以为这些内容还"在途"而不敢动；
        // 删得比信息文件提交还早，则会出现两边都不认的空档，刚传上去的内容会被当成孤儿删掉。
        if (control is not null)
            await control.CompleteAsync();
```

- [ ] **Step 5: 单文件 blob 的写入点**

编辑 `HandleBlobAsync`：签名末尾（`CancellationToken ct` 之前）加 `BackupRunControl? control,`；在 `tailByPath[file.Path] = content.TailHash;` 与 `await LogFileAsync(...)` 之间插入：

```csharp
        // journal：上传（或 if-missing 命中）已经确认返回，这块内容此刻确实在云上了，现在才敢记。
        // 顺序不能动——先记后传就会记下一块并不存在的内容，下次恢复直接跳过它，那是数据丢失。
        if (control is not null)
            await control.RecordBlobAsync(
                file.Path, placement.Ref, content.FullHash, content.HeadHash, content.TailHash, content.Length,
                Math.Max(1, placement.Volumes), content.Raw, [.. placement.VolumeSizes], ct);
```

`ConsumeAsync` 里调用 `HandleBlobAsync` 的那一处补上实参 `control`。

- [ ] **Step 6: pack 的写入点**

`RecordPack` 有两个调用点（稳定包、剔除变化成员后的重压包），所以把写入放进它自己，别在外面抄两遍。把 `RecordPack` 改成：

```csharp
    private static async Task RecordPackAsync(
        BackupRequest request, string packId, IReadOnlyList<PackEntry> members, IReadOnlyList<long> volumeSizes,
        bool storeOnly, BackupInfoFile info, ConcurrentDictionary<string, StorageRef> storageByPath,
        BackupRunControl? control, CancellationToken ct)
```

方法体保持原样（写 `storageByPath`、组装 `PackInfo`、`lock (info.Packs)`），在末尾追加：

```csharp
        // journal：pack 已经传完确认。成员表要记全，恢复时得靠它重建 PackInfo——
        // 信息文件是最后才提交的，崩溃时它里面根本没有这个包。
        if (control is not null)
            await control.RecordPackAsync(
                packId,
                [.. members.Select(m => new JournalMember(m.Path, m.EntryName, m.FullHash, m.Length))],
                volumeSizes, storeOnly, ct);
```

两个调用点改成 `await RecordPackAsync(request, packId, members, vols, storeOnly, info, storageByPath, control, ct);` 和 `await RecordPackAsync(request, packId, stable, vols2, storeOnly, info, storageByPath, control, ct);`。`ProcessPackAsync` 签名同样加 `BackupRunControl? control,`，`ConsumeAsync` 的调用点补实参。

- [ ] **Step 7: BackupRunner 造 control**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs` 的 `RunCoreAsync`，把调用编排器那一行包起来：

```csharp
        await using var control = new BackupRunControl(
            sp.GetRequiredService<BackupJournalStore>(), configId, state.RunId);
        var result = await sp.GetRequiredService<BackupOrchestrator>().RunAsync(
            BackupRequestMapper.From(config, account, password, settings, sp.GetService<PackLimits>()),
            new StateProgress(state), ct, control);
```

并给 `BackupRunState` 加一个 runId（Task 8 还要用它做前端展示）：

```csharp
    /// <summary>这一次运行的标识。journal 文件名就是它，恢复时按它对上号。</summary>
    public string RunId { get; } = Guid.NewGuid().ToString("N")[..12];
```

- [ ] **Step 8: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~BackupJournalWriteTests
```

Expected: PASS，2 个用例（Azurite + 7z 在位时）。

- [ ] **Step 9: 跑全量后端测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿，且没有既有用例被新参数破坏（`control` 是可选参数，老调用点不动）。

- [ ] **Step 10: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupJournalWriteTests.cs
git commit -m "feat(backup): journal every block after its upload is confirmed

Write-only for now: no behaviour changes, no reads. Records land after
the upload call returns, never before, so a journal line always means the
content is really in the cloud. The journal is deleted once the index is
committed and before retention cleanup runs, so neither side ever sees a
window where fresh content looks orphaned.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 6: 挂起闸门

一个纯内存的小状态机，不碰云也不碰盘，所以整块能用毫秒级的真延迟做单测（不引 `TimeProvider.Testing` 依赖）。

语义：第一个撞上瞬时错误的工作者**开闸门**（记下原因、起自愈计时器），后到的工作者一起等同一个信号。计时器到点、或用户点 `Retry now`，闸门放行，所有等待者一起重试。任何一个工作者干成一件活就 `ReportSuccess()`，失败计数清零——网络显然是通的，不该因为一个倒霉文件把整轮判死。连续出事超过耐心阈值则 `Downgrade()`，`WaitAsync` 返回 `false`，调用方据此走挂起退出。

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/PauseGate.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/PauseGateTests.cs`

**Interfaces:**
- Produces:
  - `sealed record PauseInfo(string Reason, DateTimeOffset Since, DateTimeOffset? NextRetryAt, int Failures)`
  - `sealed class PauseGate : IDisposable` — 构造 `PauseGate(IReadOnlyList<TimeSpan>? schedule = null, TimeSpan? steady = null, TimeSpan? patience = null)`；`PauseInfo? Current`；`bool IsDowngraded`；`Task<bool> WaitAsync(Exception cause, CancellationToken ct)`；`void ReleaseNow()`；`void ReportSuccess()`；`void Downgrade()`

- [ ] **Step 1: 写失败的测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/PauseGateTests.cs`：

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class PauseGateTests
{
    private static PauseGate Fast(TimeSpan? patience = null) => new(
        schedule: [TimeSpan.FromMilliseconds(10)],
        steady: TimeSpan.FromMilliseconds(10),
        patience: patience ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Self_heal_timer_releases_the_waiter()
    {
        using var gate = Fast();
        Assert.True(await gate.WaitAsync(new IOException("blip"), default));
        Assert.Null(gate.Current);
    }

    [Fact]
    public async Task Exposes_why_it_is_paused_while_waiting()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromSeconds(30)], steady: TimeSpan.FromSeconds(30),
            patience: TimeSpan.FromMinutes(10));
        var waiting = gate.WaitAsync(new IOException("network down"), default);

        // 等它把状态立起来（开闸是同步做的，但等待者还没跑到 await）
        for (var i = 0; i < 200 && gate.Current is null; i++)
            await Task.Delay(5);

        Assert.Equal("network down", gate.Current!.Reason);
        Assert.Equal(1, gate.Current.Failures);
        Assert.NotNull(gate.Current.NextRetryAt);

        gate.ReleaseNow();
        Assert.True(await waiting);
    }

    [Fact]
    public async Task Manual_push_releases_immediately()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMinutes(5)], steady: TimeSpan.FromMinutes(5),
            patience: TimeSpan.FromHours(1));
        var waiting = gate.WaitAsync(new IOException("blip"), default);
        gate.ReleaseNow();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task All_waiters_are_released_together()
    {
        using var gate = Fast();
        var a = gate.WaitAsync(new IOException("blip"), default);
        var b = gate.WaitAsync(new IOException("blip"), default);
        var c = gate.WaitAsync(new IOException("blip"), default);
        Assert.Equal([true, true, true], await Task.WhenAll(a, b, c));
    }

    // 耐心用尽 -> 降级。调用方据此走挂起退出，而不是继续傻等。
    [Fact]
    public async Task Downgrades_when_patience_runs_out()
    {
        using var gate = Fast(patience: TimeSpan.Zero);
        Assert.False(await gate.WaitAsync(new IOException("blip"), default));
        Assert.True(gate.IsDowngraded);
    }

    [Fact]
    public async Task Downgraded_gate_never_waits_again()
    {
        using var gate = Fast();
        gate.Downgrade();
        Assert.False(await gate.WaitAsync(new IOException("blip"), default));
    }

    // 别的工作者干成了活 -> 网络显然是通的 -> 失败计数清零，退避从头来，耐心也重新计时。
    [Fact]
    public async Task Success_resets_the_failure_count()
    {
        using var gate = Fast();
        Assert.True(await gate.WaitAsync(new IOException("blip"), default));
        Assert.True(await gate.WaitAsync(new IOException("blip"), default));

        gate.ReportSuccess();

        var waiting = gate.WaitAsync(new IOException("blip"), default);
        for (var i = 0; i < 200 && gate.Current is null; i++)
            await Task.Delay(1);
        // 计数清零之后这一次算"第一次出事"
        Assert.True(gate.Current is null || gate.Current.Failures == 1);
        Assert.True(await waiting);
    }

    // 用户按了取消：取消永远赢，闸门不能把它吞掉。
    [Fact]
    public async Task User_cancellation_wins_over_waiting()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMinutes(5)], steady: TimeSpan.FromMinutes(5),
            patience: TimeSpan.FromHours(1));
        using var cts = new CancellationTokenSource();
        var waiting = gate.WaitAsync(new IOException("blip"), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    // 5 分钟的定时器不能比运行活得还久。
    [Fact]
    public void Dispose_kills_the_pending_timer()
    {
        var gate = new PauseGate(
            schedule: [TimeSpan.FromMinutes(5)], steady: TimeSpan.FromMinutes(5),
            patience: TimeSpan.FromHours(1));
        _ = gate.WaitAsync(new IOException("blip"), default);
        gate.Dispose();
        Assert.True(gate.IsDowngraded);
    }
}
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~PauseGateTests
```

Expected: 编译失败，`The name 'PauseGate' does not exist`。

- [ ] **Step 3: 写实现**

新建 `backend/src/AzureStorageBackup.Api/Services/PauseGate.cs`：

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>挂起中的现场，给前端看的。</summary>
/// <param name="Reason">触发挂起的那条错误消息。</param>
/// <param name="Since">这一轮挂起是什么时候开始的。</param>
/// <param name="NextRetryAt">自愈计时器下一次放行的时刻。</param>
/// <param name="Failures">连续第几次出事（成功一次即清零）。</param>
public sealed record PauseInfo(string Reason, DateTimeOffset Since, DateTimeOffset? NextRetryAt, int Failures);

/// <summary>
/// 瞬时错误的挂起闸门。撞上网络/云端抖动的工作者在这里原地等，而不是把整轮备份判死。
/// <para>
/// 第一个出事的工作者开闸门并起自愈计时器；后到的一起等同一个信号。计时器到点、
/// 或用户点 <c>Retry now</c>，所有等待者一起放行重试。
/// </para>
/// <para>
/// <see cref="ReportSuccess"/> 是关键的一味：只要还有工作者在正常干活，网络就是通的，
/// 失败计数与耐心计时一并清零。否则一个始终传不上去的倒霉文件会把整轮好端端的备份拖去降级。
/// </para>
/// <para>
/// 耐心用尽则降级：<see cref="WaitAsync"/> 返回 false，调用方据此走"挂起退出"——
/// 落盘 journal、放掉暂存席位与产出锁。不这么做，一个挂起的运行会一直占着全局暂存额度，
/// 把并行的其它备份**完全**卡死（StagingArea 的额度闸门是全局的，不分席位）。
/// </para>
/// </summary>
public sealed class PauseGate : IDisposable
{
    private static readonly TimeSpan[] DefaultSchedule =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private readonly IReadOnlyList<TimeSpan> _schedule;
    private readonly TimeSpan _steady;
    private readonly TimeSpan _patience;
    private readonly Lock _lock = new();

    /// <summary>整个闸门的寿命。挂着的 5 分钟 Task.Delay 绝不能比运行活得还久。</summary>
    private readonly CancellationTokenSource _life = new();

    private TaskCompletionSource<bool>? _release;   // 非 null = 此刻正挂着
    private CancellationTokenSource? _timer;
    private int _failures;
    private DateTimeOffset? _troubleSince;          // null = 眼下没在出事（成功清零）
    private PauseInfo? _current;
    private bool _downgraded;

    public PauseGate(
        IReadOnlyList<TimeSpan>? schedule = null, TimeSpan? steady = null, TimeSpan? patience = null)
    {
        _schedule = schedule is { Count: > 0 } ? schedule : DefaultSchedule;
        _steady = steady ?? TimeSpan.FromMinutes(5);
        _patience = patience ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>此刻的挂起现场；没挂着就是 null。</summary>
    public PauseInfo? Current { get { lock (_lock) return _current; } }

    public bool IsDowngraded { get { lock (_lock) return _downgraded; } }

    /// <summary>
    /// 在闸门前等。
    /// </summary>
    /// <returns>true = 放行，去重试；false = 已降级，调用方该走挂起退出了。</returns>
    /// <exception cref="OperationCanceledException">用户取消了运行。取消永远赢。</exception>
    public async Task<bool> WaitAsync(Exception cause, CancellationToken ct)
    {
        Task<bool> release;
        lock (_lock)
        {
            if (_downgraded)
                return false;
            release = _release?.Task ?? OpenLocked(cause);
        }
        return await release.WaitAsync(ct);
    }

    /// <summary>用户点了 <c>Retry now</c>：不等计时器，现在就放，并当作重新开始（退避与耐心一并归零）。</summary>
    public void ReleaseNow()
    {
        lock (_lock)
        {
            _failures = 0;
            _troubleSince = null;
            ReleaseLocked(true);
        }
    }

    /// <summary>有工作者干成了一件活。网络是通的，把失败计数与耐心计时清零。</summary>
    public void ReportSuccess()
    {
        lock (_lock)
        {
            _failures = 0;
            _troubleSince = null;
        }
    }

    /// <summary>降级：用户点了 Suspend，或耐心用尽。所有等待者收到 false。</summary>
    public void Downgrade()
    {
        lock (_lock)
            DowngradeLocked();
    }

    private Task<bool> OpenLocked(Exception cause)
    {
        var now = DateTimeOffset.UtcNow;
        _troubleSince ??= now;
        _failures++;

        if (now - _troubleSince.Value >= _patience)
        {
            DowngradeLocked();
            return Task.FromResult(false);
        }

        var delay = DelayFor(_failures);
        _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _current = new PauseInfo(cause.Message, now, now + delay, _failures);
        _timer = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);

        var token = _timer.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) { return; }   // 提前放行 / 降级 / 闸门没了
            lock (_lock)
            {
                // 到点了先问一句：这一轮麻烦持续得是不是已经超过耐心了？
                // 只在开闸时判是不够的——最后一次退避可能长达 5 分钟。
                if (_troubleSince is { } since && DateTimeOffset.UtcNow - since >= _patience)
                    DowngradeLocked();
                else
                    ReleaseLocked(true);
            }
        }, CancellationToken.None);

        return _release.Task;
    }

    /// <summary>退避表用完之后按固定间隔继续，别无限翻倍成几个小时。</summary>
    private TimeSpan DelayFor(int failures)
        => failures <= _schedule.Count ? _schedule[failures - 1] : _steady;

    private void ReleaseLocked(bool proceed)
    {
        _timer?.Cancel();
        _timer?.Dispose();
        _timer = null;
        _current = null;
        var tcs = _release;
        _release = null;
        tcs?.TrySetResult(proceed);
    }

    private void DowngradeLocked()
    {
        _downgraded = true;
        ReleaseLocked(false);
    }

    public void Dispose()
    {
        lock (_lock)
            DowngradeLocked();
        _life.Cancel();
        _life.Dispose();
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~PauseGateTests
```

Expected: PASS，9 个用例全绿。（若目标框架不支持 `System.Threading.Lock`，把 `private readonly Lock _lock = new();` 换成 `private readonly object _lock = new();`。）

- [ ] **Step 5: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/PauseGate.cs \
        backend/tests/AzureStorageBackup.Api.Tests/PauseGateTests.cs
git commit -m "feat(backup): add pause gate with self-healing retry

Workers that hit a transient error park here instead of failing the run.
The first one opens the gate and starts a 30s/1m/5m/every-5m timer; the
rest join the same signal. Any worker completing an item resets the
failure count, so one unlucky file cannot drag a healthy run down. Once
patience runs out the gate downgrades and tells its waiters to suspend.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 7: 闸门接进编排器

把闸门装到消费循环上：一件活撞上瞬时错误就在闸门前等，放行了原样重试，降级了抛 `BackupSuspendedException`。

重试整件活是安全的：单文件 blob 路径每次都从头读、压、暂存（`PlaceBlobAsync` 的 `finally` 会释放上一次的暂存物），pack 路径复用同一个 `packId` 且分卷是逐卷 if-missing 上传，重传只会把缺的卷补齐。journal 里可能因此出现重复行——无害，恢复查表是按内容对号，重复只是多一次相同的命中。

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/BackupSuspendedException.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs`（加 `Gate`）
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`（`ConsumeAsync`）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupPauseGateIntegrationTests.cs`

**Interfaces:**
- Consumes: `PauseGate`, `PauseInfo`（Task 6）、`TransientErrors`（Task 1）、`BackupRunControl`（Task 5）
- Produces:
  - `enum SuspendReason { UserRequested, AutoSuspended }`（设计稿里的第三个值 `Crashed` 故意不要，理由见 Step 3）
  - `sealed class BackupSuspendedException(SuspendReason reason, string message) : Exception(message)` — `SuspendReason Reason { get; }`
  - `PauseGate BackupRunControl.Gate { get; }`

- [ ] **Step 1: 写失败的测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/BackupPauseGateIntegrationTests.cs`：

```csharp
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupPauseGateIntegrationTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupPauseGateIntegrationTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-gate-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
        _journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 42,
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private void WriteBytes(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
    }

    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory) Build(IBlobUploader uploader)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"),
            staging);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
        return (orchestrator, factory);
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 } },
    };

    /// <summary>头 N 次上传抛瞬时错误，之后放行。用来验证"抖一下会自愈，不该判死"。</summary>
    private sealed class FlakyUploader(IBlobUploader inner, int failures) : IBlobUploader
    {
        private int _left = failures;

        public int Attempts { get; private set; }

        private void Gate()
        {
            Attempts++;
            if (Interlocked.Decrement(ref _left) >= 0)
                throw new AggregateException("Retry failed after 6 tries.", new TaskCanceledException("timeout"));
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, CancellationToken ct)
        {
            Gate();
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, metadata, options, ct);
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, IProgress<long>? progress, CancellationToken ct)
        {
            Gate();
            return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, metadata, options, progress, ct);
        }

        public Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, Azure.Storage.Blobs.Models.AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, CancellationToken ct)
        {
            Gate();
            return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, metadata, options, ct);
        }
    }

    [SkippableFact]
    public async Task Transient_failure_pauses_then_heals()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("gate");
        var flaky = new FlakyUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), failures: 1);
        var (orchestrator, factory) = Build(flaky);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big.bin", 6_000_000);
            await using var control = new BackupRunControl(_journals, 5, "run-heal", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
                patience: TimeSpan.FromMinutes(5)));

            var result = await orchestrator.RunAsync(Request(account, name), null, default, control);

            Assert.Equal(1, result.Version);
            Assert.True(flaky.Attempts >= 2);   // 抖了一次，重试了一次
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Patience_running_out_suspends_instead_of_failing()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("gate");
        var flaky = new FlakyUploader(new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), failures: 1000);
        var (orchestrator, factory) = Build(flaky);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big.bin", 6_000_000);
            await using var control = new BackupRunControl(_journals, 5, "run-susp", new PauseGate(
                schedule: [TimeSpan.FromMilliseconds(10)], steady: TimeSpan.FromMilliseconds(10),
                patience: TimeSpan.Zero));

            var ex = await Assert.ThrowsAsync<BackupSuspendedException>(
                () => orchestrator.RunAsync(Request(account, name), null, default, control));
            Assert.Equal(SuspendReason.AutoSuspended, ex.Reason);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~BackupPauseGateIntegrationTests
```

Expected: 编译失败，`BackupSuspendedException` 不存在、`BackupRunControl` 没有第 4 个构造参数。

- [ ] **Step 3: 写异常类型**

新建 `backend/src/AzureStorageBackup.Api/Services/BackupSuspendedException.cs`：

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>一次运行为什么会停在挂起态。</summary>
public enum SuspendReason
{
    /// <summary>用户主动点了 Suspend。</summary>
    UserRequested,

    /// <summary>瞬时错误持续超过耐心阈值，闸门降级。</summary>
    AutoSuspended,

    // 设计稿里还有第三个值 Crashed（进程被 kill / 断电）。这里**故意不要**：
    // 崩溃时没有任何代码在跑，没人能给自己写下一个 reason。那种运行是靠盘上还留着 journal
    // 认出来的，由 GET /{id}/interrupted 直接读目录得到（Task 12），不必在内存里伪造一条
    // 没有 Control、没有忙碌锁的 Suspended 记录——伪造出来的那条记录，每个碰它的分支都要
    // 额外记得"这一条是假的"。
}

/// <summary>
/// "这轮没做完，但现场保住了"。与失败的区别很实在：失败是终点，挂起是可以接着跑的中点，
/// 所以它不能走 <c>RunStatus.Failed</c> 那条路——否则用户看到的是一个红字终局，
/// 而 journal 里其实躺着一整轮已经传上去的内容。
/// </summary>
public sealed class BackupSuspendedException(SuspendReason reason, string message) : Exception(message)
{
    public SuspendReason Reason { get; } = reason;
}
```

- [ ] **Step 4: 给 BackupRunControl 装上闸门**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs`，把类声明改成：

```csharp
public sealed class BackupRunControl(
    BackupJournalStore store, int configId, string runId, PauseGate? gate = null) : IAsyncDisposable
{
    /// <summary>瞬时错误的挂起闸门。默认 30s/1m/5m/每 5m 自愈，10 分钟不见好就降级。</summary>
    public PauseGate Gate { get; } = gate ?? new PauseGate();
```

并在 `DisposeAsync` 里补一句 `Gate.Dispose();`（挂着的 5 分钟计时器不能比运行活得久）：

```csharp
    public async ValueTask DisposeAsync()
    {
        Gate.Dispose();
        if (_journal is not null)
            await _journal.DisposeAsync();
        _journal = null;
    }
```

- [ ] **Step 5: 消费循环加闸门包装**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs` 的 `ConsumeAsync`，把 `try { if (item.Single is ...) ... else ... }` 那一段整个换成 `await WithPauseAsync(item, ct);`，并在 `ConsumeAsync` 的**外层**（即 `async Task ConsumeAsync()` 定义之前）加这个局部函数：

```csharp
        // 一件活撞上瞬时错误就在闸门前等，放行了原样重试。
        // 重试整件活是安全的：单文件路径每次从头读/压/暂存（PlaceBlobAsync 的 finally 会释放上一次的
        // 暂存物），pack 路径复用同一个 packId 且分卷逐卷 if-missing，重传只补缺的卷。
        // journal 因此可能出现重复行——无害，恢复是按内容对号，重复只是多命中一次。
        async Task WithPauseAsync(WorkItem item, CancellationToken token)
        {
            while (true)
            {
                try
                {
                    if (item.Single is { } single)
                        await HandleBlobAsync(request, single, addressing, localResolver, storageByPath, tailByPath,
                            overrides, postDiffUnreadable, uploadScope, ReportItem, uploadTracker, state, control, token);
                    else
                        await ProcessPackAsync(request, item.Pack!, item.StoreOnly, addressing, localResolver,
                            info, storageByPath, tailByPath, overrides, postDiffUnreadable, uploadScope, ReportItem,
                            uploadTracker, state, control, token);
                    control?.Gate.ReportSuccess();
                    return;
                }
                // 判据带上 token：用户按了取消时 OperationCanceledException 必须原样上抛，
                // 被闸门当成"网络抖了一下"吞掉的话，取消按钮就静悄悄失效了。
                catch (Exception ex) when (control is not null && TransientErrors.IsTransient(ex, token))
                {
                    if (!await control.Gate.WaitAsync(ex, token))
                        throw new BackupSuspendedException(SuspendReason.AutoSuspended, ex.Message);
                }
            }
        }
```

- [ ] **Step 6: 挂起不要报成失败**

`RunAsync` 顶层那个 `catch (Exception ex)` 会把**任何**异常报成 `BackupFailure` 并写一条 Error 日志。`BackupSuspendedException` 也是 `Exception`，照这样它会给用户推一条"Backup failed"——而现场明明好端端保着，下次跑就接上了。必须在它之前截住。

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`，在 `catch (Exception ex)` **之前**插入：

```csharp
        catch (BackupSuspendedException ex)
        {
            // 走 BackupFailure 这个订阅频道，但级别降为 Warning：
            // 频道选它，是因为订阅"备份没跑完"的人要的正是这条消息，而为此新增一个通知事件位
            // 意味着所有已有用户默认都收不到——一个只在出事那天才发现的静默默认值。
            // 级别降下来，是因为这不是错误：Error 会让它长存进审计日志、在界面上顶着红字，
            // 而它其实是一个可以接着跑的中点。措辞里把"接下来该做什么"直说。
            await Record(NotificationEvents.BackupFailure, source, $"Backup suspended: {request.Name}",
                $"{ex.Message} Progress is saved; run this backup again to pick up where it stopped.",
                ct, OperationLogLevel.Warning);
            throw;
        }
```

并给 `Record` 加一个可选的级别覆盖（默认仍按事件推导）：

```csharp
    private async Task Record(
        NotificationEvents evt, string source, string title, string body, CancellationToken ct,
        OperationLogLevel? level = null)
    {
        await _recordGate.WaitAsync(ct);
        try
        {
            if (opLog is not null)
                await opLog.AppendAsync(level ?? EventLog.LevelOf(evt), source, $"{title} — {body}", ct, durable: true);
            if (notifier is not null)
                await notifier.NotifyAsync(evt, title, body, ct);
        }
        finally
        {
            _recordGate.Release();
        }
    }
```

在 `BackupPauseGateIntegrationTests` 里补一条：自动降级那个用例跑完后，断言操作日志里那条记录不是 Error。

```csharp
    // 挂起不是失败。报成 Error 会让这份备份在界面上顶着红字，还要手动 Reset 才消——
    // 而现场明明保着，下次跑就接上了。
    [SkippableFact]
    public async Task Auto_suspend_is_logged_as_a_warning_not_an_error()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var log = new RecordingOperationLog();
        await Assert.ThrowsAsync<BackupSuspendedException>(
            () => RunWithAlwaysFailingUploadAsync(log));

        var suspended = Assert.Single(log.Entries, e => e.Message.Contains("Backup suspended"));
        Assert.Equal(OperationLogLevel.Warning, suspended.Level);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("Backup failed"));
    }
```

（`RecordingOperationLog` 是一个把 `AppendAsync` 收进 `List<(OperationLogLevel Level, string Message)>` 的 `IOperationLog` 替身；`RunWithAlwaysFailingUploadAsync` 复用本文件 Step 1 里那个总是抛 `TaskCanceledException` 的上传替身，把耐心阈值设成 0 让它一撞墙就降级。两者都写在本测试类里。）

- [ ] **Step 7: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~BackupPauseGateIntegrationTests
```

Expected: PASS，3 个用例。

- [ ] **Step 8: 跑全量后端测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿。没传 control 的老调用点行为不变（`control is not null` 是 catch filter 的第一个条件）。

- [ ] **Step 9: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupSuspendedException.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupPauseGateIntegrationTests.cs
git commit -m "feat(backup): park work items on the pause gate

A transient upload error now parks the item and retries it whole once the
gate releases, instead of failing the run. User cancellation still wins:
the transient predicate takes the token, so a cancelled OperationCanceled
propagates rather than being swallowed as a network blip. When the gate
downgrades, the run raises BackupSuspendedException.

That exception is caught above the general handler so a suspended run is
not announced as a failure: it goes out on the same subscription channel,
because whoever subscribed to "the backup did not finish" wants exactly
this message, but at warning level and with wording that says the
progress is saved and running again picks up from there.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 8: `Suspended` 终态 + 运行状态里的 `Pause`

两件事必须分清，否则会踩到用户明确点名的那颗雷（"挂起状态时不要被后续的计划任务打断"）：

- **`Pause` 是 `Running` 的子状态**，不是新的 `RunStatus` 值。挂起等待时运行**仍在跑**，调度器看到"在跑"就不会再起一轮。把它做成新的状态值，全后端 31 处 `RunStatus.Running` 比较和前端的 `while (run.status === 'Running')` 轮询循环都会当它已经结束。
- **`Suspended` 是新的终态**：Task 真的退出了，席位和产出锁都放掉了，journal 在盘上等着下次续。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs:113-122`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupRunStateTests.cs`

**Interfaces:**
- Consumes: `PauseInfo`（Task 6）、`SuspendReason`、`BackupSuspendedException`（Task 7）、`BackupRunControl`（Task 5/7）
- Produces:
  - `RunStatus.Suspended`
  - `BackupRunState.RunId`（Task 5 已加）、`BackupRunState.Pause`（转发 `Control?.Gate.Current`）、`BackupRunState.SuspendReason`、`internal BackupRunState.Control`
  - `BackupRunResponse(string Status, BackupProgress? Progress, int? Version, int? UnreadableFiles, string? Error, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string RunId, PauseInfo? Pause, string? SuspendReason)`
  - `bool BackupRunner.RetryNow(int configId)`

- [ ] **Step 1: 写失败的测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/BackupRunStateTests.cs`：

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class BackupRunStateTests
{
    [Fact]
    public void Response_carries_run_id_and_no_pause_by_default()
    {
        var state = new BackupRunState();
        var response = BackupRunResponse.From(state);

        Assert.Equal("Running", response.Status);
        Assert.False(string.IsNullOrEmpty(response.RunId));
        Assert.Null(response.Pause);
        Assert.Null(response.SuspendReason);
    }

    // 挂起等待时状态仍是 Running（子状态），否则调度器会以为这轮结束了，再起一轮把它顶掉。
    [Fact]
    public async Task Paused_run_is_still_reported_as_running()
    {
        var store = new BackupJournalStore(Path.Combine(Path.GetTempPath(), "asb-rs-" + Guid.NewGuid().ToString("N")));
        var gate = new PauseGate(
            schedule: [TimeSpan.FromMinutes(5)], steady: TimeSpan.FromMinutes(5), patience: TimeSpan.FromHours(1));
        await using var control = new BackupRunControl(store, 1, "run-x", gate);
        var state = new BackupRunState { Control = control };

        _ = gate.WaitAsync(new IOException("network down"), default);
        for (var i = 0; i < 200 && gate.Current is null; i++)
            await Task.Delay(5);

        var response = BackupRunResponse.From(state);
        Assert.Equal("Running", response.Status);
        Assert.Equal("network down", response.Pause!.Reason);
    }

    [Fact]
    public void Suspended_is_a_terminal_status_with_a_reason()
    {
        var state = new BackupRunState
        {
            Status = RunStatus.Suspended,
            SuspendReason = SuspendReason.AutoSuspended,
        };
        var response = BackupRunResponse.From(state);

        Assert.Equal("Suspended", response.Status);
        Assert.Equal("AutoSuspended", response.SuspendReason);
    }
}
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~BackupRunStateTests
```

Expected: 编译失败，`RunStatus` 没有 `Suspended`、`BackupRunState` 没有 `Control`/`SuspendReason`、`BackupRunResponse` 没有 `RunId`/`Pause`。

- [ ] **Step 3: 扩状态模型**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs`。

枚举加一个值（放在末尾，别插在中间——前端按名字比，但数据库里若有序列化过的整数会错位）：

```csharp
    /// <summary>
    /// 现场保住了，活没干完。与 Failed 的区别很实在：journal 还在盘上，下一轮（用户点 Resume
    /// 或下次计划任务）会把已经传上去的内容原样认下来，不重传。
    /// <para>
    /// 注意这**只**用于运行真的退出了的时刻。瞬时错误等待重试期间状态仍是 Running（见
    /// <see cref="BackupRunState.Pause"/>）——那时 Task 还活着、席位还占着，报成终态会让调度器
    /// 以为这轮完了，再起一轮把它顶掉。
    /// </para>
    /// </summary>
    Suspended,
```

`BackupRunState` 加：

```csharp
    /// <summary>这一次运行的标识。journal 文件名就是它，恢复时按它对上号。</summary>
    public string RunId { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>挂起（Suspended）的缘由；没挂起就是 null。</summary>
    public SuspendReason? SuspendReason { get; set; }

    /// <summary>内部机制，不进 HTTP 契约：这次运行的把手，Suspend / Retry now 要靠它够到闸门。</summary>
    internal BackupRunControl? Control { get; set; }

    /// <summary>
    /// 眼下是不是卡在瞬时错误上等重试。**这不是一个状态值**：Status 仍是 Running，
    /// 因为 Task 还活着、席位还占着，报成终态会让调度器再起一轮把它顶掉。
    /// </summary>
    public PauseInfo? Pause => Control?.Gate.Current;
```

`BackupRunResponse` 扩成：

```csharp
public sealed record BackupRunResponse(
    string Status, BackupProgress? Progress, int? Version, int? UnreadableFiles, string? Error,
    DateTimeOffset? StartedAt = null, DateTimeOffset? CompletedAt = null,
    string RunId = "", PauseInfo? Pause = null, string? SuspendReason = null)
{
    public static BackupRunResponse From(BackupRunState s) =>
        new(s.Status.ToString(), s.Progress, s.Version, s.UnreadableFiles, s.Error, s.StartedAt, s.CompletedAt,
            s.RunId, s.Pause, s.SuspendReason?.ToString());
}
```

- [ ] **Step 4: 运行器认下挂起**

在 `BackupRunner.RunCoreAsync` 里，Task 5 建好 control 之后立刻挂到状态上：

```csharp
        state.Control = control;
```

并在 `catch (OperationCanceledException)` **之前**插入新的 catch（顺序要紧：`BackupSuspendedException` 不是 `OperationCanceledException`，但把它放前面能让阅读顺序与优先级一致）：

```csharp
        catch (BackupSuspendedException ex)
        {
            // 不是失败：journal 还在盘上，Error 也不写（否则这份备份此后一直挂着红字，
            // 还要手动 Reset 才消），下一轮会把已传的内容原样认下来。
            state.Status = RunStatus.Suspended;
            state.SuspendReason = ex.Reason;
            // 和其它三个终态分支一样要放行等待者，否则 RunTrackedAsync 会一直挂在 Completion 上。
            state.Completion.TrySetResult();
        }
```

再加一个手动推一把的入口（放在 `Cancel` 旁边）：

```csharp
    /// <summary>用户点了 <c>Retry now</c>：不等自愈计时器，立刻放行重试。</summary>
    public bool RetryNow(int configId)
    {
        BackupRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running } || state.Pause is null)
            return false;
        state.Control!.Gate.ReleaseNow();
        return true;
    }
```

- [ ] **Step 5: 调度器认下挂起**

编辑 `backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs`，在既有的 Failed/Canceled 判断旁边加：

```csharp
        // 挂起不是失败：抛异常会给这次计划任务记一笔红色错误，而现场其实好端端保着，
        // 下一轮会接着跑。与 Canceled 同等处置：安静收场。
        if (backupState.Status == RunStatus.Suspended)
            return;
```

- [ ] **Step 6: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~BackupRunStateTests
```

Expected: PASS，3 个用例。

- [ ] **Step 7: 跑全量后端测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿。`BackupRunResponse` 的新参数都有默认值，既有构造点不受影响。

- [ ] **Step 8: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs \
        backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupRunStateTests.cs
git commit -m "feat(backup): add Suspended terminal status and Pause sub-state

Pause is deliberately not a RunStatus value. While a run waits out a
transient error its task is alive and its staging seat is held, so it
must keep reporting Running or the scheduler would treat it as finished
and start another round on top of it. Suspended is a real terminal state
for when the task has actually exited with its journal on disk.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 9: 主动暂停与两种取消

三种停法，区别只在两件事上：**在途的活等不等它做完**、**已经传上去的东西留不留**。

| 停法 | 在途的活 | 已经传完的块 | 结局 |
| --- | --- | --- | --- |
| `Suspend` | 做完当前这件，再停 | 留（journal 保着） | `Suspended` |
| `Cancel / Finish current files` | 做完当前这件（含它的全部分卷），再停 | 留（journal 保着，下次备份复用） | `Canceled` |
| `Cancel / Stop now` | 立刻中断 | 完整传完的留；**在途那个文件的全部残留卷删掉** | `Canceled` |

两个取消令牌，别混：`StopToken`（任何停法都触发）只用来叫停 diff——继续读盘没有意义；`AbortToken`（只有 `Stop now` 触发）才打断在途上传。`Suspend` 与 `Finish current files` 绝不能碰 `AbortToken`，否则"做完当前这件再停"这句承诺就是假的。

用户要求 **Cancel 要等落盘成功再返回**，所以端点侧是 `await state.Completion.Task`。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupCancelModesTests.cs`

**Interfaces:**
- Consumes: `BackupSuspendedException`, `SuspendReason`（Task 7）
- Produces:
  - `enum StopKind { None, Suspend, FinishCurrentFiles, StopNow }`
  - `BackupRunControl`：`StopKind Stop { get; }`、`CancellationToken StopToken`、`CancellationToken AbortToken`、`void RequestStop(StopKind kind)`、`void TrackInFlight(string blobRef)`、`void ClearInFlight(string blobRef)`、`IReadOnlyCollection<string> InFlight`
  - `BackupRunner`：`Task<bool> SuspendAsync(int configId, CancellationToken ct = default)`、`Task<bool> CancelAsync(int configId, bool finishCurrentFiles, CancellationToken ct = default)`

- [ ] **Step 1: 写失败的测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/BackupCancelModesTests.cs`：

```csharp
using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupCancelModesTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupCancelModesTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-stop-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
        _journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 43,
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private void WriteBytes(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
    }

    private (BackupOrchestrator Orchestrator, BlobClientFactory Factory) Build(IBlobUploader uploader)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"),
            staging);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
        return (orchestrator, factory);
    }

    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = null,
        Options = new BackupEngineOptions
        {
            UploadConcurrency = 1,
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    /// <summary>第 N 次上传时按指定停法叫停；<paramref name="thenThrow"/> 用来模拟"在途被打断"。</summary>
    private sealed class StopAt(IBlobUploader inner, int at, Func<StopKind> stop, bool thenThrow) : IBlobUploader
    {
        private int _count;

        private async Task<T> RunAsync<T>(Func<Task<T>> call)
        {
            var n = Interlocked.Increment(ref _count);
            var result = await call();
            if (n == at)
            {
                stop();
                if (thenThrow)
                    throw new OperationCanceledException("aborted mid-flight");
            }
            return result;
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, CancellationToken ct)
            => RunAsync(() => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, metadata, options, ct));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, IProgress<long>? progress, CancellationToken ct)
            => RunAsync(() => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, metadata, options, progress, ct));

        public async Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, CancellationToken ct)
            => await RunAsync<bool>(async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, metadata, options, ct);
                return true;
            });
    }

    private static async Task<List<string>> DataBlobsAsync(Azure.Storage.Blobs.BlobContainerClient container)
    {
        var names = new List<string>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, "data/"))
            names.Add(b.Name);
        return names;
    }

    [SkippableFact]
    public async Task Suspend_keeps_the_journal_and_ends_as_suspended()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        BackupRunControl? control = null;
        var uploader = new StopAt(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), at: 1,
            stop: () => { control!.RequestStop(StopKind.Suspend); return StopKind.Suspend; }, thenThrow: false);
        var (orchestrator, factory) = Build(uploader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            WriteBytes("big3.bin", 6_000_002);
            await using var c = new BackupRunControl(_journals, 8, "run-suspend");
            control = c;

            var ex = await Assert.ThrowsAsync<BackupSuspendedException>(
                () => orchestrator.RunAsync(Request(account, name), null, default, c));
            Assert.Equal(SuspendReason.UserRequested, ex.Reason);

            // 第一件活是做完了的：journal 留着它，云上也留着它。
            var journal = Assert.Single(await _journals.ListAsync(account.Id, name, default));
            Assert.NotEmpty(journal.Content.Records);
            Assert.NotEmpty(await DataBlobsAsync(container));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Stop_now_deletes_the_in_flight_residue_but_keeps_finished_blocks()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("stop");
        BackupRunControl? control = null;
        // 第 2 次上传：传上去了，但随即"在途中断"——上传确认没能返回，所以它没进 journal，
        // 在途登记也没销。这正是 Stop now 要清掉的那种残留。
        var uploader = new StopAt(
            new BlobUploader(new BlobClientFactory(TestSecrets.Reader)), at: 2,
            stop: () => { control!.RequestStop(StopKind.StopNow); return StopKind.StopNow; }, thenThrow: true);
        var (orchestrator, factory) = Build(uploader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("big1.bin", 6_000_000);
            WriteBytes("big2.bin", 6_000_001);
            WriteBytes("big3.bin", 6_000_002);
            await using var c = new BackupRunControl(_journals, 8, "run-stopnow");
            control = c;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => orchestrator.RunAsync(Request(account, name), null, default, c));

            var journal = Assert.Single(await _journals.ListAsync(account.Id, name, default));
            var kept = Assert.Single(journal.Content.Records);       // 只有第一件确认完成
            var blobs = await DataBlobsAsync(container);
            // 完整传完的留着（下次复用），在途那个的残留被删干净。
            Assert.Equal([kept.Ref], blobs);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~BackupCancelModesTests
```

Expected: 编译失败，`StopKind` 不存在、`BackupRunControl` 没有 `RequestStop`。

- [ ] **Step 3: 给 BackupRunControl 加停止意图与在途登记**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs`。文件顶部（命名空间之下）加：

```csharp
/// <summary>怎么个停法。</summary>
public enum StopKind
{
    None,

    /// <summary>主动暂停：做完手上这件，落盘，退出成 Suspended。</summary>
    Suspend,

    /// <summary>取消，但把正在上传的文件（含它的全部分卷）做完再停。</summary>
    FinishCurrentFiles,

    /// <summary>取消，立刻中断在途上传，并删掉它留下的残留卷。</summary>
    StopNow,
}
```

类里加：

```csharp
    /// <summary>任何停法都会触发：叫停 diff（继续读盘没有意义）。</summary>
    private readonly CancellationTokenSource _stop = new();

    /// <summary>**只有** Stop now 会触发：打断在途上传。
    /// Suspend 与 Finish current files 绝不能碰它，否则"做完当前这件再停"就是句空话。</summary>
    private readonly CancellationTokenSource _abort = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

    private int _stopKind;

    public StopKind Stop => (StopKind)Volatile.Read(ref _stopKind);
    public CancellationToken StopToken => _stop.Token;
    public CancellationToken AbortToken => _abort.Token;

    /// <summary>登记/销账"正在上传的这块内容"。Stop now 收尾时按它删残留卷。</summary>
    public void TrackInFlight(string blobRef) => _inFlight[blobRef] = 1;
    public void ClearInFlight(string blobRef) => _inFlight.TryRemove(blobRef, out _);
    public IReadOnlyCollection<string> InFlight => _inFlight.Keys.ToList();

    /// <summary>下达停止意愿。重复下达只认第一次（用户点了 Stop now 之后再点 Suspend 没有意义）。</summary>
    public void RequestStop(StopKind kind)
    {
        if (kind == StopKind.None)
            return;
        if (Interlocked.CompareExchange(ref _stopKind, (int)kind, (int)StopKind.None) != (int)StopKind.None)
            return;
        // 正卡在闸门上等重试的工作者要被叫醒，否则它们会一直等到下一次自愈计时器到点。
        Gate.Downgrade();
        _stop.Cancel();
        if (kind == StopKind.StopNow)
            _abort.Cancel();
    }
```

`DisposeAsync` 里补上 `_stop.Dispose(); _abort.Dispose();`。

- [ ] **Step 4: 编排器：两个令牌 + 停止收尾 + 残留清理**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`。

1) 把 `using var stopProducing = CancellationTokenSource.CreateLinkedTokenSource(ct);` 换成两条：

```csharp
        // 上传侧出错、或用户下达任何停法 → 叫停 diff。
        using var stopProducing = control is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, control.StopToken);
        // 消费者用的令牌：只有 Stop now 会打断在途上传。Suspend/Finish current files 走的是
        // "循环顶上检查一下就 break"，在途那件活照做完。
        using var working = control is null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : CancellationTokenSource.CreateLinkedTokenSource(ct, control.AbortToken);
```

2) `ConsumeAsync` 内所有用 `ct` 的地方换成 `working.Token`：`work.DequeueAsync(working.Token)`、`WithPauseAsync(item, working.Token)`；`StartConsumers` 里 `Task.Run(ConsumeAsync, working.Token)`。循环顶上加停止检查：

```csharp
                while (await work.DequeueAsync(working.Token) is { } item)
                {
                    // 还没开工的活，停下来之后就不做了。已经开工的那件不受影响——
                    // "做完当前这件再停"这句承诺就落在这个位置上。
                    if (control is { Stop: not StopKind.None })
                        break;
```

3) 在 `RunCoreAsync` 里（`ConsumeAsync` 定义附近）加停止收尾的局部函数：

```csharp
        // 停止收尾：journal 一律落盘（Cancel 也要落——已经传完的块留着给下一轮复用，
        // 这是用户明确要的），Stop now 还要把在途文件的残留卷删掉。
        // 全程用 CancellationToken.None：运行自己的令牌此刻多半已经触发，用它一句清理都做不下去。
        async Task<Exception> SettleStopAsync(StopKind kind)
        {
            if (kind == StopKind.StopNow)
                await PurgeInFlightAsync(request, control!);
            await control!.FlushAsync(fsync: true, CancellationToken.None);
            return kind == StopKind.Suspend
                ? new BackupSuspendedException(SuspendReason.UserRequested, "Suspended by user.")
                : new OperationCanceledException("Backup stopped by user.");
        }
```

4) 把 `catch (OperationCanceledException) when (stopProducing.IsCancellationRequested && !ct.IsCancellationRequested)` 分支的体改成：

```csharp
            // diff 是被叫停的：可能是上传侧失败，也可能是用户下达了停法。
            await SettleAsync(consumers);
            if (control is { Stop: var stopped } && stopped != StopKind.None)
                throw await SettleStopAsync(stopped);
            await Task.WhenAll(consumers);
            throw; // 消费者居然没抛：那就把这个取消交上去，绝不静默当成功
```

5) 把 `await Task.WhenAll(consumers);`（别名回填之前那一处）换成：

```csharp
        // 先把消费者收干净再看有没有停止意愿：停了就不能再往下写版本索引——
        // 一轮没跑完的备份写出一个版本，等于宣称那些没传的文件已经备份好了。
        await SettleAsync(consumers);
        if (control is { Stop: var stopKind } && stopKind != StopKind.None)
            throw await SettleStopAsync(stopKind);
        await Task.WhenAll(consumers);
```

6) 加残留清理方法（放在 `ClearLeftoverVolumesAsync` 旁边）：

```csharp
    /// <summary>
    /// Stop now 的收尾：把还挂在在途登记里的内容连同它的全部分卷删掉。
    /// 登记只在上传确认返回后才销账，所以留在里面的就是"传了一半、没人认得"的残留。
    /// 完整传完的块不在此列——它们留着给下一轮复用，这是用户明确要的。
    /// </summary>
    private async Task PurgeInFlightAsync(BackupRequest request, BackupRunControl control)
    {
        var container = factory.CreateServiceClient(request.Account).GetBlobContainerClient(request.Container);
        foreach (var blobRef in control.InFlight)
        {
            await foreach (var b in container.GetBlobsAsync(
                BlobTraits.None, BlobStates.None, blobRef, CancellationToken.None))
            {
                if (b.Name == blobRef || VolumeBlobIO.IsVolumeOf(blobRef, b.Name))
                    await container.GetBlobClient(b.Name).DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
            }
        }
    }
```

7) 在 `UploadStagedBlobAsync` 里，`ClearLeftoverVolumesAsync` 之后、`VolumeBlobIO.UploadAsync` 之前登记，上传确认返回之后销账：

```csharp
        control?.TrackInFlight(blobRef);
        var sizes = await VolumeBlobIO.UploadAsync(/* 原参数不变 */);
        // 确认返回了才销账。抛异常时故意**不**销：那份残留正是 Stop now 要清掉的东西。
        control?.ClearInFlight(blobRef);
```

`UploadStagedPackAsync` 同样处理，登记的名字用 `blobName`（即 `packs/{packId}.7z`）。两个方法的签名各加 `BackupRunControl? control`，调用点补实参。

- [ ] **Step 5: 运行器的三个入口**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs`，把 `Cancel` 改写并新增两个异步入口：

```csharp
    /// <summary>下达停止意愿。返回被叫停的运行，没有在跑则返回 null。</summary>
    private BackupRunState? RequestStop(int configId, StopKind kind)
    {
        BackupRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running })
            return null;
        if (state.Control is { } control)
            control.RequestStop(kind);
        else
            state.Cancellation.Cancel();   // 还没跑到建 control 那一步（解析配置阶段）
        return state;
    }

    /// <summary>立刻停（不等落盘）。保留给共用的 /cancel 端点与其它运行器同形。</summary>
    public bool Cancel(int configId) => RequestStop(configId, StopKind.StopNow) is not null;

    /// <summary>主动暂停：做完手上这件活，落盘，退出成 Suspended。等落盘完成才返回。</summary>
    public async Task<bool> SuspendAsync(int configId, CancellationToken ct = default)
    {
        if (RequestStop(configId, StopKind.Suspend) is not { } state)
            return false;
        await state.Completion.Task.WaitAsync(ct);
        return true;
    }

    /// <summary>取消。<paramref name="finishCurrentFiles"/> 为 true 时等在途文件（含其全部分卷）传完。
    /// 用户要求"Cancel 要等落盘成功再返回"，所以这里一定要等到终态。</summary>
    public async Task<bool> CancelAsync(int configId, bool finishCurrentFiles, CancellationToken ct = default)
    {
        var kind = finishCurrentFiles ? StopKind.FinishCurrentFiles : StopKind.StopNow;
        if (RequestStop(configId, kind) is not { } state)
            return false;
        await state.Completion.Task.WaitAsync(ct);
        return true;
    }
```

- [ ] **Step 6: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~BackupCancelModesTests
```

Expected: PASS，2 个用例。

- [ ] **Step 7: 跑全量后端测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿。

- [ ] **Step 8: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupCancelModesTests.cs
git commit -m "feat(backup): add suspend and two cancel modes

Suspend and 'finish current files' let the in-flight item complete; only
'stop now' aborts it, and only 'stop now' purges the residue that item
left behind. Blocks whose upload was confirmed stay in the container and
in the journal so the next run reuses them. Both cancel entry points wait
for the journal to reach disk before returning.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 10: 恢复——读 journal，跳过已经传上去的东西

到这里 journal 一直是只写不读的。本任务让它开始起作用。

**恢复不是一种特殊模式。** 用户点 `Resume`、计划任务到点、用户手动点 `Run`——走的是同一条路：开卷时看看这个容器上有没有还作数的旧 journal，有就接着用。这正是用户对 Cancel 的要求（"留着，下次备份自动复用"）。

**前置校验分三档，别混：**

| 情形 | 处置 | 为什么 |
| --- | --- | --- |
| 基线版本 / 本地根 / 寻址身份任一对不上 | **当场删掉** | 换了钥匙地址空间就变了，换了根目录路径就不是同一批文件，基线变了说明已经跑完一轮。里面的引用全对不上号，留着只会误导 |
| `ConfigId` 不是我们的 | **当场删掉** | `(AccountId, ContainerName)` 在 `AppDbContext` 里是唯一索引，一个容器至多一个配置——所以这只可能是"配置删了又在同一个容器上重建"留下的陈迹。留着它会永远保住那批块不被清理 |
| 三样全对上且 `ConfigId` 是我们的 | **采纳** | 记录并进查找表 |

> 上面第二条依赖那个唯一索引。哪天允许多个配置共用一个容器了，这一条必须改回"不是我们的就完全不碰"——否则会把别人挂起着的运行的成果变成孤儿。

**采纳是只读的**：本轮仍然新开一卷自己的 journal，被采纳的那些卷原样留着，等本轮**成功提交索引**时和自己那卷一起删。这样就不必把复用来的记录再抄写一遍，也不会出现"抄到一半又崩了"的半截状态。代价是反复挂起/恢复会攒下多卷 journal（每卷都很小，重复记录按"先命中者胜"处理）。

**匹配判据是路径 + 内容双对**：光凭路径不行——文件在中断之后完全可能被改过；光凭内容 hash 也不行——journal 是按路径记的，而同内容不同路径在索引里是两条不同的条目。

**单文件 blob 有两道坎。** `PlaceBlobAsync` 的第一步是"预筛"：只读文件头算 head hash，本地索引里连（长度 + head）都对不上就跳过探测直奔压缩。恢复时上一轮传上去的内容**还没进任何版本索引**，`localResolver` 根本认不出它——不把 journal 也接进预筛，整轮的活会一件不落地重做一遍。所以：预筛要带上 journal，探测出完整内容身份之后再用（路径 + 全文 hash + 长度 + 头尾 hash）做精确匹配。

**pack 要求成员集合逐一相同**（路径 + 全文 hash + 长度，按序）。宽松一点的部分匹配听起来更省，但 `entryName` 的编号（`0001_a.txt`）是跟着分组走的，成员对不上就会让索引指向箱里根本不存在的条目。分组本身是确定性的（同样的基线、同样的源、同样的界），所以严格相等在实际中命中率并不低；对不上就重压——一箱都是小文件，重压很便宜。

**别名表不能漏。** pack 命中 journal 之后仍然要走 `RecordPackAsync`（只是不上传）：本轮内跨箱去重的收尾靠 `storageByPath[leaderPath]` 判 leader 有没有走岔，命中就跳过等于让所有挂在这个 leader 上的别名全部悬空重跑。

**已知取舍（写出来，不要以为是 bug）：** 复用来的 blob 不回填进 `localResolver` 的跨版本映射。于是同一轮里**另一条路径**的相同内容仍会被重压一遍，靠上传时的 if-missing 兜住。这只费 CPU 不出错，和"内容不在任何索引里"的既有行为完全一致。

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/JournalResume.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs`（`OpenJournalAsync` 改为"采纳或作废"，`CompleteAsync` 删全部采纳卷）
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`（`PlaceBlobAsync`、`ProbeForDedupAsync`、`HandleBlobAsync`、`ProcessPackAsync`）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/JournalResumeTests.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupResumeTests.cs`

**Interfaces:**
- Consumes: `JournalRecord`、`JournalMember`、`BackupJournalStore.ListAsync`（Task 3/4）、`BackupRunControl`（Task 5）、`StopKind`（Task 9）
- Produces:
  - `sealed class JournalResume(IReadOnlyList<JournalRecord> records)` — `static readonly JournalResume Empty`；`bool IsEmpty`；`int RecordCount`；`bool MayResumeBlob(string path, long length, string headHash)`；`JournalRecord? FindBlob(string path, string fullHash, long length, string headHash, string tailHash)`；`JournalRecord? FindPack(IReadOnlyList<JournalMember> members)`
  - `BackupRunControl.Resume { get; }` → `JournalResume`

- [ ] **Step 1: 写 JournalResume 的失败测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/JournalResumeTests.cs`：

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class JournalResumeTests
{
    private static JournalRecord Blob(string path, string full) => new()
    {
        Kind = "blob", Ref = "data/" + full, Path = path, FullHash = full,
        HeadHash = "h" + full, TailHash = "t" + full, Length = 100, Volumes = 1, VolumeSizes = [100],
    };

    private static JournalRecord Pack(string packId, params JournalMember[] members) => new()
    {
        Kind = "pack", Ref = packId, Members = members, VolumeSizes = [500], Volumes = 1,
    };

    [Fact]
    public void Empty_resume_finds_nothing()
    {
        Assert.True(JournalResume.Empty.IsEmpty);
        Assert.False(JournalResume.Empty.MayResumeBlob("a.bin", 100, "haaa"));
        Assert.Null(JournalResume.Empty.FindBlob("a.bin", "aaa", 100, "haaa", "taaa"));
    }

    [Fact]
    public void Prescreen_matches_on_path_length_and_head()
    {
        var r = new JournalResume([Blob("a.bin", "aaa")]);
        Assert.True(r.MayResumeBlob("a.bin", 100, "haaa"));
        Assert.False(r.MayResumeBlob("b.bin", 100, "haaa"));   // 路径不同
        Assert.False(r.MayResumeBlob("a.bin", 101, "haaa"));   // 长度变了
        Assert.False(r.MayResumeBlob("a.bin", 100, "other"));  // 文件头变了
    }

    [Fact]
    public void Blob_needs_path_and_content_to_both_match()
    {
        var r = new JournalResume([Blob("a.bin", "aaa")]);
        Assert.Equal("data/aaa", r.FindBlob("a.bin", "aaa", 100, "haaa", "taaa")!.Ref);
        // 中断之后文件被改过：路径还在，内容不是那一份了，绝不能复用。
        Assert.Null(r.FindBlob("a.bin", "zzz", 100, "hzzz", "tzzz"));
        // 同内容不同路径：journal 是按路径记的，索引里这是两条条目。
        Assert.Null(r.FindBlob("copy.bin", "aaa", 100, "haaa", "taaa"));
    }

    [Fact]
    public void Pack_matches_only_on_the_exact_member_set()
    {
        var m1 = new JournalMember("a.txt", "0001_a.txt", "ha", 5);
        var m2 = new JournalMember("b.txt", "0002_b.txt", "hb", 7);
        var r = new JournalResume([Pack("p000000010001", m1, m2)]);

        Assert.Equal("p000000010001", r.FindPack([m1, m2])!.Ref);
        Assert.Null(r.FindPack([m1]));                                            // 少一个成员
        Assert.Null(r.FindPack([m1, m2, new JournalMember("c.txt", "0003_c.txt", "hc", 9)]));  // 多一个
        Assert.Null(r.FindPack([m1, m2 with { FullHash = "changed" }]));           // 成员内容变了
        Assert.Null(r.FindPack([m1, m2 with { Length = 8 }]));                     // 成员长度变了
    }

    [Fact]
    public void Duplicate_records_across_journals_take_the_first()
    {
        // 反复挂起/恢复会攒下多卷 journal，同一条路径可能被记过不止一次。
        var r = new JournalResume([Blob("a.bin", "aaa"), Blob("a.bin", "aaa")]);
        Assert.Equal(1, r.RecordCount);
        Assert.Equal("data/aaa", r.FindBlob("a.bin", "aaa", 100, "haaa", "taaa")!.Ref);
    }

    [Fact]
    public void Records_without_a_path_are_ignored()
    {
        // 头坏一半、字段缺失的行不该把查找表带崩。
        var r = new JournalResume([new JournalRecord { Kind = "blob", Ref = "data/x" }]);
        Assert.Null(r.FindBlob("x", "x", 1, "x", "x"));
    }
}
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~JournalResumeTests
```

Expected: 编译失败，`The name 'JournalResume' does not exist`。

- [ ] **Step 3: 写 JournalResume**

新建 `backend/src/AzureStorageBackup.Api/Services/JournalResume.cs`：

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 从若干卷还作数的 journal 里建起来的查找表：回答"这份内容上一轮是不是已经传上去了"。
/// <para>
/// 判据一律是**路径 + 内容双对**。光凭路径不行——中断之后文件完全可能被改过；光凭内容 hash
/// 也不行——journal 是按路径记的，同内容不同路径在索引里是两条不同的条目。
/// </para>
/// <para>
/// 纯内存、纯本地，不读云端。记录能进 journal 的前提就是"上传已经确认返回"，所以这里不需要
/// （也不应该）再去云上核对一次——那会违反"备份期间零云读"这条底线。
/// </para>
/// </summary>
public sealed class JournalResume(IReadOnlyList<JournalRecord> records)
{
    public static readonly JournalResume Empty = new([]);

    /// <summary>按路径索引的单文件 blob 记录。重复路径先命中者胜（多卷 journal 会有重复）。</summary>
    private readonly Dictionary<string, JournalRecord> _blobs = BuildBlobs(records);

    /// <summary>按成员集合的规范化键索引的 pack 记录。</summary>
    private readonly Dictionary<string, JournalRecord> _packs = BuildPacks(records);

    /// <summary>预筛用：(路径, 长度, head hash)。三样齐了才值得把整个文件读一遍算全文 hash。</summary>
    private readonly HashSet<string> _prescreen = BuildPrescreen(records);

    public bool IsEmpty => _blobs.Count == 0 && _packs.Count == 0;
    public int RecordCount => _blobs.Count + _packs.Count;

    private static Dictionary<string, JournalRecord> BuildBlobs(IReadOnlyList<JournalRecord> records)
    {
        var map = new Dictionary<string, JournalRecord>(StringComparer.Ordinal);
        foreach (var r in records)
            if (r.Kind == "blob" && r.Path is { } p && r.FullHash is not null)
                map.TryAdd(p, r);
        return map;
    }

    private static Dictionary<string, JournalRecord> BuildPacks(IReadOnlyList<JournalRecord> records)
    {
        var map = new Dictionary<string, JournalRecord>(StringComparer.Ordinal);
        foreach (var r in records)
            if (r.Kind == "pack" && r.Members.Count > 0)
                map.TryAdd(MemberKey(r.Members), r);
        return map;
    }

    private static HashSet<string> BuildPrescreen(IReadOnlyList<JournalRecord> records)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
            if (r.Kind == "blob" && r.Path is { } p && r.HeadHash is { } h)
                set.Add(PrescreenKey(p, r.Length, h));
        return set;
    }

    /// <summary>分隔符一律用 NUL：路径里什么都可能有（空格、竖线、制表符），换个可打印字符就会撞键。</summary>
    private static string PrescreenKey(string path, long length, string headHash)
        => $"{path}\0{length}\0{headHash}";

    /// <summary>成员集合的规范化键：按序拼 路径 + 全文 hash + 长度。顺序也算数——entryName 的编号跟着它走。</summary>
    private static string MemberKey(IReadOnlyList<JournalMember> members)
        => string.Join('\n', members.Select(m => $"{m.Path}\0{m.FullHash}\0{m.Length}"));

    /// <summary>
    /// 预筛：只用（路径 + 长度 + head hash）问一句"值不值得把整个文件读一遍"。
    /// 这一关必须存在——恢复时那份内容还没进任何版本索引，本地去重表认不出它，
    /// 不在这里放行的话整轮的活会一件不落地重做一遍。
    /// </summary>
    public bool MayResumeBlob(string path, long length, string headHash)
        => _prescreen.Contains(PrescreenKey(path, length, headHash));

    /// <summary>精确匹配一个单文件 blob。四项内容判据全对上才认。</summary>
    public JournalRecord? FindBlob(string path, string fullHash, long length, string headHash, string tailHash)
        => _blobs.TryGetValue(path, out var r)
            && string.Equals(r.FullHash, fullHash, StringComparison.Ordinal)
            && r.Length == length
            && string.Equals(r.HeadHash, headHash, StringComparison.Ordinal)
            && string.Equals(r.TailHash, tailHash, StringComparison.Ordinal)
            ? r
            : null;

    /// <summary>
    /// 精确匹配一箱 pack。成员集合必须逐一相同，宽松不得：
    /// entryName 的编号是跟着分组走的，成员对不上就会让索引指向箱里根本不存在的条目。
    /// </summary>
    public JournalRecord? FindPack(IReadOnlyList<JournalMember> members)
        => members.Count > 0 && _packs.TryGetValue(MemberKey(members), out var r) ? r : null;
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~JournalResumeTests
```

Expected: PASS，6 个用例。

- [ ] **Step 5: 开卷时采纳或作废**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs`。加字段与属性：

```csharp
    /// <summary>被本轮采纳的旧 journal 的 runId。本轮成功提交索引时，它们和自己那卷一起删。</summary>
    private readonly List<string> _adopted = [];

    /// <summary>上一轮（或上几轮）已经确认传上去的东西。没有可采纳的卷时是空表。</summary>
    public JournalResume Resume { get; private set; } = JournalResume.Empty;

    /// <summary>开卷时采纳过或作废过旧卷 → 容器里多半躺着孤儿块，收尾清理该做一次扫描（Task 11）。</summary>
    public bool SweepNeeded { get; private set; }
```

并在 `OpenJournalAsync` 方法体开头声明 `var voided = false;`。

把 `OpenJournalAsync` 的方法体改成：

```csharp
        _accountId = accountId;
        _container = container;

        // 对得上号的采纳，对不上的当场删。
        //
        // configId 不同也照删：(AccountId, ContainerName) 在 AppDbContext 里是唯一索引，
        // 一个容器至多一个配置——所以那只可能是"配置删了又在同一个容器上重建"留下的陈迹。
        // 留着它会永远保住那批块不被清理（清理判据认 journal，不认 configId）。
        // 哪天允许多个配置共用一个容器了，这一条必须改回"不是我们的就完全不碰"，
        // 否则会把别人正挂起着的运行的成果变成孤儿。
        var adopted = new List<JournalContent>();
        foreach (var (oldRunId, content) in await store.ListAsync(accountId, container, ct))
        {
            var h = content.Header;
            if (h.ConfigId == configId
                && h.BaselineVersion == baselineVersion
                && string.Equals(h.LocalRoot, localRoot, StringComparison.Ordinal)
                && string.Equals(h.EncryptionIdentity, encryptionIdentity, StringComparison.Ordinal))
            {
                adopted.Add(content);
                _adopted.Add(oldRunId);
            }
            else
            {
                store.Delete(accountId, container, oldRunId);
                voided = true;
            }
        }
        // 采纳过、或作废过 → 这个容器里多半躺着"云上有、索引里没有"的块。
        // 收尾清理据此决定要不要做一次孤儿扫描（见 Task 11）。
        SweepNeeded = voided || adopted.Count > 0;
        // 采纳是**只读**的：本轮仍新开自己那一卷，旧卷原样留着。这样就不必把复用来的记录再抄一遍，
        // 也不会出现"抄到一半又崩了"的半截状态。旧卷等本轮成功提交索引时一起删。
        Resume = adopted.Count == 0
            ? JournalResume.Empty
            : new JournalResume([.. adopted.SelectMany(c => c.Records)]);

        _journal = await store.CreateAsync(accountId, container, runId, new JournalHeader
        {
            RunId = runId,
            ConfigId = configId,
            StartedAt = startedAt,
            BaselineVersion = baselineVersion,
            LocalRoot = localRoot,
            EncryptionIdentity = encryptionIdentity,
        }, ct);
```

`CompleteAsync` 里 `store.Delete(_accountId, _container, runId);` 那一行之后补：

```csharp
        // 采纳来的旧卷同样功成身退——它们记的内容此刻已经全在提交好的索引里了。
        foreach (var old in _adopted)
            store.Delete(_accountId, _container, old);
        _adopted.Clear();
```

- [ ] **Step 6: 编排器——单文件 blob 的两道坎**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`。

1) `BlobPlacement` 加一个末位默认参数，用来告诉调用方"这一份是复用来的，别再往 journal 里记一遍"：

```csharp
    /// <summary>单文件 blob 的最终落位：存储引用 + 实际存下去的内容身份。</summary>
    /// <param name="Resumed">命中了 journal 里上一轮已经传完的记录。它已经被记过，本轮不必再记。</param>
    private sealed record BlobPlacement(
        string Ref, bool Collision, int Volumes, IReadOnlyList<long> VolumeSizes, BlobContent Content,
        bool Resumed = false);
```

2) `PlaceBlobAsync` 与 `ProbeForDedupAsync` 各加一个参数 `BackupRunControl? control,`（放在 `LocalDedupResolver localResolver,` 之后）；`HandleBlobAsync` 调用 `PlaceBlobAsync` 处补实参 `control`。

3) `ProbeForDedupAsync` 里那一行 `var may = localResolver.MayDeduplicate(length, head);` 改成：

```csharp
            // journal 也要参与预筛。恢复时上一轮传上去的内容**还没进任何版本索引**，
            // localResolver 根本认不出它——只问它一个，整轮的活会一件不落地重做一遍。
            var may = localResolver.MayDeduplicate(length, head)
                || (control?.Resume.MayResumeBlob(file.Path, length, head) ?? false);
```

4) `PlaceBlobAsync` 的第 1 步改成（原来是一个合并的 `if`，现在拆成外层探测 + 内层两档）：

```csharp
        // 1. 预筛 + 探测。命中就到此为止：一个字节都不用压、不用传。
        if (await ProbeForDedupAsync(file, localPath, headBytes, localResolver, control, uploadTracker, ct) is { } p)
        {
            // 第一档：上一轮已经确认传上去的这一份。路径 + 内容双对才认——中断之后文件完全
            // 可能被改过，光凭路径复用就是把旧内容当成新内容写进索引。
            if (control?.Resume.FindBlob(file.Path, p.FullHash, p.Length, p.HeadHash, p.TailHash) is { } done)
                return new BlobPlacement(
                    done.Ref, false, Math.Max(1, done.Volumes), [.. done.VolumeSizes], p with { Raw = done.Raw },
                    Resumed: true);

            // 第二档：跨版本的既有 blob（原有行为，一字未动）。
            if (localResolver.TryFindExisting(p.FullHash, p.Length, p.HeadHash, p.TailHash) is { } prior)
                return new BlobPlacement(prior.Ref, false, prior.Volumes, prior.VolumeSizes, p with { Raw = prior.Raw });
        }
```

5) `HandleBlobAsync` 里 Task 5 加的 journal 写入点加一个条件：

```csharp
        // journal：上传（或 if-missing 命中）已经确认返回，这块内容此刻确实在云上了，现在才敢记。
        // 顺序不能动——先记后传就会记下一块并不存在的内容，下次恢复直接跳过它，那是数据丢失。
        // Resumed 的那一份是从旧卷复用来的，旧卷本轮成功之前一直留着，不必再抄一遍。
        if (control is not null && !placement.Resumed)
            await control.RecordBlobAsync(
                file.Path, placement.Ref, content.FullHash, content.HeadHash, content.TailHash, content.Length,
                Math.Max(1, placement.Volumes), content.Raw, [.. placement.VolumeSizes], ct);
```

- [ ] **Step 7: 编排器——pack 的复用**

在 `ProcessPackAsync` 里，`var members = group.Select(f => new PackEntry(f.Path, f.Path, f.FullHash!, f.Length)).ToList();` 这一行**之后**、逐成员 stat 快照之前插入：

```csharp
            // 恢复：这一整箱上一轮已经确认传上去了。成员集合必须逐一对得上——
            // entryName 的编号跟着分组走，成员对不上，索引就会指到箱里根本不存在的条目。
            //
            // 仍然要走 RecordPackAsync（只是不上传）：本轮内跨箱去重的收尾靠 storageByPath[leaderPath]
            // 判 leader 有没有走岔，在这里直接 continue 掉，挂在这个 leader 身上的别名会全部悬空重跑。
            //
            // control 传 null：这条记录还留在被采纳的那卷 journal 里，本轮成功提交索引之前一直在，
            // 不必再抄一遍。
            var journalMembers = members
                .Select(m => new JournalMember(m.Path, m.EntryName, m.FullHash, m.Length)).ToList();
            if (control?.Resume.FindPack(journalMembers) is { } donePack)
            {
                await RecordPackAsync(
                    request, donePack.Ref, members, donePack.VolumeSizes, donePack.StoreOnly, info,
                    storageByPath, control: null, ct);
                foreach (var m in members) await LogFileAsync(request, m.Path, ct);
                onItem(bytes);   // 这一组的槽位与字节照常销账，否则进度永远追不上 total
                continue;
            }
```

（`packId` 是在这之前取的，命中时那个号就作废不用了——pack 号只要求唯一，不要求连续。）

- [ ] **Step 8: 写恢复的集成测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/BackupResumeTests.cs`：

```csharp
using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class BackupResumeTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _root;
    private readonly string _temp;
    private readonly BackupJournalStore _journals;

    public BackupResumeTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-resume-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "src");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_temp);
        _journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 44,
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private void WriteBytes(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        // 每个文件的内容必须互不相同，否则三个文件会去重成一个 blob，上传次数就说明不了问题。
        for (var i = 0; i < bytes.Length; i += 4096) bytes[i] = (byte)rel.Length;
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>数一数真正发起了多少次内容上传，顺带支持"第 N 次之后叫停"。</summary>
    private sealed class CountingUploader(IBlobUploader inner, int stopAt = 0, Func<StopKind>? stop = null)
        : IBlobUploader
    {
        private int _count;

        public int Uploads => Volatile.Read(ref _count);

        private async Task<T> RunAsync<T>(Func<Task<T>> call)
        {
            var n = Interlocked.Increment(ref _count);
            var result = await call();
            if (stopAt > 0 && n == stopAt) stop!();
            return result;
        }

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, CancellationToken ct)
            => RunAsync(() => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, metadata, options, ct));

        public Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, IProgress<long>? progress, CancellationToken ct)
            => RunAsync(() => inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, metadata, options, progress, ct));

        public async Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath, AccessTier tier,
            IDictionary<string, string>? metadata, UploadOptions? options, CancellationToken ct)
            => await RunAsync<bool>(async () =>
            {
                await inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, metadata, options, ct);
                return true;
            });
    }

    private (BackupOrchestrator Orchestrator, IBackupInfoStore Store, BlobClientFactory Factory) Build(
        IBlobUploader uploader)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var compactor = new DeadWeightCompactor(
            new BlobUploader(factory), new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "compact"),
            staging);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader, factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), compactor,
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
        return (orchestrator, store, factory);
    }

    private BackupRequest Request(Account account, string container, string? password = null) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _root,
        Name = "photos",
        Password = password,
        Options = new BackupEngineOptions
        {
            UploadConcurrency = 1,
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000 },
        },
    };

    [SkippableFact]
    public async Task Second_run_reuses_what_the_suspended_run_already_uploaded()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("resume");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("a.bin", 6_000_000);
            WriteBytes("b.bin", 6_000_001);
            WriteBytes("c.bin", 6_000_002);

            // 第一轮：传完一个就挂起。
            BackupRunControl? first = null;
            var stopping = new CountingUploader(
                new BlobUploader(factory0), stopAt: 1,
                stop: () => { first!.RequestStop(StopKind.Suspend); return StopKind.Suspend; });
            await using (var c = new BackupRunControl(_journals, 9, "run-a"))
            {
                first = c;
                var (o1, _, _) = Build(stopping);
                await Assert.ThrowsAsync<BackupSuspendedException>(
                    () => o1.RunAsync(Request(account, name), null, default, c));
            }
            Assert.Equal(1, stopping.Uploads);
            Assert.Single((await _journals.ListAsync(account.Id, name, default))[0].Content.Records);

            // 第二轮：同一个配置、同样的钥匙和根目录 → 采纳旧卷，只补剩下的两个。
            var resuming = new CountingUploader(new BlobUploader(factory0));
            var (o2, store2, _) = Build(resuming);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
            {
                var result = await o2.RunAsync(Request(account, name), null, default, c2);
                Assert.Equal(1, result.Version);
            }
            Assert.Equal(2, resuming.Uploads);   // 复用来的那一个一个字节都没重传

            // 索引三条齐全，且 journal 全都功成身退了。
            var info = await store2.ReadInfoAsync(account, name, null, default);
            var index = await store2.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null, default);
            Assert.Equal(3, index.Entries.Count(e => e.Storage is not null));
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task A_changed_key_voids_the_journal_instead_of_reusing_it()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
        Skip.IfNot(SevenZip(), "7z executable not available");

        var account = AzuriteAccount();
        var name = RandomName("resume");
        var factory0 = new BlobClientFactory(TestSecrets.Reader);
        var container = factory0.CreateServiceClient(account).GetBlobContainerClient(name);
        try
        {
            WriteBytes("a.bin", 6_000_000);
            WriteBytes("b.bin", 6_000_001);
            WriteBytes("c.bin", 6_000_002);

            BackupRunControl? first = null;
            var stopping = new CountingUploader(
                new BlobUploader(factory0), stopAt: 1,
                stop: () => { first!.RequestStop(StopKind.Suspend); return StopKind.Suspend; });
            await using (var c = new BackupRunControl(_journals, 9, "run-a"))
            {
                first = c;
                var (o1, _, _) = Build(stopping);
                await Assert.ThrowsAsync<BackupSuspendedException>(
                    () => o1.RunAsync(Request(account, name), null, default, c));
            }

            // 换了密码 → 寻址身份变了 → 旧卷里的引用全对不上号，必须整卷作废，三个文件全部重传。
            var again = new CountingUploader(new BlobUploader(factory0));
            var (o2, _, _) = Build(again);
            await using (var c2 = new BackupRunControl(_journals, 9, "run-b"))
                await o2.RunAsync(Request(account, name, password: "pw"), null, default, c2);

            Assert.Equal(3, again.Uploads);
            Assert.Empty(await _journals.ListAsync(account.Id, name, default));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
```

- [ ] **Step 9: 跑测试确认通过**

```bash
npx azurite --skipApiVersionCheck &
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter FullyQualifiedName~BackupResumeTests
```

Expected: PASS，2 个用例。

- [ ] **Step 10: 跑全量后端测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿。特别留意既有的去重用例——预筛多了一条 `||`，没有 journal 时 `control` 为 null，短路成原样。

- [ ] **Step 11: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/JournalResume.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/tests/AzureStorageBackup.Api.Tests/JournalResumeTests.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupResumeTests.cs
git commit -m "feat(backup): reuse journalled blocks instead of re-uploading them

Resuming is not a special mode: every run adopts any still-valid journal
left on the same container. A journal belonging to another config is left
strictly alone, one of ours whose baseline, local root or key no longer
matches is voided, and the rest are adopted read-only and deleted once
this run commits its index.

Matching needs both the path and the content to agree, since a file can
be edited between the interruption and the retry. Packs must match on the
whole member set because entry names are numbered per group. A resumed
pack still goes through RecordPackAsync so within-run alias dedup keeps
resolving against its leader.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 11: 统一清理判据——别删 journal 保着的东西

`RetentionCleaner` 今天的判据是一条："不被任何保留版本的索引引用 → 删。"取消和挂起打破了这条：容器里现在会有**云上已有、索引里还没有**的完整块，它们只有 journal 记着。判据要并上一条：

> 既不被任何保留版本引用、**也不被任何活动 journal 引用**，才是孤儿。

同时要拆掉 `RetentionCleaner.cs:74-75` 那个早退：

```csharp
        if (toDelete.Count == 0)
            return CleanupReport.Empty;
```

没有版本退役并不等于没有东西可清——取消留下的那批块正是"一个版本都没退役，但确实有孤儿"的情形。但也不能无条件全扫：孤儿扫描要把 `data/` 和 `packs/` 两个前缀整个列一遍，几十万对象的容器上这不是白干的，而绝大多数备份根本没有孤儿。所以加一个 `sweepOrphans` 开关：

- 计划任务里独立跑的 Cleanup → 永远 `true`（它就是干这个的）
- 备份收尾顺带的清理 → `control.SweepNeeded`（开卷时采纳过或作废过旧卷才为真）

**journal 每追加一行就要刷到 OS。** 不是 fsync——是 `Flush()`，把字节从进程缓冲交给页缓存。清理器是**另一个读者**，读的是同一台机器上的同一个文件；不刷，它看到的就是一卷少了最后几行的 journal，于是把刚传上去的块当孤儿删掉。fsync 依然不做（那是防掉电的，代价是每条一次磁盘同步，不值）。

> **残留窗口，写出来不要当 bug**：从"上传确认返回"到"journal 那一行刷出去"之间有毫秒级的窗口，此刻并发跑的清理仍可能看不见它。这个窗口比现状窄得多——今天这段"云上有、索引里没有"的时间是**整轮备份**那么长，journal 把它压到了单次追加。彻底堵死需要在上传**之前**再写一条 pending 行，那是另一件事，不在本次范围里。

**删配置的兜底。** 用户明确要过："留着，下次备份自动复用。但用户如果不恢复且又删了这个备份，这些应该被清理掉。"删配置时把这个容器的 journal 全部删掉即可——**不要**顺手去删它们引用的 blob：journal 记的既包括真上传，也包括 if-missing 命中，后者完全可能同时被一个已提交的版本索引引用着，删了就是把保留下来的版本挖穿。删掉 journal 之后那批块失去保护，等这个容器上再有配置时，第一次清理会用完整判据（读得到索引、认得出引用）把真孤儿扫掉。

> **已知局限**：如果用户删了配置、留下了容器、而且再也不在这个容器上建备份，那批块就一直留着。要判"它到底被哪个版本引用着"必须读版本索引，而那需要备份密码——删配置的"保留容器"这一支是密码丢失时的唯一出口，按设计就拿不到密码。容器是用户自己选择留下的，这个结果诚实。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/RetentionCleaner.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`（传 `sweepOrphans`）
- Modify: `backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs`（独立清理永远扫）
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`（删配置兜底）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/RetentionCleanerJournalTests.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupConfigEndpointsTests.cs`（追加一个用例）

**Interfaces:**
- Consumes: `BackupJournalStore.LoadActiveRefsAsync`、`ActiveJournalRefs`（Task 4）、`BackupRunControl.SweepNeeded`（Task 10）
- Produces:
  - `RetentionCleaner` 构造参数末尾新增 `BackupJournalStore? journals = null`
  - 两个 `CleanupAsync` 重载末尾各新增 `bool sweepOrphans = false`

- [ ] **Step 1: 写失败的测试**

新建 `backend/tests/AzureStorageBackup.Api.Tests/RetentionCleanerJournalTests.cs`：

```csharp
using System.Net.Sockets;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class RetentionCleanerJournalTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "asb-cleanj-" + Guid.NewGuid().ToString("N"));
    private readonly BackupJournalStore _journals;

    public RetentionCleanerJournalTests()
    {
        Directory.CreateDirectory(_temp);
        _journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Id = 45,
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private static async Task PutAsync(BlobContainerClient container, string name, string body)
        => await container.GetBlobClient(name).UploadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(body)), overwrite: true);

    private static async Task<List<string>> NamesAsync(BlobContainerClient container, string prefix)
    {
        var names = new List<string>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix))
            names.Add(b.Name);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private RetentionCleaner Cleaner(BlobClientFactory factory)
        => new(factory, new BackupInfoStore(factory, new SevenZipArchiveCodec()), new RetentionEvaluator(),
            journals: _journals);

    private async Task WriteJournalAsync(int accountId, string container, string runId, params JournalRecord[] records)
    {
        await using var j = await _journals.CreateAsync(accountId, container, runId, new JournalHeader
        {
            RunId = runId, ConfigId = 1, StartedAt = DateTimeOffset.UnixEpoch, BaselineVersion = 0,
            LocalRoot = "/data/src", EncryptionIdentity = "plain",
        }, default);
        foreach (var r in records)
            await j.AppendAsync(r, default);
    }

    private static CleanupOptions Options() => new()
    {
        Retention = new RetentionPolicy { MaxVersions = 50, MaxAgeDays = 365, Mode = RetentionMode.EitherTriggers },
    };

    [SkippableFact]
    public async Task Journalled_blocks_survive_the_orphan_sweep()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "data/keep", "kept");
            await PutAsync(container, "data/keep.001", "kept volume");
            await PutAsync(container, "data/gone", "orphan");
            await PutAsync(container, "packs/pkeep.7z", "kept pack");
            await PutAsync(container, "packs/pgone.7z", "orphan pack");
            await WriteJournalAsync(account.Id, name, "run-x",
                new JournalRecord { Kind = "blob", Ref = "data/keep", Path = "a.bin", FullHash = "keep", Volumes = 2 },
                new JournalRecord { Kind = "pack", Ref = "pkeep", VolumeSizes = [5] });

            // 一个版本都没退役，但仍要扫：取消留下的块正是这种情形。
            var report = await Cleaner(factory).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { CreatedAt = DateTimeOffset.UnixEpoch } },
                default, sweepOrphans: true);

            Assert.Equal(["data/keep", "data/keep.001"], await NamesAsync(container, "data/"));
            Assert.Equal(["packs/pkeep.7z"], await NamesAsync(container, "packs/"));
            Assert.Equal(1, report.DeletedBlobs);
            Assert.Equal(1, report.DeletedPacks);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Once_the_journal_is_gone_the_blocks_are_swept()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "data/keep", "kept");
            await WriteJournalAsync(account.Id, name, "run-x",
                new JournalRecord { Kind = "blob", Ref = "data/keep", Path = "a.bin", FullHash = "keep" });
            _journals.DeleteAll(account.Id, name);   // 删配置兜底做的就是这一步

            await Cleaner(factory).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { CreatedAt = DateTimeOffset.UnixEpoch } },
                default, sweepOrphans: true);

            Assert.Empty(await NamesAsync(container, "data/"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }

    [SkippableFact]
    public async Task Without_the_sweep_flag_a_no_op_cleanup_touches_nothing()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");

        var account = AzuriteAccount();
        var name = RandomName("cleanj");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            await PutAsync(container, "data/gone", "orphan");

            // 没有版本退役、也没让它扫 → 一个 LIST 都不该发。几十万对象的容器上这不是白干的。
            var report = await Cleaner(factory).CleanupAsync(
                account, name, null, Options(),
                new BackupInfoFile { Backup = new BackupMeta { CreatedAt = DateTimeOffset.UnixEpoch } },
                default);

            Assert.True(report.IsEmpty);
            Assert.Equal(["data/gone"], await NamesAsync(container, "data/"));
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
```

追加到 `backend/tests/AzureStorageBackup.Api.Tests/BackupConfigEndpointsTests.cs`（放在最后一个 `[Fact]` 之后、类的右花括号之前）：

```csharp
    [Fact]
    public async Task Delete_config_discards_its_journals()
    {
        var accountId = await CreateAccountAsync("journal-sweep");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("j", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var journals = _services.GetRequiredService<BackupJournalStore>();
        await using (var j = await journals.CreateAsync(accountId, "photos", "leftover", new JournalHeader
        {
            RunId = "leftover", ConfigId = created!.Id, StartedAt = DateTimeOffset.UtcNow,
            BaselineVersion = 0, LocalRoot = "/data/photos", EncryptionIdentity = "plain",
        }, default))
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "a.bin", FullHash = "aaa" }, default);
        Assert.Single(await journals.ListAsync(accountId, "photos", default));

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/backup-configs/{created.Id}")).StatusCode);

        // 配置没了就再没人会来采纳这卷 journal；留着它只会永远保住那批块不被清理。
        Assert.Empty(await journals.ListAsync(accountId, "photos", default));
    }
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
npx azurite --skipApiVersionCheck &
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~RetentionCleanerJournalTests|FullyQualifiedName~Delete_config_discards_its_journals"
```

Expected: 编译失败，`RetentionCleaner` 没有 `journals` 参数、`CleanupAsync` 没有 `sweepOrphans`。

- [ ] **Step 3: 把"每条都刷到 OS"钉住**

**不要改 `BackupJournal`。** Task 3 的 `WriteLineAsync` 里已经有这一行了：

```csharp
            await _stream.FlushAsync(ct);   // 只刷到 OS，不落盘；见类注释
```

写端 `FileShare.Read` / 读端 `FileShare.ReadWrite`，跨读者可见性也已经成立。这里要做的只是把这个性质**钉成测试**——它现在从"实现细节"变成了清理判据依赖的契约：不刷，清理器看到的就是一卷少了最后几行的 journal，于是把刚传上去的块当孤儿删掉。（fsync 依然不做，那是防掉电的，代价是每条一次磁盘同步。）

在 `BackupJournalTests` 里加一条用例（追加到该类最后一个 `[Fact]` 之后）：

```csharp
    // 清理器读的是同一个文件。追加不立刻刷出去，它就会把刚传上去的块当孤儿删掉。
    [Fact]
    public async Task Append_is_visible_to_another_reader_without_an_explicit_flush()
    {
        var file = Path_("f.jsonl");
        await using var j = await BackupJournal.CreateAsync(file, Header(), default);
        await j.AppendAsync(new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "p", FullHash = "aaa" }, default);

        var content = await BackupJournal.ReadAsync(file, default);
        Assert.Single(content!.Records);
    }
```

- [ ] **Step 4: 统一清理判据**

编辑 `backend/src/AzureStorageBackup.Api/Services/RetentionCleaner.cs`。

1) 构造函数末尾加一个参数：

```csharp
public sealed class RetentionCleaner(
    IBlobClientFactory factory, IBackupInfoStore store, RetentionEvaluator retention,
    DeadWeightCompactor? compactor = null, ILocalIndexCache? indexCache = null, TrackedInfoStore? trackedInfo = null,
    BackupJournalStore? journals = null)
```

（`Program.cs` 不用改：`BackupJournalStore` 已是单例，`AddScoped<RetentionCleaner>()` 会把它注进来。）

2) 独立清理那个重载加开关并透传：

```csharp
    public async Task<CleanupReport> CleanupAsync(
        Account account, string container, string? password, CleanupOptions options, CancellationToken ct = default,
        StagingArea.StagingLease? lease = null, bool sweepOrphans = false)
    {
        var info = trackedInfo is not null
            ? await trackedInfo.LoadAsync(account, container, password, ct)
            : await store.ReadInfoAsync(account, container, password, ct);
        return info is not null && info.Versions.Count > 0
            ? await CleanupAsync(account, container, password, options, info, ct, lease, sweepOrphans)
            : CleanupReport.Empty;
    }
```

3) 主重载加开关，并把早退换掉：

```csharp
    public async Task<CleanupReport> CleanupAsync(
        Account account, string container, string? password, CleanupOptions options,
        BackupInfoFile info, CancellationToken ct = default, StagingArea.StagingLease? lease = null,
        bool sweepOrphans = false)
    {
        var toDelete = retention.VersionsToDelete(
            info.Versions.Select(v => new VersionRef(v.Version, v.CreatedAt)).ToList(),
            options.Retention, DateTimeOffset.UtcNow);
        // 从前这里是「没有版本退役 → 直接返回」。取消和挂起打破了这个前提：容器里会留下
        // 「云上已有、索引里还没有」的完整块，而那种情形一个版本都不会退役。
        // 但也不能无条件全扫——孤儿扫描要把 data/ 与 packs/ 两个前缀整个列一遍，
        // 几十万对象的容器上这不是白干的，而绝大多数备份根本没有孤儿。
        if (toDelete.Count == 0 && !sweepOrphans)
            return CleanupReport.Empty;
```

4) 在 `var deletedPacks = new HashSet<string>(...)` 之前插入活动 journal 的引用：

```csharp
        // 判据的另一半：活动 journal 引用着的内容。它们云上有、索引里还没有，只有 journal
        // 记着它们存在——删了就等于让下一轮恢复白跑，用户点 Resume 会发现要从头再传一遍。
        var active = journals is not null
            ? await journals.LoadActiveRefsAsync(account.Id, container, ct)
            : ActiveJournalRefs.Empty;
```

5) 两个删除循环各加一条判据：

```csharp
            var packId = PackIdOf(blob.Name);
            if (referencedPacks.Contains(packId) || active.Packs.Contains(packId))
                continue;
```

```csharp
            var baseRef = BaseRef(blob.Name);
            if (referencedBlobs.Contains(baseRef) || active.Blobs.Contains(baseRef))
                continue;
```

以及 `info.Packs` 那一行：

```csharp
        foreach (var packId in info.Packs.Keys
            .Where(id => !referencedPacks.Contains(id) && !active.Packs.Contains(id)).ToList())
            info.Packs.Remove(packId);
```

- [ ] **Step 5: 两个调用点各自决定要不要扫**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs` 第 10 步的清理调用，末尾补一个实参：

```csharp
        }, info, ct, stagingLease, sweepOrphans: control?.SweepNeeded ?? false);
```

编辑 `backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs` 的 `ScheduledTaskType.Cleanup` 分支：

```csharp
                    // 独立跑的清理永远做孤儿扫描——它就是干这个的。取消/崩溃留下的块要是没被
                    // 下一次备份复用掉，就只有这条路会来收。
                    var cleanup = await sp.GetRequiredService<RetentionCleaner>().CleanupAsync(
                        account, container, password,
                        BackupRequestMapper.CleanupOf(config, cleanupSettings), ct, cleanupLease,
                        sweepOrphans: true);
```

- [ ] **Step 6: 删配置兜底**

编辑 `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`。给 `MapDelete("/{id:int}", ...)` 的参数列表加上 `BackupJournalStore journals,`（放在 `ILocalBackupStateStore localState,` 之后），并在 `await BestEffort(logger, "remove local backup state", ...)` 之后加一步：

```csharp
                // 配置没了就再没人会来采纳这个容器上的 journal，留着它只会永远保住那批块不被清理
                // （清理判据认 journal，不认 configId）。**只删 journal 文件，不去删它引用的 blob**：
                // journal 记的既包括真上传，也包括 if-missing 命中，后者完全可能同时被一个已提交的
                // 版本索引引用着，删了就是把保留下来的版本挖穿。失去保护之后，等这个容器上再有配置时，
                // 第一次清理会用完整判据（读得到索引、认得出引用）把真孤儿扫掉。
                await BestEffort(logger, "discard backup journals",
                    () => { journals.DeleteAll(accountId, container); return Task.CompletedTask; });
```

- [ ] **Step 7: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~RetentionCleanerJournalTests|FullyQualifiedName~BackupJournalTests|FullyQualifiedName~Delete_config_discards_its_journals"
```

Expected: PASS。

- [ ] **Step 8: 跑全量后端测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿。重点看既有的保留清理用例——它们都有版本退役，`sweepOrphans` 默认 `false` 不影响它们走进扫描。

- [ ] **Step 9: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/RetentionCleaner.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs \
        backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs \
        backend/tests/AzureStorageBackup.Api.Tests/RetentionCleanerJournalTests.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupJournalTests.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupConfigEndpointsTests.cs
git commit -m "fix(backup): never sweep blocks an active journal still protects

Cleanup used to bail out whenever no version retired, which is exactly
the shape a cancelled run leaves behind: complete blocks in the cloud
that no index references yet. The criterion is now unified — orphaned
means referenced by neither a retained version nor an active journal —
and the sweep runs when the scheduled cleanup asks for it or when the
run adopted or voided a journal at open.

A test now pins that journal appends flush to the OS on every record:
the cleaner reads the same file from another thread, and a buffered tail
would look like an orphan. Deleting a config discards its journals so
the blocks stop being protected; their blobs are deliberately left for a
later reference-aware sweep, because a journal line can also be an
if-missing hit on content a retained version still points at.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 12: API 端点

到这里后端的能力都齐了，但界面还够不着。要开五个口子：

| 动作 | 路由 | 落到哪 |
| --- | --- | --- |
| 挂起 | `POST /api/backup-configs/{id}/suspend` | `BackupRunner.SuspendAsync` |
| 立刻重试 | `POST /api/backup-configs/{id}/retry-now` | `BackupRunner.RetryNow` |
| 取消（两种） | `POST /api/backup-configs/{id}/cancel?finishCurrentFiles=` | `BackupRunner.CancelAsync` |
| 列中断现场 | `GET /api/backup-configs/{id}/interrupted` | `BackupJournalStore.PeekAsync` |
| 丢弃现场 | `DELETE /api/backup-configs/{id}/interrupted` | `BackupJournalStore.DeleteAll` |

**没有 resume 端点**——恢复不是一种模式。每一轮备份开卷时都会去认还有效的 journal（Task 10），所以"继续"就是再点一次 `POST /{id}/run`，走的是同一条路。多开一个端点等于多一条要维护的分支，而它跟 `/run` 会一模一样。

**等落盘要有上限。** 用户要的是"点完就知道现场已经安全了"，所以 suspend 和 cancel 都等收尾完成再返回。但 `Suspend` 与 `Finish current files` 都要让**正在传的文件（含它所有分卷）传完**，一个大文件可能要好几分钟——而用户跑在 NAS 上，前面多半有一层反向代理，六十秒就把连接掐了，界面上看到的会是一条网络错误，尽管后台一切正常。所以最多等 20 秒：settle 了回 `200`，没 settle 回 `202 { stopping: true }`，让界面继续轮询 `GET /{id}/run`。**注意 202 并没有谎报成功**，它说的就是"还在停"。

超时不等于没停下：停止请求在 `await` 之前就发出去了，闸门也已经降级，运行一定会走到终态。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupJournalStore.cs`（加 `PeekAsync`）
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupJournal.cs`（把序列化设置提成共用的 `JournalJson`）
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupConfigEndpointsTests.cs`（追加 4 个用例）

**Interfaces:**
- Consumes: `BackupRunner.SuspendAsync/CancelAsync/RetryNow`（Task 8/9）、`BackupJournalStore`（Task 4）
- Produces:
  - `sealed record JournalSummary(string RunId, JournalHeader Header, int Records, long SizeBytes)`
  - `Task<IReadOnlyList<JournalSummary>> BackupJournalStore.PeekAsync(int accountId, string container, CancellationToken ct)`
  - `sealed record InterruptedRunResponse(string RunId, DateTimeOffset StartedAt, int Blocks, long JournalBytes, bool Resumable)`

- [ ] **Step 1: 写失败的测试**

追加到 `backend/tests/AzureStorageBackup.Api.Tests/BackupConfigEndpointsTests.cs`（放在 Task 11 那个用例之后）：

```csharp
    [Fact]
    public async Task Suspend_without_a_running_backup_is_a_conflict()
    {
        var accountId = await CreateAccountAsync("suspend-idle");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("s", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PostAsync($"/api/backup-configs/{created!.Id}/suspend", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Retry_now_without_a_paused_backup_is_a_conflict()
    {
        var accountId = await CreateAccountAsync("retry-idle");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("r", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PostAsync($"/api/backup-configs/{created!.Id}/retry-now", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Interrupted_runs_are_listed_with_their_block_count()
    {
        var accountId = await CreateAccountAsync("interrupted-list");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("i", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var journals = _services.GetRequiredService<BackupJournalStore>();
        await using (var j = await journals.CreateAsync(accountId, "photos", "run-1", new JournalHeader
        {
            RunId = "run-1", ConfigId = created!.Id, StartedAt = DateTimeOffset.UnixEpoch,
            BaselineVersion = 0, LocalRoot = "/data/photos", EncryptionIdentity = "plain",
        }, default))
        {
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "a.bin", FullHash = "aaa" }, default);
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/bbb", Path = "b.bin", FullHash = "bbb" }, default);
        }

        var listed = await _client.GetFromJsonAsync<List<InterruptedRunResponse>>(
            $"/api/backup-configs/{created.Id}/interrupted");

        Assert.Single(listed!);
        Assert.Equal("run-1", listed![0].RunId);
        Assert.Equal(2, listed[0].Blocks);          // 头一行不算进去
        Assert.True(listed[0].JournalBytes > 0);
        Assert.True(listed[0].Resumable);
    }

    [Fact]
    public async Task Interrupted_run_from_another_local_root_is_listed_but_not_resumable()
    {
        var accountId = await CreateAccountAsync("interrupted-moved");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("m", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var journals = _services.GetRequiredService<BackupJournalStore>();
        await using (var j = await journals.CreateAsync(accountId, "photos", "run-2", new JournalHeader
        {
            RunId = "run-2", ConfigId = created!.Id, StartedAt = DateTimeOffset.UnixEpoch,
            BaselineVersion = 0, LocalRoot = "/somewhere/else", EncryptionIdentity = "plain",
        }, default)) { }

        var listed = await _client.GetFromJsonAsync<List<InterruptedRunResponse>>(
            $"/api/backup-configs/{created.Id}/interrupted");
        Assert.False(listed![0].Resumable);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/backup-configs/{created.Id}/interrupted")).StatusCode);
        Assert.Empty(await _client.GetFromJsonAsync<List<InterruptedRunResponse>>(
            $"/api/backup-configs/{created.Id}/interrupted") ?? []);
    }
```

- [ ] **Step 2: 跑一次确认它失败**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~Suspend_without|FullyQualifiedName~Retry_now_without|FullyQualifiedName~Interrupted_run"
```

Expected: 编译失败，`InterruptedRunResponse` 不存在。

- [ ] **Step 3: 给 journal 目录加一个只读头的概览**

编辑 `backend/src/AzureStorageBackup.Api/Services/BackupJournalStore.cs`，在 `ActiveJournalRefs` 之后加：

```csharp
/// <summary>
/// 一卷 journal 的概览。<b>不解析记录体</b>：只反序列化头一行，剩下的只数行数。
/// 界面列"有哪些中途停下的运行"会反复调它，而一卷 journal 可能有几十万行——
/// 逐行 JSON 反序列化只为显示一个数字，代价不值。
/// </summary>
public sealed record JournalSummary(string RunId, JournalHeader Header, int Records, long SizeBytes);
```

并在 `ListAsync` 之后加：

```csharp
    /// <summary>列出该容器上每卷 journal 的概览。头读不通的直接跳过（= 这卷作废）。</summary>
    public async Task<IReadOnlyList<JournalSummary>> PeekAsync(int accountId, string container, CancellationToken ct)
    {
        var dir = DirFor(accountId, container);
        if (!Directory.Exists(dir))
            return [];

        var result = new List<JournalSummary>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            JournalHeader? header;
            var lines = 0;
            try
            {
                using var reader = new StreamReader(
                    new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
                var first = await reader.ReadLineAsync(ct);
                if (first is null)
                    continue;
                try { header = JsonSerializer.Deserialize<JournalHeader>(first, JournalJson.Options); }
                catch (JsonException) { continue; }
                if (header is null)
                    continue;
                while (await reader.ReadLineAsync(ct) is { } line)
                    if (line.Length > 0)
                        lines++;
            }
            catch (IOException)
            {
                continue;   // 正在被写的那一卷偶尔读不开；下次轮询再说
            }
            result.Add(new JournalSummary(
                Path.GetFileNameWithoutExtension(file), header, lines, new FileInfo(file).Length));
        }
        return result;
    }
```

文件顶部补上 `using System.Text;` 与 `using System.Text.Json;`。

`JournalJson.Options` 是 Task 3 里那份 `private static readonly JsonSerializerOptions Json`——它现在有了第二个使用者，把它从 `BackupJournal` 里提出来。编辑 `backend/src/AzureStorageBackup.Api/Services/BackupJournal.cs`，在 `JournalHeader` 之前加：

```csharp
/// <summary>journal 读写共用的序列化设置。读端不止 <see cref="BackupJournal"/> 一个（还有目录的概览），
/// 两边设置必须是同一份，否则同一行字节在两处解出不同结果。</summary>
internal static class JournalJson
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
}
```

把 `BackupJournal` 里的 `private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };` 删掉，类内所有 `Json` 换成 `JournalJson.Options`。

- [ ] **Step 4: 写端点**

编辑 `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`。

先在文件末尾（`Wanted` helper 附近）加共用的停止等待：

```csharp
    /// <summary>停止请求的三种结局。</summary>
    private enum StopOutcome { NothingRunning, Settled, StillStopping }

    /// <summary>
    /// 发出停止请求并等它落盘完成，但**最多等 20 秒**。
    /// <para>
    /// 为什么要等：用户点完停止，要的是"现在现场已经安全了"，而不是"信号发出去了"。
    /// 为什么要封顶：Suspend 与 Finish current files 都会让正在传的文件（含它所有分卷）传完，
    /// 一个大文件可能要好几分钟；而用户跑在 NAS 上，前面多半有一层反向代理，六十秒就把连接掐了，
    /// 界面上看到的会是一条网络错误，尽管后台一切正常。
    /// </para>
    /// <para>超时不代表没停下：停止请求在 await 之前就发出去了，闸门也已经降级，运行一定会走到终态。</para>
    /// </summary>
    private static async Task<StopOutcome> StopAndWaitAsync(
        Func<CancellationToken, Task<bool>> stop, CancellationToken ct)
    {
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            return await stop(cap.Token) ? StopOutcome.Settled : StopOutcome.NothingRunning;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StopOutcome.StillStopping;
        }
    }
```

再在 `MapGet("/{id:int}/run", ...)` 之后插入三个新端点：

```csharp
        // 挂起：安全落盘后停下，现场留着，下次点 Run 会原样接上。
        // **没有对应的 resume 端点**——恢复不是一种模式：每一轮备份开卷时都会去认还有效的 journal，
        // 所以"继续"就是再点一次 /run，走的是同一条执行体。
        group.MapPost("/{id:int}/suspend", async (int id, BackupRunner runner, CancellationToken ct) =>
            await StopAndWaitAsync(c => runner.SuspendAsync(id, c), ct) switch
            {
                StopOutcome.NothingRunning => Results.Conflict(new { error = "No backup is running." }),
                StopOutcome.StillStopping => Results.Accepted($"/api/backup-configs/{id}/run", new { stopping = true }),
                _ => Results.NoContent(),
            });

        // 卡在瞬时错误上自愈等待时，用户点「Retry now」不等计时器，立刻放行一次重试。
        group.MapPost("/{id:int}/retry-now", (int id, BackupRunner runner) =>
            runner.RetryNow(id)
                ? Results.NoContent()
                : Results.Conflict(new { error = "This backup is not waiting to retry." }));

        // 这个容器上有哪些中途停下的运行。程序刚起来时界面靠它把"有活儿没干完"摆出来等用户点，
        // 而不是替用户决定要不要接着跑。
        group.MapGet("/{id:int}/interrupted", async (
            int id, IBackupConfigService svc, BackupJournalStore journals, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();

            var runs = await journals.PeekAsync(config.AccountId, config.ContainerName, ct);
            return Results.Ok(runs.Select(r => new InterruptedRunResponse(
                r.RunId, r.Header.StartedAt, r.Records, r.SizeBytes,
                r.Header.ConfigId == id && r.Header.LocalRoot == config.LocalRoot)).ToList());
        });

        // 用户不想接着跑了：把现场丢掉。
        // 云上那批块并不在这里删——判断"它到底还被哪个版本引用着"要读版本索引，那需要备份密码，
        // 而这个端点拿不到。丢掉 journal 之后它们失去保护，下一次带孤儿扫描的清理会用完整判据收走
        // （Task 11）。
        group.MapDelete("/{id:int}/interrupted", async (
            int id, IBackupConfigService svc, BackupJournalStore journals, BackupRunner runner,
            CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            // 正在跑的那一轮自己就握着一卷 journal，从它脚下把文件抽走只会让收尾时报一堆
            // 莫名其妙的 IO 错误。让用户先停下来。
            if (runner.Get(id) is { Status: RunStatus.Running })
                return Results.Conflict(new { error = "This backup is running; stop it first." });

            journals.DeleteAll(config.AccountId, config.ContainerName);
            return Results.NoContent();
        });
```

最后改造 `MapPost("/{id:int}/cancel", ...)`：参数表加 `bool? finishCurrentFiles`，备份那一支换成等落盘的版本：

```csharp
        group.MapPost("/{id:int}/cancel", async (int id, string? what, bool? finishCurrentFiles,
            IBackupConfigService svc,
            BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner, CheckRunner checkRunner,
            CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();

            var canceled = new List<string>();
            var stopping = false;

            // 备份这一支是**等落盘再返回**的，另外三个仍是发个信号就走——它们没有需要落盘的现场。
            // finishCurrentFiles=true：正在传的文件（含它所有分卷）传完再停，这部分算数；
            // false：立刻停，半截的分卷和在途的块都删掉，不留没法用的残渣。
            if (Wanted(what, "backup"))
                switch (await StopAndWaitAsync(c => backupRunner.CancelAsync(id, finishCurrentFiles ?? false, c), ct))
                {
                    case StopOutcome.Settled: canceled.Add("backup"); break;
                    case StopOutcome.StillStopping: canceled.Add("backup"); stopping = true; break;
                }

            if (Wanted(what, "restore") && restoreRunner.Cancel(id)) canceled.Add("restore");
            if (Wanted(what, "repair") && repairRunner.Cancel(id)) canceled.Add("repair");
            if (Wanted(what, "check") && checkRunner.Cancel(id)) canceled.Add("check");

            // 除备份外，停止仍是异步的：这里只发出取消信号，运行本身要等到下一个取消检查点才真的收尾。
            // 界面据此把按钮改成「Stopping…」，而不是立刻当成已经停了。
            return canceled.Count == 0
                ? Results.Conflict(new { error = "Nothing is running for this backup." })
                : Results.Ok(new { canceled, stopping });
        });
```

在文件末尾（其它 `sealed record` 请求/响应体旁边）加：

```csharp
/// <summary>
/// 一次中途停下的运行，供界面列出来等用户决定。
/// </summary>
/// <param name="Blocks">journal 里已确认在云上的块数。接着跑能省下的，大致就是这么多。</param>
/// <param name="Resumable">
/// 便宜的那几项前置校验的预览：configId 与本地根对不对得上。
/// **不是承诺**——基线版本与加密身份要读索引和密码才能核，那要等真正开卷时才做（Task 10）。
/// 这里为 true 而开卷时仍被判作废是可能的，界面别把它说成"一定能接上"。
/// </param>
public sealed record InterruptedRunResponse(
    string RunId, DateTimeOffset StartedAt, int Blocks, long JournalBytes, bool Resumable);
```

- [ ] **Step 5: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~BackupConfigEndpointsTests"
```

Expected: PASS。

- [ ] **Step 6: 跑全量后端测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿。既有的 cancel 用例不带 `finishCurrentFiles`，可空参数缺省为 `null` → `false` → 立刻停，与从前语义一致。

- [ ] **Step 7: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupJournal.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupJournalStore.cs \
        backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupConfigEndpointsTests.cs
git commit -m "feat(api): expose suspend, retry-now, two-mode cancel, interrupted runs

Suspend and cancel wait for the run to settle before answering, because
what the operator wants to know is that the scene is safe, not that a
signal was sent. The wait is capped at 20s and falls back to 202 with
stopping=true: finishing the file currently uploading can take minutes,
and a NAS reverse proxy would drop the connection long before that and
show a network error over a perfectly healthy backup.

There is deliberately no resume endpoint. Every run adopts a still-valid
journal when it opens, so continuing is just POST /run down the same
path; a second endpoint would only be a copy that can drift.

Listing interrupted runs reads the journal header and counts lines
rather than deserializing every record — a journal can be hundreds of
thousands of lines and the UI polls this.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 13: 前端

界面上要出现的东西，按状态排：

| 运行状态 | 这一行显示 | 按钮 |
| --- | --- | --- |
| Running，一切正常 | 现有的进度顶行 | `Suspend` · `Stop` |
| Running，卡在瞬时错误上等自愈 | 进度顶行 + 一条 warn 行「Paused — 原因；第 N 次；下次重试还有 Ms」 | `Retry now` · `Suspend` · `Stop` |
| Suspended | 「Suspended — 现场已保存，继续时从这里接上」 | `Resume` · `Discard` |
| 没有内存中的运行，但盘上有 journal（程序刚重启） | 「Interrupted run — 已确认 N 块」 | `Resume` · `Discard` |

**Paused 不是一个状态值。** 后端的 `status` 仍然是 `Running`（Task 8 的注释讲了为什么：Task 还活着、席位还占着）。前端也照办——`pause` 是挂在 running 上的一条附加信息，不是第五种状态。写成状态就会让"停止"按钮消失，而卡住的时候恰恰是最想停的时候。

**Resume 就是 Run。** 没有 resume 端点（Task 12），所以 `Resume` 按钮调的是 `run()`。文案上叫 Resume 是因为对用户来说它确实是"接着跑"；调的是同一个接口这件事不必让用户知道，但代码里要写清楚，免得后来的人以为漏了一个 API。

**中断现场要主动去问。** 程序重启后内存里什么都没有，`GET /{id}/run` 会 404，所以列表加载时要顺带拉一次 `interrupted`。不放进 1 秒的轮询——那个 tick 只跑活跃配置，而中断现场恰恰属于"没在跑"的那一类；跟着 5 秒的 `load()` 走就够了。

**Stop 要问清楚。** 用户明确要过："Cancel 时应该询问用户是否完成当前正在上传的文件（包括他所有分卷）再停止。"所以备份的停止换成一个弹窗，两个动作分开摆，措辞把后果说死：

- `Finish current files, then stop` — 正在传的文件（含它所有分卷）传完再停，这部分算数，下次能接上
- `Stop now` — 立刻停；半截的分卷和在途的块会被删掉，不留没法用的残渣

还原/修复/检查三个不变，仍走原来的 `window.confirm`。

**Files:**
- Modify: `frontend/src/api/backupConfigs.ts`
- Create: `frontend/src/components/StopBackupDialog.tsx`
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`
- Modify: `frontend/src/index.css`（新增 `.stacked-actions`，现在没有；`.text-warn` 在 695 行，已有）

**Interfaces:**
- Consumes: Task 12 的五个端点
- Produces:
  - `RunStatus` 增加 `'Suspended'`
  - `interface PauseInfo { reason: string; since: string; nextRetryAt: string | null; failures: number }`
  - `BackupRun` 增加 `runId` / `pause` / `suspendReason`
  - `interface InterruptedRun { runId: string; startedAt: string; blocks: number; journalBytes: number; resumable: boolean }`
  - `backupConfigsApi.suspend / retryNow / interrupted / discardInterrupted`，`cancel` 增加 `finishCurrentFiles`

- [ ] **Step 1: 扩类型与 API**

编辑 `frontend/src/api/backupConfigs.ts`。

把 `RunStatus` 改成：

```ts
// 后台运行的终态。Canceled = 用户按了停止：既不是成功也不是失败，后端因此不会把它写成
// 该备份的 Error 状态（否则停一次就要手动 Reset 一次）。
// Suspended = 现场安全保存后停下的，语义上离 Canceled 更近而不是 Failed：下次跑会原样接上。
export type RunStatus = 'Running' | 'Completed' | 'Failed' | 'Canceled' | 'Suspended'
```

在 `BackupRun` 之前加：

```ts
// 卡在瞬时错误上等自愈重试。**这不是一种状态**：status 仍然是 Running，因为后台那个 Task
// 还活着、暂存席位也还占着。写成状态会让停止按钮消失，而卡住的时候恰恰是最想停的时候。
export interface PauseInfo {
  reason: string
  since: string
  // 下次自动重试的时刻（UTC）。已经在重试路上时为 null。
  nextRetryAt: string | null
  // 连续失败次数。涨到阈值就自动转挂起。
  failures: number
}

// 盘上留着的一次中途停下的运行（程序重启后内存里什么都没有，只能从这里知道）。
export interface InterruptedRun {
  runId: string
  startedAt: string
  // journal 里已确认在云上的块数。接着跑能省下的，大致就是这么多。
  blocks: number
  journalBytes: number
  // 便宜的那几项前置校验的预览，**不是承诺**：基线版本与加密身份要读索引和密码才能核，
  // 那要等真正开卷时才做。这里为 true 而开卷时仍被判作废是可能的。
  resumable: boolean
}
```

`BackupRun` 尾部加三个字段：

```ts
  // 本次运行的标识，与盘上的 journal 文件同名。
  runId: string
  // 非 null＝正卡在瞬时错误上等重试。status 仍是 'Running'。
  pause: PauseInfo | null
  // status === 'Suspended' 时的缘由：UserRequested / AutoSuspended。
  suspendReason: string | null
```

`backupConfigsApi` 里把 `cancel` 换掉并加四个方法：

```ts
  // 停止正在跑的操作。what 省略＝停掉这个配置上所有在跑的操作。
  //
  // 备份这一支后端是**等落盘再答**的，所以这个 Promise resolve 就意味着现场已经安全了——
  // 除非 stopping 为 true：那表示后端等了 20 秒还没收尾（正在传的大文件还没传完），
  // 运行仍会走到终态，界面继续轮询即可。
  //
  // finishCurrentFiles=true：正在传的文件（含它所有分卷）传完再停，这部分算数；
  // false：立刻停，半截的分卷和在途的块都删掉，不留没法用的残渣。
  cancel: (id: number, what?: 'backup' | 'restore' | 'repair' | 'check', finishCurrentFiles = false) => {
    const p = new URLSearchParams()
    if (what) p.set('what', what)
    if (finishCurrentFiles) p.set('finishCurrentFiles', 'true')
    const q = p.toString()
    return api.post<{ canceled: string[]; stopping?: boolean }>(
      `/backup-configs/${id}/cancel${q ? `?${q}` : ''}`, {})
  },
  // 挂起：安全落盘后停下。**没有对应的 resume**——恢复不是一种模式，每一轮备份开卷时都会去认
  // 还有效的 journal，所以"继续"就是再调一次 run()。
  suspend: (id: number) => api.post<void>(`/backup-configs/${id}/suspend`, {}),
  // 不等自愈计时器，立刻放行一次重试。
  retryNow: (id: number) => api.post<void>(`/backup-configs/${id}/retry-now`, {}),
  interrupted: (id: number) => api.get<InterruptedRun[]>(`/backup-configs/${id}/interrupted`),
  discardInterrupted: (id: number) => api.del(`/backup-configs/${id}/interrupted`),
```

（`api.del` 在 `frontend/src/api/client.ts:65`，签名是 `(path: string) => Promise<void>`，不带类型参数。）

- [ ] **Step 2: 停止对话框**

新建 `frontend/src/components/StopBackupDialog.tsx`：

```tsx
import { useState } from 'react'
import { Modal } from './Modal'

/**
 * 停止一次备份要问清楚：正在传的那个文件（连同它所有分卷）是传完再停，还是立刻扔掉。
 * 从前这里只有一句 window.confirm，两种后果被含混成一个"停止"——而它们差得很远：
 * 一个是"这部分算数，下次接着传"，另一个是"这部分删掉，下次重来"。
 */
export function StopBackupDialog({
  name,
  onStop,
  onClose,
}: {
  name: string
  onStop: (finishCurrentFiles: boolean) => Promise<void>
  onClose: () => void
}) {
  const [busy, setBusy] = useState<'finish' | 'now' | null>(null)

  const stop = async (finish: boolean) => {
    setBusy(finish ? 'finish' : 'now')
    try {
      await onStop(finish)
      onClose()
    } finally {
      setBusy(null)
    }
  }

  return (
    <Modal
      title={`Stop Backup — ${name}`}
      onClose={onClose}
      footer={
        <button type="button" onClick={onClose} disabled={busy !== null}>
          Keep running
        </button>
      }
    >
      <p>
        Files already uploaded are kept either way. The difference is what happens to the file being
        uploaded right now.
      </p>
      <div className="stacked-actions">
        <button type="button" className="btn-primary" onClick={() => void stop(true)} disabled={busy !== null}>
          {busy === 'finish' ? 'Finishing…' : 'Finish current files, then stop'}
        </button>
        <p className="text-faint">
          The file being uploaded — including every one of its volumes — is finished first. It counts,
          so the next run picks up from there. This can take a few minutes for a large file.
        </p>
        <button type="button" className="btn-danger" onClick={() => void stop(false)} disabled={busy !== null}>
          {busy === 'now' ? 'Stopping…' : 'Stop now'}
        </button>
        <p className="text-faint">
          Stops immediately. Volumes already uploaded for the unfinished file are deleted, so nothing
          unusable is left behind in the container.
        </p>
      </div>
    </Modal>
  )
}
```

`.stacked-actions` 在 `index.css` 里还没有，加一条最小定义（**改 CSS 前先把被它覆盖的那组规则逐个算一遍 specificity**，这个文件的层叠已经栽过几次）：

```css
.stacked-actions {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}
```

- [ ] **Step 3: 页面接线**

编辑 `frontend/src/pages/BackupConfigsPage.tsx`。

导入处加 `StopBackupDialog` 与 `InterruptedRun` 类型。

在 `runs` / `restores` 那几个 state 旁加：

```tsx
  // 盘上留着的中断现场，按配置 id 存。程序重启后内存里什么都没有，只能从这里知道"有活儿没干完"。
  const [interrupted, setInterrupted] = useState<Record<number, InterruptedRun[]>>({})
  // 打开着的停止对话框对应的配置。
  const [stopping, setStopping] = useState<BackupConfig | null>(null)
```

在 `load()` 拿到配置列表之后补一次中断现场的拉取（**不要**放进 1 秒的 tick——那个 tick 只跑活跃配置，而中断现场恰恰属于"没在跑"的那一类）：

```tsx
      // 中断现场跟着 5 秒的列表刷新走。单个失败不打断整页：拿不到就当没有，下一轮再说。
      void Promise.all(
        list.map(async (c) => [c.id, await backupConfigsApi.interrupted(c.id).catch(() => [])] as const),
      ).then((pairs) => setInterrupted(Object.fromEntries(pairs)))
```

`stopOp` 里把备份那一支分出去，其余三个不动：

```tsx
  const stopOp = async (c: BackupConfig, what: 'backup' | 'restore' | 'repair' | 'check', label: string) => {
    // 备份要问清楚"正在传的文件是传完还是扔掉"，一句 confirm 说不清，走对话框。
    if (what === 'backup') {
      setStopping(c)
      return
    }
    if (!window.confirm(`Stop the running ${label} for "${c.name}"? Work done so far is kept, but the operation will not finish.`))
      return
    setError(null)
    try {
      await backupConfigsApi.cancel(c.id, what)
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const suspendBackup = async (c: BackupConfig) => {
    setError(null)
    try {
      await backupConfigsApi.suspend(c.id)
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const retryNow = async (c: BackupConfig) => {
    setError(null)
    try {
      await backupConfigsApi.retryNow(c.id)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const discardInterrupted = async (c: BackupConfig) => {
    if (!window.confirm(
      `Discard the interrupted run for "${c.name}"? The blocks already uploaded stop being reserved and will be removed by the next cleanup, so the next backup re-uploads them.`))
      return
    setError(null)
    try {
      await backupConfigsApi.discardInterrupted(c.id)
      setRuns((m) => without(m, c.id))
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }
```

`ops` 数组里给 `RunStatus` 补上新回调，并在它后面加一条中断现场行：

```tsx
              const ops = [
                runs[c.id] && (
                  <RunStatus
                    key="run"
                    run={runs[c.id]}
                    onStop={() => stopOp(c, 'backup', 'backup')}
                    onSuspend={() => void suspendBackup(c)}
                    onRetryNow={() => void retryNow(c)}
                    onResume={() => void run(c)}
                    onDiscard={() => void discardInterrupted(c)}
                  />
                ),
                // 内存里没有运行、盘上却有 journal＝程序重启前那一轮没跑完。摆出来等用户点，
                // 不替他决定要不要接着跑。
                !runs[c.id] && interrupted[c.id]?.length > 0 && (
                  <InterruptedNotice
                    key="interrupted"
                    runs={interrupted[c.id]}
                    onResume={() => void run(c)}
                    onDiscard={() => void discardInterrupted(c)}
                  />
                ),
                restores[c.id] && <RestoreStatus key="restore" run={restores[c.id]} onStop={() => stopOp(c, 'restore', 'restore')} />,
                repairs[c.id] && <RepairStatus key="repair" run={repairs[c.id]} onStop={() => stopOp(c, 'repair', 'repair')} />,
                checks[c.id] && <CheckStatus key="check" run={checks[c.id]} onStop={() => stopOp(c, 'check', 'check')} />,
              ].filter(Boolean)
```

在页面底部其它弹窗旁边挂上停止对话框：

```tsx
      {stopping && (
        <StopBackupDialog
          name={stopping.name}
          onStop={async (finishCurrentFiles) => {
            setError(null)
            try {
              await backupConfigsApi.cancel(stopping.id, 'backup', finishCurrentFiles)
              load()
            } catch (e) {
              setError(e instanceof Error ? e.message : String(e))
            }
          }}
          onClose={() => setStopping(null)}
        />
      )}
```

- [ ] **Step 4: 改 RunStatus 组件**

同一文件。`StopButton` 换成一组按钮：

```tsx
// 运行中的按钮组。停止是异步的（信号发出后要等到下一个取消检查点），所以点完这一行不会立刻变——
// 文案里不作"已停止"的承诺。
function RunButtons({
  onStop,
  onSuspend,
  onRetryNow,
}: {
  onStop: () => void
  onSuspend: () => void
  onRetryNow?: () => void
}) {
  return (
    <>
      {onRetryNow && (
        <>
          {' '}
          <button type="button" className="btn-ghost" style={{ padding: '0 0.3rem' }} onClick={onRetryNow}>
            Retry now
          </button>
        </>
      )}{' '}
      <button type="button" className="btn-ghost" style={{ padding: '0 0.3rem' }} onClick={onSuspend}>
        Suspend
      </button>{' '}
      <button type="button" className="btn-ghost btn-danger" style={{ padding: '0 0.3rem' }} onClick={onStop}>
        Stop
      </button>
    </>
  )
}

// 中断现场：程序重启前那一轮没跑完，journal 还在盘上。
function InterruptedNotice({
  runs,
  onResume,
  onDiscard,
}: {
  runs: InterruptedRun[]
  onResume: () => void
  onDiscard: () => void
}) {
  const blocks = runs.reduce((n, r) => n + r.blocks, 0)
  return (
    <div className="text-warn">
      Interrupted run — {blocks.toLocaleString()} block(s) already uploaded are kept and will be reused{' '}
      <button type="button" className="btn-ghost" style={{ padding: '0 0.3rem' }} onClick={onResume}>
        Resume
      </button>{' '}
      <button type="button" className="btn-ghost btn-danger" style={{ padding: '0 0.3rem' }} onClick={onDiscard}>
        Discard
      </button>
    </div>
  )
}
```

`RunStatus` 的签名与前三个分支改成：

```tsx
function RunStatus({
  run,
  onStop,
  onSuspend,
  onRetryNow,
  onResume,
  onDiscard,
}: {
  run: BackupRun
  onStop: () => void
  onSuspend: () => void
  onRetryNow: () => void
  onResume: () => void
  onDiscard: () => void
}) {
  // 展开状态留在组件内：轮询每秒都在换 props，但 React 保留同一个实例，所以展开不会被刷掉。
  const [showDetail, setShowDetail] = useState(false)

  if (run.status === 'Failed')
    return <div className="text-danger">Failed: {run.error}</div>
  // 停止既不是成功也不是失败：后端不会把它写成该备份的 Error 状态，这里也不用红色。
  if (run.status === 'Canceled')
    return <div className="text-warn">Backup stopped — nothing was recorded for this run</div>
  // 挂起同理，而且比停止更进一步：现场保着，下次跑会从这里接上，所以按钮是 Resume 而不是 Run。
  // 「Resume」调的其实就是 run()——恢复不是一种模式，每一轮开卷时都会去认还有效的 journal。
  if (run.status === 'Suspended')
    return (
      <div className="text-warn">
        {run.suspendReason === 'AutoSuspended'
          ? 'Suspended after repeated network errors — progress is saved'
          : 'Suspended — progress is saved'}
        {' '}
        <button type="button" className="btn-ghost" style={{ padding: '0 0.3rem' }} onClick={onResume}>
          Resume
        </button>{' '}
        <button type="button" className="btn-ghost btn-danger" style={{ padding: '0 0.3rem' }} onClick={onDiscard}>
          Discard
        </button>
      </div>
    )
```

`if (!p)` 那一行与末尾的 `<StopButton .../>` 都换成 `<RunButtons onStop={onStop} onSuspend={onSuspend} onRetryNow={run.pause ? onRetryNow : undefined} />`。

最后在 `return (<div className="text-faint">…` 里，`{changed}` 与按钮之间插入暂停横幅：

```tsx
      {run.pause && (
        <div className="text-warn">
          Paused — {run.pause.reason} (attempt {run.pause.failures})
          {run.pause.nextRetryAt && `; retrying ${formatRetryIn(run.pause.nextRetryAt)}`}
        </div>
      )}
```

以及一个小工具（放在 `formatDuration` 旁边）：

```tsx
// 下次重试还有多久。这个数每秒都在变，但整行本来就每秒重渲染一次（轮询），不额外起计时器。
// 已经过点了就说 "now"，而不是显示一个负数——真正放行还要等当前这一拍走到闸门。
function formatRetryIn(at: string): string {
  const seconds = Math.round((new Date(at).getTime() - Date.now()) / 1000)
  return seconds <= 0 ? 'now' : `in ${formatDuration(seconds)}`
}
```

- [ ] **Step 5: 类型检查与构建**

```bash
cd frontend && npm run build
```

Expected: 通过，无 TypeScript 报错。

- [ ] **Step 6: 界面自查**

界面文案一律英文（本仓库的硬约束），逐条对一遍：

- [ ] 正常跑时这一行有 `Suspend` 和 `Stop` 两个按钮
- [ ] 卡在瞬时错误上时多出 `Retry now`，并有一条 warn 行写着原因、第几次、下次重试还有多久
- [ ] 点 `Stop` 弹出对话框，两个动作分开摆，措辞把后果说死
- [ ] 挂起后这一行变成 warn，按钮是 `Resume` / `Discard`
- [ ] 刷新页面（模拟重启）后，`Interrupted run — N block(s)…` 那一行仍在，按钮同上
- [ ] `Discard` 的确认里说清楚"下次会重传"

- [ ] **Step 7: 提交**

```bash
git add frontend/src/api/backupConfigs.ts \
        frontend/src/components/StopBackupDialog.tsx \
        frontend/src/pages/BackupConfigsPage.tsx \
        frontend/src/index.css
git commit -m "feat(ui): surface suspend, resume, retry-now and two-mode stop

Stopping a backup now asks which kind of stop is meant, because the two
outcomes are far apart: finishing the file in flight means that work
counts and the next run picks up from there, while stopping immediately
deletes its half-uploaded volumes. One confirm() blurred them into each
other.

Pause is rendered as a line attached to a running backup rather than as
a status of its own, matching the backend: the run is still alive and
still holds its staging seat, and treating it as a status would hide the
Stop button exactly when the operator most wants it.

Interrupted runs left on disk are listed from a separate fetch on the
list refresh, since the one-second tick only polls active configs and an
interrupted run is by definition not active.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---
