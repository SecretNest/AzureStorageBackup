# 备份功能修复（2026-07-18）

> 2026-07-18 对备份功能做完整审查（需求合理性 + 实现完整性，5 子系统并行核对 + 精读核心文件 + 实跑构建），发现 1 项高危数据完整性缺陷、5 项中危 bug、8 组需求缺口、9 项低危债务。本文件固化「审查发现 + 已锁定的设计决策 + 已实施的修复」，作为该轮修复的唯一记录。修复以 TDD 逐项完成、逐项独立评审，全部并入分支后整分支复审通过。
>
> 对应里程碑 M4–M8 的补强，补充 [product-requirements.md](product-requirements.md)、[backup-feature-design.md](backup-feature-design.md)、[consistency-audit.md](consistency-audit.md)。

## 1. 设计决策（本轮锁定）

| # | 决策点 | 结论 |
|---|--------|------|
| 1 | 还原文件浏览深度 | **完整懒加载树**：后端按目录路径返回直接子节点（`GET /backup-configs/{id}/tree`），前端按需展开、文件夹级联全选。数据源=版本索引本地权威缓存（零云读）。 |
| 2 | 备份状态管理 | 持久态仅 **Normal/Error**（`BackupConfig.Status` 列）；失败置 Error，下次同类操作成功**自动清回** Normal，也可手动 reset。瞬时态（备份中/还原中/检查中/修复中）从 runner + BusyTracker **派生，不落库**。 |
| 3 | 还原冲突"重命名保留"命名 | **改名本地旧文件为 `{原名}.bak-{yyyyMMdd-HHmmss}`（冲突追加 `-1/-2…`），还原写回原名**，先改名后写、旧内容永不丢失。 |
| 4 | 临时区尺寸运行时可配 | **立即生效**：`StagingArea` 每次背压判断经 provider 实时读 `GlobalSettings.StagedLimitBytes`（默认 2GB）。 |
| 5 | 还原活化判定 | 估算下载量时**发 HEAD 实查**涉及 blob/pack 的真实 tier + 活化状态（元数据读取，不计 GB 取回费；还原本不占锁）。 |

追加需求：还原选文件后实时计算**下载数据量**（涉及 blob/pack 压缩分卷尺寸，去重/pack 共享只计一次）+ **解压后总量** + 文件数；pack 语义——同一 pack 恢复多个文件时**先过滤到选中路径集**再分组，只下一次、只写选中成员（不过量恢复）。

## 2. 已修复项

### 🔴 高危（数据完整性）
- **StagingArea 跨备份暂存文件名竞态**：产出名固定 + `File.Move(overwrite)`，不同 container 并发备份互相覆盖在传文件（Linux 静默损坏）。→ 每次 `StageAsync` 用 **GUID 子目录**隔离。

### 🟠 中危 bug
- **还原替代判据**：改为「是否真解析成功」而非「用户意图」——替代版本被清理时回落跳过、不再整体失败；`Task.WhenAll` 改逐组容错（`RestoreResult.FailedFiles`）。
- **修复走本地权威状态机**：`BackupRepairer` 注入 `TrackedInfoStore`+`LocalIndexCache`，信息/索引写经本地权威（消除修复后下次备份误 412）。
- **ETag 冲突不留幽灵版本**：版本索引本地缓存 `Put` 移到信息文件提交**成功之后**（冲突时不污染缓存）。
- **原子替换**：`IBlobUploader.UploadOverwriteAsync` + `VolumeBlobIO.ReplaceAsync`——先覆盖上传新卷（保持 .001 末位提交标记）、后删残留旧卷；崩溃窗口从「整 blob 丢失」降为「可恢复的新旧卷混合」。压实（`DeadWeightCompactor`）与修复共用。
- **多卷 Archive 全卷活化**：抽 `BlobRehydration.BeginAsync`（按前缀枚举全部分卷），修复检查器只活化首卷的缺陷；还原保持 fail-fast 不复用（避免吞异常挂起占锁）。

### 🟡 需求缺口
- **临时区上限运行时可配**（决策 4）。
- **不压缩/不分组的目录规则**：`IgnoreRuleSet.MatchesFileOrAncestorDir`——文件命中或任一祖先目录命中，`logs/` 类规则对其下文件生效。
- **云端列表检查 + 孤儿回收**：`CheckOptions.ListOrphans` 枚举容器 blob 减去引用集（信息文件 + 全部保留版本索引 + 各 StorageRef 全部分卷）= 孤儿；修复删除**删前重建引用集**（TOCTOU 安全）、取不全则放弃删除记 Warning，绝不删被引用/信息/索引 blob。
- **备份状态持久化 + reset**（决策 2）。
- **锁定基础字段**：创建后 AccountId/ContainerName/LocalRoot/Password/Tier 不可改（`UpdateAsync` 拒绝→400；前端只读），跨设备重指定走导入。
- **删配置可连删 container**：`DELETE ?deleteContainer=true` 额外删云端 container（前端二次确认不可逆）。删配置一并清 `CachedVersionIndex`+`LocalBackupState`（按 account+container，防 re-create 残留）。
- **选择性还原**：懒加载树端点（决策 1）+ 估算端点（决策 5，需求 A）+ `RestoreRequest.SelectedPaths/Conflict/RehydratePriority`（决策 3、需求 B）+ 前端还原对话框（树浏览 + 实时估算 + 冲突模式 + 优先级）。
- **向导「立即备份/暂不」**。

