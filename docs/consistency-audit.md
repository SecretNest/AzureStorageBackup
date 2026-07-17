# 文档 ↔ 代码一致性审计（2026-07-17）

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
- **生产前待办：EF 迁移**。启动仍用 `db.Database.EnsureCreated()`（`Program.cs`），只在库文件不存在时建表。近期新增了表（`CachedVersionIndexes`、`LocalBackupStates`）与列（`LogEntries.Ephemeral`），对**已存在**的库不会自动加。因当前**未投产、可删旧库重建**，暂不阻塞；投产前应改用 `db.Database.Migrate()` + 迁移。同时 `OperationLogLevel` 枚举重编号（Debug=0/Info=1/Warning=2/Error=3，原 Info=0…），若届时有旧日志行需在迁移里把旧 `Level` +1 重映射（当前无旧库，无影响）。
- ~~**verbose per-file 日志逐条 SaveChanges**~~（已解决）：逐文件 verbose 日志改落到按备份+按日期的文本文件（`VerboseFileLog`），不再每文件一次 DB 写。
