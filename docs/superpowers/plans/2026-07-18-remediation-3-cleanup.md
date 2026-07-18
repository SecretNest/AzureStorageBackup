# 备份修复 Plan 3 — 低危清理 + CI（⚪）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 清掉 9 项低危债务：可配阈值、死代码、日志 account 维度、通知 content-type、删配置清 verbose、组成员排序、前端重复 label、端点测试补全、CI 起 Azurite。

**Architecture:** 逐项 TDD、各自 commit。多为定点小改。最后一项（CI Azurite）让全部 `[Integration]` 测试在 CI 实跑，是对整套修复的兜底验证。

**Tech Stack:** .NET 10, xUnit, Azurite；Vite+React+TS；GitHub Actions。

## Global Constraints

- 界面/日志文案英文；代码注释中文。
- 收尾门槛：`dotnet build -c Release` 0 警告；非集成单测全绿；前端 build/lint 干净；CI 中集成测试实跑绿。
- 每个 Task 结束独立 commit。**前置：Plan 1、Plan 2 已完成。**

---

### Task 1: ProcessingVerifier MaxAttempts 可配（§5.1）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Models/GlobalSettings.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/GlobalSettingsService.cs`
- Modify: 编排器构造 `ProcessingOptions` 处（传入 MaxAttempts）
- Migration + 前端 Settings 字段
- Test: `backend/tests/AzureStorageBackup.Api.Tests/GlobalSettingsServiceTests.cs`（或既有）

- [ ] **Step 1: 写失败测试——设置往返含 ProcessingMaxAttempts**

```csharp
    [Fact]
    public async Task Upsert_Persists_ProcessingMaxAttempts()
    {
        var s = await svc.GetAsync();
        s.ProcessingMaxAttempts = 8;
        await svc.UpsertAsync(s);
        Assert.Equal(8, (await svc.GetAsync()).ProcessingMaxAttempts);
    }
```

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~Upsert_Persists_ProcessingMaxAttempts"`
Expected: FAIL（字段不存在）。

- [ ] **Step 3: 实现**

`GlobalSettings` 增 `public int ProcessingMaxAttempts { get; set; } = 5;`；`UpsertAsync` 补赋值；生成迁移 `AddProcessingMaxAttempts`。编排器构造 `ProcessingOptions`（现默认 `MaxAttempts=5`）改为读设置值透传。

- [ ] **Step 4: 前端 Settings 字段**

增 "Processing re-verify max attempts" 数字输入。

- [ ] **Step 5: 运行 + 前端**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~GlobalSettingsServiceTests"` ; `cd frontend && npm run build && npm run lint`
Expected: PASS / 干净。

- [ ] **Step 6: Commit**

```bash
git add backend frontend
git commit -m "feat: ProcessingVerifier max attempts configurable via settings (§5.1)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: 删除死代码 DeadWeightAnalyzer + UploadBatchAsync（§5.2）

**Files:**
- Delete: `backend/src/AzureStorageBackup.Api/Services/DeadWeightAnalyzer.cs`（若确认无引用）
- Modify: `backend/src/AzureStorageBackup.Api/Services/BlobUploader.cs`（删 `UploadBatchAsync`）
- Delete/Modify: 对应测试文件（若测试仅覆盖被删代码）

- [ ] **Step 1: 确认零引用**

Run:
```bash
grep -rn "DeadWeightAnalyzer" backend/src backend/tests
grep -rn "UploadBatchAsync" backend/src backend/tests
```
Expected: 仅定义处（及其单元测试）出现，无生产调用方。若 `DeadWeightAnalyzer` 有测试，评估该测试是否仍有价值（逻辑已由 `DeadWeightCompactor` 覆盖）→ 一并删除。

- [ ] **Step 2: 删除**

删 `DeadWeightAnalyzer.cs` 及其测试；从 `BlobUploader` 删 `UploadBatchAsync` 及其测试。检查 DI 注册无 `DeadWeightAnalyzer`。

- [ ] **Step 3: 编译 + 全测**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "Category!=Integration"`
Expected: 0 警告 + 全绿（无残留引用）。

