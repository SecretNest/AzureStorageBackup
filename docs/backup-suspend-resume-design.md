# 备份可挂起、可暂停、可恢复 — 设计

2026-08-07

## 问题

一次正在跑的备份因为一条网络错误整轮倒掉，已经传上云的字节全部作废。现场报告：

```
Failed: Retry failed after 6 tries. Retry settings can be adjusted in ClientOptions.Retry …
(The operation was cancelled because it exceeded the configured timeout of 0:01:40.) ×6
```

同时在跑的另一个备份正常。

### 根因：本层退避对这个错误形状完全没生效

`BlobClientFactory.cs:31` 构造 `BlobClientOptions` 时没有配 `Retry`，走 Azure Storage SDK
默认：`MaxRetries = 5`（共 6 次尝试）、`NetworkTimeout = 100s`。六次全部超时之后，
Azure.Core 抛出的是一个 **`AggregateException`**（消息即上面那句，六个 inner 都是
`TaskCanceledException`）。

而 `BlobUploader.cs:122` 的 `IsTransient` 只认两种形状：

```csharp
private static bool IsTransient(Exception ex) => ex switch
{
    RequestFailedException rfe => rfe.Status == 0 || rfe.Status >= 500 || rfe.Status is 408 or 429,
    IOException => true,
    _ => false,          // ← AggregateException 落这里
};
```

`AggregateException` 两条都不匹配，落到 `_ => false`。于是 `RetryPolicy.ExecuteAsync`
（`RetryPolicy.cs:43` 的 `when (isTransient?.Invoke(ex) ?? true)`）一次都不重试，
异常直接穿到 `BackupRunner.RunCoreAsync` 的兜底 catch，整轮标记 Failed。

**设置里那套「200ms 起指数退避、最多 5 次」的重试，对这个错误一次都没跑过。**

从报错到放弃约 10 分钟：6 × 100 秒网络超时 + SDK 内部指数退避（0.8s 起、60s 封顶，累计约 25 秒）。

### 一轮失败要重传多少

收尾（写索引、写信息文件、写本地状态）在第 7–9 步，整轮失败时一个字节都没提交。
已传上去的数据分三种命运：

| 走哪条路 | 重跑时 |
|---|---|
| 大文件 → 独立 `data/{fullHash}` | **不重传字节**。要重压、重算 hash，但上传走 if-missing（`BlobUploader.cs:85` 的 `If-None-Match`），云上已有就跳过 |
| 大文件 + 加密 + 多卷 | **全量重传**。`ClearLeftoverVolumesAsync`（`BackupOrchestrator.cs:1283`）主动删残留卷——AES 每次 salt/IV 不同，新旧卷拼起来解不开 |
| 小文件 → pack | **一定重传**。pack 号带每轮随机前缀（`BackupOrchestrator.cs:179`），跨运行故意不重号，拿不到 if-missing 的便宜 |

小文件多的备份，重跑基本等于从零开始。

### 两个现存缺口

- **暂存目录泄漏**：`StagingArea.cs:286` 按 `Guid.NewGuid()` 建 `staged/{guid}` 子目录，
  `Program.cs` 没有任何启动清理逻辑。崩溃后这些目录永久残留。
- **停止即全丢**：`BackupRunner.Cancel` 是唯一的中止手段，按下去整轮作废，没有第二个选项。

## 目标

1. 网络类错误不再毁掉整轮：挂起、自愈重试、可手动推一把。
2. 进程崩溃/重启不再白跑：已确认传上云的记账落盘，重启后接着跑。
3. 用户可主动暂停并交还资源，之后随时恢复。
4. 中断留下的无用块能被安全清理，而**正在被复用的块绝不能被误删**。

## 非目标

- 不调 `NetworkTimeout`，不改 `TransferOptions`。链路够用时超时不是瓶颈；链路真断了，
  调长超时也没用，正确解法是挂起等人。
- 不复用崩溃前已压缩的临时文件。压缩相对上传便宜，复用要多一类记录和一条完整性校验，
  不值当。既然不复用，那些文件就是垃圾，该删。

