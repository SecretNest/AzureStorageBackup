# 备份功能修复设计（2026-07-18）

## 0. 范围与方法

本 spec 覆盖 2026-07-18 备份功能全面审查（见 `memory/backup-audit-gaps.md`）发现的**全部**欠账：
🔴 高危数据完整性 1 项、🟠 中危 bug 5 项、🟡 需求缺口 8 组（含用户本轮追加的云端列表检查+孤儿回收）、⚪ 低危清理 9 项，外加用户本轮追加的 2 个还原增强需求（选择性还原估算 + pack 语义）。

**执行方法**：每一项走 TDD（先写失败测试→实现→绿）。按优先级顺序：🔴 → 🟠 → 🟡 → ⚪。
每项独立 commit。收尾要求：`dotnet build -c Release` 0 警告、非集成单测全绿、前端 build+lint 干净、CI 起 Azurite 后集成测试实跑。

---

## 1. 已锁定的设计决策（本轮 brainstorming 澄清）

| # | 决策点 | 结论 |
|---|--------|------|
| 1 | 还原文件浏览深度 | **完整懒加载树**。后端新增按目录路径返回子节点的端点，前端按需展开、文件夹级联全选。数据源=版本索引本地权威缓存（零云读）。 |
| 2 | 备份状态管理 | 持久态仅 **Normal/Error**（落库 `BackupConfig.Status` 列）；失败置 Error，**下次同类操作成功自动清回 Normal**，也可手动 reset。瞬时态（备份中/还原中/检查中/修复中）从 runner + BusyTracker **派生，不落库**。 |
| 3 | 还原冲突"重命名保留"命名 | **改名本地旧文件为 `{原名}.bak-{yyyyMMdd-HHmmss}`（冲突追加 `-1/-2…`），还原写回原名**。后缀模板固定，不做可配。 |
| 4 | 临时区尺寸运行时可配 | **立即生效**。`StagingArea` 每次背压判断经轻量 provider 实时读 `GlobalSettings`，下次 acquire 即用新值。 |
| 5 | 还原活化判定 | 估算下载量时**发 HEAD 实查**涉及的唯一 blob/pack 的真实 tier + 活化状态（元数据读取，Archive 亦可，不计 GB 取回费；还原本不占锁，不违背"备份期零云读"原则）。 |

用户追加需求（本轮）：
- **A**. 还原选文件后实时计算**下载数据量**（涉及 blob/pack 的压缩分卷尺寸合计，去重/pack 共享只计一次）与**解压后总量**（选中文件长度合计）与**文件数**，并在需要时等待活化。
- **B**. pack 语义：同一 pack 恢复多个文件时**不重复下载**、**不过量恢复**（只写选中成员）。现有 `RestoreGroupAsync` 已按 `StorageKey` 分组下载一次、仅写 `needed`；只要选择性还原**先把索引条目过滤到选中路径集**再喂现有管线即可自然满足。

---

## 2. 🔴 高危：StagingArea 跨备份暂存文件名竞态

**问题**（`StagingArea.cs:57-69`）：`MoveToStaged` 用产出文件名（`pNNNN.7z` / `{hash}.7z`）作 `stagedTempDir` 里的固定暂存名，且 `File.Move(overwrite:true)`。压缩在 `_compressLock` 内串行，但**上传在锁外**——不同 container 被调度器 fire-and-forget 并发备份时，两次备份可能产出同名文件（如都从 `p0001` 起、或同 hash），后到者 `File.Move(overwrite:true)` 覆盖前者**正在上传**的暂存文件 → Linux 静默数据损坏。`BackupBusyTracker` 只挡同 (account,container)，挡不住跨 container。restore/check/compact 都已用 GUID 子目录隔离，唯独备份主路径没有。

**修复**：每次 `StageAsync` 调用在 `stagedTempDir` 下用 **GUID 子目录**隔离该次暂存产出。
- `MoveToStaged` 改为 `Path.Combine(stagedTempDir, guid, fileName)`，`StagedItem` 记该子目录。
- `Release` 删文件后连带删空的 GUID 子目录。
- `compress-temp` 侧同理需隔离（压缩虽串行，但产出→移动之间若有异常残留也应按次清理）。