### ⚪ 低危清理
- `ProcessingVerifier.MaxAttempts` 可配（`GlobalSettings.ProcessingMaxAttempts`）。
- 删死代码 `DeadWeightAnalyzer` + `BlobUploader.UploadBatchAsync`。
- 操作日志 source 含 account 维度：`{op}:{accountId}/{container}`；`DeleteForContainerAsync` 按 account 精确匹配（防跨账户误删）。
- 通知 content-type 容忍 `; charset=…`（`MediaTypeHeaderValue.Parse`），并保留 utf-8 默认声明。
- 组成员稳定排序 `(AccountId, ContainerName)`。
- 前端合并重复枚举 label 到 `constants/labels.ts`。
- 补 8 个 HTTP 端点级测试。
- 新增 `.github/workflows/ci.yml`：起 Azurite + 7-Zip，使 `[Integration]` 测试在 CI 实跑（原本全跳过，引擎主链路零 CI 验证）。
  - CI 首跑暴露并修复了一个**并发 flaky**：xUnit 跨测试类并行，各 `TestWebAppFactory` 的 `StagingArea` 单例共享默认 `/tmp/azurestoragebackup` 压缩临时区，并行备份相同小内容→同压缩输出名→跨主机撞车。修法：每个测试主机独立 `Backup:TempPath`。修复后 CI 连续多次全绿。

## 3. 评审额外发现并修复的缺陷
逐项两阶段评审在计划外发现并修复 3 个真实缺陷：
1. **跨账户日志误删**：`DeleteForContainerAsync` 曾以 `EndsWith("/"+container)` 匹配，会连删同名 container 的其它账户日志 → 改账户范围精确匹配。
2. **还原活化 fail-fast 回归**：复用 best-effort 活化助手会吞 `SetAccessTier` 异常并在无超时轮询里挂起占锁 → 还原保持异常传播。
3. **rehydrate tier 空引用崩溃**：`rehydrate is {} t ? MapTier(t) : null` 因 `AccessTier` 隐式 string 转换，在未指定 tier 时对 /check、/repair、计划 Check 全部抛 `ArgumentNullException`（默认路径）→ 3 处显式 `(AccessTier?)` 转换。

## 4. 验证
- `dotnet build -c Release` 0 警告；后端全量含集成 **332/332 通过、0 跳过**（Azurite + 7-Zip 在场）；前端 build + lint 干净。
- 逐项 TDD（失败测试→实现→绿）+ 逐项 spec/质量评审 + 整分支复审（判可合并，零 Critical/Important）。

## 5. 收尾 follow-up（全部已修，2026-07-18 第二批）
终审列出的 Minor 已逐项修复（全量含集成 330/330 通过、0 警告）：
- **`VolumeBlobIO.ReplaceAsync` 前缀删卷**：新增 `IsVolumeOf`（仅匹配基名或 `基名.<数字>` 卷后缀），删除时按它过滤，排除碰撞避让兄弟 `data/{hash}~N`。
- **迁移新列默认 0 显示**：`GlobalSettingsService.GetAsync` 规范化非正值到模型默认（`StagedLimitBytes`→2GB、`ProcessingMaxAttempts`→5），`GET /settings` 不再显示 0。
- **派生活动误标**：`BackupBusyTracker` 记录操作标签，`DeriveActivity` 读实际标签；`TaskDispatcher` 按任务类型传 `BackingUp/Checking/CleaningUp`（前端 `BackupActivity` 加 `CleaningUp`）。
- **`WriteStatusAsync` 重复 + 静默吞**：抽 `IBackupConfigService.WriteStatusAsync` 扩展（五处共用），失败记 Warning。
- **`/tree` 语义**：未知版本改返回 200 `[]`，与 `/unrecoverable`、`/file-versions` 一致。
- **删配置清理非事务**：三步善后（日志/索引缓存/本地状态）各自 best-effort，单步失败记 Warning、不阻断其余、不 500。
- **修复读信息文件**：`BackupRepairer.RepairAsync` 改走 `TrackedInfoStore.LoadAsync`（本地权威优先），与编排器/检查器一致。

**零残留清理（评审曾判「可发布」的 5 个 cosmetic/既有 Minor，也一并清掉）**：
- `StagingArea.MoveToStaged`：空产出不建 GUID 子目录；`File.Move` 中途失败清理子目录后重抛（异常路径不错记字节）。
- `BlobRehydration.BeginAsync`：直接读 List Blobs 返回的 AccessTier/ArchiveStatus，免每卷单独 `GetProperties`。
- 孤儿引用集构造的 `catch` 排除 `OperationCanceledException`（取消正常传播，而非被当作「放弃删除」吞掉）。
- 孤儿删除循环逐个 best-effort：单个 `DeleteIfExists` 失败记 Warning 并继续，不中断其余（引用集外才删，绝不碰有效数据）。