- [ ] **Step 4: Commit**

```bash
git add -A backend
git commit -m "chore: remove dead code DeadWeightAnalyzer + BlobUploader.UploadBatchAsync (§5.2)

Superseded by DeadWeightCompactor / per-item concurrent upload; no callers.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: 操作日志来源含 account 维度（§5.3）

**Files:**
- Modify: `BackupOrchestrator.cs`/`RestoreOrchestrator.cs`/`BackupChecker.cs`/`BackupRepairer.cs`/`TaskDispatcher.cs`（`source` 构造处）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/OperationLogSourceTests.cs`（新建或并入既有）

**Interfaces:**
- Produces: 统一 source 格式 `"{op}:{accountId}/{container}"`（如 `check:3/photos`），便于按 account 过滤。

- [ ] **Step 1: 写失败测试——日志 source 含 account**

以 `RestoreOrchestrator`/`BackupChecker` 为例，注入 spy `IOperationLog` 断言 `source` 含 accountId。选一个已能纯构造的引擎（如 `BackupChecker` 可注入 fake store）或用集成断言查询。示例（spy）：

```csharp
    [Fact]
    public async Task Check_Log_Source_Includes_Account_Id()
    {
        var log = new RecordingOperationLog();
        var checker = new BackupChecker(factory, store, opLog: log /* ... */);
        try { await checker.CheckAsync(account /* Id=3 */, "photos", null, null, options); } catch { }
        Assert.Contains(log.Entries, e => e.Source == "check:3/photos");
    }
```

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~Check_Log_Source_Includes_Account_Id"`
Expected: FAIL（现为 `check:{container}`）。

- [ ] **Step 3: 实现**

各引擎的 `source = $"...:{container}"` 改为携带 account：因这些方法多已有 `Account account` 或 `container` 参数，改为 `$"check:{account.Id}/{container}"` 等。`TaskDispatcher` 同步。确认 `Account.Id` 在各方法可得（restore/backup 经 request.Account，checker/repairer 经 account 参数）。

- [ ] **Step 4: 运行**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "Category!=Integration"`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add backend
git commit -m "feat: operation-log source includes account id (§5.3)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: 通知 content-type 容忍 charset（§5.4）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/NotificationSender.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/NotificationSenderTests.cs`

- [ ] **Step 1: 写失败测试——content-type 带 charset 不抛**

```csharp
    [Fact]
    public async Task Post_With_Charset_Content_Type_Does_Not_Throw()
    {
        var sender = new NotificationSender(/* HttpClient to a stub */);
        // content-type = "application/json; charset=utf-8"
        var ex = await Record.ExceptionAsync(() => sender.SendAsync(new NotificationRequest {
            Method = "POST", Url = stubUrl, Body = "{}", ContentType = "application/json; charset=utf-8" }, default));
        Assert.Null(ex);  // 现会因 Headers.ContentType 直接赋值抛 FormatException
    }
```

> 按 `NotificationSender` 实际的请求构造 API 调整测试构造方式。

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~Post_With_Charset_Content_Type"`
Expected: FAIL（FormatException）。

- [ ] **Step 3: 实现——用 MediaTypeHeaderValue.Parse**

设置 content 时：

```csharp
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
```

替换直接字符串赋值（`new StringContent(body, Encoding.UTF8, contentType)` 在 contentType 带参时会抛；改为先建 `StringContent(body)` 再 `Headers.ContentType = MediaTypeHeaderValue.Parse(contentType)`）。

- [ ] **Step 4: 运行**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~NotificationSenderTests"`
Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add backend
git commit -m "fix: notification content-type tolerates charset parameter (§5.4)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: 删配置连带清 verbose/debug 日志（§5.5）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/OperationLogService.cs`（`DeleteForContainerAsync` 覆盖全等级）或删配置端点
- Test: `backend/tests/AzureStorageBackup.Api.Tests/OperationLogServiceTests.cs`

- [ ] **Step 1: 写失败测试——删 container 日志含 Debug/verbose**