**测试**（`StagingAreaTests`，纯单测无需 Azurite）：
- 构造两次并发 `StageAsync`，`produce` 故意产出**同名**文件（如都叫 `p0001.7z`），断言两次 `StagedItem.Files` 路径**不相等**、两份内容都完整存在、`Release` 各自删对。
- 现有背压/字节上限测试须继续通过（GUID 子目录不改变字节记账逻辑）。

---

## 3. 🟠 中危 bug（5 项）

### 3.1 还原替代用"意图"而非"解析成功"判据
**问题**（`RestoreOrchestrator.cs:111`）：`unresolved = UnrecoverablePaths.Where(p => !Substitutions.ContainsKey(p))`——只要用户**声明**了替代意图就不跳过，但若该替代版本被保留清理删掉（`info.Versions` 找不到 / `srcByPath` 无此路径），`byPath[kv.Key]` 仍是**本版本的不可恢复条目**，还原时 blob 缺失 → 抛错，且 `Task.WhenAll` 无逐组容错 → **整个还原失败**。

**修复**：判据改为"**替代是否真解析成功**"。
- 替代解析阶段记录 `resolved`（Set<path>）：仅当替代版本存在**且** `srcByPath` 含该路径时加入并写 `byPath`。
- `unresolved = UnrecoverablePaths.Where(p => !resolved.Contains(p))`——声明了但没解析成功的路径回落"跳过"（记 skipped），不进还原、不报错。
- 附带健壮性：`RestoreGroupAsync` 内单组失败不应炸全局——`Task.WhenAll` 改为收集逐组结果/异常，单组失败记入一个 `failedPaths` 列表返回给 `RestoreResult`（新增字段 `FailedFiles`），其余组照常完成。

**测试**：替代到一个已被保留清理删除的版本 → 断言该路径落 skipped、其余文件正常还原、整体不抛。

### 3.2 修复绕过本地权威状态机
**问题**（`BackupRepairer.cs:73-75`）：修复用 `store.WriteIndexAsync`/`store.WriteInfoAsync` 直接写云端，**未经 `TrackedInfoStore`**（ETag + 本地信息缓存）**也未更新 `LocalIndexCache`**。修复改了信息文件（云端 ETag 变），但本地缓存仍持旧 ETag/旧索引 → 下次备份 `WriteInfoConditionalAsync` 带旧 `If-Match` 必 412 失败一次（自愈，但误报一次错误 + 触发状态置 Error）。

**修复**：给 `BackupRepairer` 注入 `TrackedInfoStore` 与 `ILocalIndexCache`（都可选，保持无缓存回落）。
- 信息文件写改走 `trackedInfo.WriteAsync`（串 ETag、更新本地缓存）。
- 改动过的版本索引写云端后，`indexCache.PutAsync` 回填（或 `Invalidate`）对应版本，使缓存与云端一致。
- `identity` 用信息文件 `Backup.CreatedAt`（与编排器一致，识别 container 重建）。

**测试**：修复后紧接一次备份的 `WriteInfoConditionalAsync` 用**新** ETag（不 412）；本地索引缓存读到修复后的尺寸/不可恢复标记。

### 3.3 ETag 冲突未失效版本索引缓存
**问题**（`BackupOrchestrator.cs`，`indexCache.PutAsync` 在 `trackedInfo.WriteAsync` **之前**）：新版本索引先写云端 + `indexCache.PutAsync` 入本地缓存，随后 `trackedInfo.WriteAsync` 提交信息文件。若信息文件写遇 412/409 冲突抛错，本次版本**从未提交**到信息文件，但其版本索引已污染 `LocalIndexCache` → 下次备份可能读到这个**幽灵版本索引**做 diff → 错乱。

**修复**：把 `indexCache.PutAsync` 移到 `trackedInfo.WriteAsync` **成功之后**；或用 try/catch 在信息文件写失败时 `indexCache.InvalidateAsync(account, container, version)`。选**前者**（顺序调整最简、最不易错）：先提交信息文件，成功后再入索引缓存。
- 注意：`WriteIndexAsync`（上传云端索引 blob）仍在前（信息文件需引用其 blob 名），仅**本地缓存 Put** 后移。冲突时云端多一个未被引用的孤儿索引 blob，由保留清理的孤儿回收处理（已有）。

