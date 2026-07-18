# 备份修复 Plan 1 — 核心数据完整性（🔴 + 🟠）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 消除备份主链路的高危数据损坏竞态与 5 个中危 bug，使并发备份、还原替代、修复、死重压实、多卷 Archive 检查在崩溃/冲突下都不丢数据、不误报。

**Architecture:** 逐项 TDD，各自 commit。多为对既有单一 service 的定点修改；测试以纯单测为主（Azurite 集成仅在少数点，`[Integration]`+`SkippableFact`）。

**Tech Stack:** .NET 10, xUnit, Azure.Storage.Blobs, Azurite（集成）。

## Global Constraints

- 语言：代码注释与既有风格一致（中文注释）；界面/日志文案英文。
- 单用户系统，无认证；Azure 一律 Blob。
- 本地权威原则：备份运行期尽量不读云端；本地缓存（`TrackedInfoStore`/`LocalIndexCache`）= 真相。
- 收尾门槛：`dotnet build -c Release` 0 警告；非集成单测全绿。
- 测试约定：集成测试 `[Trait("Category","Integration")]` + `Skip.IfNot(...)`，需 Azurite（well-known 账户 `devstoreaccount1`）。
- 每个 Task 结束独立 commit。

---

### Task 1: 🔴 StagingArea 每次暂存用 GUID 子目录隔离（§2）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/StagingArea.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/StagingAreaTests.cs`

**Interfaces:**
- Produces: `StagedItem(IReadOnlyList<string> Files, long Bytes)` 不变；`StageAsync`/`Release` 签名不变。行为变化：每次 `StageAsync` 的产出移入 `stagedTempDir/{guid}/`，`Release` 删文件后连带删空子目录。

- [ ] **Step 1: 更新既有会被 GUID 化破坏的断言**

`StagingAreaTests.Staged_Item_Is_Moved_From_Compress_To_Staged_Temp` 里这行断言暂存文件直接在 `_stagedTemp` 下，将不再成立。改为断言在 `_stagedTemp` 的**子目录**里：

```csharp
        Assert.Empty(Directory.GetFiles(_compressTemp));               // moved out of compress-temp
        var staged = Assert.Single(item.Files);
        // 现在暂存文件在 staged-temp 的 GUID 子目录里（跨备份隔离，防同名覆盖）。
        Assert.Equal(_stagedTemp, Path.GetDirectoryName(Path.GetDirectoryName(staged)));
        Assert.True(File.Exists(staged));
        Assert.Equal(500, item.Bytes);
        Assert.Equal(500, area.StagedBytes);
```

- [ ] **Step 2: 写失败测试——同名并发暂存不互相覆盖**

在 `StagingAreaTests` 增：

```csharp
    [Fact]
    public async Task Concurrent_Same_Named_Outputs_Do_Not_Collide()
    {
        using var area = Area(limit: 1_000_000);

        // 两次暂存产出「同名」文件（模拟不同 container 都从 p0001.7z 起）。
        // 压缩串行，但两份必须落在不同子目录、内容各自完整。
        var item1 = await area.StageAsync(Produce("p0001.7z", 100));
        var item2 = await area.StageAsync(Produce("p0001.7z", 200));

        var f1 = Assert.Single(item1.Files);
        var f2 = Assert.Single(item2.Files);
        Assert.NotEqual(f1, f2);                       // 不同路径
        Assert.True(File.Exists(f1) && File.Exists(f2));
        Assert.Equal(100, new FileInfo(f1).Length);    // 各自内容完整、未被覆盖
        Assert.Equal(200, new FileInfo(f2).Length);
        Assert.Equal(300, area.StagedBytes);

        area.Release(item1);
        Assert.False(File.Exists(f1));
        Assert.False(Directory.Exists(Path.GetDirectoryName(f1)));  // 空子目录一并清除
        Assert.True(File.Exists(f2));
    }
```

- [ ] **Step 3: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~StagingAreaTests.Concurrent_Same_Named_Outputs_Do_Not_Collide"`
Expected: FAIL（当前 `MoveToStaged` 用固定名 `File.Move(overwrite:true)`，第二次覆盖第一次 → f1 内容变 200 或路径相同）。

- [ ] **Step 4: 实现 GUID 子目录隔离**

改 `MoveToStaged`（`StagingArea.cs:57-69`）：

