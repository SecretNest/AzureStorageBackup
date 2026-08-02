# 版本时间戳（开始 / 结束）显示 — 设计

2026-08-02

## 问题

还原对话框的版本下拉里只有一个裸编号（`1`、`2`、`3`）。操作员要选"上周四那次备份"，
编号帮不上任何忙——必须知道每个版本对应的时刻。备份完成提示同理：只写
`Completed — version 3`，跑了多久、什么时候跑完，一概看不到。

## 现状

- `BackupVersion.CreatedAt`（UTC）已存在，语义是**版本提交时刻**，即备份结束。
  `/api/backup-configs/{id}/versions` 端点已经返回它，只是 `RestoreDialog.tsx` 在
  `versions.map(v => v.version)` 处把它丢掉了。
- **开始时间没有任何地方持久化**。`BackupRunState` 也没有时间字段。
- 时间一律 `DateTimeOffset.UtcNow` 存储，前端 `toLocaleString()` 渲染 → 已经是
  "UTC 存储 + 客户端时区显示"，本设计沿用，不引入任何时区配置。

## 设计

### 1. 数据模型：`BackupVersion.StartedAt`

```csharp
/// <summary>本次备份开始跑的时刻（UTC）。升级前写下的版本没有此信息 → null。</summary>
public DateTimeOffset? StartedAt { get; init; }
```

`CreatedAt` 语义不变（提交时刻 = 结束）。`StartedAt` 取
`BackupOrchestrator.RunAsync` 入口、扫描开始前的 `DateTimeOffset.UtcNow`。

### 2. 序列化：`InfoFormat` 2 → 3

`IndexSerializer` 写版本条目时总是写 `StartedAt`（可空 DTO），读时
`format >= 3 ? ReadNullableDto(r) : null`。旧信息文件照常读，`StartedAt` 为 null。

**单向升级**：现有代码对 `format > InfoFormat` 直接抛 `NotSupportedException`，
所以一旦新版本程序写过信息文件，**旧镜像就读不了了**。单实例 NAS 滚动升级没问题，
但升级后不能回滚旧镜像。

### 3. 完成提示与还原对话框显示同一组数字

完成提示**不用运行器自己的时钟**。收尾清理（保留策略、压实）在版本提交之后还要跑一阵，
拿 run 的结束时刻会得到比版本记录晚几分钟的数字——于是完成提示写 14:47、还原对话框
写 14:44，同一次备份两个答案。改为让两处都显示**版本记录里的那两个时间**：

- `BackupRunResult` 加 `StartedAt` / `CompletedAt`（就是写进 `BackupVersion` 的
  `StartedAt` / `CreatedAt` 那两个值）。
- `BackupRunState` 加 `StartedAt` / `CompletedAt`，由 runner 从结果填入。
- `BackupRunResponse` 暴露 `startedAt` / `completedAt`（ISO 8601，带 `Z`）。

### 4. 前端

新增共用格式化函数，放 `frontend/src/constants/format.ts`（与 `formatBytes` 作伴），
两处调用同一个函数，行文不会走样：

```
Version 3 — 2026-08-02 14:03 → 14:47              同日：结束侧省略日期
Version 3 — 2026-08-02 23:41 → 2026-08-03 05:12   跨日：结束侧写全日期
Version 2 — — → 2026-08-01 03:12                  无开始时间：写「—」
```

"同日"按**客户端本地时区**判断（先各自转本地日期再比较），不是按 UTC 日期——否则
本地时区里明明同一天的备份会因为跨了 UTC 零点而写出两个日期。

- `RestoreDialog`：`versions` state 从 `number[]` 改为带时间的对象数组；下拉 option 文案
  如上；`Latest` 选项保留在首位不变。
- `RunStatus` 的 `Completed` 分支：`Completed — version 3 (2026-08-02 14:03 → 14:47)`，
  既有的"N file(s) could not be read"追加段落原样保留。
- 老后端（响应里没有新字段）时退化为只显示编号，不显示时间括号。

界面文案一律英文（项目既有约定）。

## 测试

- `IndexSerializer` 往返：format 3 写读 `StartedAt`；用 format 2 的旧字节流读出 null。
- `BackupOrchestrator`：新版本的 `StartedAt` 非 null 且 `<= CreatedAt`。
- `/versions` 端点返回 `startedAt` 字段；已有版本（无该值）返回 null。
- `BackupRunResponse` 在 Completed 时带出与版本记录一致的两个时间。
- 前端 `tsc --noEmit`。

## 明确不做

- 不回填历史版本的开始时间（没有这个数据，猜出来的数字比空着更坏）。
- 不显示时长（`44m`）：两个时刻已经能算，加一个派生数字只会让行更长。
- 不引入时区设置项：浏览器时区就是操作员所在时区。