**测试**：模拟 `trackedInfo.WriteAsync` 抛冲突 → 断言 `LocalIndexCache` 无该版本条目。

### 3.4 死重压实/修复"先删后传"非原子
**问题**（`DeadWeightCompactor.cs:135-138`、`BackupRepairer.cs:191-192/214`）：重压/替换 pack 与 blob 时**先删旧全部分卷再上传新卷**。根因是 `IBlobUploader.UploadIfMissingAsync` 是 **upload-if-missing** 语义（同名存在则跳过），所以无法直接覆盖，只能先删。但删完到传完之间若崩溃 → **blob 彻底丢失**（比损坏更糟：连自身都无法作为修复源）。

**修复**：引入**可覆盖上传**并改顺序为"**先传新、后删残留旧卷**"。
- `IBlobUploader` 增 `UploadOverwriteAsync`（或给 `UploadIfMissingAsync` 加 `overwrite` 参数）；`VolumeBlobIO` 增 `ReplaceAsync(volumes...)`：以 overwrite 上传新卷 `.001..M`，**上传全部成功后**再枚举删除残留的旧 `.(M+1)..N`（新卷数 ≤ 旧卷数时）。
- 崩溃窗口从"整 blob 丢失"降为"新旧卷混合"——数据仍在，深度检查可发现、修复可从本地重建，属可恢复态。单用户场景下此改进足够（完整两阶段 temp-blob+swap 成本高、边际收益小，不采）。**用户已确认接受此残余窗口**。
- 应用点：`DeadWeightCompactor.RecompactAsync`、`BackupRepairer.ReplaceBlobAsync` + `RepairPackAsync`。
- **配套**：残余"新旧卷混合"里多出来的旧卷（`.M+1..N` 若删除失败），以及其它来源的孤儿 blob，由新增的 **§4.8 云端列表检查 + 孤儿回收** 统一发现并在修复时删除。

**测试**：装饰 uploader 在"删旧后、传新前"注入崩溃（抛异常）→ 断言旧卷仍在（未被提前删空）；正常路径断言新内容替换成功、无残留多余旧卷。

### 3.5 Content 检查对多卷 Archive 活化无效
**问题**（`BackupChecker.cs:234-239` `RehydrateAsync`）：深度检查遇 Archive 未活化时，只对 `baseRef`（**首卷**）`SetAccessTierAsync`。多卷归档（`.001..N`）只有首卷被活化，其余卷仍 Archive，下次重跑检查仍下载失败。对照 `RestoreOrchestrator.EnsureOnlineAsync` 已正确按前缀枚举全部分卷活化。

**修复**：`RehydrateAsync` 改为**按前缀枚举全部分卷**逐个发起活化（对仍是 Archive 且未在活化中的卷 `SetAccessTierAsync`），与 `EnsureOnlineAsync` 一致。可抽出共享助手 `BlobRehydration.BeginAsync(container, baseRef, tier, ct)` 供 checker 与 restore 复用。

**测试**（集成/Azurite；Azurite 不支持 Archive 语义，故以单测覆盖枚举逻辑）：用 fake container 记录 `SetAccessTierAsync` 调用，断言多卷 baseRef 下**每一卷**都被请求活化。

---

## 4. 🟡 需求缺口

### 4.1 还原：选择性文件浏览 + 冲突模式 + Rehydrate 优先级 + 下载量估算（决策 1/3/5 + 需求 A/B）

这是本轮最大一块，拆成协同的几件：

**(a) 懒加载目录树端点**（决策 1）
- 新增 `GET /api/backup-configs/{id}/tree?version={v}&path={dir}`：返回 `path` 目录**直接子节点**（子目录 + 文件），每个文件节点带 `length`、`mtime`、`storageKind`（blob/pack）、`storageRef`（用于估算去重）。数据源=版本索引（本地权威缓存优先，`TrackedInfoStore`/`LocalIndexCache`；缺则云端读一次）。`path` 为空=根。
- 目录节点标 `hasChildren`，前端展开时再请求。空目录（`EmptyDirs`）也作为可展开节点纳入。

