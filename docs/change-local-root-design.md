# 迁移备份源路径（Change Local Root）— 设计

2026-08-06

## 问题

`BackupConfig.LocalRoot` 创建后锁定（`BackupConfigService.cs:46`，前端 `BackupConfigsPage.tsx:857`
的输入框 `disabled`）。源目录在宿主上换了挂载点、且容器内路径也跟着变时，这条配置就
再也指不到自己的数据：备份、检查、还原全部卡在路径边界或"本地全缺失"上，而界面上
没有任何办法修正。

同一把锁还造成第二个缺口：导入已有备份时，若云端信息文件没有 `SourceRootHint`，
`LocalRoot` 会落成空串（`BackupConfigEndpoints.cs:90-99` 的注释已经记下了这件事），
而锁定字段让用户**无法把它补上**——导入出来的配置生下来就是半残的。

需要的是：一条专用的、带防呆的通道，把 `LocalRoot` 从旧路径迁到新路径。

## 锁定的理由对 `LocalRoot` 不成立

`BackupConfigService.cs:34-36` 把 `AccountId` / `ContainerName` / `LocalRoot` /
`IndexTier` / `DataTier` 归为一类锁定，理由写的是"本地权威状态按 账户+container 键控，
改这些字段会与云端/本地索引失步"。这个理由对前两个字段完全成立，对 `LocalRoot` 不成立：

- `LocalBackupState`（`LocalBackupState.cs:10-11`）与 `CachedVersionIndex` 都按
  `(AccountId, Container)` 键控，**与本地路径无关**。
- 索引条目 `IndexEntry.Path` 存的是相对根的路径（`LocalFileScanner.RelativePath`，
  `LocalFileScanner.cs:205`）。`ScopeRules` 同样是相对坐标。
- 绝对路径只出现在两处：扫描时的拼接前缀，以及云端信息文件里的 `SourceRootHint`
  ——后者文档明写"仅供参考"（`BackupIndex.cs:22`），下次备份自动写成新值
  （`BackupOrchestrator.cs:1551`）。

结论：只要新路径下是**同一份数据**，换根不会让索引失步，也不会触发全量重传。
真正的危险不是"内容变了"，而是"填了个不相干的目录"——一旦发生，下次备份把整个
备份记成全删全增，云端多一个巨大的新版本，还可能触发保留策略淘汰掉旧版本。
本设计的全部防呆都对准这一个失败模式。

`IndexTier` / `DataTier` 的锁定不在本设计范围内，保持原样。

## 对既有前提的修订

`docs/backup-scope-selection-design.md`"现状"一节写着：

> `BackupConfig.LocalRoot` 创建后锁定（`BackupConfigService.cs:46`），因此范围规则的
> 相对路径基准永远稳定，不需要额外防护。

本设计推翻了这条前提，需要正面交代其后果：`ScopeRules` 是相对根的坐标，换根后
**规则文本原样保留、不做任何改写**。新根下是同一份数据时，相对结构一致，规则继续
正确命中——这正是本功能的正常路径。用户强行迁到一棵结构不同的目录树时，范围规则
可能指向不存在的路径，后果是范围命中变空或部分失效，**不会损坏数据、不会误删云端版本**，
下次备份会把落到范围外的文件记为删除（与用户手工改窄范围的后果完全一致，
`backup-scope-selection-design.md` 语义 4 已定义）。此风险在强制迁移的确认界面上明写。

## 设计

### 1. 两个端点：preview 与 apply 分开

```
POST /api/backup-configs/{id}/local-root/preview   { newRoot }
  → 200 LocalRootPreviewResponse            （只校验，绝不改动任何状态）
POST /api/backup-configs/{id}/local-root           { newRoot, force }
  → 200 BackupConfigResponse | 400 | 409
```

分开而不是"单端点 `confirm` 双模式"：preview 是纯查询，幂等、可反复重试（换个路径
再试一次不留任何痕迹），apply 的确认语义在日志里独立可辨。仓库里已有同形先例——
还原就是 `restore-estimate` 与 `restore` 分开（`BackupConfigEndpoints.cs:426`、`298`）。

也不选"放开 `PUT` 上的锁、把校验塞进现有更新端点"：那会让"改名字"这类日常编辑与
"迁移根路径"这类运维动作共用一条路径，且必须在端点里判断"这次 `LocalRoot` 变了吗"
来决定走不走抽样校验；更要紧的是 `UpdateAsync` 的基础字段防线会被撕开口子，
而 `AccountId` / `ContainerName` 的锁定理由与 `LocalRoot` 不同，混在一处日后容易被误改。
**新通道是另开一道门，不是把旧锁撬开**：`UpdateAsync` 里那条锁定检查一行不改。

### 2. 校验流程（顺序短路）

1. **忙检查** — `BackupBusyTracker.IsBusy(accountId, container)` 为真 → 409，不做后续。
2. **路径校验** — 非空、绝对路径、`PathBoundaryGuard` 边界内、存在、是目录、可列出。
   越界 → 409 + `code: "path_outside_root"`（`PathBoundaryGuard.Blocked` 的既有约定，
   全仓一致，不为本功能另立一套）；其余非法输入 → 400。
