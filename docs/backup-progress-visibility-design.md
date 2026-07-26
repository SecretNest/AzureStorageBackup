# 备份进度的可见性（2026-07-26）

> 界面上看不到备份进度。查下来是**两个独立缺陷**，不是一个。
>
> **一、前端的轮询循环活在闭包里。** `BackupConfigsPage.tsx` 的 `run()` 内部有一个 `while (state.status === 'Running')` 循环，只有在本浏览器会话里亲手点了按钮才会启动。刷新页面、换标签页、换设备，循环就没了——**尽管服务端仍然知道它在跑**（`BackupRunner` 是单例，`GET /backup-configs/{id}/run` 随时可查）。Restore 与 Check/Repair 是同一个毛病。
>
> **二、定时任务根本没有进度。** `TaskDispatcher.cs:105-106` 绕开 `BackupRunner`，直接调 `BackupOrchestrator.RunAsync(request, null, ct)`，第二个参数正是进度回调，传的是 `null`。所以计划任务触发的备份从来不注册任何运行状态，前端怎么轮询都查不到。而定时备份恰恰是常态。
>
> 补充 [product-requirements.md](product-requirements.md) §2。

## 1. 设计决策（本轮锁定）

| # | 决策点 | 结论 |
|---|--------|------|
| 1 | 进度的存活范围 | **仅内存，活过刷新，不活过重启**。重启会杀掉正在跑的备份且无法自动恢复，落库只会留下一个百分比停在半途、实际早已死亡的记录，还要额外写一套「启动时标记孤儿」去清理自己制造的假象 |
| 2 | 重启后的残留状态 | **无需处理**。`BackupRunner` 与 `BackupBusyTracker` 都是内存单例，随进程一起清空，不会有任何不一致 |
| 3 | 定时任务接入方式 | **调度器改走 `BackupRunner`**，与界面按钮同一条路。进度、忙碌锁、错误处理自动一致，杜绝「两条路行为不同」这类缺陷再次发生 |
| 4 | 调度器的等待语义 | `BackupRunState` 暴露其内部 `Task`；`Start()` 照常立即返回供界面使用，调度器 `await` 该 Task 以记录任务成败 |
| 5 | 前端状态来源 | **服务端权威**。列表每 5 秒刷新（纯本地查询），有活跃项时对其每秒拉一次进度；闭包内的循环删除 |
| 6 | 推送方式 | **轮询，不引入 SSE / WebSocket**。长连接会带来重连、反向代理兼容等一堆问题，而这里轮询的是本地 SQLite 查询 |
| 7 | 本轮范围 | 界面刷新后可恢复的是 **Backup / Restore / Repair** 三者。**Check 不在其中**——它是同步端点，服务端没有可查状态，见 §3.4。定时任务的进度只做 Backup |

## 2. 后端：让定时任务走同一条路

### 2.1 改动

`Services/BackupRunner.cs`：`BackupRunState` 增加一个只读的 `Task Completion`，在 `Start()` 中由 `Task.Run(...)` 的返回值填充。既有的 `Start()` / `Get()` 签名与行为不变。

`Services/TaskDispatcher.cs`：`ScheduledTaskType.Backup` 分支不再自行取设置、构造 `BackupRequest`、调用 `BackupOrchestrator`，改为：

```csharp
var state = sp.GetRequiredService<BackupRunner>().Start(config.Id);
await state.Completion;
```

随后按 `state.Status` / `state.Error` 记录任务成败，替代原先依赖 `RunAsync` 抛异常的写法。

### 2.2 顺带消除的重复

`BackupRunner.RunAsync` 已经自己取配置、账户、全局设置、备份密码，并调用 `busy.TryAcquire` 抢忙碌锁。`TaskDispatcher` 目前把这些又做了一遍。走同一条路之后，调度器 Backup 分支里的这些准备代码删除。