**(b) 下载量/解压量估算端点**（需求 A + 决策 5）
- 新增 `POST /api/backup-configs/{id}/restore-estimate` body `{version, paths[]}`：
  - 把 `paths` 映射到版本索引条目 → 收集其 `StorageRef`（blob/pack）**去重**（同 pack/同 data blob 只计一次）。
  - `downloadBytes` = 这些唯一存储对象的 `VolumeSizes` 合计（压缩后下载量；pack 即便只选其中一个成员也计整包）。
  - `uncompressedBytes` = 选中文件 `Length` 合计；`fileCount` = 选中文件数。
  - **HEAD 实查**（决策 5）每个唯一存储对象首卷 tier + `ArchiveStatus`：`archivedObjects`（需活化的对象数）+ `rehydratePending`（已在活化中）→ 前端提示"需等待活化（约数小时）"。
  - 估算只读元数据，不下载；并发 HEAD（复用 `DownloadConcurrency` 上限）。

**(c) 选择性还原 + 冲突模式 + Rehydrate 优先级**（决策 3、需求 B）
- `RestoreRequest` 增：
  - `IReadOnlyList<string>? SelectedPaths`（null=整版本，兼容现状）——非 null 时在 `RunCoreAsync` 先把 `byPath`/`fileEntries` **过滤到选中集**（自然满足需求 B：pack 只下一次、只写选中成员）。
  - `RestoreConflictMode Conflict { Skip, RenameKeep, OverwriteIfChanged }`，默认 `OverwriteIfChanged`（现状语义）。
  - `RehydratePriority { Standard, High }`，透传到活化 `SetAccessTierAsync`（`RehydratePriority` 参数），High=数分钟级、计费更高。
- `NeedsRestoreAsync` / 写回逻辑按 `Conflict` 分流：
  - `Skip`：目标已存在（无论内容）→ 跳过，计 skipped。
  - `OverwriteIfChanged`：现状（hash 不同才写）。
  - `RenameKeep`：目标存在且内容不同 → 先把本地旧文件改名 `{name}.bak-{yyyyMMdd-HHmmss}`（存在则 `-1/-2…`），再写还原内容到原名。目标不存在或内容相同 → 直接写/跳过（无需改名）。时间戳由后端生成。
- 端点 `POST /{id}/restore` body 增 `selectedPaths?`、`conflict?`、`rehydratePriority?`；`RestoreRunner.Start` 与 `RestoreRunResponse` 透传。

**(d) 前端**（`BackupConfigsPage.tsx` 还原区）
- 还原对话框：选版本 → 懒加载树浏览 + 勾选（文件夹级联全选）→ 展示实时估算（下载量/解压量/文件数/是否需活化，选择变化时防抖调 estimate）→ 选冲突模式（下拉）+ Rehydrate 优先级（仅当估算显示有 Archive 对象时出现）→ 目标根路径 → 开始。
- `api/backupConfigs.ts` 增 `tree`、`restoreEstimate`，`restore` 增参。

**测试**：
- 树端点：构造多层版本索引，断言分层返回、`hasChildren` 正确、空目录纳入。
- 估算：pack 内选 2 文件 → `downloadBytes` 只计一次 pack 卷尺寸；去重 blob 被多路径引用只计一次；`uncompressedBytes`/`fileCount` 正确。
- 选择性还原：只选子集 → 只写选中、pack 只下载一次（装饰 IO 计数）、未选成员不落地（防过量恢复）。
- 冲突模式：`Skip`/`RenameKeep`（断言旧文件被改成 `.bak-*` 且原名=还原内容）/`OverwriteIfChanged` 三路径各一测。

### 4.2 备份状态持久化 + reset（决策 2）
- `BackupConfig` 增列 `Status`（enum `BackupStatus { Normal=0, Error=1 }`，默认 Normal）+ `LastError`（string?）+ `LastErrorAt`（DateTimeOffset?）。EF 迁移新增列。
- **写入点**：`BackupRunner`/`RestoreRunner`/`RepairRunner` 及 `/check` 完成时——失败置 `Error`+`LastError`+`LastErrorAt`；成功置 `Normal` 清 `LastError`（决策 2 的"成功自清"）。调度器路径（`TaskDispatcher`）同样回写。
- **派生瞬时态**：configs 列表/详情端点在返回 DTO 时叠加当前运行态——查 `BackupRunner`/`RestoreRunner`/`RepairRunner` 内存状态 + `BackupBusyTracker`，若在跑则 `effectiveStatus` = `BackingUp/Restoring/Checking/Repairing`，否则=持久 `Status`。DTO 同时回传持久 `status` 与派生 `activity`。
- 新增 `POST /{id}/reset-status`：手动清 Error→Normal。
- 前端：配置列表每行展示状态徽标（灰=Normal、蓝=进行中活动、红=Error 附 tooltip 显示 LastError + reset 按钮）。