## 状态模型

这是整份设计里最容易出事的地方，先定死。

`RunStatus` 是四个 runner 共用的枚举（`BackupRunner` / `CheckRunner` / `RepairRunner` /
`RestoreRunner`），`== RunStatus.Running` 的判断后端 15 处，前端还有
`while (run.status === 'Running')` 的轮询循环（`BackupConfigsPage.tsx:2114`、`2175`）。

**给它加一个 `Paused` 值是错的。** 漏改任何一处的后果都很难看：后端那 15 处会认为
"没在跑"——忙碌锁的判断、调度器 `TaskDispatcher` 的跳过逻辑全部失效，计划任务会插进来
另起一轮；前端轮询循环会直接 `break`，界面卡在最后一帧不再更新。

所以：

### 挂起是 `Running` 的子状态，不是新状态

`BackupRunState` 增加 `Pause? Pause` 字段（原因、连续失败次数、下次自动重试时刻）。
`Status` 保持 `Running`。

语义上也对：它确实还在运行，自愈重试就在跑，只是没有进展。**现有 15 处判断一行都不用改**，
忙碌锁、暂存席位、调度器跳过全部天然正确，不存在漏改的可能。

### `Suspended` 是新的终态，两种 reason 共用

```csharp
public enum RunStatus
{
    Running, Completed, Failed, Canceled,
    /// <summary>有 journal 待恢复，进程里没有活动运行。</summary>
    Suspended,
}

public enum SuspendReason { UserRequested, AutoSuspended, ShuttingDown }
```

主动暂停、自动降级、计划内关机**合并成同一个状态**：恢复路径、清理判据、界面按钮完全
一致，拆成三个枚举值只会让那 15 处判断从考虑 5 种变成考虑 7 种。界面用 reason 区分措辞：

- `Suspended — 3,412 items uploaded, paused by you`
- `Suspended — 3,412 items uploaded, network unreachable for 10 min`
- `Suspended — 3,412 items uploaded, interrupted by shutdown`

> **实现说明（以代码为准）**：第三个值最终是 `ShuttingDown`（`SIGTERM` 顺手挂起，见后文
> 「优雅关机与自动恢复」），设计稿原本写的 `Crashed` **有意没有实现**
> （`BackupSuspendedException.cs` 留了说明）。两者不是一回事：关机时代码还在跑，能给自己
> 写下理由；崩溃时没有任何代码在跑，没人能写。为崩溃伪造一条既没有 `Control`、也没有忙碌锁
> 的 `Suspended` 内存记录，代价是此后每个碰到它的分支都要额外记得"这一条是假的"。
> 崩溃遗留因此改为**不进内存状态**，由 `GET /{id}/interrupted` 直接读 journal 目录得到。

`Suspended` 是终态，现有 `== Running` 的判断自动把它当"不在跑"——也正确。

### 状态与按钮全景

| 状态 | 含义 | 资源 | 按钮 |
|---|---|---|---|
| `Running` | 正常跑 | 占席位、占忙碌锁 | `Suspend` `Cancel` |
| `Running` + `Pause` | 撞网络墙，自愈重试中 | 占席位、占忙碌锁 | `Retry now` `Suspend` `Cancel` |
| `Running` + `Suspending` | 收尾中，等在途上传返回 | 占席位、占忙碌锁 | （无，显示等待） |
| `Running` + `Canceling` | 收尾中，见「取消」一节 | 占席位、占忙碌锁 | （无，显示等待） |
| `Suspended` | 可恢复 | 全部已释放 | `Resume` `Discard` |

`Suspend` 保住恢复现场，`Cancel` 放弃这一轮——顺带补上"停止即全丢"这个缺口。
两个过渡态的 `Status` **都仍是 `Running`**：资源还没交出去，所有判断仍应视为"正忙"。

## 挂起闸门

### 错误分类

只有网络/云端瞬时错误挂起。密码错、7z 崩溃、磁盘满、配置错照旧终止——那些等人点一百次
还是同样的错。

