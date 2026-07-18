# 备份修复 Plan 2 — 需求缺口（🟡）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 补齐 8 组需求缺口：临时区尺寸可配、目录规则、云端列表检查+孤儿回收、备份状态持久化+reset、锁定基础字段、删 container、选择性还原（懒加载树+估算+冲突模式+Rehydrate 优先级）、向导立即备份。

**Architecture:** 后端为主，逐项 TDD、各自 commit。新增列走 EF 迁移（`dotnet ef migrations add`）。前端跟随既有 `BackupConfigsPage.tsx`/`api/backupConfigs.ts`/Settings 模式。

**Tech Stack:** .NET 10, EF Core (SQLite, migrations), xUnit, Azurite（集成）；Vite+React+TS。

## Global Constraints

- 界面/日志文案英文；代码注释中文（随既有风格）。
- 单用户、无认证；Azure 一律 Blob；本地权威（尽量不读云端）。
- 新增 EF 列必须生成迁移：`dotnet ef migrations add <Name> --project src/AzureStorageBackup.Api`（启动 `Migrate()` 自动应用）。
- 收尾门槛：`dotnet build -c Release` 0 警告；非集成单测全绿；前端 `npm run build && npm run lint` 干净。
- 每个 Task 结束独立 commit。**前置：Plan 1 已完成。**

---

### Task 1: 临时区上限运行时可配（§4.7，决策 4）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Models/GlobalSettings.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/StagingArea.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/GlobalSettingsService.cs`（Upsert 补字段）
- Modify: DI 注册（`Program.cs`）
- Modify: `frontend/src/pages/SettingsPage.tsx`（或对应 Settings 组件）+ `frontend/src/api/*`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/StagingAreaTests.cs`

**Interfaces:**
- Produces: `StagingArea` 构造从 `long stagedLimitBytes` 改为 `Func<long> stagedLimit`；背压判断实时读。`GlobalSettings.StagedLimitBytes`（默认 2GB）。

- [ ] **Step 1: 写失败测试——limit provider 变化即时影响背压**

在 `StagingAreaTests` 增（并把辅助 `Area` 改为接受 provider）：

```csharp
    private StagingArea AreaP(Func<long> limit) => new(_compressTemp, _stagedTemp, limit);

    [Fact]
    public async Task Backpressure_Reads_Limit_Live_From_Provider()
    {
        long limit = 100;                      // 初始极小上限
        using var area = AreaP(() => limit);

        // 首个结果允许临时超限（从上限以下起步）。
        var first = await area.StageAsync(Produce("a", 500));
        Assert.Equal(500, area.StagedBytes);   // 已超过 100

        // 第二个压缩应被背压阻塞（StagedBytes 500 >= limit 100）。
        var blocked = area.StageAsync(Produce("b", 10));
        Assert.False(blocked.IsCompleted);

        // 调大上限 → 唤醒需要一次 Release 触发信号；这里改为先 Release 首个腾出空间。
        area.Release(first);                   // StagedBytes -> 0，唤醒
        var second = await blocked;
        Assert.Equal(10, area.StagedBytes);
    }
```

> 注：`StagingArea` 现有唤醒依赖 `Release` 的 `_releaseSignal`。provider 调大后若无 Release 不会自动唤醒——这符合"下次 acquire/Release 时读新值"的立即生效语义（决策 4：每次判断实时读）。测试用 Release 触发重判，验证读的是 provider 当前值。

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~StagingAreaTests.Backpressure_Reads_Limit_Live"`
Expected: FAIL（构造签名不匹配 / 编译错误）。

- [ ] **Step 3: 实现——StagingArea 用 provider**

```csharp
public sealed class StagingArea(string compressTempDir, string stagedTempDir, Func<long> stagedLimit) : IDisposable
{
    // ...
        // 背压：每次实时读当前上限（决策 4 立即生效）。
        while (Interlocked.Read(ref _stagedBytes) >= stagedLimit())
            await _releaseSignal.WaitAsync(ct);
    // ...
}
```

更新 `StagingAreaTests` 里其余 `Area(limit)` 调用为 `AreaP(() => limit)`（或保留 `Area(long)` 重载包装成 `() => limit`）。

- [ ] **Step 4: GlobalSettings 加字段 + Upsert + 迁移**

`GlobalSettings.cs` 增：

```csharp
    /// <summary>压缩临时区（staged-temp）字节上限，背压阈值（决策 4，可经 Settings 实时改）。默认 2GB。</summary>
    public long StagedLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;
```

`GlobalSettingsService.UpsertAsync` 补 `existing.StagedLimitBytes = s.StagedLimitBytes;`。

生成迁移：

```bash
cd backend && dotnet ef migrations add AddStagedLimitBytes --project src/AzureStorageBackup.Api
```

- [ ] **Step 5: DI——StagingArea 从设置读 provider**

`Program.cs` 注册 `StagingArea` 单例处，把固定 `Backup:StagedLimitBytes` 改为 provider：

```csharp
builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var tempPath = cfg["Backup:TempPath"] ?? Path.Combine(Path.GetTempPath(), "asb");
    var compress = Path.Combine(tempPath, "compress");
    var staged = Path.Combine(tempPath, "staged");
    // 上限实时从 GlobalSettings 读（带 scope，短读一次）。带回退默认 2GB。
    long Limit()
    {
        using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IGlobalSettingsService>().GetAsync().GetAwaiter().GetResult();
        return settings.StagedLimitBytes > 0 ? settings.StagedLimitBytes : 2L * 1024 * 1024 * 1024;
    }
    return new StagingArea(compress, staged, Limit);
});
```