```csharp
    private StagedItem MoveToStaged(IReadOnlyList<string> producedFiles)
    {
        // 每次暂存独立 GUID 子目录：不同备份即使产出同名文件也不互相覆盖（跨 container 并发安全）。
        var subDir = Path.Combine(stagedTempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subDir);
        var staged = new List<string>(producedFiles.Count);
        long bytes = 0;
        foreach (var src in producedFiles)
        {
            var dest = Path.Combine(subDir, Path.GetFileName(src));
            File.Move(src, dest, overwrite: false);
            bytes += new FileInfo(dest).Length;
            staged.Add(dest);
        }
        return new StagedItem(staged, bytes);
    }
```

改 `Release`（`StagingArea.cs:47-55`）删文件后清空子目录：

```csharp
    public void Release(StagedItem item)
    {
        foreach (var file in item.Files)
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
        // 删空的 GUID 子目录。
        foreach (var dir in item.Files.Select(Path.GetDirectoryName).Distinct())
        {
            try { if (dir is not null && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch { /* best effort */ }
        }
        Interlocked.Add(ref _stagedBytes, -item.Bytes);
        _releaseSignal.Release();
    }
```

（文件顶部若无 `using System.Linq;` 则 .NET 隐式 global using 已含；确认编译。）

- [ ] **Step 5: 运行全部 StagingArea 测试**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~StagingAreaTests"`
Expected: PASS（新测 + 改后的移动断言 + 背压/并发既有测试全绿）。

- [ ] **Step 6: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/StagingArea.cs backend/tests/AzureStorageBackup.Api.Tests/StagingAreaTests.cs
git commit -m "fix: isolate staging per-run in GUID subdir to prevent cross-backup file collision

StagingArea used the produced file name as a fixed staged/ name with
File.Move(overwrite:true); concurrent backups of different containers
could overwrite each other's in-flight staged files (Linux silent data
corruption). Each StageAsync now stages into its own GUID subdir.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: 🟠 还原替代用"解析成功"判据 + 逐组容错（§3.1）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/RestoreOrchestrator.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/RestoreOrchestratorTests.cs`

**Interfaces:**
- Produces: `RestoreResult(int Version, int RestoredFiles, int SkippedFiles, int RestoredDirs, int FailedFiles)`（新增 `FailedFiles`；所有构造点与 `RestoreRunResponse.From` 需同步）。

- [ ] **Step 1: 写失败测试——替代到已删除版本时回落跳过、不炸整体**

先看 `RestoreOrchestratorTests` 现有构造 orchestrator 的辅助方式（多为 Azurite 集成）。本测试用集成风格（`[Trait("Category","Integration")]` + `Skip.IfNot(AzuriteFixture.Available)`），构造：v1 含文件 A、B；v2 把 A 标记 unrecoverable；请求还原 v2、`Substitutions = { ["A"] = 99 }`（版本 99 不存在）。断言：不抛异常，A 计入 skipped，B 正常还原。

```csharp
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Substitution_To_Missing_Version_Skips_Path_Without_Failing_Whole_Restore()
    {
        Skip.IfNot(await AzuriteFixture.IsAvailableAsync(), "Azurite not running");
        // ... 用现有集成脚手架建 v2（A 不可恢复、B 正常）...
        var result = await orchestrator.RunAsync(new RestoreRequest
        {
            Account = account, Container = container, TargetRoot = target, Version = 2,
            Substitutions = new Dictionary<string, int>(StringComparer.Ordinal) { ["A"] = 99 }, // 不存在的版本
        });
        Assert.True(result.SkippedFiles >= 1);          // A 回落跳过
        Assert.True(File.Exists(Path.Combine(target, "B"))); // B 正常还原
    }
```

> 若现有测试文件无可复用的建版本辅助，参照本文件同目录既有集成测试的 setup 复制其账户/container/上传骨架。

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~RestoreOrchestratorTests.Substitution_To_Missing_Version"`
Expected: FAIL（当前替代未解析成功仍从 unresolved 排除 A → 尝试还原 A 的缺失/坏 blob → 抛错）。或 Azurite 未起则 Skip（此时改跑 Step 3 后用纯逻辑覆盖，见下）。

- [ ] **Step 3: 实现——记录真正解析成功的路径**

改 `RestoreOrchestrator.cs:96-112`：