```
IsPausable(ex, ct) =
    RequestFailedException (status 0 / 5xx / 408 / 429)
  | IOException | SocketException | TimeoutException
  | OperationCanceledException 且 !ct.IsCancellationRequested
  | AggregateException 且其 inner 全部 IsPausable      ← 今天这个
```

第三条必须带 `ct` 判据，**搞错就是取消按钮失效**：SDK 的网络超时抛的是
`TaskCanceledException`（继承 `OperationCanceledException`），而用户按 `Cancel` 走
`state.Cancellation`（`BackupRunner.cs:48`）抛的是同一个基类。唯一可靠的区分是问
本次运行的取消令牌有没有被触发——没触发就是网络超时，触发了就是用户要停。
这与 `BackupOrchestrator.cs:718` 已有的 `when (stopProducing.IsCancellationRequested
&& !ct.IsCancellationRequested)` 是同一个套路。

同一个判定同时替换 `BlobUploader.IsTransient`，这就是根因修复：`AggregateException`
被认出来之后，本层退避（默认 5 次、200ms 起指数）会真正跑起来。

**两层是串联的**：本层退避先吃一遍，退避耗尽后才交给闸门。

### 闸门

运行级单例，挂在 `RunState` 上。撞墙的上传任务停在 `await gate.WaitAsync(ct)`；
没撞墙的任务继续跑完手上的活——网络只是抖一下的话它们能正常收尾，不必陪葬。

于是挂起是**部分**的：可能 1 件停在闸门上、5 件还在正常传。界面口径定为
「只要有任务停在闸门上就显示 Paused」，同时照常显示仍在跑的那几条明细
（`BackupProgress.Details` 本来就是列表，见 `BackupOrchestrator.cs:115`）。
`Pause` 字段里带停在闸门上的任务数，让"3 件挂起、2 件仍在传"这种真实形状能表达出来。

放行有三个来源：

1. 自愈重试定时器到点（30s → 1m → 5m，之后每 5m）
2. 用户点 `Retry now`
3. 用户点 `Cancel`（放行成取消）

这条阶梯是闸门**自己的**，与 PRD 4.1 那套（`5s / 30s / 90s / 300s`，之后每 300s，上限 2h，
落在 `GlobalSettings.RetryBackoffSeconds`）无关，也没有复用 `RetryOptions`——两层管的不是
同一件事：那一层退避的是**单次 HTTP 调用**，量级是秒；这一层等的是**一条链路恢复**，量级是
分钟，而且每多等一分钟就多占一分钟别人的全局暂存额度。硬编码在 `PauseGate` 里（连同 10 分钟
的耐心阈值），只有构造函数参数可改，供测试注入毫秒级的值；生产不可配。

挂起时发一条通知（沿用 `NotificationEvents` 那套），无人值守时才有人知道。

### 挂起会绑架别的备份，必须限时

闸门不释放暂存席位、不释放忙碌锁——现场和账本一字不动，恢复时不必重新排队等额度。
但这有一个**必须解掉**的后果。

`StagingArea.cs:87` 的第一道闸是全局的：

```csharp
private bool HasRoom(StagingLease? lease) =>
    Interlocked.Read(ref _stagedBytes) < stagedLimit()      // ← 全局，不分席位
    && (lease is null || lease.Bytes < QuotaFor(lease));
```

挂起的运行手上那批已压缩、待上传的产物**照样计在 `_stagedBytes` 里**。一旦吃满全局上限
（默认 2 GB），另一个备份的 `HasRoom` 恒为 false，卡在 `WaitForRoomAsync` 的
`await _releaseSignal.WaitAsync(ct)` 上——而 `SignalRelease` 只在有卷上传完或
`Reservation.Dispose` 时发信号，挂起的运行不再上传，**信号永远不来**。

不是永久死锁（网络一恢复就全活了），但等于：一个跟这次故障毫不相干的备份——可能是完全
不同的账户、不同的网络路径——被绑架到故障恢复为止。第二道闸（席位均分）只是让它变慢，
第一道闸是让它彻底停摆。

**解法：挂起有耐心阈值，超时自动降级为 `Suspended`。**