> 若既有注册用 `Backup:StagedLimitBytes` 作初值，可在迁移后一次性把该值写入 GlobalSettings；否则默认 2GB 即可。`GetAwaiter().GetResult()` 在此单例工厂可接受（每次压缩判断一次，非热路径）。

- [ ] **Step 6: 前端 Settings 加字段**

`api/*` 设置类型加 `stagedLimitBytes`（以 MB 展示，提交时 ×1024×1024）。SettingsPage 增 "Staging area size limit (MB)" 数字输入，跟随既有 Upload/Download concurrency 字段样式。

- [ ] **Step 7: 运行 + 前端构建**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~StagingAreaTests"`
Run: `cd frontend && npm run build && npm run lint`
Expected: PASS / 干净。

- [ ] **Step 8: Commit**

```bash
git add backend frontend
git commit -m "feat: staging area size limit configurable at runtime via Settings (§4.7)

StagingArea reads its backpressure limit live from GlobalSettings
(default 2GB) instead of a startup-fixed value.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: 不压缩/不分组"目录模式"祖先匹配（§4.4）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/IgnoreRuleSet.cs`（增祖先匹配助手）
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`（DontCompress/DontGroup 判定点）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/IgnoreRuleSetTests.cs`

**Interfaces:**
- Produces: `IgnoreRuleSet.MatchesFileOrAncestorDir(string relativePath) : bool`——文件自身命中，或任一祖先目录以 `isDirectory:true` 命中。

- [ ] **Step 1: 写失败测试——目录规则命中其下文件**

```csharp
    [Fact]
    public void Directory_Rule_Matches_Files_Beneath_It()
    {
        var rules = IgnoreRuleSet.Parse("logs/\n*.iso");   // 目录规则 + 文件规则
        Assert.True(rules.MatchesFileOrAncestorDir("logs/app.log"));   // 祖先目录 logs/ 命中
        Assert.True(rules.MatchesFileOrAncestorDir("a/logs/b/c.bin")); // 深层祖先命中
        Assert.True(rules.MatchesFileOrAncestorDir("disk.iso"));       // 文件规则直接命中
        Assert.False(rules.MatchesFileOrAncestorDir("src/main.cs"));   // 不命中
    }
```

> 确认 `IgnoreRuleSet` 的构造/解析 API 名（`Parse` 或 ctor）与 `IsIgnored(path, isDirectory)` 签名，测试按实际调整。

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~IgnoreRuleSetTests.Directory_Rule_Matches_Files_Beneath_It"`
Expected: FAIL（`MatchesFileOrAncestorDir` 不存在）。

- [ ] **Step 3: 实现祖先匹配**

```csharp
    /// <summary>文件是否命中：自身以文件判定命中，或任一祖先目录以目录判定命中
    /// （使 `logs/` 这类目录规则对其下文件生效，与忽略列表按目录遍历的行为一致）。</summary>
    public bool MatchesFileOrAncestorDir(string relativePath)
    {
        if (IsIgnored(relativePath, isDirectory: false))
            return true;
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = "";
        for (var i = 0; i < parts.Length - 1; i++) // 逐级祖先目录
        {
            prefix = prefix.Length == 0 ? parts[i] : prefix + "/" + parts[i];
            if (IsIgnored(prefix, isDirectory: true))
                return true;
        }
        return false;
    }
```

- [ ] **Step 4: 编排器改用祖先匹配**

在 `BackupOrchestrator` 判定 DontCompress/DontGroup 的每一处（如 `HandleBlobAsync` 的 `storeOnly = request.Options.DontCompress?.IsIgnored(file.Path)`），改为 `?.MatchesFileOrAncestorDir(file.Path)`。同理 DontGroup 在分组阶段的判定（`GroupingPlanner` 入参或编排器传入前的过滤）——grep `DontCompress`/`DontGroup`/`IsIgnored` 定位全部调用点统一替换。

- [ ] **Step 5: 运行**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~IgnoreRuleSetTests"`
Expected: PASS。

- [ ] **Step 6: Commit**

```bash
git add backend
git commit -m "fix: don't-compress/don't-group directory rules match files beneath them (§4.4)

'logs/' style rules were checked per-file with isDirectory=false and never
matched. Add MatchesFileOrAncestorDir; orchestrator uses it for
DontCompress/DontGroup, matching the ignore-list directory semantics.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: 云端列表检查 + 孤儿回收（§4.8）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/CheckOptions.cs`（或其定义处）+ `CheckReport`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupChecker.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRepairer.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`（`/check`、`/repair` 参数）
- Modify: `frontend/src/pages/BackupConfigsPage.tsx` + `frontend/src/api/backupConfigs.ts`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupCheckerTests.cs`

**Interfaces:**
- Produces:
  - `CheckOptions` 增 `bool ListOrphans` (默认 false)。
  - `CheckReport` 增 `IReadOnlyList<string> OrphanBlobs`（默认空）。
  - `BackupChecker.BuildReferencedSetAsync(account, container, password, info, ct) : HashSet<string>`——全部保留版本引用的 blob 名（信息文件 + 各 IndexBlob + 各 StorageRef 全部分卷）。
  - `RepairReport` 增 `IReadOnlyList<string> DeletedOrphans`。