```csharp
    [Fact]
    public async Task DeleteForContainer_Removes_All_Levels_Including_Debug()
    {
        await log.AppendAsync(LogLevel.Debug, "backup:3/c", "verbose file x", default);
        await log.AppendAsync(LogLevel.Warning, "backup:3/c", "done", default);
        await log.DeleteForContainerAsync(/* 该配置对应的 source 前缀或 account/container */);
        var remaining = await log.QueryAsync(new LogQuery { /* source=backup:3/c */ });
        Assert.Empty(remaining);
    }
```

> 若 `DeleteForContainerAsync` 现按 source 精确/前缀匹配，确认 verbose(Debug) 条目 source 与非 verbose 一致（Task 3 已统一为 `op:{acct}/{container}`）——若一致则本项可能已被覆盖，测试用于回归锁定；若 verbose 用了不同 source 则修正删除条件。

- [ ] **Step 2: 运行验证失败/通过**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~DeleteForContainer_Removes_All_Levels"`
Expected: 若失败→修实现；若已通过→本项转为回归测试，直接 Step 4 提交。

- [ ] **Step 3: 实现（如需要）**

确保 `DeleteForContainerAsync` 删除条件覆盖全部等级（不因 Ephemeral/Debug 遗漏）与该配置全部 source 前缀。

- [ ] **Step 4: 运行 + Commit**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~OperationLogServiceTests"`
```bash
git add backend
git commit -m "fix: deleting a backup config purges all its logs incl. debug/verbose (§5.5)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: 组成员稳定排序（§5.6）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/GroupService.cs`（或组成员查询处）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/GroupServiceTests.cs`

- [ ] **Step 1: 写失败测试——成员按稳定序返回**

```csharp
    [Fact]
    public async Task Group_Members_Are_Returned_In_Stable_Order()
    {
        // 乱序加入成员 (container: "c", "a", "b")
        var g = await svc.GetAsync(groupId);
        Assert.Equal(new[] { "a", "b", "c" }, g!.Members.Select(m => m.ContainerName).ToArray());
    }
```

> 稳定序按 (AccountId, ContainerName) 或既有 Order 字段。按实际模型选键。

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~Group_Members_Are_Returned_In_Stable_Order"`
Expected: FAIL（现按插入/DB 默认序）。

- [ ] **Step 3: 实现**

组成员查询加 `.OrderBy(m => m.AccountId).ThenBy(m => m.ContainerName)`。

- [ ] **Step 4: 运行 + Commit**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~GroupServiceTests"`
```bash
git add backend
git commit -m "fix: group members returned in stable order (§5.6)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: 前端合并重复 label 字典（§5.7）

**Files:**
- Create: `frontend/src/constants/labels.ts`
- Modify: `frontend/src/pages/BackupConfigsPage.tsx` + `frontend/src/api/backupConfigs.ts`（引用统一常量）

- [ ] **Step 1: 定位重复**

Run: `grep -rn "By version count\|EitherTriggers\|RetentionMode\|Hot\|Cool\|Cold\|Archive" frontend/src | grep -i label`
找出散落的枚举→label 映射（RetentionMode、StorageTier 等）。

- [ ] **Step 2: 抽单一常量模块**

`labels.ts` 导出 `RETENTION_MODE_LABELS`、`STORAGE_TIER_LABELS` 等；各处 import 复用，删重复定义。

- [ ] **Step 3: 构建 + lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 干净（无未使用/重复）。

- [ ] **Step 4: Commit**

```bash
git add frontend
git commit -m "refactor: consolidate duplicated enum label maps into constants/labels.ts (§5.7)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: 补 HTTP 端点级测试（§5.8）

**Files:**
- Modify/Create: `backend/tests/AzureStorageBackup.Api.Tests/BackupConfigEndpointsTests.cs`

**Interfaces:** 覆盖 `/check`、`/repair`、`/versions`、`/file-versions`、`/unrecoverable`、`/tree`（Plan2 T7）、`/restore-estimate`（Plan2 T8）、`/reset-status`（Plan2 T4）。

- [ ] **Step 1: 写端点测试（WebApplicationFactory）**

