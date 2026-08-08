# 文档 ↔ 代码一致性审计

逐轮追加，每轮一节。目前两轮：[2026-07-17](#第一轮审计2026-07-17)、[2026-08-08](#第二轮审计2026-08-08)。

---

# 第一轮审计（2026-07-17）

对 `docs/`（product-requirements / backup-feature-design / m4-backup-engine-design / roadmap）与 `.claude/.../memory/product-overview.md`
逐条对照实际代码后的发现与处置。方法：三个方向并行核对——端点/DTO/枚举、存储/算法约定、记忆断言。

## 处置总览

| # | 发现 | 类别 | 处置 |
|---|------|------|------|
| A1 | 保留清理误删**被引用的分卷 data blob** → 数据丢失 | 代码 bug | ✅ 改代码：`RetentionCleaner` 分卷名归一化后比对 |
| A2 | 保留清理只删 pack 基名，漏删分卷 pack → 存储泄漏 | 代码 bug | ✅ 改代码：按 packId 归组枚举 `packs/` 删除 |
| A3 | 死重压实（§6）设计存在、`DeadWeightAnalyzer` 已实现但**从未接入管线** | 功能缺口 | ✅ 改代码：`DeadWeightCompactor` 接入 `RetentionCleaner`，原地重压回收死重（Archive tier 跳过） |
| F1 | 前端 `CheckResult` 缺 `corruptedPaths`、`check()` 不传 `deep` → 深度校验结果不可见 | 代码缺口 | ✅ 改前端：补字段 + Deep check 按钮 + 展示损坏条目 |
| T1 | 数据 Tier 默认：文档=Archive，代码=Hot | 设计默认冲突 | ✅ 改**代码**对齐文档：默认恢复为 **Archive**（成本最优，备份归档语义；用户 2026-07-17 确认）。测试显式用 Hot 便于评估。 |
| D1 | PRD/设计仍写「Smart」tier | 文档过时 | ✅ 改文档：明确不提供 Smart |
| D2 | m4 §2/§3.2 示例用 `sha256:`，代码用 XxHash128 | 已知有意变更 | ✅ 改文档：加实现更新说明 |
| D3 | m4 §2/§3 用 JSON schema，实际为紧凑二进制序列化 | 已知有意变更 | ✅ 改文档：说明 JSON 仅描述逻辑结构 |
| D4 | m4 §6「尺寸限制仅对新增文件生效」与实现（Added+Modified 同等分组）措辞不符 | 措辞/语义 | 📝 改文档：说明现状依赖未接入的死重压实（见 A3） |
| M1 | product-overview.md 第 100 行三项「更小的遗留」已全部实现 | 记忆过时 | ✅ 更新记忆 |
| M2 | 记忆未含并行上传/可配退避/pack 校验/`IFileHasher` ctor 依赖 | 记忆遗漏 | ✅ 更新记忆 |

## 无差异（抽查一致，未改动）

- **所有枚举数值前后端零错位**：`StorageTier`(0-3)、`RetentionMode`(0-3)、`ScheduledTaskType`(0-2)、`TaskTargetKind`(0-1)、
  `NotificationEvents`[Flags] 位值、`NotificationMethod`(0-1)、`OperationLogLevel`(0-2)、`AzureRegion`、`ProxyMode`、`BackupPresence`、`BackupStage`。
- 两级 hash diff 流程（length→mtime/权限→headHash→fullHash，仅元数据变只更新索引）与 §4.2/§13.5 一致。
- 索引 schema 字段名（`IndexEntry/BackupInfoFile/BackupVersion/VersionIndex/PackInfo/StorageRef/VersionStats`）与 §3.1/§3.2 一致。
- 信息文件原子写（临时 blob→下载校验→覆盖→删临时）＝ §8；临时区状态机（单锁串行、超量背压、先临时后移动、默认 1GB）＝ §7。
- 保留策略四模式 + 始终保留最新 ＝ §10；7z 参数（`-mhe=on`/密码即加密/`-v{n}b` 分卷/StoreOnly `-mx0`/PATH 探测 7zz→7z→7za）＝ §13.1。
- 文档未写任何显式 HTTP 路径/方法，故端点层无「文档 ↔ 代码」路径冲突；DTO 字段名与前端接口逐字段一致。

> 注：本小结为 2026-07-17 首轮审计的快照；后续多轮已把当时"待办/边界"大多落地——死重压实已接入（`DeadWeightCompactor`）、信息文件已本地权威（`TrackedInfoStore`）、每备份日志保留已按两级模型实现。最新状态以各设计文档「实现说明」段与 memory `product-overview.md` 为准。

## 有意保留的边界

- **§9 pack 成员变化 → 进入下一组（增量分组）**：pack 处理改为按目录**增量成组**——每次取目录中未处理、
  总长≤上限的一组小文件压缩+校验，压缩中变化的成员以稳定后的新 hash 重新入队，自然进入下一组（§9「放当前目录下一个分组」）；
  仅当变大到超阈值或反复变化达阈值时才降级为单文件。与设计 §9 字面一致。
- **重校验的初始基准取处理开始时的 stat**：仅检测「处理期间」的内容变化；「diff 与处理之间」的极窄竞态未特殊处理（下次备份自愈）。
- ~~**生产前待办：EF 迁移**~~（已完成）：启动改用 `db.Database.Migrate()`（`Program.cs`），初始迁移 `InitialCreate` 固化当前全部 schema（含 `CachedVersionIndexes`、`LocalBackupStates`、`LogEntries.Ephemeral`、`ScheduledTasks.Check*Level` 等）。设计时用 `AppDbContextFactory`（`IDesignTimeDbContextFactory`）。**当前无部署**——旧 EnsureCreated 建的库无迁移历史，需删 `data/app.db` 重建。测试仍各自 `EnsureCreated`（一次性库，无需迁移历史）。
  - 序列化侧：索引/信息文件 index format 3 / info format 2（新增 `VolumeSizes`、`UnrecoverablePaths`）——旧格式可读（条件读），旧本地缓存行会自动重建或重新拉取。同时 `OperationLogLevel` 枚举重编号（Debug=0/Info=1/Warning=2/Error=3，原 Info=0…）；这次重编号发生在**迁移接入之前、且当时无旧库**，因此没有为它写重映射迁移。**这一条到此为止，不再是待办**：数据库侧已由 `db.Database.Migrate()` + `Migrations/` 承接（自 `InitialCreate` 起，最新一条为 `20260808014309_AddAutoResumeInterruptedRuns`），此后的 schema 变更一律走迁移。唯一残留的前提是：`InitialCreate` 之前用 `EnsureCreated` 建的库没有迁移历史，若还存在这样一个库，需删 `data/app.db` 重建。
- ~~**verbose per-file 日志逐条 SaveChanges**~~（已解决）：逐文件 verbose 日志改落到按备份+按日期的文本文件（`VerboseFileLog`），不再每文件一次 DB 写。

---

# 第二轮审计（2026-08-08）

范围：**只查"声称描述当前行为"的文档**——README、PRD、roadmap、本文件，以及最近上线那批功能的
设计稿。历史性的 plan/spec 文档不在范围内（它们是当时的施工记录，不承诺跟着代码走）。

结论：**代码没有发现问题，13 处全部是文档欠账**，且大半集中在同一个成因——2026-08-08 合并的
suspend/resume 那一轮功能很大，设计稿在定稿之后又追加了两个任务、实现期间还推翻了它三处判断，
而这些都只写进了 plan 与代码注释；README 则完全没跟上。

| # | 发现 | 处置 |
|---|------|------|
| R1 | README 完全没有「暂停 / 挂起 / 恢复」——最新且最显眼的用户可见功能 | ✅ 新增 "Stopping, pausing and resuming" 整节（含 journal、闸门、自动降级、重启与自动恢复） |
| R2 | README 的 `docker run` 没有 `--stop-timeout`，默认 10s 短于应用自己的 30s 关机超时 → `SIGKILL` → 丢掉整轮；而 compose 特意写了 45s 并解释了原因。两份部署说明结论相反 | ✅ 命令补 `--stop-timeout 45`，并新增 "Shutdown grace period" 说明三个超时的嵌套关系 |
| R3 | README「Published image (GHCR)」只写 GHCR，实际 workflow 双推 GHCR + ACR | ✅ 改标题与正文，写明双注册表、所需 secrets、以及缓存放 GHCR 的理由 |
| R4 | 卷说明 `/data` 只写 `app.db`，但 journal 也在那儿——不持久化就等于关掉崩溃恢复；`/temp` 漏了 `diff-spill` | ✅ 两行都补，并加一段说明 journal 为何跟着库走而不是跟着 `/temp` |
| R5 | 设计稿 `SuspendReason` 写 `Crashed`，代码是 `ShuttingDown`，且代码注释明确写着"设计稿那个故意不要" | ✅ 设计稿改正 + 加实现说明（为崩溃伪造一条内存状态的代价） |
| R6 | 设计稿「启动」一节说在内存里登记 `Suspended(Crashed)`；代码改为 `GET /{id}/interrupted` 现读现返 | ✅ 重写该节，保留"不起 Task、不抢忙碌锁"这条没变的约束 |
| R7 | 设计稿说 "`Cancel` 删 journal"；代码两种停法都落盘，真正删 journal 的只有成功提交 / Discard / 删配置三处 | ✅ 改正 + 说明理由（删了账，pack 一件都认不回来） |
| R8 | 设计稿把闸门退避阶梯记成「复用 `RetryOptions`，PRD 4.1」；PRD 4.1 实际是 `5,30,90,300`/上限 2h，而闸门是自己硬编码的 `30s/1m/5m` + 10min 耐心 | ✅ 去掉错误出处，写清两层管的不是同一件事（单次调用 vs 一条链路） |
| R9 | 优雅关机与启动自动恢复整块不在设计稿里（只写进了 plan） | ✅ 设计稿新增「优雅关机与自动恢复」一节 + 实施顺序补阶段 8/9 + 测试补一条 |
| R10 | PRD 顶部「无身份验证」与已上线的密码闸门相反 | ✅ PRD 加实现说明：**单用户**没变，变的是多了一道**可选**闸门 |
| R11 | roadmap 停在 M8 + 07-18 修复轮次，之后 16 项一条没记 | ✅ 补「M8 之后」表 + 说明改为逐项交付、只留 `main` |
| R12 | 设计稿的 journal 路径写 `{configId}/`，代码按 `{accountId}/{container}/` 分目录（清理器手上没有 configId） | ✅ 改正 + 补挂起标记那个兄弟文件的说明 |
| R13 | 本文件第 41 行自相矛盾：上一行说迁移"已完成"，下一行仍写"投产前应改用 `Migrate()`" | ✅ 改写该条，收束成一句仍然成立的前提（`InitialCreate` 之前的旧库需重建） |

## 无差异（本轮抽查一致，未改动）

- README 的**全部**环境变量名与默认值逐个对上 `Program.cs`：`IndexCacheSize` / `MaxPackMembers`(20000) /
  `MaxPackPathBytes`(1000000) / `DiffQueueMaxItems`(2000) / `DiffQueueMemoryBytes`(64MB) /
  `SevenZipMethodArgs`(-mx9) / `TempPath` / `Backup__Root`。
- 暂存区默认 2 GB、7z CPU 优先级默认 `Lowest`、`GET /api/system/paths` 存在。
- README 反复强调的"压缩全局串行、只有上传并行"属实（`StagingArea` 单一 `_compressLock`）。
- 两种停法从 `StopBackupDialog.tsx` → `?finishCurrentFiles=` → `BackupRunner.CancelAsync` 全程打通。
- `AutoResumeInterruptedRuns` 确实出现在 Settings 页（不是只有后端）。
- `docker-compose.yml` 的 45s 与代码里 45 > 30 > 20 的注释自洽。