- [ ] **Step 1: 写失败测试——引用集构造正确（纯逻辑）**

孤儿判定的核心是"引用集"。把它做成可单测的纯函数 `ReferencedBlobNames(BackupInfoFile info, IReadOnlyDictionary<int,VersionIndex> indexes)`：

```csharp
    [Fact]
    public void Referenced_Set_Includes_Info_Indexes_And_All_Volumes_Across_Versions()
    {
        var info = /* 2 版本：v1 IndexBlob=idx/1, v2 IndexBlob=idx/2；pack p1 3 卷；data/h 单卷 */;
        var indexes = /* v1 引用 pack p1 成员；v2 引用 data/h */;
        var refs = BackupChecker.ReferencedBlobNames(info, indexes);
        Assert.Contains("idx/1", refs);
        Assert.Contains("idx/2", refs);
        Assert.Contains("packs/p1.7z.001", refs);
        Assert.Contains("packs/p1.7z.002", refs);
        Assert.Contains("packs/p1.7z.003", refs);
        Assert.Contains("data/h", refs);       // 只被旧版本引用者也在集内
    }
```

`ReferencedBlobNames`：
- 加 info 文件 blob 名（`{container}` 约定的信息文件名；从 `IBackupInfoStore` 取或按约定 `backup.json`/`.enc`——参考 `BackupDiscovery` 的信息文件名常量）。
- 加每个 `info.Versions[].IndexBlob`。
- 遍历每个版本索引每个 `StorageRef`：单卷 blob = `Ref`（+ `.001..Volumes`）；pack = `packs/{Ref}.7z`（+ `.001..PackInfo.Volumes`）。分卷名生成复用 `VolumeBlobIO` 的命名规则。

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~Referenced_Set_Includes"`
Expected: FAIL（方法不存在）。

- [ ] **Step 3: 实现 ReferencedBlobNames + BuildReferencedSetAsync + 列表检查**

实现纯函数 `ReferencedBlobNames`。`BuildReferencedSetAsync` 读全部保留版本索引（本地缓存优先）后调它。`CheckCoreAsync` 在 `options.ListOrphans` 时：枚举 container 全部 blob（`cc.GetBlobsAsync`）− 引用集 = `OrphanBlobs`，填入 `CheckReport`。`Ok` 不因孤儿变 false。

- [ ] **Step 4: 写失败测试——修复删除孤儿、保留被引用者（集成）**

```csharp
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Repair_Deletes_Orphans_But_Keeps_Referenced_And_Info_Index()
    {
        Skip.IfNot(await AzuriteFixture.IsAvailableAsync(), "Azurite not running");
        // 建 v1（pack 多卷 + data blob）。手动往 container 塞一个 garbage blob "data/ZZZ" + 残余旧卷 "packs/p1.7z.099"。
        var check = await checker.CheckAsync(account, container, null, null, options with { ListOrphans = true }, localRoot);
        Assert.Contains("data/ZZZ", check.OrphanBlobs);
        var report = await repairer.RepairAsync(account, container, null, localRoot, null, options with { ListOrphans = true }, AccessTier.Hot, null);
        Assert.Contains("data/ZZZ", report.DeletedOrphans);
        // 引用 blob 与信息/索引仍在：
        Assert.True(await cc.GetBlobClient("data/ZZZ").ExistsAsync() is { Value: false });
        // pack 首卷仍在：
        Assert.True((await cc.GetBlobClient("packs/p1.7z.001").ExistsAsync()).Value);
    }
```

- [ ] **Step 5: 实现修复删除孤儿（TOCTOU 安全）**

`BackupRepairer.RepairAsync`：当传入 `ListOrphans` 时，删除前**重新**读信息文件 + 全部版本索引构引用集，`orphans = 实际列出 − 引用集`，逐个 `DeleteIfExistsAsync`（含分卷）；填 `DeletedOrphans`。若无法取全引用集（缺版本索引且云端读失败）→ 放弃删除、记 Warning 操作日志。绝不删信息文件/索引/被引用卷。

- [ ] **Step 6: 端点 + 前端**

`/check` 增 `listOrphans=bool` 参 → `CheckOptions.ListOrphans`；`CheckReport` DTO 回传 `orphanBlobs`。`/repair` 增 `cleanupOrphans=bool`。前端检查 UI 增"Detect unreferenced blobs"勾选、结果展示孤儿数/列表；修复对话框"Delete unreferenced blobs"选项。

- [ ] **Step 7: 运行**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~BackupCheckerTests"` ; `cd frontend && npm run build && npm run lint`
Expected: PASS / 干净。

- [ ] **Step 8: Commit**