```
撞墙 → 挂起，保持现场，自愈重试（30s → 1m → 5m → 每 5m）
     → 累计超过阈值（默认 10 分钟）仍不通
     → 自动走 Suspend 的收尾路径：清 staged、释放席位与忙碌锁、
       journal 落盘、Task 结束，reason = UserRequested 之外的第三种：AutoSuspended
```

短暂抖动零成本（绝大多数情况几十秒内自愈，现场完好，别人最多等这几十秒）；长时间故障
自动交还全部资源，别的备份立刻解放，而这一轮的进度一件不丢——随时可 Resume。

复用的是已定的 Suspend 路径，不引入新机制。`SuspendReason` 因此有三个值：
`UserRequested` / `AutoSuspended` / `ShuttingDown`（第三个值的由来见前文实现说明），
界面措辞各不同，恢复路径完全一致。

自动降级同样发通知。

## journal

### 位置与格式

`data/journal/{accountId}/{container}/{runId}.jsonl`，追加式文本，**不进 SQLite**。

> **实现说明（以代码为准）**：分目录用的是 `(accountId, container)` 而不是设计稿原本写的
> `configId`——清理器就是按这两样定位容器的，手上根本没有 `configId`。`configId` 记在
> journal 头里，需要时从头读。挂起标记是同名同目录、只多一个后缀的兄弟文件，而枚举只认
> `*.jsonl`，所以它天然不会被当成一卷 journal 去解析。

理由是本项目自己的先例：`BackupOrchestrator.cs:307` 记着 verbose 日志"落到按备份+按日期的
文本文件而非 SQLite，避免每文件一次 DB 写成为超大备份的瓶颈"。journal 是同一个形状——
每件一行，几十万件。`data/` 就是 `app.db` 所在目录，本来就是持久卷。

第一行 header：`runId`、`configId`、`startedAt`、基线版本号、`localRoot`、加密身份。
之后每确认一件追加一行：`path`、`ref`、`kind`、`length`、`fullHash`。

### 写入时序

**方向不能反：**

```
压缩 → 上传 → 上传确认返回 → 才追加 journal 行
                            ↑ 绝不能提前一格
```

`UploadIfMissingAsync` 返回 `false`（"云上已经有了"）**也要记**——那同样是确认在云上。

### 不做 fsync

故意的。两个方向的错误代价完全不对称：

| 崩溃时 journal | 后果 |
|---|---|
| 少记了几行 | 那几件重压重传。浪费，但**正确** |
| 多记了一行 | 索引指向不存在或残缺的 blob。**静默丢数据** |

不 fsync 只会导致前者。真正的风险在时序，不在落盘延迟；几十万次 fsync 换不来任何安全性
提升。主动暂停是唯一的例外——优雅路径上 fsync 一次，不心疼。

### 前提校验

恢复前逐条核对，任一不符则整份 journal 作废：

- `localRoot` 变了
- 基线版本号变了（中间有别的成功备份提交了新版本）
- 加密身份/密码变了

基线那条与"活动 journal 保护块"是配套的：块受保护 ⟹ 基线不该变；基线真变了说明保护
失效，那就别赌。

压缩设置变了**不**作废：单文件 blob 按明文内容寻址不受影响，已成箱的 pack 不受影响，
剩下的文件本来就重新装箱。

## 恢复

### 启动

扫 `data/journal/`，未完成的 journal **不进内存状态**：`GET /{id}/interrupted` 现读现返
（`PeekAsync` 只读头一行、其余数行数——停在半路那卷可能有几十万条记录，而这是启动路径）。
界面列出来等人点，`DELETE` 同一路径即 Discard。

> **实现说明（以代码为准）**：设计稿原本写的是"登记一条 `Suspended (reason = Crashed)`
> 的 state"。改掉的理由见前文 `SuspendReason` 那条说明——那会是一条没有 `Control`、
> 没有忙碌锁的假记录。**「不起 Task、不抢忙碌锁」这个约束没变，变的是不必为此伪造一条状态。**