```csharp
        // 逐路径生效条目：默认取本版本；被替代的路径改用指定版本的同路径条目（内容+元数据取该版本）。
        var byPath = index.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var resolved = new HashSet<string>(StringComparer.Ordinal); // 真正解析成功的替代路径
        foreach (var grp in request.Substitutions.GroupBy(kv => kv.Value))
        {
            var sv = info.Versions.FirstOrDefault(x => x.Version == grp.Key);
            if (sv is null)
                continue; // 替代版本已被保留清理删除 → 该组全部回落跳过
            var srcIndex = await store.ReadIndexAsync(request.Account, request.Container, sv.IndexBlob, request.Password, ct);
            var srcByPath = srcIndex.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
            foreach (var kv in grp)
                if (srcByPath.TryGetValue(kv.Key, out var se))
                {
                    byPath[kv.Key] = se;
                    resolved.Add(kv.Key);
                }
        }

        // 不可恢复且未「解析成功」替代 → 跳过（声明了意图但替代不可得的也回落跳过，不报错）。
        var unresolved = index.UnrecoverablePaths.Where(p => !resolved.Contains(p)).ToHashSet(StringComparer.Ordinal);
        skipped += unresolved.Count;
```

- [ ] **Step 4: 逐组容错——单组失败不炸全局**

改 `RestoreOrchestrator.cs:139-149` 的 `Task.WhenAll`，改为收集逐组异常：

```csharp
        var failed = 0;
        try
        {
            var groups = fileEntries.Where(e => e.Storage is not null).GroupBy(e => StorageKey(e.Storage!)).ToList();
            var tasks = groups.Select(async g =>
            {
                try { return await RestoreGroupAsync(container, request, work, g.ToList(), gate, rehydrated, phase, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    phase?.Report($"Group failed ({g.Key}): {ex.Message}");
                    return (Restored: 0, Skipped: 0, Failed: g.Count());
                }
            });
            var counts = await Task.WhenAll(tasks);
            restored += counts.Sum(c => c.Restored);
            skipped += counts.Sum(c => c.Skipped);
            failed += counts.Sum(c => c.Failed);
        }
        finally
        {
            TryDelete(work);
        }
```

把 `RestoreGroupAsync` 的返回类型从 `(int Restored, int Skipped)` 改为 `(int Restored, int Skipped, int Failed)`（成功路径 `Failed=0`），其 `return` 语句相应补 `0`。

- [ ] **Step 5: 更新 RestoreResult + 所有构造点**

`RestoreResult` 记录（`RestoreOrchestrator.cs:35`）加 `int FailedFiles`：

```csharp
public sealed record RestoreResult(int Version, int RestoredFiles, int SkippedFiles, int RestoredDirs, int FailedFiles);
```

`RunCoreAsync` 末尾（`:159`）：

```csharp
        return new RestoreResult(version.Version, restored, skipped, index.EmptyDirs.Count, failed);
```

`RestoreRunResponse`（`RestoreRunner.cs`）加回传 `FailedFiles`：

```csharp
public sealed record RestoreRunResponse(
    string Status, int? Version, int? RestoredFiles, int? SkippedFiles, int? FailedFiles, string? Error, string? Phase)
{
    public static RestoreRunResponse From(RestoreRunState s) => new(
        s.Status.ToString(), s.Result?.Version, s.Result?.RestoredFiles, s.Result?.SkippedFiles,
        s.Result?.FailedFiles, s.Error, s.Phase);
}
```

前端 `RestoreRun` 接口加 `failedFiles: number | null`（`frontend/src/api/backupConfigs.ts`）。

- [ ] **Step 6: 编译 + 运行还原测试**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~RestoreOrchestratorTests"`
Expected: PASS（或 Azurite 缺失则集成 Skip，但 build 0 警告、既有还原单测绿）。

- [ ] **Step 7: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/RestoreOrchestrator.cs backend/src/AzureStorageBackup.Api/Services/RestoreRunner.cs backend/tests/AzureStorageBackup.Api.Tests/RestoreOrchestratorTests.cs frontend/src/api/backupConfigs.ts
git commit -m "fix: restore substitution keyed on successful resolution, not intent; per-group fault tolerance