**测试**：失败运行→ `Status=Error`+LastError 落库；随后成功运行→自动 Normal；reset 端点清错；运行中列表派生出 BackingUp。

### 4.3 删除备份询问是否连删 container
- `DELETE /{id}` 增查询参 `deleteContainer=bool`（默认 false，仅删本地配置 + 本地缓存 + 日志，现状）。为 true 时**额外**删除云端整个 container（`ContainerService`/`BlobContainerClient.DeleteAsync`）。
- 前端删除确认对话框加复选框"同时删除云端 container（不可逆，抹除所有备份数据）"，默认不勾。二次确认文案强调不可逆。

**测试**：`deleteContainer=false` 只删配置、container 仍在；`=true` 连 container 一并删（Azurite 集成）。

### 4.4 不压缩/不分组"目录模式"失效
**问题**：`DontCompress`/`DontGroup` 按**单文件路径**以 `IsIgnored(path, isDirectory:false)` 匹配，规则如 `logs/`（目录规则）对文件 `logs/a.txt` 匹配不到 → 目录级不压缩/不分组失效，与忽略列表（扫描器按目录遍历，目录规则生效）行为不一致。
- 排查 `IgnoreRuleSet.IsIgnored` 的目录规则语义：忽略列表在扫描时对**目录节点**判定，命中则整棵剪枝；而 DontCompress/DontGroup 只在文件上判定，从不问"某祖先目录是否命中规则"。
**修复**：判定文件是否命中 DontCompress/DontGroup 时，除自身路径外，**逐级检查祖先目录**是否命中（对每个祖先以 `isDirectory:true` 调 `IsIgnored`），任一命中即视为命中。抽一个共享助手 `RuleSet.MatchesFileOrAncestorDir(path)` 供两处（及未来）复用。

**测试**：规则 `logs/` + 文件 `logs/app.log` → DontCompress 命中（store-only）；规则 `*.iso` 直接文件命中仍工作；深层 `a/logs/b/c.bin` 命中。

### 4.5 向导"创建后锁定基础字段"前后端强制
**问题**：`BackupMeta` 注释称"配置创建后不可改，除名字/描述"，但 `PUT /{id}` 与前端均未强制——改 `ContainerName`/`AccountId`/`LocalRoot`/`Password`/加密性/Tier 会使本地状态（`TrackedInfoStore`/`LocalIndexCache` 按 account+container 键）与云端错位。
**修复**：
- 后端 `BackupConfigService.UpdateAsync`：创建后**仅允许改** `Name`/`Description`/规则（Ignore/DontCompress/DontGroup）/保留策略/`VolumeBytes`/`VerboseLogging`/并发无关项；对基础字段（AccountId/ContainerName/**LocalRoot**/Password/IndexTier/DataTier/加密）若与现值不同则拒绝（400，明确报"基础字段创建后不可修改"）。
  - **`LocalRoot` 纳入锁定**（用户已确认）：编辑同一配置时不可改根，跨设备重指定根走**导入**流程。
- 前端编辑态：基础字段渲染为只读（现向导已"编辑时字段锁定"，需确认与后端一致并补齐所有基础字段）。

**测试**：`PUT` 改 ContainerName → 400；改 Name/规则 → 200。

### 4.6 向导"立即备份 / 暂不"
- 新建配置成功后，前端提供"立即运行首次备份"/"暂不"两个动作（"立即"即调 `POST /{id}/run`）。纯前端交互，无后端改动。

**测试**：前端组件测试（若有）或手动冒烟；至少保证不回归 build/lint。