3. **历史判定** — 是否有可比的基线？
   - `LocalRoot` 当前为空（导入缺 `SourceRootHint`），**或**该备份尚无任何版本
     → verdict `NoBaseline`，跳过抽样，允许直接改。
   - 否则经 `TrackedInfoStore` 取最新版本号 → `ILocalIndexCache` 取该版本索引
     （与 `file-versions`、`tree` 端点同一套依赖，不新造取索引的路子）。
     索引取不到（缓存缺失/读不出）→ verdict `NoBaseline`，理由写进 `reason` 字段。
4. **抽样比对** — 从索引条目中分层抽最多 200 个，逐个在新根下拼出绝对路径检查。
5. **分档裁决** — 见下。

### 3. 匹配判定：只看「存在 + size」

mtime **不参与判定**，只单独统计并在报告里附带显示（"其中 43 个 mtime 有出入"）。

理由：跨文件系统搬迁时 mtime 的精度与保留情况经常不一致（rsync 未加 `-t`、
不同 fs 的时间戳粒度），拿它当判据会大面积误伤；而 mtime 对不上的真实后果只是
下次备份重传这些文件，size 对不上才说明可能填错了目录。

`Kind == "symlink"` 的条目只比"存在且仍是符号链接"，不比 size（`IndexEntry.Length`
对 symlink 恒为 0，见 `LocalFileScanner.cs:170`）。

带 `UnreadableAt` 的条目**排除出抽样池**：它们的 size/mtime 沿用上一版本，
本来就不保证与磁盘一致，拿来判定只会制造假不匹配。

### 4. 分层抽样

按 `Length` 分档（0 / <1MB / 1–100MB / >100MB 四档），每档按档内条目数占比分配名额，
档内**按索引顺序等距取样**而非取头部——索引顺序近似目录序，取头部会把样本全压在
第一个子目录里，那样"只挂上了其中一个子目录"这种半对半错的迁移就检不出来。

可抽条目总数不足 200 时全部取用（此时匹配率就是全量比对的结果）；某档条目数少于
分配名额时，把剩余名额让给其它档，不让样本白白浪费。

抽样是纯函数：输入条目列表，输出样本列表。单独可测。

### 5. 分档裁决

| 匹配率 | verdict | 行为 |
|---|---|---|
| `[95%, 100%]` | `Ok` | 直接允许 apply |
| `[5%, 95%)` | `NeedsConfirm` | 需 `force: true` |
| `[0, 5%)`（含新目录一个都找不到） | `Rejected` | 需 `force: true` |
| 无基线 | `NoBaseline` | 直接允许 apply |
| 基线读不出来 | `BaselineUnreadable` | 需 `force: true` |

`BaselineUnreadable` 是实施期评审补上的一档，它修正了本设计初稿的一个错误：初稿把
「索引取不到」也归进 `NoBaseline`，而 `NoBaseline` 恰恰是免确认直接放行的一档。于是一个
**云端有历史、但索引读不出来**的备份——最该被多问一句的那种——会被当成「这备份还没跑过」
一路放行。现在两者分开：确实没有历史照旧放行；有历史却读不出来，把底层异常原文放进
`Reason` 并要求 `force`。

区间左闭右开，边界值归入更宽松的一档（恰好 95% 判 `Ok`，恰好 5% 判 `NeedsConfirm`）。

`Rejected` 默认拒绝，但**仍可用 `force: true` 越过**。这一条是刻意的：用户在 NAS 上
拿不到命令行（无法自己 `ls` 排查），若硬拦截判断失准就彻底没有旁路。前端把 override
做成必须手动勾选的复选框，而不是一个顺手就点掉的按钮。

### 6. 报告体

```csharp
public record LocalRootPreviewResponse(
    string Verdict,            // "Ok" | "NeedsConfirm" | "Rejected" | "NoBaseline"
    int Sampled,
    int Matched,
    int Missing,
    int SizeMismatch,
    int MtimeDiffers,          // 仅供参考，不参与判定
    double MatchRate,
    string? Reason,            // NoBaseline / 路径校验失败的具体原因
    IReadOnlyList<string> Examples);   // 最多 10 条不匹配的相对路径
```

`Examples` 不是装饰：用户没有命令行，界面必须把"到底哪些文件对不上"直接摆出来，
否则一个 68% 的匹配率无从判断该不该强制。

### 7. 落库

只改 `LocalRoot` 一个字段，其余一概不动。`ScopeRules` 保持原文（理由见"对既有前提的修订"）。
本地索引缓存、`LocalBackupState` 都不失效、不清空——它们与路径无关。

写一条操作日志：旧根 → 新根、verdict、匹配率、是否 force。

### 8. 竞态