A declared substitution to a version that was retention-pruned no longer
fails the whole restore; the path falls back to skipped. Per-group failures
are isolated (FailedFiles) instead of aborting Task.WhenAll.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: 🟠 修复经本地权威状态机（§3.2）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRepairer.cs`
- Modify: DI 注册处（`backend/src/AzureStorageBackup.Api/Program.cs` 或引擎 DI 扩展）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupRepairerTests.cs`（若无则新建）

**Interfaces:**
- Consumes: `TrackedInfoStore`（`WriteAsync(Account, container, BackupInfoFile, password, AccessTier, ct)`）、`ILocalIndexCache`（`PutAsync(accountId, container, version, identityTicks, VersionIndex, ct)`）。
- Produces: `BackupRepairer` ctor 增两个可选参数 `TrackedInfoStore? trackedInfo = null, ILocalIndexCache? indexCache = null`。

- [ ] **Step 1: 写失败测试——修复后信息文件写经 tracked（本地缓存被更新）**

用一个记录调用的 fake `IBackupInfoStore`/或直接断言 `trackedInfo` 被调用。最简：集成风格——修复后立即再跑一次备份，断言不产生 412（现回归表现为"下次备份失败一次"）。纯单测版本：注入 spy `TrackedInfoStore`（若可 mock）断言 `WriteAsync` 被调用而非 `store.WriteInfoAsync`。鉴于 `TrackedInfoStore` 是具体类，采集成断言"修复后本地缓存 ETag 与云端一致"：

```csharp
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Repair_Updates_Local_Authoritative_State_So_Next_Write_Uses_Fresh_ETag()
    {
        Skip.IfNot(await AzuriteFixture.IsAvailableAsync(), "Azurite not running");
        // 建 v1（含一个 data blob），删除云端该 blob，本地文件仍在。
        // 经 tracked 载入信息文件（回填本地 ETag）。
        // 修复（应重写信息文件 + 更新本地缓存 ETag）。
        // 断言：再次 tracked.WriteAsync（模拟下次备份 finalize）不抛冲突。
        await repairer.RepairAsync(account, container, password: null, localRoot, version: null,
            checkOptions, AccessTier.Hot, volumeBytes: null);
        // 下次备份的信息写不 412：
        var info = await trackedInfo.LoadAsync(account, container, null, default);
        await trackedInfo.WriteAsync(account, container, info!, null, Azure.Storage.Blobs.Models.AccessTier.Hot, default); // 不抛
    }
```

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~BackupRepairerTests.Repair_Updates_Local_Authoritative_State"`
Expected: FAIL（当前修复用 `store.WriteInfoAsync` 直写云端，本地缓存 ETag 未更新 → 后续 tracked 写 412）。Azurite 缺失则 Skip。

- [ ] **Step 3: 实现——注入 tracked/indexCache 并改写写入点**

`BackupRepairer` ctor（`:16-26`）加参数：

```csharp
public sealed class BackupRepairer(
    IBlobClientFactory factory,
    IBackupInfoStore store,
    IFileCompressor compressor,
    IFileHasher hasher,
    IBlobUploader uploader,
    string tempRoot,
    INotifier? notifier = null,
    IOperationLog? opLog = null,
    BackupChecker? checker = null,
    TrackedInfoStore? trackedInfo = null,
    ILocalIndexCache? indexCache = null)
```

改持久化段（`:69-75`）：

```csharp
        // 持久化被改动的版本索引 + 信息文件（经本地权威状态机，保持 ETag/缓存一致）。
        var identity = info.Backup.CreatedAt.UtcTicks;
        foreach (var vnum in changedVersions)
        {
            var ver = info.Versions.First(x => x.Version == vnum);
            await store.WriteIndexAsync(account, container, vnum, indexes[vnum], password, ct: ct);
            if (indexCache is not null)
                await indexCache.PutAsync(account.Id, container, vnum, identity, indexes[vnum], ct);
        }
        if (trackedInfo is not null)
            await trackedInfo.WriteAsync(account, container, info, password, ct: ct);
        else
            await store.WriteInfoAsync(account, container, info, password, ct: ct);
```

> 确认 `ILocalIndexCache.PutAsync` 与 `TrackedInfoStore.WriteAsync` 的确切签名（参考 `BackupOrchestrator.cs:246-258` 的调用）。若 `WriteAsync` 需要 AccessTier，传 `AccessTier` 版本（修复默认沿用现有 tier，可用 `info` 里的 index tier 或 `AccessTier.Hot`——与编排器一致即可）。

