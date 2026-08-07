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

public enum SuspendReason { UserRequested, Crashed }
```

主动暂停与崩溃遗留**合并成同一个状态**：恢复路径、清理判据、界面按钮完全一致，
拆成两个枚举值只会让那 15 处判断从考虑 5 种变成考虑 6 种。界面用 reason 区分措辞：

- `Suspended — 3,412 items uploaded, paused by you`
- `Suspended — 3,412 items uploaded, interrupted by shutdown`

`Suspended` 是终态，现有 `== Running` 的判断自动把它当"不在跑"——也正确。

### 状态与按钮全景

| 状态 | 含义 | 资源 | 按钮 |
|---|---|---|---|
| `Running` | 正常跑 | 占席位、占忙碌锁 | `Suspend` `Cancel` |
| `Running` + `Pause` | 撞网络墙，自愈重试中 | 占席位、占忙碌锁 | `Retry now` `Suspend` `Cancel` |
| `Running` + `Suspending` | 收尾中，等在途上传返回 | 占席位、占忙碌锁 | （无，显示等待） |
| `Suspended` | 可恢复 | 全部已释放 | `Resume` `Discard` |

`Suspend` 保住已传的，`Cancel` 才是放弃——顺带补上"停止即全丢"这个缺口。

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

1. 自愈重试定时器到点（复用 `RetryOptions` 的序列退避模式，PRD 4.1：30s → 1m → 5m，之后每 5m）
2. 用户点 `Retry now`
3. 用户点 `Cancel`（放行成取消）

**闸门不释放暂存席位、不释放忙碌锁。** 代价是另一个并发备份在挂起期间只能拿到一半
暂存额度（`StagingArea.QuotaFor` 按席位数均分）；好处是现场和账本一字不动，恢复时不必
重新排队等额度。要交还资源就用主动暂停。

挂起时发一条通知（沿用 `NotificationEvents` 那套），无人值守时才有人知道。

## journal

### 位置与格式

`data/journal/{configId}/{runId}.jsonl`，追加式文本，**不进 SQLite**。

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

扫 `data/journal/`，每份未完成的 journal 在对应 configId 上登记一条 `Suspended`
(`reason = Crashed`) 的 state：**不起 Task、不抢忙碌锁**。界面列出来等人点。

手动点 Run 时若存在 `Suspended`，等价于 Resume（不会从头来）。

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
- `Cancel` 时：同 Discard——按下 `Cancel` 就是放弃这一轮，journal 随之摘出活动集合并
  删除，然后跑一次正常清理。`Cancel` 与 `Suspend` 的区别正在于此，二者不能含糊。

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

## 测试

安全性优先，下面几条都是"错了就丢数据"的形状：

- journal 时序：上传抛异常时**绝不**留下记账行；`UploadIfMissingAsync` 返回 `false` 时
  **必须**留下记账行
- 恢复命中判据：path 同、fullHash 不同 → 不命中
- 清理判据：活动 journal 引用的块不被删；作废后走正常判据；**"info 已提交、journal 未删"
  那个边界——块已被新版本引用，作废清理绝不能碰它**
- 状态模型：`Suspending` / `Suspended` 期间调度器不插进来另起一轮

其余：`IsTransient` 分类表（含嵌套 `AggregateException`）、闸门放行的三个来源、
主动暂停时在途上传等待收尾、启动清空暂存目录。

Azurite 必须起着跑（`npx azurite --skipApiVersionCheck`），否则相关集成测试静默跳过。

## 实施顺序

按"能独立上线"切：

| 阶段 | 内容 | 价值 |
|---|---|---|
| 1 | 修 `IsTransient` + 错误分类 | 当场解决线上问题，退避真正生效 |
| 2 | 启动时清空 `compress`/`staged` | 修掉现存目录泄漏 |
| 3 | journal 只写不读 | 时序安全先立住，不改变任何行为 |
| 4 | 挂起闸门 + 自愈重试 + API/UI | 网络抖动不再毁掉整轮 |
| 5 | 恢复 + 清理判据 | 崩溃不再白跑 |
| 6 | 主动暂停 | 资源可交还 |

阶段 1 单独就能合并上线，不必等后面。