手动点 Run 时若盘上还有作数的 journal，等价于 Resume（不会从头来）。这也是为什么
**没有单独的 resume 端点**：恢复不是一种模式，每一轮开卷时都会去认还作数的 journal，
界面上那个按钮叫 `Resume` 只是因为对用户来说这确实是"接着跑"。

### Resume

就是正常跑一遍 `RunCoreAsync`，只多一个入参。扫描和 diff 照常跑（本地的、快的），
区别只在装箱之前多一道查表：

```
diff 产出 (path, length, fullHash)
  → journal 命中（path + fullHash 双匹配）？
     命中  → 直接填 storageByPath，不压缩、不上传
     未命中 → 正常走；剩下的重新装箱成新 pack
```

判据必须是 **path + fullHash 双匹配**，不能只看 path——崩溃后文件可能又被改过。
fullHash 在 diff 阶段本来就要算（`ToPlannedFile` 就带着它），不多花一分钱。

pack 每轮随机前缀在这里反而是优势：重算出的新箱绝不会和已传的旧箱重号。

恢复期间**继续追加同一份 journal**，不新建。二次崩溃照样能接着来。

### 生命周期

提交信息文件（第 9 步）成功 → 删 journal → 再跑第 10 步 cleanup。

## 清理

### 统一判据

> 删除判据 = 不被任何保留版本引用 **且** 不被任何活动 journal 引用。

`RetentionCleaner.cs:99-158` 的 `referencedBlobs` / `referencedPacks` 各并入一份
"活动 journal 引用集"即可。

**不存在"按 journal 反查删除"这个动作。** Discard 或作废一份 journal，做的只是把它从
活动集合里摘掉，然后跑一次**正常**清理。

这个写法避开一个边界：崩在"info 已提交、journal 还没删"那一格之间时，启动会发现基线
版本变了、判定 journal 作废——而它引用的块此刻**已经被新版本索引引用着**。按"反查删除"
去清就是删正在用的数据。统一判据天生没这个问题。

### 时机

- 备份完成时：既有的第 10 步 cleanup，天然覆盖
- Discard 时：主动跑一次
- 作废（前提校验不过）时：主动跑一次
- **`Cancel` 时不清理**，理由见「取消」一节
- 删除备份配置时：见「取消」一节末尾

### 暂存目录

启动时直接清空 `{tempPath}/compress` 和 `{tempPath}/staged`。

判据天然明确：进程刚起来，按定义没有任何活着的运行，那里的一切都是上一个进程的遗留，
而遗留的临时文件我们已经决定不复用。顺带修掉现存的目录泄漏。

运行期的 staged 文件仍受内存里的对象保护，与本条无关。

## 主动暂停

**主动暂停 ≈ 优雅版的崩溃恢复。** 恢复路径、清理判据、界面按钮与上面完全一致，
只是到达方式是干净的：

```
点 Suspend
  → 停止从 diff 队列取新工作
  → 等在途的上传各自跑完（不强杀）
  → fsync journal
  → 结束 Task：释放暂存席位、释放忙碌锁、清掉 staged 临时文件
  → 状态落为 Suspended (reason = UserRequested)
```

**在途上传必须等它返回，不能强杀。** 中途中止会落在"传没传成不确定"的位置，而 journal
只认确认返回。等它自己返回（成功就记账、失败就不记）是唯一干净的边界。

代价是暂停不是瞬时的：手上有大文件在传时可能要等几分钟。这期间是 `Suspending` 子状态，
`Status` **仍是 `Running`**——资源还没交出去，所有判断仍应视为"正忙"。界面显示
`Suspending… (waiting for 2 uploads to finish)`。

自动挂起状态下也能点 `Suspend`，走同一条收尾路径。

## 取消

`Cancel` 与 `Suspend` 的区别是**这一轮算不算数**：`Suspend` 落 `Suspended`（界面给
`Resume` / `Discard`），`Cancel` 落 `Canceled`（界面回到普通的 `Run`）。云上已传的块
两者都留着（见下）。