- [ ] **Step 4: DI 注册补齐**

在注册 `BackupRepairer` 的地方补注入 `TrackedInfoStore`、`ILocalIndexCache`（这两者已在 DI，编排器已用）。

- [ ] **Step 5: 编译 + 运行**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~BackupRepairerTests"`
Expected: PASS（或 Azurite 缺失则 Skip；build 0 警告）。

- [ ] **Step 6: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupRepairer.cs backend/src/AzureStorageBackup.Api/Program.cs backend/tests/AzureStorageBackup.Api.Tests/BackupRepairerTests.cs
git commit -m "fix: repair writes info/index through local-authoritative state (TrackedInfoStore + LocalIndexCache)

Repair bypassed the ETag/local cache, so the next backup's conditional
write failed once with 412. Repair now updates tracked info + index cache.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: 🟠 ETag 冲突不污染版本索引缓存（§3.3）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupOrchestratorTests.cs`

**Interfaces:** 无签名变化，仅调整 `indexCache.PutAsync` 相对 `trackedInfo.WriteAsync` 的顺序。

- [ ] **Step 1: 写失败测试——信息文件写冲突时索引缓存不留幽灵版本**

注入一个在 `WriteAsync` 抛 412/409 的 `TrackedInfoStore`（或用 spy `ILocalIndexCache` 断言冲突后无该版本条目）。因两者均具体类，最实用是 spy `ILocalIndexCache`（接口）+ 让 tracked 写失败（构造一个云端 ETag 不匹配场景，集成）。纯逻辑做法：把 orchestrator 的 finalize 段抽成可测方法。**最小可行**：spy indexCache 记录 Put/Invalidate；令 `store.WriteInfoConditionalAsync`（tracked 内部）抛冲突；断言最终 indexCache 无 version N。

```csharp
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Info_Write_Conflict_Does_Not_Leave_Ghost_Version_In_Index_Cache()
    {
        Skip.IfNot(await AzuriteFixture.IsAvailableAsync(), "Azurite not running");
        // 预置本地缓存持有过期 ETag（模拟外部改动云端信息文件后本地未同步）。
        // 跑一次备份 → finalize 的 tracked.WriteAsync 应 412 抛错。
        await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.RunAsync(request));
        // 断言：本次未提交的版本 N 未留在 LocalIndexCache。
        var cached = await indexCache.TryGetAsync(account.Id, container, expectedVersion, identity, default);
        Assert.Null(cached);
    }
```

> 若 spy 更易：实现一个 `RecordingIndexCache : ILocalIndexCache` 记录 Put 的版本集合，断言不含冲突版本。

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~BackupOrchestratorTests.Info_Write_Conflict"`
Expected: FAIL（当前 `indexCache.PutAsync` 在 `trackedInfo.WriteAsync` 之前，冲突后缓存已含版本 N）。

- [ ] **Step 3: 实现——PutAsync 移到信息文件写成功之后**

在 `BackupOrchestrator.cs`：把 `:246-247` 的

```csharp
        var indexBlob = await store.WriteIndexAsync(request.Account, request.Container, version, index, password, request.IndexTier, ct);
        if (indexCache is not null)
            await indexCache.PutAsync(request.Account.Id, request.Container, version, identity, index, ct);
```

改为**仅上传云端索引**，把本地缓存 Put 挪到 `trackedInfo.WriteAsync`（`:257-260`）**成功之后**：

```csharp
        var indexBlob = await store.WriteIndexAsync(request.Account, request.Container, version, index, password, request.IndexTier, ct);
        // 本地索引缓存 Put 推迟到信息文件提交成功后（见下），避免冲突时留下未提交版本的幽灵缓存。
```

在 finalize 写信息文件之后：

```csharp
        if (trackedInfo is not null)
            await trackedInfo.WriteAsync(request.Account, request.Container, info, password, request.IndexTier, ct);
        else
            await store.WriteInfoAsync(request.Account, request.Container, info, password, request.IndexTier, ct);

        // 信息文件已提交 → 现在把版本索引写入本地缓存（冲突已在上一步抛出，不会到这里）。
        if (indexCache is not null)
            await indexCache.PutAsync(request.Account.Id, request.Container, version, identity, index, ct);
```