对每个端点写至少一条：正常返回 2xx + 形状正确；无效 id → 404；需要 Azurite 的走 `[Integration]`+Skip。示例：

```csharp
    [Fact]
    public async Task Reset_Status_Endpoint_Returns_NoContent_And_Clears_Error()
    {
        // 预置 config Status=Error
        var resp = await client.PostAsync($"/api/backup-configs/{id}/reset-status", null);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        // GET 配置断言 status=Normal
    }

    [Fact]
    public async Task Tree_Endpoint_Returns_Root_Children()
    {
        // 预置一个版本索引（本地缓存），GET /tree?version=1 → 200 + 节点数组
    }
```

为每个端点补一条，形状断言用返回 JSON 字段。

- [ ] **Step 2: 运行**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~BackupConfigEndpointsTests"`
Expected: PASS（Azurite 缺失则集成条 Skip，纯本地条绿）。

- [ ] **Step 3: Commit**

```bash
git add backend
git commit -m "test: HTTP endpoint coverage for check/repair/versions/file-versions/unrecoverable/tree/estimate/reset-status (§5.8)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: CI 起 Azurite 使集成测试实跑（§5.9）

**Files:**
- Modify: `.github/workflows/*.yml`（CI 工作流）
- Test: 无新测试；目标是让既有 `[Integration]` 在 CI 实跑绿。

- [ ] **Step 1: 定位 CI 工作流 + 当前测试步骤**

Run: `ls .github/workflows && grep -rn "dotnet test\|azurite\|Azurite" .github/workflows`
确认现在测试步骤是否跳过集成、有无 Azurite。

- [ ] **Step 2: 加 Azurite 启动步骤**

在 backend 测试步骤前启动 Azurite（与本地约定一致，npm 方式）：

```yaml
      - name: Start Azurite
        run: |
          npx -y -p azurite azurite-blob --skipApiVersionCheck --blobPort 10000 &
          for i in $(seq 1 30); do curl -s http://127.0.0.1:10000/devstoreaccount1 && break || sleep 1; done
      - name: Test (incl. integration)
        run: cd backend && dotnet test -c Release
        env:
          # 若集成测试用连接串/端点环境变量，在此提供 devstoreaccount1 well-known。
```

确保运行器已装 7-Zip（`7zz`/`7z`），集成测试需要——加安装步骤（如 `sudo apt-get install -y 7zip` 或对应）。

- [ ] **Step 3: 本地/CI 验证**

推分支触发 CI，确认集成测试**实跑**（非 Skip）且绿。若本地可跑 Azurite：`cd backend && dotnet test -c Release` 全跑一遍确认。

- [ ] **Step 4: Commit**

```bash
git add .github/workflows
git commit -m "ci: run Azurite + 7-Zip so integration tests actually execute (§5.9)

Engine main-path (orchestrator/checker/restore/repairer/compactor) had zero
verification in CI because integration tests were all skipped without Azurite.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## 收尾验证（全部三 Plan 完成后）

- [ ] `cd backend && dotnet build -c Release`：0 警告。
- [ ] `cd backend && dotnet test -c Release`（Azurite + 7-Zip 在场）：全绿，集成实跑。
- [ ] `cd frontend && npm run build && npm run lint`：干净。
- [ ] 真实进程冒烟：并发两 container 备份（验证 §2 无损坏）、选择性还原 + RenameKeep（§4.1c）、状态徽标 Error→成功自清（§4.2）、临时区尺寸改后生效（§4.7）、云端列表检查发现孤儿并修复删除（§4.8）。
- [ ] 更新 `memory/backup-audit-gaps.md`：标注各项已修复。

## Self-Review 覆盖对照

- §5.1→T1 §5.2→T2 §5.3→T3 §5.4→T4 §5.5→T5 §5.6→T6 §5.7→T7 §5.8→T8 §5.9→T9 ✅
- T3 统一 source 格式后，T5（删日志）与 T8（端点测试）依赖该格式——顺序 T3 在前。
- T8 覆盖 Plan 2 新增端点（tree/estimate/reset-status），故 Plan 3 在 Plan 2 之后。