### 4.7 临时区尺寸经 Settings UI 配（决策 4）
- `GlobalSettings` 增 `StagedLimitBytes`（**默认 2GB** = `2L * 1024 * 1024 * 1024`，用户已确认；启动首次迁移时若 appsettings `Backup:StagedLimitBytes` 有值则作初始值）。
- `StagingArea` 从"构造固定 `stagedLimitBytes`"改为**注入一个 provider**（`Func<long>` 或轻量 `IStagedLimitProvider`），背压判断 `while (StagedBytes >= _limit())` 每次实时读（决策 4 立即生效）。provider 实现从 `GlobalSettings` 读（带短缓存避免每次打库，或直接读单例服务）。
- DI：`StagingArea` 单例仍全局串行压缩；provider 从 scoped `IGlobalSettingsService` 取值（用 `IServiceScopeFactory` 或缓存快照）。
- 前端 Settings 增"Staging area size limit (MB)"字段。

**测试**：provider 返回不同值 → 背压阈值随之变（单测注入可变 provider，断言超阈值阻塞、调低后放行）。

### 4.8 云端列表检查 + 孤儿回收（用户本轮追加）

**动机**：容器里可能残留**未被任何保留版本引用的垃圾 blob**——来源包括 §3.4 非原子窗口的残余旧卷、失败上传的半成品、ETag 冲突留下的孤儿索引 blob（§3.3）、外部误操作等。它们只占用存储、不影响还原正确性，但应能被发现并清理。

**设计**：给云端检查加一个**列表检查**维度（container-wide，区别于现有按文件的 CloudCheckLevel）。
- `CheckOptions` 增 `bool ListOrphans`（默认 false；前端"云端列表检查"勾选项触发）。
- 开启时，`BackupChecker` 枚举 container 下全部 blob（`GetBlobsAsync`），构造**引用集** = 以下全部 blob 名的并集：
  - 信息文件 blob（`.json` / `.json.enc`）；
  - **每个保留版本**的 `IndexBlob`（遍历 `info.Versions`，不止被检查的那个版本）；
  - **全部保留版本索引**里每个 `StorageRef`（data blob / pack）**含其所有分卷**（`.001..N`，按 `Volumes`/`VolumeSizes` 或前缀枚举）。
  - 注意需读取**所有**保留版本的索引（本地权威缓存优先），否则只被旧版本引用的 blob 会被误判孤儿。
- 引用集之外的 blob = **孤儿**，计入 `CheckReport.OrphanBlobs`（新字段 `IReadOnlyList<string>`）。检查结果 `Ok` 不因孤儿转 false（孤儿是"可清理"而非"损坏"），但报告里单独列出、前端展示数量与列表。
- **修复删除**：`BackupRepairer.RepairAsync` 在开启列表检查/传入孤儿集时，删除 `OrphanBlobs`（`DeleteIfExistsAsync`，含分卷）。为安全起见，孤儿删除**只在显式修复**时执行（检查只报告不删），且删除前再次确认不在引用集（防 TOCTOU：删除阶段重新读一次信息文件 + 全版本索引构引用集）。
- 端点：`/check` 增 `listOrphans=bool` 参；`/repair` 增 `cleanupOrphans=bool` 参（或复用 check 级别推导）。`RepairReport` 增 `DeletedOrphans` 字段。

**安全边界**：绝不删信息文件、任何保留版本的索引 blob、任何被引用的数据/pack 卷。删除集严格 = 实际列出 − 引用集。跨设备/未同步场景（本地无全部版本索引缓存）应回落云端读全部版本索引再比对；若无法取全引用集则**放弃孤儿删除**（记 Warning，不冒险删）。

**测试**：
- 构造 container：投入 1 个被引用 pack（多卷）+ 1 个只被旧版本引用的 data blob + 1 个真正孤儿 blob + 残余旧卷 → 断言 `OrphanBlobs` 恰含孤儿与残余旧卷，**不含**被任何保留版本引用者与信息/索引 blob。
- 修复删除后重列 container：孤儿消失，引用 blob 全在。
- 无法取全引用集（模拟缺版本索引）→ 放弃删除、记 Warning。

---

## 5. ⚪ 低危清理（9 项）