> **实现说明（以代码为准）**：设计稿原本写的是"`Cancel` 删 journal"，实现**没有**这么做——
> 两种停法都落盘（`SettleStopAsync` 里 journal 一律 `FlushAsync(fsync: true)`）。
> 理由正是下面「完整的留着」那一节自己的结论：既然决定让已传完的块留在云上给下一轮复用，
> 那就没有理由把"哪些块已经传完"这份账一并烧掉——删了账，下一轮只能靠 `If-None-Match`
> 逐个撞运气，pack 更是一件都认不回来（随机前缀，见上）。于是 `Cancel` 之后下一次 Run
> 同样是接着跑的，只是界面不把它叫作 `Resume`。
> 真正删 journal 的只有三处：本轮成功提交索引（`BackupRunControl.Complete`）、
> 用户点 `Discard`（`DELETE /{id}/interrupted`）、删除备份配置。

### 等收尾真正完成才返回

现在 `BackupRunner.Cancel`（`BackupRunner.cs:185`）只是 `Cancellation.Cancel()` 然后
立刻返回 `bool`，调用方无从知道那一轮到底停干净了没有。改成异步：端点等到 journal 落盘、
临时文件清掉、席位与忙碌锁释放之后才返回。期间是 `Canceling` 子状态，界面显示
`Canceling…`。

理由与 `Suspend` 同：忙碌锁没释放时若端点已返回成功，用户下一步操作（改配置、删配置、
再跑一次）会撞上一个还没死透的运行。

### 两种停法，按下 Cancel 时问

| 选项 | 行为 |
|---|---|
| `Stop now` | 取消令牌一穿到底，强杀在途上传。最快停下 |
| `Finish current files` | 等在途的文件各自传完（**含全部分卷**）再停 |

第二项对多卷大文件尤其值：一个 50 GB 的文件传到第 19 卷被强杀，那 19 卷全废；加密多卷
下次还要被 `ClearLeftoverVolumesAsync` 全删重传（`BackupOrchestrator.cs:1283`）。

`Suspend` 没有这个选项——它按定义就是 `Finish current files`（不强杀，见上一节）。
所以 `Stop now` 是唯一会强杀在途上传的路径。

### 完整的留着，不完整的当场删掉

判据是 **journal**：它只记确认返回的那些，所以 journal 里的 = 完整，在途的 = 不完整。

**完整传完的留着。** 单文件 blob 是内容寻址的 `data/{fullHash}`，下一次备份跑到同一个
文件时 `If-None-Match` 直接命中，**这一轮传的字节一分不白费**；复用之后它被新版本索引
引用，自然不再是孤儿。若 `Cancel` 时把它们也删干净，`Finish current files` 就等于白等
——传完也是删。两个决定必须一致，这里选了让字节有价值的那一侧。

**不完整的当场删掉。** `Stop now` 强杀在途上传时，那个文件可能已经传了一部分分卷：
20 卷的归档只落地 19 卷，它是**解不开的**。收尾时按在途的 `blobRef` 列举并删除它自己的
全部卷，复用 `ClearLeftoverVolumesAsync` 里的 `VolumeBlobIO.IsVolumeOf`
（`BackupOrchestrator.cs:1300`）——那个判定只认这个归档自己的卷，不会误删碰撞避让的
兄弟 `data/{hash}~1`，那是另一份内容、由别的索引条目引用着，误删就是真丢数据。

于是两种停法的收尾是不同的：

| 选项 | staged 未传的 | 完整传完的 | 在途那个文件 |
|---|---|---|---|
| `Stop now` | 删 | 留 | **删掉它的全部残留卷** |
| `Finish current files` | 删 | 留 | 等它传完，也是完整的，留 |

严格说，逐卷 if-missing 本来就能把缺的卷补上（`VolumeBlobIO.cs:110` 的注释记着这件事，
明文下重压产出的卷逐字节相同，实测确认过），所以残留卷不至于导致错误。当场删的价值在
另一头：**不必等到下一次备份才回收**，而 `Stop now` 的语义本来就是干净利落地停。

pack 不享受"留着复用"：随机前缀让下一轮必然是新箱，旧 pack 永远是孤儿，会在下一次备份的
第 10 步 cleanup 里被正常清掉。