```bash
git add backend frontend
git commit -m "feat: cloud list-check detects orphan blobs; repair deletes them safely (§4.8)

Enumerate container blobs minus the referenced set (info + every retained
version's index + all storage volumes) to find unreferenced garbage
(incl. stale volumes from non-atomic replace). Repair deletes them after
re-reading the reference set (TOCTOU-safe); never touches referenced data.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: 备份状态持久化 + 派生瞬时态 + reset（§4.2，决策 2）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Models/BackupConfig.cs`（Status/LastError 列）
- Create: `backend/src/AzureStorageBackup.Api/Models/BackupStatus.cs`（枚举）
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupConfigService.cs`（写状态方法）
- Modify: `BackupRunner`/`RestoreRunner`/`RepairRunner` + `TaskDispatcher` + `/check` 端点（写状态点）
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`（DTO 派生 + reset 端点）
- Migration + 前端徽标
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupConfigStatusTests.cs`（新建）

**Interfaces:**
- Produces:
  - `enum BackupStatus { Normal = 0, Error = 1 }`。
  - `BackupConfig.Status`（默认 Normal）、`LastError` (string?)、`LastErrorAt` (DateTimeOffset?)。
  - `IBackupConfigService.SetErrorAsync(int id, string message, ct)` / `SetNormalAsync(int id, ct)`。
  - configs DTO 增 `status`（持久）+ `activity`（派生：`Idle/BackingUp/Restoring/Checking/Repairing`）+ `lastError`。
  - `POST /api/backup-configs/{id}/reset-status`。

- [ ] **Step 1: 写失败测试——失败置 Error、成功自清 Normal、reset 清错**

```csharp
    [Fact]
    public async Task Failure_Sets_Error_Success_Clears_To_Normal_Reset_Clears()
    {
        // 用内存 sqlite 建 config。
        await svc.SetErrorAsync(id, "boom");
        var c1 = await svc.GetAsync(id);
        Assert.Equal(BackupStatus.Error, c1!.Status);
        Assert.Equal("boom", c1.LastError);

        await svc.SetNormalAsync(id);              // 成功自清（决策 2）
        var c2 = await svc.GetAsync(id);
        Assert.Equal(BackupStatus.Normal, c2!.Status);
        Assert.Null(c2.LastError);

        await svc.SetErrorAsync(id, "again");
        await svc.ResetStatusAsync(id);            // 手动 reset
        Assert.Equal(BackupStatus.Normal, (await svc.GetAsync(id))!.Status);
    }
```

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~BackupConfigStatusTests.Failure_Sets_Error"`
Expected: FAIL（列/方法不存在）。

- [ ] **Step 3: 加枚举 + 列 + service 方法 + 迁移**

`BackupStatus.cs`：

```csharp
namespace AzureStorageBackup.Api.Models;

/// <summary>备份配置的持久状态（决策 2）。瞬时态（备份中/还原中…）由 runner 派生，不落库。</summary>
public enum BackupStatus { Normal = 0, Error = 1 }
```

`BackupConfig` 增：

```csharp
    public BackupStatus Status { get; set; } = BackupStatus.Normal;
    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
```

`BackupConfigService` 增 `SetErrorAsync`/`SetNormalAsync`（=`ResetStatusAsync`；成功自清与手动 reset 同实现）。

```bash
cd backend && dotnet ef migrations add AddBackupConfigStatus --project src/AzureStorageBackup.Api
```

- [ ] **Step 4: 写状态点接线**

`BackupRunner`/`RestoreRunner`/`RepairRunner` 完成时：成功 → `SetNormalAsync`；异常 → `SetErrorAsync(id, ex.Message)`。`TaskDispatcher` 各目标运行后同样回写（按 config id）。`/check` 端点完成/失败回写。用各自 scope 的 `IBackupConfigService`。

> 注意：这些 runner 已在 `IServiceScopeFactory` scope 内取 scoped 服务，直接多取一个 `IBackupConfigService` 即可。

- [ ] **Step 5: DTO 派生瞬时态 + reset 端点**

configs 列表/详情端点在映射 DTO 时叠加运行态：查 `BackupRunner.Get(id)`/`RestoreRunner.Get(id)`/`RepairRunner.Get(id)` 是否 Running + `BackupBusyTracker` 是否忙 → `activity`。DTO 同时回传持久 `status`、`lastError`、`activity`。

```csharp
        group.MapPost("/{id:int}/reset-status", async (int id, IBackupConfigService svc, CancellationToken ct) =>
        {
            await svc.ResetStatusAsync(id, ct);
            return Results.NoContent();
        });
```

- [ ] **Step 6: 前端徽标**

配置列表每行状态徽标：`activity` 非 Idle → 蓝色进行中（BackingUp/Restoring/Checking/Repairing）；否则 `status==Error` → 红色 + tooltip 显示 `lastError` + "Reset" 按钮（调 reset 端点）；Normal → 灰/无。

- [ ] **Step 7: 运行 + 前端**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~BackupConfigStatusTests"` ; `cd frontend && npm run build && npm run lint`
Expected: PASS / 干净。

- [ ] **Step 8: Commit**

```bash
git add backend frontend
git commit -m "feat: persist backup Status (Normal/Error) with auto-clear on success + reset; derive transient activity (§4.2)

Failure sets Error+LastError; next successful op auto-clears to Normal;
manual POST /reset-status. Configs DTO overlays derived activity
(BackingUp/Restoring/Checking/Repairing) from runners+BusyTracker.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: 锁定创建后不可改的基础字段（§4.5）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupConfigService.cs`（UpdateAsync 校验）
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`（编辑态只读）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupConfigServiceTests.cs`

**Interfaces:**
- Produces: `UpdateAsync` 对基础字段变更抛/返回可翻译为 400 的错误（如 `InvalidOperationException("Base fields cannot be changed after creation.")`），端点映射 400。

- [ ] **Step 1: 写失败测试——改基础字段被拒、改可变字段通过**