| # | 项 | 修复 |
|---|----|------|
| 5.1 | 重试/重校验阈值硬编码 | `ProcessingVerifier` 的 `MaxAttempts`（默认 5）提到 `GlobalSettings.ProcessingMaxAttempts`，经选项透传。 |
| 5.2 | 死代码 | 删除 `DeadWeightAnalyzer`（纯逻辑未接入，已被 `DeadWeightCompactor` 取代）与 `BlobUploader.UploadBatchAsync`（无调用方）。删前 grep 确认零引用。 |
| 5.3 | 日志来源无 account 维度 | 操作日志 `source` 从 `check:{container}` 改为含 account 标识（如 `check:{accountId}/{container}` 或结构化字段）。三个引擎 + 调度器统一。 |
| 5.4 | 通知 content-type 带 charset 抛异常 | `NotificationSender` 设置 content-type 时用 `MediaTypeHeaderValue.Parse` 容忍 `; charset=utf-8`，或分离 media-type 与 charset，避免 `application/json; charset=utf-8` 抛 FormatException。 |
| 5.5 | 删配置不清 verbose 文本 | 确认 `DeleteForContainerAsync` 连带清该配置的 debug/verbose 日志（含 ephemeral）；补齐遗漏。 |
| 5.6 | 组成员未排序 | `GroupMember` 查询/展示按稳定序（如 Order 字段或按 name）排序，避免 UI 抖动。 |
| 5.7 | 前端重复 label 字典 | 合并 `BackupConfigsPage`/api 里重复的枚举 label 映射到单一常量模块。 |
| 5.8 | 多个 HTTP 端点无测试 | 为 `/check`、`/repair`、`/versions`、`/file-versions`、`/unrecoverable`、新增的 `/tree`、`/restore-estimate`、`/reset-status` 补端点级测试。 |
| 5.9 | CI 未起 Azurite | CI workflow 增 Azurite 服务（npm 方式，同本地约定），使 `[Integration]`+`SkippableFact` 实跑而非整批跳过——否则引擎主链路（orchestrator/checker/restore/repairer/compactor）零验证。 |

---

## 6. 测试与验证策略

- **纯逻辑/单测**（无需 Azurite）：StagingArea 竞态、还原替代判据、冲突模式命名、目录规则祖先匹配、估算去重、状态机迁移、原子上传顺序（fake IO）、多卷活化枚举、staging provider。
- **集成/Azurite**：选择性还原端到端、删 container、修复后备份不 412、树/估算端点、云端列表检查发现孤儿 + 修复删除。CI 起 Azurite 后这些实跑。
- **收尾门槛**：`dotnet build -c Release` 0 警告；非集成单测全绿；CI Azurite 下集成绿；前端 build+lint 干净；真实进程冒烟（选择性还原 + 状态徽标 + 临时区尺寸改后生效）。

---

## 7. 执行顺序（逐项 TDD，各自 commit）

1. 🔴 §2 StagingArea GUID 隔离
2. 🟠 §3.1 还原替代判据 → §3.2 修复状态机 → §3.3 ETag 缓存失效 → §3.4 原子上传 → §3.5 多卷活化
3. 🟡 §4.7 临时区可配（小、独立）→ §4.4 目录规则（小）→ §4.8 云端列表检查 + 孤儿回收（承 §3.4）→ §4.2 状态持久化（迁移，多处联动）→ §4.5 锁定基础字段 → §4.3 删 container → §4.1 还原大件（树/估算/选择性/冲突/优先级/前端）→ §4.6 向导立即备份
4. ⚪ §5.1–5.8 清理 → §5.9 CI Azurite（最后，让全部集成测试实跑验证）

> §4.1 是最大件，内部再拆：树端点 → 估算端点 → RestoreRequest 选择性+冲突+优先级 → 前端。每子件独立 TDD。

---

## 评审确认点（已定）

1. **§4.5** `LocalRoot` **纳入创建后锁定**（用户确认），跨设备走导入。
2. **§3.4** 原子性采"先传新卷 + 可覆盖上传，后删残留旧卷"，**接受**残余"新旧卷混合"窗口；用户追加要求：残余旧卷及其它孤儿由 **§4.8 云端列表检查 + 修复删除** 统一回收。
3. **§4.7** `StagedLimitBytes` 默认 **2GB**（用户确认）。