> 确认 `identity` 变量在该作用域可见（编排器已有；即 `info.Backup.CreatedAt.UtcTicks`）。

- [ ] **Step 4: 运行**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~BackupOrchestratorTests"`
Expected: PASS（或 Azurite 缺失则相关集成 Skip；既有编排器测试全绿）。

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs backend/tests/AzureStorageBackup.Api.Tests/BackupOrchestratorTests.cs
git commit -m "fix: put version index into local cache only after info-file commit succeeds

On ETag conflict the uncommitted version index polluted LocalIndexCache,
which the next backup could read as a ghost version. Cache Put now runs
after the tracked info write succeeds.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: 🟠 死重压实/修复原子替换——先传新卷、后删残留旧卷（§3.4）

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BlobUploader.cs`（或 `IBlobUploader` 定义处）
- Modify: `backend/src/AzureStorageBackup.Api/Services/VolumeBlobIO.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/DeadWeightCompactor.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRepairer.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/DeadWeightCompactorTests.cs`

**Interfaces:**
- Produces: `IBlobUploader.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata?)`（覆盖上传，不做 if-missing 短路）。`VolumeBlobIO.ReplaceAsync(uploader, account, container, baseRef, volumeFiles, tier, retry, ct, metadata?)`：以 overwrite 上传新卷 `.001..M`，全部成功后删除残留旧卷 `.(M+1)..N`。

- [ ] **Step 1: 写失败测试——崩溃在"传新前"不清空旧数据**

`DeadWeightCompactorTests`（集成）：建一个 3 卷 pack，触发重压但让**上传阶段注入异常**（装饰 uploader 在首个 `UploadOverwrite` 抛）。断言：旧 pack 全部分卷**仍在**（未被提前删空）。

```csharp
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Recompact_Failure_During_Upload_Leaves_Old_Volumes_Intact()
    {
        Skip.IfNot(await AzuriteFixture.IsAvailableAsync(), "Azurite not running");
        // 建含死重的多卷 pack；用一个「首次 UploadOverwrite 即抛」的装饰 uploader。
        await Assert.ThrowsAnyAsync<Exception>(() => compactor.CompactAsync(/* ... */));
        // 旧 pack 分卷仍完整存在（原来的 delete-first 会导致这里为空）。
        var remaining = new List<string>();
        await foreach (var b in cc.GetBlobsAsync(prefix: "packs/" + packId + ".7z"))
            remaining.Add(b.Name);
        Assert.NotEmpty(remaining);
    }
```

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~DeadWeightCompactorTests.Recompact_Failure_During_Upload"`
Expected: FAIL（当前 `RecompactAsync` 先 `DeleteIfExistsAsync` 删全部旧卷，再上传 → 抛异常后旧卷已空）。

- [ ] **Step 3: 实现覆盖上传**

`IBlobUploader` 加 `UploadOverwriteAsync`（签名同 `UploadIfMissingAsync` 但不做存在性短路，直接 `Upload(overwrite: true)`）。若 `UploadIfMissingAsync` 内部已有上传实现，抽出私有 `UploadCoreAsync(..., bool overwrite)` 复用；`UploadIfMissingAsync` 保持 if-missing 语义。

- [ ] **Step 4: 实现 VolumeBlobIO.ReplaceAsync**

```csharp
    /// <summary>替换某归档全部分卷：以覆盖方式上传新卷（.001..M），全部成功后删除残留旧卷（.M+1..N）。
    /// 先传后删——崩溃窗口从「整 blob 丢失」降为「新旧卷混合」（可经检查/修复恢复）。</summary>
    public static async Task ReplaceAsync(
        IBlobUploader uploader, Account account, string container, string baseRef,
        IReadOnlyList<string> volumeFiles, AccessTier tier, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        // 1) 覆盖上传新卷（单卷=baseRef；多卷=baseRef.001..M，与现有命名一致）。
        var newNames = VolumeNames(baseRef, volumeFiles.Count);
        for (var i = 0; i < volumeFiles.Count; i++)
            await uploader.UploadOverwriteAsync(account, container, newNames[i], volumeFiles[i], tier, retry, ct, metadata);

        // 2) 删残留旧卷（旧卷数 > 新卷数时的尾部）。
        var keep = new HashSet<string>(newNames, StringComparer.Ordinal);
        // 用 BlobContainerClient 枚举 baseRef 前缀，删除不在 keep 里的。
        // （复用现有 factory/container 传入；或让调用方传 BlobContainerClient 以枚举删除。）
    }
```