```csharp
    [Fact]
    public async Task Update_Rejects_Base_Field_Changes_Allows_Editable_Ones()
    {
        var created = await svc.CreateAsync(NewConfig(container: "c1", localRoot: "/data", accountId: 1));
        // 改 ContainerName → 拒绝
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateAsync(created.Id, created with { ContainerName = "c2" }));
        // 改 LocalRoot → 拒绝（§4.5 锁定，跨设备走导入）
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateAsync(created.Id, created with { LocalRoot = "/other" }));
        // 改 Name/规则 → 通过
        var ok = await svc.UpdateAsync(created.Id, created with { Name = "renamed", IgnoreRules = "*.tmp" });
        Assert.Equal("renamed", ok.Name);
    }
```

> `BackupConfig` 是 class（非 record），`with` 不可用——测试改为逐字段设值或加一个测试辅助。按实际类型调整。

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~BackupConfigServiceTests.Update_Rejects_Base_Field"`
Expected: FAIL（UpdateAsync 现允许改任意字段）。

- [ ] **Step 3: 实现基础字段校验**

`UpdateAsync` 载入现有实体后，对 `AccountId/ContainerName/LocalRoot/Password(加密性)/IndexTier/DataTier` 若与传入不同则抛 `InvalidOperationException`。仅应用可变字段：`Name/Description/IgnoreRules/DontCompressRules/DontGroupRules/IncludeSymlinks/MaxVersions/MaxAgeDays/RetentionMode/SingleFileThresholdBytes/GroupCapBytes/VolumeBytes/VerboseLogging`。

> Password：更新请求空密码通常表示"保留原密码"（现有约定），此时不算变更；非空且与原不同（加密性/内容变化）则拒绝。沿用现有 `HasPassword` 语义。

端点 `PUT /{id}` catch `InvalidOperationException` → `Results.BadRequest(new { error = ex.Message })`。

- [ ] **Step 4: 前端编辑态只读**

编辑现有配置时，基础字段（账户/container/本地根/密码/Tier/加密）渲染为 disabled/只读（现向导"编辑时字段锁定"已部分实现，补齐 LocalRoot 与全部基础字段一致）。

- [ ] **Step 5: 运行 + 前端**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~BackupConfigServiceTests"` ; `cd frontend && npm run build && npm run lint`
Expected: PASS / 干净。

- [ ] **Step 6: Commit**

```bash
git add backend frontend
git commit -m "feat: lock base fields after creation (account/container/localRoot/tier/encryption) (§4.5)

Changing base fields would desync local-authoritative state keyed by
account+container. UpdateAsync rejects such changes (400); cross-device
re-root goes through import. Frontend renders base fields read-only in edit.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: 删除备份可选连删 container（§4.3）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`（DELETE 增参）
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`（删除确认加复选框）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupConfigEndpointsTests.cs`（或集成）

**Interfaces:**
- Produces: `DELETE /api/backup-configs/{id}?deleteContainer=bool`（默认 false）。true 时额外 `BlobContainerClient.DeleteAsync`。

- [ ] **Step 1: 写失败测试（集成）——deleteContainer 控制 container 存亡**

```csharp
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Delete_Config_Optionally_Deletes_Cloud_Container()
    {
        Skip.IfNot(await AzuriteFixture.IsAvailableAsync(), "Azurite not running");
        // 建 config + container（跑一次备份使 container 存在）。
        // deleteContainer=false：container 仍在。
        await client.DeleteAsync($"/api/backup-configs/{id}?deleteContainer=false");
        Assert.True((await cc.ExistsAsync()).Value);
        // 再建一个 config 指向同 container；deleteContainer=true：container 被删。
        await client.DeleteAsync($"/api/backup-configs/{id2}?deleteContainer=true");
        Assert.False((await cc.ExistsAsync()).Value);
    }
```

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~Delete_Config_Optionally_Deletes_Cloud_Container"`
Expected: FAIL（当前 DELETE 无 deleteContainer 参、不删 container）。Azurite 缺失则 Skip。

- [ ] **Step 3: 实现**

`DELETE /{id}` 增 `bool deleteContainer = false` 查询参。删本地配置（现状：配置 + 本地缓存 + 日志）后，若 `deleteContainer` 为 true，用 `IBlobClientFactory` + account 取 `BlobContainerClient` 并 `DeleteIfExistsAsync`。注意先取 account/container 名再删配置，或删配置前先做云端删除。

- [ ] **Step 4: 前端删除确认复选框**

删除对话框加 "Also delete cloud container (irreversible — erases all backup data)" 复选框，默认不勾；勾选时二次确认文案强调不可逆；调用 `remove(id, deleteContainer)`。

- [ ] **Step 5: 运行 + 前端**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~BackupConfigEndpointsTests"` ; `cd frontend && npm run build && npm run lint`
Expected: PASS / 干净。

- [ ] **Step 6: Commit**