**代价**：如果就此不再跑这个备份，这批块会一直占着云存储费用。所以——

### 删除备份配置时兜底清理

`Cancel` 留下的块靠"下一次备份"回收，而删配置意味着不会有下一次了。删除路径
（`BackupConfigEndpoints.cs:209`）要补上兜底：

- `deleteContainer = true`：整个 container 删掉，孤儿一并消失，无需额外动作
- `deleteContainer = false`：云端数据留着供以后导入，但那批孤儿是纯垃圾——删配置前
  跑一次孤儿清理（journal 已摘出活动集合，走的还是统一判据），并删掉该配置的全部 journal

同理，`Suspended` 状态下删配置也走这条：现场不再有人恢复，journal 和它护着的块一起清。

## 优雅关机与自动恢复

> 本节是设计稿定稿之后追加的（对应实施顺序里的阶段 8、9）。上面那套机制解决了"中断之后
> 进度还在"，但计划内的重启——`docker stop`、换镜像升级——仍然要人事后回来点一下。
> 这一节把那一下也去掉，且**只去掉这一种**。

### 关机：`SIGTERM` 顺手挂起

宿主收到停止信号时，让所有 `Running` 的运行走一遍已有的 Suspend 收尾路径，
`reason = ShuttingDown`，journal `fsync` 落盘，并在 journal 旁边写一个**同名标记文件**记下这个理由。

标记为什么是 journal 的兄弟文件、而不是 journal 里的一条记录：清理判据按 `Kind` 把记录分进
"blob" 或 "pack" 两个桶，凭空多出第三种 Kind 会静悄悄落进 blob 桶里去。

三个超时是**一串**的，动一个就得回头看另外两个：

```
docker stop_grace_period 45s  >  HostOptions.ShutdownTimeout 30s  >  等运行落盘 20s
```

- `45 > 30`：docker 的宽限期一到就是 `SIGKILL`。必须让 .NET 自己的超时先到，才还有机会把日志写出来。
- `30 > 20`：宿主等 `StopAsync` 也有超时，超了它不等、直接往下拆服务——那时连"谁没停下来"都没人记得下。

**这个等待必须有上限，而且要如实说清它做不到什么。** `StopKind.Suspend` 有意不碰 AbortToken，
消费循环也是在**下一件**活开始前才退出的，所以手上这件（可能是一个几 GB 的上传）会被放着跑完。
到点还没落盘的运行就丢在半路——它下次启动时是一次**没有标记**的中断运行，得人工点 Resume。
这是有意的取舍：放弃一件在途文件的代价是重传它一次，而等它等到超过宿主宽限期的代价是 `SIGKILL`，
那才是真的丢掉整轮。

下达停止与等待落盘**必须分两趟**：先把停止意愿发给每一个运行，再统一等。合成一趟（发一个、
等一个）在并发备份下是致命的——排头那个若压着一个几 GB 的上传，它一个人就吃掉整个关机预算，
后面的运行连停止请求都收不到。

### 启动：只认关机标记

`AutoResumeService` 在启动约 15 秒后开工（让 Web 端口先起来、调度器先跑第一拍），受全局设置
`AutoResumeInterruptedRuns`（默认开）控制，逐个起、**等前一个跑完再起下一个**——产出锁是全局的，
一起冲上去只会互相排队，而且并发备份反而更慢（本仓库实测过）。

判据只有一条：这个配置**至少留了一卷** journal，而且**每一卷**旁边的标记都是 `ShuttingDown`。

| 盘上的样子 | 自动接着跑？ | 为什么 |
|---|---|---|
| `ShuttingDown` | **是** | 唯一一种"这个进程自己造成的中断、而且现场是好的" |
| `UserRequested` | 否 | 替他重开等于把他按那一下的意图擦掉 |
| `AutoSuspended` | 否 | 那个瞬时错误多半还在，马上接着跑只会立刻再撞一次墙 |
| 没有标记 | 否 | 说不清 |