> 注意现有 `VolumeBlobIO.UploadAsync` 的命名约定（`baseRef` vs `baseRef.001`）：单卷用 `baseRef`，多卷用 `baseRef.001..N`（倒序上传作提交标记）。`ReplaceAsync` 须沿用同命名与**倒序**（.001 最后传）以保持 dedup 提交语义。删残留旧卷需要 `BlobContainerClient` 来枚举——把 `ReplaceAsync` 签名调整为接收 `BlobContainerClient container` 与 `Account account`（当前 `UploadAsync` 用 `uploader`+`container.Name`；`ReplaceAsync` 两者都要：上传用 uploader，枚举删除用 client）。参考 `DeadWeightCompactor.RecompactAsync:135-140` 现有枚举删除写法。

- [ ] **Step 5: 改 DeadWeightCompactor.RecompactAsync 用 ReplaceAsync**

把 `:135-140`（先枚举删旧、再 `VolumeBlobIO.UploadAsync`）替换为单次 `VolumeBlobIO.ReplaceAsync(...)`（先传后删）。删掉提前的 delete 循环。

- [ ] **Step 6: 改 BackupRepairer 的 blob/pack 替换用 ReplaceAsync**

`ReplaceBlobAsync`（`:210-239`）与 `RepairPackAsync`（`:185-192`）：把"`DeleteVolumesAsync` 后 `VolumeBlobIO.UploadAsync`"改为 `VolumeBlobIO.ReplaceAsync`。raw 单文件路径同理（覆盖上传 + 无残留旧卷时 Replace 退化为纯覆盖）。

- [ ] **Step 7: 运行**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~DeadWeightCompactorTests"`
Expected: PASS（新测 + 既有压实测试；Azurite 缺失则 Skip）。

- [ ] **Step 8: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BlobUploader.cs backend/src/AzureStorageBackup.Api/Services/VolumeBlobIO.cs backend/src/AzureStorageBackup.Api/Services/DeadWeightCompactor.cs backend/src/AzureStorageBackup.Api/Services/BackupRepairer.cs backend/tests/AzureStorageBackup.Api.Tests/DeadWeightCompactorTests.cs
git commit -m "fix: atomic-ish pack/blob replace — upload new volumes then delete stale (no delete-first)

UploadIfMissing semantics forced delete-then-upload, so a crash between
lost the whole blob. Add UploadOverwriteAsync + VolumeBlobIO.ReplaceAsync
(upload new .001..M, then delete residual old .M+1..N). Used by dead-weight
compaction and repair.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: 🟠 多卷 Archive 全卷活化（§3.5）

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/BlobRehydration.cs`（共享助手）
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupChecker.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/RestoreOrchestrator.cs`（复用助手，可选）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupCheckerTests.cs`

**Interfaces:**
- Produces: `BlobRehydration.BeginAsync(BlobContainerClient container, string baseRef, AccessTier tier, CancellationToken ct)`——枚举 `baseRef` 前缀全部分卷，对仍是 Archive 且未在活化中的卷发起 `SetAccessTierAsync(tier)`。

- [ ] **Step 1: 写失败测试——多卷 baseRef 每卷都被请求活化**

用 fake/记录型 container（若测试项目已有 blob fake 基础）。若无 fake 基础，用集成 + Azurite 建多卷 blob，把首卷设 Archive tier（Azurite 支持 SetAccessTier 但不真活化）——较脆。**推荐**：把枚举逻辑做成可单测的纯函数——`BlobRehydration` 接收"卷名列表 + 每卷 tier 快照"返回"需活化的卷名"，单测该纯函数：

```csharp
    [Fact]
    public void Rehydrate_Targets_All_Archived_Volumes_Not_Just_First()
    {
        var volumes = new[]
        {
            ("packs/p1.7z.001", "Archive", (string?)null),   // 需活化
            ("packs/p1.7z.002", "Archive", "rehydrate-pending-to-hot"), // 已在活化中，跳过
            ("packs/p1.7z.003", "Hot", null),                // 已在线，跳过
        };
        var toBegin = BlobRehydration.SelectToBegin(volumes);
        Assert.Equal(new[] { "packs/p1.7z.001" }, toBegin);
    }
