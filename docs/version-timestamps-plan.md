# 版本时间戳（开始 / 结束）显示 — 实施计划

设计见 `docs/version-timestamps-design.md`。

**目标**：还原对话框的版本下拉与备份完成提示，都显示该版本的开始时刻与结束时刻
（UTC 存储、客户端时区渲染）。

**命名统一**：贯穿后端 result / run state / HTTP 响应一律用 `StartedAt` / `CompletedAt`，
其中 `CompletedAt` 就是写进 `BackupVersion.CreatedAt` 的那个值（版本提交时刻）。
前端字段 `startedAt` / `completedAt`。

## 全局约束

- 界面文案一律英文。
- 时间存储 `DateTimeOffset.UtcNow`，前端 `toLocaleString()` 渲染，不引入时区设置。
- 前端无单元测试设施，验证靠 `npm run build`（`tsc -b`）+ `npm run lint`。
- 后端全量测试 `dotnet test`，当前基线 875 passed。

---

### Task 1：模型与序列化（`StartedAt` + InfoFormat 3）

**Files**
- Modify: `backend/src/AzureStorageBackup.Api/Models/BackupIndex.cs`（`BackupVersion`）
- Modify: `backend/src/AzureStorageBackup.Api/Services/IndexSerializer.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/IndexSerializerTests.cs`

**Produces**：`BackupVersion.StartedAt`（`DateTimeOffset?`），info 二进制 format 3。

- [ ] 写失败测试：往返一个带 `StartedAt` 的版本，读回相等；再手工构造一段 format 2
      的 info 字节（版本条目无 `StartedAt`），读回 `StartedAt == null` 且其余字段完好。
- [ ] 跑测试确认失败（编译不过：`BackupVersion` 无 `StartedAt`）。
- [ ] `BackupVersion` 加：
      ```csharp
      /// <summary>本次备份开始跑的时刻（UTC）。format 3 之前写下的版本没有此信息 → null。</summary>
      public DateTimeOffset? StartedAt { get; init; }
      ```
- [ ] `IndexSerializer`：`InfoFormat` 2 → 3（注释补 `format 3: BackupVersion.StartedAt`）；
      写版本条目处加 `WriteNullableDto(w, v.StartedAt);`，读处加
      `StartedAt = format >= 3 ? ReadNullableDto(r) : null,`。
- [ ] 跑 `dotnet test --filter IndexSerializer` 确认通过。
- [ ] 提交。

---

### Task 2：编排器记录开始时刻，结果带出两个时间

**Files**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`
  （`BackupRunResult`、`RunAsync`、`RunCoreAsync`、`info.Versions.Add` 处）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/`（编排器既有测试文件）

**Consumes**：Task 1 的 `BackupVersion.StartedAt`。
**Produces**：`BackupRunResult.StartedAt` / `.CompletedAt`（均 `DateTimeOffset`）。

- [ ] 写失败测试：跑一次备份，断言 `info.Versions[^1].StartedAt` 非 null 且
      `<= CreatedAt`；断言 `result.StartedAt == version.StartedAt` 且
      `result.CompletedAt == version.CreatedAt`（两处同源，不是各取各的时钟）。
- [ ] 跑测试确认失败。
- [ ] `BackupRunResult` 加两个 init 属性 `StartedAt` / `CompletedAt`，注释写明
      `CompletedAt` = 版本提交时刻，收尾清理不计入。
- [ ] `RunAsync` 入口取 `var startedAt = DateTimeOffset.UtcNow;`（在 BackupStart 上报之前），
      传入 `RunCoreAsync(request, startedAt, progress, ct)`。
- [ ] `RunCoreAsync` 里 `info.Versions.Add` 处：`StartedAt = startedAt`，
      `CreatedAt` 改为先算出的局部变量 `completedAt`，`BackupRunResult` 用同一个值。
- [ ] 跑测试确认通过。
- [ ] 提交。

---

### Task 3：HTTP 契约（`/versions` 与 run 状态）

**Files**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs:497`（versions 投影）
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs`
  （`BackupRunState`、`BackupRunResponse`、完成分支 211-213 行附近）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/`（端点测试）

**Consumes**：Task 2 的 `BackupRunResult.StartedAt` / `.CompletedAt`。
**Produces**：`/versions` 项含 `startedAt`；run 响应含 `startedAt` / `completedAt`。

- [ ] 写失败测试：`GET /versions` 返回的项含 `startedAt`；备份跑完后
      `GET /run` 的响应 `startedAt` / `completedAt` 与该版本一致。
- [ ] 跑测试确认失败。
- [ ] versions 投影加 `v.StartedAt`。
- [ ] `BackupRunState` 加 `DateTimeOffset? StartedAt` / `CompletedAt`；完成分支填入
      `result.StartedAt` / `result.CompletedAt`；`BackupRunResponse` 加两个字段并在 `From` 传递。
- [ ] 跑全量 `dotnet test` 确认通过。
- [ ] 提交。

---

### Task 4：前端格式化函数与类型

**Files**
- Modify: `frontend/src/constants/format.ts`
- Modify: `frontend/src/api/backupConfigs.ts`（`BackupVersionInfo`、`BackupRun`）

**Produces**：`formatVersionSpan(startedAt: string | null, completedAt: string): string`。

- [ ] `BackupVersionInfo` 加 `startedAt: string | null`；`BackupRun` 加
      `startedAt: string | null` 与 `completedAt: string | null`。
- [ ] `format.ts` 加：
      ```ts
      /** 版本的起止时刻。同一本地日期只写一次日期；跨日两侧都写全；无开始时刻写「—」。 */
      export function formatVersionSpan(startedAt: string | null, completedAt: string): string {
        const end = new Date(completedAt)
        if (!startedAt) return `— → ${end.toLocaleString()}`
        const start = new Date(startedAt)
        const sameDay = start.toLocaleDateString() === end.toLocaleDateString()
        return `${start.toLocaleString()} → ${sameDay ? end.toLocaleTimeString() : end.toLocaleString()}`
      }
      ```
- [ ] 跑 `npm run build` 确认类型通过。
- [ ] 提交。

---

### Task 5：还原对话框与完成提示显示

**Files**
- Modify: `frontend/src/components/RestoreDialog.tsx:29,64,301-306`
- Modify: `frontend/src/pages/BackupConfigsPage.tsx:1216-1226`（`RunStatus` 的 Completed 分支）

**Consumes**：Task 4 的 `formatVersionSpan` 与类型。

- [ ] `RestoreDialog`：`versions` state 类型改为 `BackupVersionInfo[]`，`useEffect` 里
      不再 `.map(v => v.version)`；option 文案
      `Version {v.version} — {formatVersionSpan(v.startedAt, v.createdAt)}`；
      `Latest` 选项保持首位不变。
- [ ] `BackupConfigsPage` 的 Completed 分支：版本号后接
      `{run.completedAt && ` (${formatVersionSpan(run.startedAt, run.completedAt)})`}`
      （老后端无此字段时退化为只显示编号）；既有的 unreadable 追加段落原样保留。
- [ ] 跑 `npm run build` 与 `npm run lint` 确认通过。
- [ ] 提交。

---

### Task 6：收尾

- [ ] 跑全量 `dotnet test`（预期 875+ 全绿）与 `npm run build`。
- [ ] 把设计文档一并提交，合并进 `main`（仓库只留 main 一条线）。