**注意**：`TaskDispatcher` 的忙碌检测与 `BackupRunner` 的 `TryAcquire` 语义必须核对一致后再删——若调度器原本在忙碌时是「跳过并记日志」，而 runner 是「置为失败」，两者对「这次计划执行算不算失败」的判断不同。实现时以调度器原有的可见行为为准。

### 2.3 不在范围

Restore 与 Check/Repair 由界面发起，各自已有 runner 与状态端点。计划任务只有 Backup / Check / Cleanup 三种类型，本轮只让 Backup 具备进度。

## 3. 前端：状态跟着服务端走

### 3.1 现状

`BackupConfigsPage.tsx` 中 `run()` 与 `pollRestore()` 各自持有 `while` 循环，把状态写进组件的 `runs` / `restores`。这些状态随组件卸载而消失，且只覆盖本会话发起的操作。

### 3.2 改法

删除两处闭包内循环，改为单一的轮询机制：

- 备份配置列表每 **5 秒**刷新一次。该请求只读本地 SQLite（配置行 + 内存中的 `activity`），不连云。
- 列表返回后，对每个 `activity !== 'Idle'` 的配置，每 **1 秒**拉一次**该 activity 对应的那一个**状态端点。全部空闲时不发这些请求。

  `BackupActivity` 的取值决定拉哪个，不要三个都拉：

  | `activity` | 拉取端点 |
  |---|---|
  | `BackingUp` | `GET /backup-configs/{id}/run` |
  | `Restoring` | `GET /backup-configs/{id}/restore` |
  | `Repairing` | `GET /backup-configs/{id}/repair` |
  | `Checking` | **无状态端点**，见 §3.4。只显示徽章 |
  | `CleaningUp` | 无状态端点。只显示徽章 |
  | `Idle` | 不拉 |

### 3.4 Check 无法通过本设计恢复

`POST /backup-configs/{id}/check`（`BackupConfigEndpoints.cs:447`）是**同步**的：它一直占着那个 HTTP 请求直到检查报告生成，没有 runner，也没有对应的 `GET` 状态端点——与 `repair` 不同，后者是 `POST` 启动 + `GET` 查状态。

因此刷新页面时，正在进行的检查在服务端**没有任何可查的状态**，前端无从恢复。徽章仍会显示 `Checking`（来自内存的忙碌跟踪器），但报告会丢失，用户需要重新发起。

要修好它得把 check 改造成与 repair 同构的 runner 模式。那是一次独立的改动，不在本轮范围内，但应如实告知：**本轮修好的是 Backup、Restore、Repair 三者，Check 不在其中。**
- 按钮的点击处理只负责发起操作，不再自行轮询。

组件卸载时清除定时器。

### 3.5 文案

`Run` 按钮改名为 `Backup`。该按钮触发的是备份，`Run` 与同一行的 `Restore`、`Check/Repair` 并列时不表意。

## 4. 测试

**后端**

- `BackupRunState.Completion` 在运行结束后完成，且 `Status` 已终态——避免调度器 `await` 返回时状态尚未写入的竞态。
- 调度器执行 Backup 任务后，`BackupRunner.Get(configId)` 能返回该次运行的状态。这条直接钉住本轮要修的缺陷：在修复前它必然失败。
- 忙碌冲突下调度器的可见行为与改动前一致（以 §2.2 核对结果为准）。
- 现有调度器测试全部保持绿。

**前端**

无自动化测试（项目既有约束，见 [web-ui-modernization-design.md](web-ui-modernization-design.md) §8）。验证为 `npm run build` 与 `npm run lint`，加人工核对。

## 5. 已知局限

- 进度不落库，进程重启后归零。这是决策 1 的直接后果，且与「重启后备份需重跑」一致。
- 轮询意味着页面打开时持续产生后台请求。5 秒一次的本地查询代价可忽略，但它是本设计唯一持续消耗资源的部分。
- 前端无测试框架，因此轮询的启停、卸载时的清理、以及刷新后恢复显示，只能人工核对。
- **Check 刷新后仍会丢失**（§3.4）。它是本轮唯一没有修好的操作，原因是服务端缺少可查状态，而不是前端没去查。