最后一行是这套判据的重心：**盘上没有标记不说明"进程被 kill"**。崩溃、掉电、关机等落盘超时被丢在
半路、操作员按了 Cancel（两种取消都照样落盘，都有意不写标记）、甚至写标记这一步本身失败，
长在盘上一模一样。其中至少有一种（Cancel）是用户明确表达过"别跑了"的，所以这一大类整个不碰。

要求**每一卷**都合格而不是"最新那卷说了算"：标记是按卷记的，一个配置底下完全可能出现取值打架的
几卷（按暂停 → 又点 Run → 新一轮采纳了旧卷 → 一次关机把新一轮停成 `ShuttingDown`），而接着跑
那一轮开卷时会把所有还作数的卷一起认下来。要求全票通过，就不必再发明一套"哪卷更新"的仲裁。

被否掉的配置**每个都要记一句日志**，点名是哪一卷、什么标记。这不是排场：这个部署形态是 NAS 上的
成品机，操作员既没有 shell 也没有看标记文件的工具。少了它，"重启之后我的备份怎么没接上"在他那边
是完全没有线索的——界面上开关是开的，日志里一个字都没有，而真正的原因只长在盘上。

## 测试

安全性优先，下面几条都是"错了就丢数据"的形状：

- journal 时序：上传抛异常时**绝不**留下记账行；`UploadIfMissingAsync` 返回 `false` 时
  **必须**留下记账行
- 恢复命中判据：path 同、fullHash 不同 → 不命中
- 清理判据：活动 journal 引用的块不被删；作废后走正常判据；**"info 已提交、journal 未删"
  那个边界——块已被新版本引用，作废清理绝不能碰它**
- 状态模型：`Suspending` / `Canceling` / `Suspended` 期间调度器不插进来另起一轮
- **自动降级解绑架**：A 挂起且吃满 `stagedLimit` → B 卡在 `WaitForRoomAsync` →
  A 超时降级 → B 解冻并跑完。这是并发时序测试，也是这一整节存在的理由
- `Stop now` 删掉在途文件的残留卷，且**不碰** `data/{hash}~1` 这个碰撞避让的兄弟
- 取消令牌区分：用户按 `Cancel` 不被误判成网络错误而挂起
- **自动恢复的判据**：四种盘上形态（`ShuttingDown` / `UserRequested` / `AutoSuspended` /
  无标记）各自该不该接着跑；一个配置底下几卷标记打架时**不**接着跑；设置关掉时真的不开工——
  最后这条坏掉的样子是**静默**的（界面照样显示保存成功，直到某天重启后一轮他不想要的备份自己跑起来）

其余：`IsTransient` 分类表（含嵌套 `AggregateException`）、闸门放行的三个来源、
主动暂停与取消时在途上传等待收尾、`Cancel` 端点等收尾完成才返回、
删除配置时的兜底清理、启动清空暂存目录。

Azurite 必须起着跑（`npx azurite --skipApiVersionCheck`），否则相关集成测试静默跳过。

## 实施顺序

**一次性上线，不分批发版。** 下面的切分只是实施与验证的次序，每阶段各自可测。

| 阶段 | 内容 |
|---|---|
| 1 | 修 `IsTransient` + 错误分类（含取消令牌的区分） |
| 2 | 启动时清空 `compress`/`staged`，修掉现存目录泄漏 |
| 3 | journal 只写不读——时序安全先立住，不改变任何行为 |
| 4 | 挂起闸门 + 自愈重试 + 超时自动降级 |
| 5 | 主动暂停与取消（两种停法、异步收尾、残留卷清理） |
| 6 | 恢复 + 统一清理判据 + 删配置兜底 |
| 7 | API 与前端：状态、按钮、Cancel 对话框、轮询适配 |
| 8 | 优雅关机：`SIGTERM` 挂起全部运行 + 落盘标记（定稿后追加） |
| 9 | 启动自动恢复：只认关机标记，受全局设置控制（定稿后追加） |

阶段 4 依赖 3（降级要 journal 落盘），6 依赖 3 和 5，9 依赖 8（没有标记就没有判据）。