```

`BlobRehydration.SelectToBegin(IEnumerable<(string Name, string? AccessTier, string? ArchiveStatus)>)` = 选 `AccessTier=="Archive" && string.IsNullOrEmpty(ArchiveStatus)`。

- [ ] **Step 2: 运行验证失败**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~BlobRehydration|Rehydrate_Targets_All_Archived"`
Expected: FAIL（`BlobRehydration` 不存在）。

- [ ] **Step 3: 实现 BlobRehydration**

```csharp
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Archive 活化助手：对某归档（含全部分卷）发起活化。checker 与 restore 共用，避免只活化首卷。</summary>
public static class BlobRehydration
{
    /// <summary>从（卷名, AccessTier, ArchiveStatus）快照中选出需发起活化的卷：仍是 Archive 且尚未在活化中。</summary>
    public static IReadOnlyList<string> SelectToBegin(
        IEnumerable<(string Name, string? AccessTier, string? ArchiveStatus)> volumes) =>
        volumes.Where(v => v.AccessTier == "Archive" && string.IsNullOrEmpty(v.ArchiveStatus))
               .Select(v => v.Name).ToList();

    /// <summary>枚举 baseRef 前缀全部分卷，对需活化者发起 SetAccessTier（best effort）。</summary>
    public static async Task BeginAsync(BlobContainerClient container, string baseRef, AccessTier tier, CancellationToken ct)
    {
        var snapshot = new List<(string, string?, string?)>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, ct))
        {
            var props = (await container.GetBlobClient(b.Name).GetPropertiesAsync(cancellationToken: ct)).Value;
            snapshot.Add((b.Name, props.AccessTier, props.ArchiveStatus));
        }
        foreach (var name in SelectToBegin(snapshot))
        {
            try { await container.GetBlobClient(name).SetAccessTierAsync(tier, cancellationToken: ct); }
            catch { /* best effort */ }
        }
    }
}
```

- [ ] **Step 4: BackupChecker 用共享助手替换只活化首卷的 RehydrateAsync**

把 `BackupChecker.cs:234-239` 的 `RehydrateAsync`（只 `GetBlobClient(baseRef).SetAccessTierAsync`）改为调用：

```csharp
    private static Task RehydrateAsync(BlobContainerClient cc, string baseRef, AccessTier tier, CancellationToken ct) =>
        BlobRehydration.BeginAsync(cc, baseRef, tier, ct);
```

- [ ] **Step 5: （可选）RestoreOrchestrator.EnsureOnlineAsync 复用助手的发起段**

`EnsureOnlineAsync`（`:304-309`）的"发起活化"循环可替换为 `BlobRehydration.BeginAsync`（轮询等待段保留）。非必需，但去重更佳。

- [ ] **Step 6: 运行**

Run: `cd backend && dotnet build -c Release && dotnet test --filter "FullyQualifiedName~BlobRehydration|FullyQualifiedName~BackupCheckerTests"`
Expected: PASS。

- [ ] **Step 7: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BlobRehydration.cs backend/src/AzureStorageBackup.Api/Services/BackupChecker.cs backend/src/AzureStorageBackup.Api/Services/RestoreOrchestrator.cs backend/tests/AzureStorageBackup.Api.Tests/BackupCheckerTests.cs
git commit -m "fix: content-check rehydrates all archived volumes, not just the first

BackupChecker.RehydrateAsync only activated the base ref (first volume);
multi-volume archives could never complete rehydration. Extract shared
BlobRehydration.BeginAsync (enumerate by prefix) reused by checker/restore.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review 覆盖对照

- §2 → Task 1 ✅  §3.1 → Task 2 ✅  §3.2 → Task 3 ✅  §3.3 → Task 4 ✅  §3.4 → Task 5 ✅  §3.5 → Task 6 ✅
- 类型一致性：`RestoreResult` 新增 `FailedFiles` 在 Task 2 内一次性同步所有构造点与 `RestoreRunResponse`/前端接口。`ReplaceAsync`/`UploadOverwriteAsync` 在 Task 5 定义并被 compactor/repairer 消费。`BlobRehydration.SelectToBegin`/`BeginAsync` 在 Task 6 定义并被 checker 消费。
- 执行完 Plan 1 后进入 Plan 2（🟡 需求缺口）。
