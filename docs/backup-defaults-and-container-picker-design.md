# 备份表单：默认值继承与 container 选择器（2026-07-26）

> 新建备份的表单有两处缺口，都在同一个界面上，本轮一并处理。
>
> **一、`使用默认` 没有实现。** PRD §3 要求「每个备份若勾选『使用默认』则套用这些值」，代码却在打开表单时把全局默认**抄了一份具体数值**（`BackupConfigsPage.tsx` 的 `startNew`）。存进配置的是快照，此后改全局设置，已有备份一个都不跟随。这是实现未达 PRD，不是新需求。
>
> **二、container 只能手打。** account 是下拉选的，container 却是纯文本框，尽管应用已有列举容器的接口。后端 `POST /api/backup-configs` 只校验非空，既不查存在也不建，打错一个字母就静默存下一份跑起来才失败的配置。
>
> 补充 [product-requirements.md](product-requirements.md) §3 与 §4。

## 1. 设计决策（本轮锁定）

| # | 决策点 | 结论 |
|---|--------|------|
| 1 | 两件事的关系 | **同一份 spec、同一轮实现**，但在计划中保持独立任务组与独立提交，评审与回滚仍分得开 |
| 2 | 继承粒度 | **每个字段一个勾选**。整份配置一个总开关会让「tier 自定、保留策略跟随」这类常见组合无法表达 |
| 3 | 继承的表示 | **字段为 NULL 即继承，非 NULL 即覆盖**。11 个可继承字段一条规则，无例外，不引入 -1 之类的哨兵值 |
| 4 | `VolumeBytes` 的冲突 | 该字段现有 `null` = 关闭分卷，与继承撞车。**把「关闭」挪到 `0`**，让 `null` 在全部可继承字段上含义一致。Settings 页本就写着 `0=off`，界面语义已经是 0 |
| 5 | 三个规则字段的冲突 | `null` = 继承，`''` = 明确无规则。已验证 `string?` 从 DTO 到实体全程直传（`BackupConfigDtos.cs:108`），两者可区分 |
| 6 | 解析时机 | **使用时解析，不在读取时填充**。见 §3.2——填充会让功能自我作废 |
| 7 | 现有配置的迁移 | **一律保持覆盖**，11 个字段全部保留当前具体值，不静默改成继承 |
| 8 | `IndexTier` / `DataTier` | **不可继承**。`BackupConfigService.cs:40-45` 在创建后拒绝变更这两个字段，而继承的含义正是「随全局设置变化」——即一次创建后的变更。改为新建时以全局默认预填，保存即固定 |
| 9 | 已有备份的容器 | 在下拉里**标记但不禁用**。本地库对 `(accountId, container)` 无唯一约束，禁用等于替用户定一条产品规则，超出本轮范围 |

## 2. Container 选择器

### 2.1 行为

选定 account 后拉取 `GET /api/accounts/{accountId}/containers`（现有接口，`ContainersPage` 已在用），下拉呈现结果，末项固定为 `+ New container…`。选中末项才显示输入框，用 `validateContainerName`（`frontend/src/api/containers.ts`）即时校验。

- **标记已有备份**：该接口返回 `BackupPresence`，据此在选项后附 `● has backup`。指向已有备份的容器通常是误操作，那种场景应走 Import 流程；但仅提示，不阻止（决策 8）。
- **切换 account 清空已选容器**：否则会残留属于上一个账户的名字，而后端不校验存在性。
- **列举失败可继续**：列举需连云。失败时下拉降级为纯输入框，并显示失败原因——不能因为列不出来就无法新建备份。

编辑模式下该字段本就锁定（`disabled={!!editing}`），不受影响。

### 2.2 不做

不改后端。不为 `POST /api/backup-configs` 增加容器存在性校验——`BackupOrchestrator.cs:158` 会在首次备份时 `CreateIfNotExistsAsync`，指定尚不存在的容器是受支持的正常流程。

## 3. 「使用默认」继承

### 3.1 字段清单

`BackupConfig` 中在 `GlobalSettings` 有对应 `Default*` 的字段共 13 个。逐个审过「创建后能否变更」后，**11 个可继承**，2 个不可。

判据是 `BackupConfigService.UpdateAsync` 的锁定清单（`AccountId` / `ContainerName` / `LocalRoot` / `IndexTier` / `DataTier` 与密码）。落在这 13 个里的只有两个 tier；其余 11 个在 `UpdateAsync:51-63` 中逐一赋值，即用户本就能在编辑页修改，跟随全局变化并不比这更危险。

**不可继承（新建时预填，保存即固定）**

| 字段 | 全局默认 | 原因 |
|---|---|---|
| `IndexTier` | `DefaultIndexTier` | `UpdateAsync:43` 拒绝创建后变更 |
| `DataTier` | `DefaultDataTier` | `UpdateAsync:44` 拒绝创建后变更 |

这两行在界面上不显示勾选框，改为标注 `locked after creation`——同一个表单里不应出现两种行为不同、仅靠文案区分的勾选框。

**可继承（11 个）**