```bash
git add backend frontend
git commit -m "feat: delete backup can optionally delete the cloud container (§4.3)

DELETE /backup-configs/{id}?deleteContainer=true also removes the Azure
container (irreversible). Default false keeps cloud data.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: 还原懒加载目录树端点（§4.1a，决策 1）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`
- Create: `backend/src/AzureStorageBackup.Api/Services/VersionTreeService.cs`（从版本索引构树的纯逻辑）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/VersionTreeServiceTests.cs`

**Interfaces:**
- Produces:
  - `VersionTreeService.Children(VersionIndex index, string? dirPath) : IReadOnlyList<TreeNode>`。
  - `record TreeNode(string Name, string Path, bool IsDir, bool HasChildren, long? Length, DateTimeOffset? Mtime, string? StorageKind, string? StorageRef)`。
  - `GET /api/backup-configs/{id}/tree?version={v}&path={dir}` → `TreeNode[]`（数据源本地权威版本索引优先）。

- [ ] **Step 1: 写失败测试——分层返回直接子节点**

```csharp
    [Fact]
    public void Children_Returns_Direct_Children_With_HasChildren_Flag()
    {
        var index = new VersionIndex
        {
            Version = 1,
            Entries =
            [
                new IndexEntry { Path = "a/b/c.txt", Kind = "file", Length = 10, Permissions = "0644",
                    Storage = new StorageRef { Kind = "pack", Ref = "1", EntryName = "a/b/c.txt", VolumeSizes = [50] } },
                new IndexEntry { Path = "a/d.txt", Kind = "file", Length = 20, Permissions = "0644",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/h", VolumeSizes = [30] } },
                new IndexEntry { Path = "top.txt", Kind = "file", Length = 5, Permissions = "0644",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/t", VolumeSizes = [8] } },
            ],
            EmptyDirs = ["a/empty"],
        };

        var root = VersionTreeService.Children(index, null);
        Assert.Equal(new[] { "a", "top.txt" }, root.Select(n => n.Name).OrderBy(x => x).ToArray());
        Assert.True(root.Single(n => n.Name == "a").IsDir);
        Assert.True(root.Single(n => n.Name == "a").HasChildren);

        var a = VersionTreeService.Children(index, "a");
        Assert.Equal(new[] { "b", "d.txt", "empty" }, a.Select(n => n.Name).OrderBy(x => x).ToArray());
        Assert.True(a.Single(n => n.Name == "empty").IsDir);           // 空目录也作可展开节点
        Assert.False(a.Single(n => n.Name == "d.txt").IsDir);
    }
```

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~VersionTreeServiceTests"`
Expected: FAIL（`VersionTreeService` 不存在）。

- [ ] **Step 3: 实现 VersionTreeService.Children**

从 `index.Entries` + `index.EmptyDirs` 计算 `dirPath` 的直接子节点：按 `/` 切分，取 `dirPath` 前缀下**下一段**去重；某段还有更深内容 → `IsDir=true, HasChildren=true`；叶子文件 → 文件节点带 length/mtime/storage。空目录路径也纳入（其自身为可展开 dir 节点，可能无子）。

- [ ] **Step 4: 端点接线**

```csharp
        group.MapGet("/{id:int}/tree", async (int id, int? version, string? path,
            IBackupConfigService svc, IAccountService accounts, TrackedInfoStore trackedInfo, ILocalIndexCache indexCache,
            IBackupInfoStore store, CancellationToken ct) =>
        {
            // 载入指定版本索引（本地缓存优先，缺则云端）→ VersionTreeService.Children(index, path)
            // 返回 TreeNode[]。
        });
```

> 参考现有 `/file-versions`（`:130`）如何取 config/account/info/index，复用同套载入。

- [ ] **Step 5: 运行**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~VersionTreeServiceTests"`
Expected: PASS。

- [ ] **Step 6: Commit**

```bash
git add backend
git commit -m "feat: lazy version tree endpoint for restore browsing (§4.1a)

GET /backup-configs/{id}/tree?version=&path= returns a directory's direct
children from the (local-authoritative) version index; folders flagged
hasChildren for lazy expansion. Empty dirs included.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: 还原下载量/解压量估算端点（§4.1b，需求 A + 决策 5）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs`
- Create: `backend/src/AzureStorageBackup.Api/Services/RestoreEstimator.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/RestoreEstimatorTests.cs`

**Interfaces:**
- Produces:
  - `RestoreEstimator.Compute(VersionIndex index, BackupInfoFile info, IReadOnlyCollection<string> paths) : RestoreEstimate`（纯逻辑：去重存储对象、合计尺寸/文件数）。
  - `record RestoreEstimate(long DownloadBytes, long UncompressedBytes, int FileCount, IReadOnlyList<string> DistinctObjects)`。
  - HEAD 实查阶段（端点内）填 `ArchivedObjects`/`RehydratePending`。
  - `POST /api/backup-configs/{id}/restore-estimate` body `{version, paths[]}` → `{downloadBytes, uncompressedBytes, fileCount, archivedObjects, rehydratePending}`。

- [ ] **Step 1: 写失败测试——pack/去重只计一次**

```csharp
    [Fact]
    public void Estimate_Counts_Shared_Pack_And_Dedup_Blob_Once()
    {
        var index = new VersionIndex { Version = 1, Entries =
        [
            // 两文件同 pack p1（3 卷，尺寸 [100,100,50]）
            new IndexEntry { Path="a.txt", Kind="file", Length=40, Permissions="0644",
                Storage=new StorageRef{ Kind="pack", Ref="1", EntryName="a.txt" } },
            new IndexEntry { Path="b.txt", Kind="file", Length=60, Permissions="0644",
                Storage=new StorageRef{ Kind="pack", Ref="1", EntryName="b.txt" } },
            // 两文件同 data blob（去重，卷尺寸 [30]）
            new IndexEntry { Path="c.txt", Kind="file", Length=70, Permissions="0644",
                Storage=new StorageRef{ Kind="blob", Ref="data/h", VolumeSizes=[30] } },
            new IndexEntry { Path="d.txt", Kind="file", Length=70, Permissions="0644",
                Storage=new StorageRef{ Kind="blob", Ref="data/h", VolumeSizes=[30] } },
        ]};
        var info = new BackupInfoFile { Backup = /* meta */, Packs = { ["1"] = new PackInfo { Blob="packs/1.7z", Volumes=3, VolumeSizes=[100,100,50] } } };

        var est = RestoreEstimator.Compute(index, info, ["a.txt","b.txt","c.txt","d.txt"]);
        Assert.Equal(250 + 30, est.DownloadBytes);   // pack 250（计一次）+ data 30（计一次）
        Assert.Equal(40 + 60 + 70 + 70, est.UncompressedBytes);
        Assert.Equal(4, est.FileCount);
        Assert.Equal(2, est.DistinctObjects.Count);  // pack:1 + blob:data/h
    }
```

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~RestoreEstimatorTests"`
Expected: FAIL。

- [ ] **Step 3: 实现 RestoreEstimator.Compute**

选中 paths → 条目；按 `StorageKey`（`pack:{Ref}` / `blob:{Ref}`）去重存储对象；`DownloadBytes` = 各唯一对象卷尺寸合计（pack 用 `PackInfo.VolumeSizes`，blob 用 `StorageRef.VolumeSizes`；空则回退 0/未知，记 `DistinctObjects` 供端点 HEAD 补）；`UncompressedBytes` = 选中文件 `Length` 合计；`FileCount` = 选中文件数。

- [ ] **Step 4: 端点 + HEAD 活化判定（决策 5）**

`POST /{id}/restore-estimate`：载入版本索引 → `Compute` → 对 `DistinctObjects` 各首卷并发 HEAD（`GetPropertiesAsync`，复用 `DownloadConcurrency`）读 `AccessTier`/`ArchiveStatus` → `archivedObjects`（tier==Archive）、`rehydratePending`（有 ArchiveStatus）。返回 JSON。

- [ ] **Step 5: 运行**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~RestoreEstimatorTests"`
Expected: PASS。

- [ ] **Step 6: Commit**

```bash
git add backend
git commit -m "feat: restore estimate endpoint — download/uncompressed size + archived count (§4.1b)

POST /restore-estimate computes download bytes (dedup/pack counted once
via volume sizes), uncompressed total, file count, then HEADs distinct
objects for live tier/rehydration state (decision 5).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: 选择性还原 + 冲突模式 + Rehydrate 优先级（§4.1c，决策 3 + 需求 B）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/RestoreOrchestrator.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/RestoreRunner.cs` + 端点 body
- Test: `backend/tests/AzureStorageBackup.Api.Tests/RestoreOrchestratorTests.cs`

**Interfaces:**
- Produces:
  - `enum RestoreConflictMode { OverwriteIfChanged = 0, Skip = 1, RenameKeep = 2 }`。
  - `enum RestoreRehydratePriority { Standard = 0, High = 1 }`。
  - `RestoreRequest` 增 `IReadOnlyList<string>? SelectedPaths`（null=整版本）、`RestoreConflictMode Conflict`、`RestoreRehydratePriority RehydratePriority`。

- [ ] **Step 1: 写失败测试——RenameKeep 改名旧文件、还原落原名（纯逻辑单测）**

冲突改名是纯文件系统逻辑，抽 `RestoreOrchestrator.ResolveConflictTarget(dest, mode, now, existsWithSameContent)` 或直接测 helper：

```csharp
    [Fact]
    public void RenameKeep_Renames_Existing_To_Bak_Timestamp()
    {
        var dir = NewTempDir();
        var dest = Path.Combine(dir, "file.txt");
        File.WriteAllText(dest, "OLD");
        var now = new DateTimeOffset(2026, 7, 18, 14, 30, 22, TimeSpan.Zero);

        var bak = RestoreConflict.RenameExisting(dest, now);   // 返回改名后的备份路径
        Assert.Equal(Path.Combine(dir, "file.txt.bak-20260718-143022"), bak);
        Assert.False(File.Exists(dest));                        // 原名腾空
        Assert.Equal("OLD", File.ReadAllText(bak));             // 旧内容保留
    }

    [Fact]
    public void RenameKeep_Appends_Counter_On_Collision()
    {
        var dir = NewTempDir();
        var dest = Path.Combine(dir, "file.txt");
        File.WriteAllText(dest, "OLD");
        var now = new DateTimeOffset(2026, 7, 18, 14, 30, 22, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(dir, "file.txt.bak-20260718-143022"), "PREV"); // 已存在

        var bak = RestoreConflict.RenameExisting(dest, now);
        Assert.Equal(Path.Combine(dir, "file.txt.bak-20260718-143022-1"), bak);
    }
```

新建 `RestoreConflict.RenameExisting(string dest, DateTimeOffset now)`（静态助手）。

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~RestoreConflict|RenameKeep"`
Expected: FAIL。

- [ ] **Step 3: 实现 RestoreConflict.RenameExisting**

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>还原冲突"重命名保留"（决策 3）：把现有本地文件改名为 {name}.bak-{yyyyMMdd-HHmmss}
/// （冲突追加 -1/-2…），腾出原名供还原写入。旧内容永不丢失。</summary>
public static class RestoreConflict
{
    public static string RenameExisting(string dest, DateTimeOffset now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var baseBak = dest + ".bak-" + stamp;
        var target = baseBak;
        var n = 1;
        while (File.Exists(target) || Directory.Exists(target))
            target = baseBak + "-" + n++;
        File.Move(dest, target);
        return target;
    }
}
```

- [ ] **Step 4: RestoreRequest 增字段 + 选择性过滤 + 冲突分流**

`RestoreRequest` 增 `SelectedPaths`/`Conflict`/`RehydratePriority`。`RunCoreAsync`：若 `SelectedPaths is not null`，把 `byPath` 过滤到选中集（`byPath = byPath.Where(kv => selected.Contains(kv.Key))`），自然满足需求 B（pack 只下一次、只写选中成员）。写回逻辑按 `Conflict`：
- `Skip`：`NeedsRestoreAsync` 目标存在即跳过。
- `OverwriteIfChanged`：现状。
- `RenameKeep`：目标存在且内容不同 → `RestoreConflict.RenameExisting(dest, DateTimeOffset.UtcNow)` 再写原名。

活化发起处（`EnsureOnlineAsync`/`SetAccessTierAsync`）透传 `RehydratePriority`（映射 Azure `RehydratePriority.Standard/High`）。

- [ ] **Step 5: Runner + 端点透传**

`RestoreRunner.Start` 增 `selectedPaths`/`conflict`/`rehydratePriority` 参；`RestoreRequestBody` + `POST /{id}/restore` 增对应字段；前端 api `restore(...)` 增参（前端 UI 在 Task 10）。

- [ ] **Step 6: 写集成测试——选择性还原只写选中、pack 只下载一次**

装饰 IO 计数下载次数；只选 pack 内一个成员 → 断言 pack 下载 1 次、未选成员不落地、选中成员落地。

- [ ] **Step 7: 运行**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~RestoreOrchestratorTests|RestoreConflict"`
Expected: PASS（集成 Azurite 缺失则 Skip；纯逻辑与 build 必绿）。

- [ ] **Step 8: Commit**

```bash
git add backend frontend/src/api/backupConfigs.ts
git commit -m "feat: selective restore + conflict modes + rehydrate priority (§4.1c)

RestoreRequest gains SelectedPaths (filter to chosen files — pack downloads
once, only selected members written, no over-restore), Conflict
{OverwriteIfChanged,Skip,RenameKeep} (RenameKeep -> {name}.bak-{ts}), and
RehydratePriority {Standard,High}.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: 还原前端——树浏览 + 估算 + 冲突/优先级（§4.1d）

**Files:**
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`
- Modify: `frontend/src/api/backupConfigs.ts`
- Create: `frontend/src/components/RestoreDialog.tsx`（如页面已臃肿，抽独立组件）

**Interfaces:**
- Consumes: `tree(id, version, path)`、`restoreEstimate(id, {version, paths})`、`restore(id, {targetRoot, version, selectedPaths, conflict, rehydratePriority})`。

- [ ] **Step 1: api 增方法**

`api/backupConfigs.ts` 增：

```ts
  tree: (id: number, version: number | null, path: string | null) =>
    api.get<TreeNode[]>(`/backup-configs/${id}/tree?${new URLSearchParams({
      ...(version != null ? { version: String(version) } : {}),
      ...(path ? { path } : {}),
    })}`),
  restoreEstimate: (id: number, version: number | null, paths: string[]) =>
    api.post<RestoreEstimate>(`/backup-configs/${id}/restore-estimate`, { version, paths }),
```

`restore(...)` 增 `selectedPaths?/conflict?/rehydratePriority?`。加 `TreeNode`/`RestoreEstimate` 接口类型。

- [ ] **Step 2: RestoreDialog 组件**

- 选版本（复用 `versions`）→ 懒加载树（展开目录调 `tree`，文件夹级联全选/取消）。
- 勾选变化（防抖 ~400ms）调 `restoreEstimate` → 展示下载量/解压量/文件数；`archivedObjects>0` 时提示"需等待活化（约数小时）"并显示 Rehydrate 优先级下拉（Standard/High）。
- 冲突模式下拉（Overwrite if changed / Skip / Rename & keep）。
- 目标根路径输入 → "Start restore" 调 `restore` 带 `selectedPaths`。

- [ ] **Step 3: 构建 + lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 干净。

- [ ] **Step 4: Commit**

```bash
git add frontend
git commit -m "feat: restore dialog with lazy tree browse, live size estimate, conflict mode + rehydrate priority (§4.1d)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 11: 向导"立即备份 / 暂不"（§4.6）

**Files:**
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`

- [ ] **Step 1: 实现**

新建配置成功后，展示两个动作："Run first backup now"（调 `run(id)` 并跳到运行进度）/"Not now"（关闭向导）。纯前端。

- [ ] **Step 2: 构建 + lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 干净。

- [ ] **Step 3: Commit**

```bash
git add frontend
git commit -m "feat: post-create wizard offers 'run first backup now' / 'not now' (§4.6)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review 覆盖对照

- §4.7→T1  §4.4→T2  §4.8→T3  §4.2→T4  §4.5→T5  §4.3→T6  §4.1a→T7  §4.1b→T8  §4.1c→T9  §4.1d→T10  §4.6→T11 ✅
- 类型一致性：`TreeNode`/`RestoreEstimate` 在 T7/T8 后端定义，T10 前端消费同形；`RestoreConflictMode`/`RestoreRehydratePriority` T9 定义端到端透传；`BackupStatus` T4 端到端。
- 依赖顺序：T7→T8→T9→T10（还原大件内部有序）；其余相对独立，按本文件顺序执行。
- 执行完 Plan 2 后进入 Plan 3（⚪ 清理 + CI）。