apply **不信任前端传来的 preview 结果**，自己重跑一遍完整校验（这正是
`InspectAsync` 必须是纯查询、可安全重入的原因）。preview 之后新根被拔掉、
或备份在两次调用之间开跑，都由 apply 自己的那一遍兜住。

## 代码落点

| 文件 | 改动 |
|---|---|
| `Services/LocalRootMigration.cs` | **新增**。唯一的领域逻辑，**静态类、无依赖注入**：`static LocalRootPreview Inspect(string? currentRoot, string newRoot, VersionIndex? baseline)`。只读文件系统，不碰数据库、不连云、不解密——取索引所需的账户/密码/云端信息由端点备好后把 `baseline` 传进来。因而可脱离 HTTP、EF 与 Azure 单测。内部拆出分层抽样（纯函数）与文件系统比对两部分。 |
| `Models/BackupConfigDtos.cs` | 加 `LocalRootChangeRequest(string NewRoot, bool Force = false)` 与 `LocalRootPreviewResponse`。 |
| `Endpoints/BackupConfigEndpoints.cs` | 两个 `MapPost`，紧挨现有 `reset-password`（同属"创建后受限字段的专用变更通道"）。端点只做编排：取配置 → 忙检查 → `InspectAsync` → 按 verdict 与 `force` 裁决 → 落库 + 写日志。 |
| `Services/BackupConfigService.cs` | 加 `ChangeLocalRootAsync(int id, string newRoot, ct)`。**不动** `UpdateAsync` 的锁定检查，只更新其文档注释说明 `LocalRoot` 现有专用通道。 |
| `frontend/src/api/backupConfigs.ts` | 加 `previewLocalRoot` / `changeLocalRoot` 与 `LocalRootPreview` 类型。 |
| `frontend/src/components/ChangeLocalRootDialog.tsx` | **新增**。当前根（只读）→ 新根输入 + Browse（复用现有目录浏览器，照搬 `BackupConfigsPage.tsx:1225` 的 `initialPath` 用法）→ Check → 报告区 → Apply。 |
| `frontend/src/pages/BackupConfigsPage.tsx` | 编辑态下 `Local Root (locked)` 的 `Browse` 按钮换成 `Change…`，打开上述对话框；成功后刷新列表并关闭。 |
| `docs/backup-scope-selection-design.md` | 修订"现状"一节中已失效的那句锁定前提，指向本文档。 |

界面文案一律英文（既有约定）。

## 报告区的 UI 变形

| verdict | 呈现 |
|---|---|
| `Ok` | 绿色摘要（"196 / 200 sampled entries match"），Apply 直接可用 |
| `NeedsConfirm` | 匹配率 + 不匹配样例列表 + 必须手动勾选的 `I understand — change anyway`，勾上 Apply 才可点 |
| `Rejected` | 同上，措辞更强，额外提示"下次备份会把全部文件记为删除并重新上传" |
| `NoBaseline` | "No previous version to compare against — only the path itself was checked." Apply 直接可用 |

## 测试

后端（新建 `LocalRootMigrationTests.cs` + 端点测试）：

- 分层抽样：四档都被覆盖；等距取样不塌在头部；条目数少于名额时不重复不越界。
- `UnreadableAt` 条目被排除出抽样池。
- symlink 条目只比存在性，不因 size 判不匹配。
- 全匹配 → `Ok`；部分匹配 → `NeedsConfirm`；空目录 → `Rejected`。
- `LocalRoot` 为空 → `NoBaseline`；无任何版本 → `NoBaseline`；索引取不到 → `NoBaseline` 且 `Reason` 非空。
- 越界路径 → 409 + `code: "path_outside_root"`；空路径 / 相对路径 / 指向文件而非目录 / 不存在 → 400。
- 忙时 → 409，且**未落库**。
- `NeedsConfirm` / `Rejected` 无 `force` → 不落库；带 `force` → 落库。
- 落库后逐字段断言：只有 `LocalRoot` 变了，其余（含 `ScopeRules`）一字未动。
- **`UpdateAsync` 仍然拒绝改 `LocalRoot`** —— 防止新通道顺手把旧防线弄松。
- preview 调用前后数据库状态完全一致（纯查询性质的回归保护）。

前端：仓库现有前端测试只覆盖纯逻辑（`lib/scopeRules.test.ts`、`constants/format.test.ts`），
没有组件渲染测试的基建，本功能不为此单独引入。因此把 verdict → UI 决策抽成纯函数
`lib/localRootVerdict.ts`（输出：能否 Apply、是否需要勾 force、提示文案键），用 vitest 测它；
对话框组件只负责把这个纯函数的输出画出来。

## 明确不做

- 不做"批量迁移多个配置"。逐条改，每条都要看自己的报告。
- 不做路径前缀的自动推断/建议。
- 不改 `IndexTier` / `DataTier` / `AccountId` / `ContainerName` 的锁定。
- 迁移后不自动触发检查。界面提示"下次备份将从新路径扫描"即可——抽样校验已经给过信心，
  再自动跑一轮全量检查是重复劳动。