| 字段 | 全局默认 | 类型变更 | 「明确不要」如何表达 |
|---|---|---|---|
| `MaxVersions` | `DefaultMaxVersions` | → `int?` | 不适用 |
| `MaxAgeDays` | `DefaultMaxAgeDays` | → `int?` | 不适用 |
| `RetentionMode` | `DefaultRetentionMode` | → `RetentionMode?` | 不适用 |
| `SingleFileThresholdBytes` | `DefaultSingleFileThresholdBytes` | → `long?` | 不适用 |
| `GroupCapBytes` | `DefaultGroupCapBytes` | → `long?` | 不适用 |
| `IncludeSymlinks` | `DefaultIncludeSymlinks` | → `bool?` | 不适用 |
| `VerboseLogging` | `DefaultVerboseLogging` | → `bool?` | 不适用 |
| `VolumeBytes` | `DefaultVolumeBytes` | 已可空 | **`0` = 关闭分卷** |
| `IgnoreRules` | `DefaultIgnoreRules` | 已可空 | **`''` = 无规则** |
| `DontCompressRules` | `DefaultDontCompressRules` | 已可空 | **`''` = 无规则** |
| `DontGroupRules` | `DefaultDontGroupRules` | 已可空 | **`''` = 无规则** |

`GlobalSettings` 中无 per-backup 对应项的设置不在范围内：`RepackDownloadHot/Cool/Cold/Archive`、`UploadConcurrency`、`DownloadConcurrency`、`LogEphemeralMaxAgeDays`、`RetryBackoffSeconds`、`RetryMaxTotalMinutes`、`DeadWeightThresholdPercent`、`StagedLimitBytes`、`ProcessingMaxAttempts`。

### 3.2 解析

新增 `ResolvedBackupSettings`：输入 `(BackupConfig, GlobalSettings)`，输出 11 个可继承字段的生效值，全部非空。规则单一——字段为 `null` 取全局，否则取字段本身。两个 tier 不经解析器，直接读配置。

**解析必须发生在使用时，不得在 `BackupConfigService.GetAsync` 中就地填充。** 一旦读取时填充，编辑界面就无法区分「继承来的 100」与「自己填的 100」，保存时会把继承悄悄固化为覆盖，功能自我作废。

改为经解析器取值的路径有四条：备份（`BackupOrchestrator`）、检查（`BackupChecker`）、清理（保留策略求值）、还原。每条路径都要有回归测试确认走的是解析后的值。

### 3.3 API 形状

`BackupConfigResponse` 同时返回两组：

- 原始字段（可空），界面据此决定每个勾选框的状态；
- `effective` 对象（全部非空），界面在勾选状态下显示为只读的当前生效值。

`BackupConfigRequest` 的 11 个可继承字段改为可空；`null` 即请求继承。两个 tier 保持非空。

### 3.4 界面

每个可继承字段一行，`Field` 左侧标签不变，右侧改为「勾选框 + 控件」。新增 `DefaultableField` 组件包住现有控件，不重写表单。

- 勾选：隐藏控件，以只读灰字显示 `effective` 值。
- 取消勾选：显示控件，并**预填当前生效值**，使「在默认基础上微调」不必重新输入。
- 重新勾上：该字段回到继承，已输入的值**丢弃**（保存时发 `null`）。表单不保留隐藏的草稿值——留着它会让界面显示的与将要保存的不一致。
- 全局设置变更后，勾选中的行下次打开即显示新值，无需任何操作。

新建备份时 11 个可继承字段**默认全部勾选继承**。这正是 PRD §3 的本意。两个 tier 以全局默认预填，可改，保存即固定。

Settings 页不变（但见 §6 关于保留策略的建议）。

## 4. 迁移

1. 11 个可继承列改为可空（SQLite 需重建表，EF Core 迁移处理）。`IndexTier` / `DataTier` 不变。
2. `VolumeBytes` 现有的 `NULL` 改写为 `0`，保住「关闭分卷」原意（决策 4）。
3. 其余 10 列的现有值原样保留——现有配置全部视为覆盖（决策 7）。

把已有配置静默改成跟随全局，等于在用户不知情的情况下改变已在运行的备份行为：一份特意设了 `MaxVersions=10` 的备份会突然变成 100。这类改动必须由人明确做出。

## 5. 测试

**后端**

- `ResolvedBackupSettings` 单测：11 个可继承字段各自的 null / 非 null 两条路径。
- 三态用例：`VolumeBytes` 的 `null`（继承）/ `0`（关闭）/ 正数；三个规则字段的 `null`（继承）/ `''`（无规则）/ 有内容。
- 迁移测试：`VolumeBytes` 为 `NULL` 的旧行迁移后必须是 `0`，而非继承。
- 端点测试：将某字段 `PUT` 为 `null` 后修改全局设置，`GET` 返回的 `effective` 必须随之改变。这条直接钉住「跟随而非快照」。
- 四条使用路径各一条回归测试。
- 锁定字段回归：`PUT` 一份 tier 与现存值不同的配置仍必须 400，本轮不得放松该约束。

**前端**

无自动化测试（项目既有约束，见 [web-ui-modernization-design.md](web-ui-modernization-design.md) §8）。验证为 `npm run build` 与 `npm run lint`，加人工核对。

## 6. 已知后果：保留策略的继承是破坏性的

`MaxVersions` / `MaxAgeDays` / `RetentionMode` 可继承，意味着把全局 `MaxVersions` 从 100 调到 10，所有勾选继承的备份在**下次清理时会真的删除多余版本**。

这是「跟随默认」的正确语义，不是缺陷——用户本就能逐个备份改出同样的结果。但它把一次设置编辑变成了跨备份的破坏性操作，而界面上看不出影响范围。

**建议**：在 Settings 页保留策略各项旁显示「N backups inherit this」。只读一行文字，数据来自现有的备份配置列表，不需要新端点。是否纳入本轮由人决定；不纳入也不影响本设计成立。

## 7. 已知局限

前端仍无测试框架，因此界面行为——勾选切换、预填、`effective` 显示、容器下拉的降级——只能人工核对。交付时如实说明，不以「已验证」表述掩盖。
