# Azure Storage Backup — 实施路线图

产品规模较大，采用里程碑迭代：每个里程碑独立走「设计 → 实现 → 验证」，交付可验证成果。
需求细节见 [product-requirements.md](product-requirements.md)。

## 里程碑划分

### M1 — 设置基础设施与账户管理（PRD 1.1）
- 数据存储层扩展（Account、代理配置实体）
- 账户 CRUD：endpoint/key/name/description
- 敏感信息（key、代理密码）可逆加密存储 + 主密钥方案
- 代理支持：独立代理 / 继承 docker 环境代理，带密码；Azure 分区选择
- 前端：账户设置界面
- 验证标准：增删改账户、连通测试通过

### M2 — Container 管理与信息文件发现（PRD 1.2–1.7）
- Container 列举 / 增删改
- 信息记录文件的发现与识别（判定哪些 container 属于本工具）
- 新账户引导流程（进入 container 列举、建议建 container）
- 信息文件格式（schema）定义：非加密版 + 加密版（密码、可逆）
- 「将已有备份导入本工具」：读取信息文件恢复配置
- 前端：container 界面
- 验证标准：能管理 container、发现并导入已有备份

### M3 — 计划任务/组的数据模型 + 默认值/全局设置（PRD 2、3、4）
- 备份列表（手动刷新）
- 组 CRUD
- 每个备份/组的备份/检查任务配置（cron + 图形化编辑器）
- 默认值设置（Tier、版本与时间、本地文件规则、网络并发、通知开关）
- 全局设置（重试退避、通知服务器 + 代理）
- 前端界面
- 验证标准：能完整配置（尚不执行）

### M4 — 备份引擎核心（PRD 3.3，最难，需再拆子任务）
- 本地扫描 + 统一 gitignore 规则引擎（忽略/不压缩/不分组三处复用）
- 分组打包（尺寸限制、不分组列表、死重压实 30%）
  - 死重压实已接入清理管线（`DeadWeightCompactor`：原地重压回收死重，2026-07-17）；对 Archive tier 因需下载而跳过。详见 m4-backup-engine-design.md §6「实现说明」。
- 7z 压缩/加密/分卷
- 临时区管理（压缩临时文件夹→压缩后临时区、非并发、超量阻塞）
- Tier 应用、版本保留策略
- 并发上传 + 重试退避
- 信息文件写入（加密/非加密双版本）
- 验证标准：能实际执行一次备份到 Azure Blob

### M5 — 检查（check）与还原（restore）（PRD 2.3、1.5）
- 检查任务：校验备份完整性
- 还原：跨设备恢复（设置账户 + 选 container → 恢复一切）

### M6 — 调度器（PRD 2.2、2.3）
- 常驻后台服务（BackgroundService）按 cron 触发
- 组内依次执行（前一个完成后再下一个）

### M7 — 通知系统（PRD 3.5、4.2）
- Webhook POST/GET、占位符替换、代理支持
- 各事件触发点接入

### M8 — 日志查看 + 目录 + 版本（PRD 5、6、7）
- 日志基础设施在 M1 即开始记录；此里程碑完成查看 UI（分级/时间/来源过滤、清空）
- 临时目录路径查看
- 版本显示

### 修复轮次 — 备份功能审查修复（2026-07-18）
- M4–M8 完成后的一轮完整审查修复：高危并发数据完整性、5 中危 bug、8 组需求缺口（含选择性还原、状态持久化、孤儿回收）、9 项低危清理 + CI 集成测试。
- 详见 [backup-remediation.md](backup-remediation.md)。

### M8 之后（按设计定稿日期）

M1–M8 交付的是"能用"，这之后的每一项都是被实际使用暴露出来的：要么是安全边界，要么是
大规模数据下的可观测性与可中断性。各项均已合并进 `main`，此处只记去向，细节以各设计文档为准。

| 日期 | 内容 | 文档 |
|---|---|---|
| 07-25 | 密钥环丢失后的恢复模式（canary 判定 + 重设闸门） | [keyring-loss-recovery-design.md](keyring-loss-recovery-design.md) |
| 07-25 | 可选的界面密码闸门（`Auth__Password`，放宽 PRD「无身份验证」） | [auth-password-design.md](auth-password-design.md) |
| 07-26 | 本地路径边界 `Backup__Root` + 目录浏览器 | [local-path-root-design.md](local-path-root-design.md) |
| 07-26 | 前端改版（设计令牌、组件体系） | [web-ui-modernization-design.md](web-ui-modernization-design.md) |
| 07-26 | 备份默认值与 container 选择器 | [backup-defaults-and-container-picker-design.md](backup-defaults-and-container-picker-design.md) |
| 07-26 | 运行进度可见性（分阶段计数、在途明细、ETA） | [backup-progress-visibility-design.md](backup-progress-visibility-design.md) |
| 07-27 | 读不开的输入：不当作删除，标记并继续 | [backup-unreadable-files-design.md](backup-unreadable-files-design.md) |
| 07-28 | 上传速度口径（端到端而非网络速度） | [upload-speed-clock-design.md](upload-speed-clock-design.md) |
| 07-31 | 移动端适配 | [mobile-adaptation-design.md](mobile-adaptation-design.md) |
| 08-01 | 7z CPU 优先级（默认 Lowest，NAS 上不抢资源） | [sevenzip-cpu-priority-design.md](sevenzip-cpu-priority-design.md) |
| 08-02 | 版本起止时间戳 | [version-timestamps-design.md](version-timestamps-design.md) |
| 08-03 | 备份范围选择（根内子集，`ScopeRuleSet`） | [backup-scope-selection-design.md](backup-scope-selection-design.md) |
| 08-06 | 上传阶段的 "checking" 一档（把本地校验与待上传分开） | [specs/2026-08-06-upload-checking-stage-design.md](superpowers/specs/2026-08-06-upload-checking-stage-design.md) |
| 08-06 | 改本地根路径（带校验的迁移，不再视根为不可变） | [change-local-root-design.md](change-local-root-design.md) |
| 08-07 | 同一轮内跨箱打包成员去重（<5MB，leader 覆盖，alias 还原） | [specs/2026-08-07-pack-alias-dedup-design.md](superpowers/specs/2026-08-07-pack-alias-dedup-design.md) |
| 08-08 | 可挂起、可暂停、可恢复的备份（journal、闸门、优雅关机、启动自动恢复） | [backup-suspend-resume-design.md](backup-suspend-resume-design.md) |
| 08-09 | unfinished 字节按 blobRef 记账、核对中的归档不算待传、进度两行合成一条时间轴 | [specs/2026-08-09-unfinished-bytes-ledger-design.md](superpowers/specs/2026-08-09-unfinished-bytes-ledger-design.md) |

## 说明
- 日志与通知的**基础设施**贯穿始终（早期即埋点），完整 UI 集中在后期里程碑。
- M4 是核心难点与最大风险，进入该里程碑时会单独细化子任务。
- M8 之后不再按里程碑推进，改为按「设计 → 计划 → 实现 → 审查 → 合并 `main`」逐项交付；
  仓库只保留 `main` 一条线，做完即合并并删分支。
