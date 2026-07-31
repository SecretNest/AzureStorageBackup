# 上传速度：时钟只在有流的时候走

## 问题

界面上的速度（`StageProgress.BytesPerSecond`）由 `StageTracker.PublishIfDue` 用一个 10 秒滚动窗口算出来：

```csharp
_samples.Enqueue((now, _bytes));
while (_samples.Count > 1 && now - _samples.Peek().Ms > SpeedWindowMs)
    _samples.Dequeue();
speed = (_bytes - oldest.Bytes) * 1000 / spanMs;
```

时间戳取自 `Stopwatch`，也就是墙钟。备份上传阶段的实际节奏是「压一箱几十秒 → 传几秒」，于是这个数字的含义随停顿长短而变，前后不一致：

- **空档 < 10 秒**（等压缩锁、去重命中、压一个小箱子）：停顿两侧的采样都还在窗口里，那段没传字节的时间落进分母 → 速度被稀释。
- **空档 > 10 秒**（一箱 100 MB 过 `7z -mx9`）：停顿期间没有任何事件触发采样；恢复时新采样入队后，窗口里的老采样因为超龄被整批淘汰，只剩一个 → 那一拍报 0，下一拍开始窗口只覆盖恢复之后 → 显示纯网线速度，压缩那几十秒完全没算。

用户看到的现象就是「速度很飘」：同一条网线、同样的传输，数字随压缩箱子的大小在半速与满速之间跳，偶尔还闪一个 0。

另一半问题在反方向：`PublishIfDue` 只被「有字节流动」或「有计数推进」触发。上传流开着却一个字节都不动（网络卡死、SDK 没触发重试）时，**没有任何事件**，界面会一直冻在卡住前的数字上——最该报警的情况看不出来。

## 目标语义

**速度 = 「网线上至少有一条流开着」那段时间的吞吐。**

- 没有在途流（全在压缩、排队、等闸门额度）→ 这段时间不进分母。
- 有在途流但字节不动 → 这段时间进分母，把速度压下去，让卡住看得见。

判据现成：`VolumeUploadScope.RunAsync` 在 `gate.WaitAsync` 拿到额度**之后**才 `BeginItem`，`RestoreOrchestrator` 与 `BackupChecker.VerifyGroupAsync` 同样如此（各自的注释里都写明了「在途标记要在拿到闸门之后才打」）。所以 `_active.Count > 0` 精确等于「网线上有几条流」，不需要新的信号。

明确**不在**目标内：ETA。`Eta()` 走 `_workStartMs` 起算的全程平均，压缩时间本来就该算进剩余时间。这次只改「速度」这一个数字的含义。

## 设计

### 1. 虚拟上传时钟

`StageTracker` 内新增两个字段：

- `_activeMs` — 已累计的活跃毫秒
- `_activeSinceMs` — 当前活跃段的起点；`-1` 表示当下没有在途流

活跃时长 = `_activeMs + (_activeSinceMs >= 0 ? now - _activeSinceMs : 0)`。

`_active` 由空变非空（`BeginItem`）时记起点，由非空变空（`EndItem`）时把这一段累进 `_activeMs`。两处都在 `_gate` 锁内完成，`_active` 的增删也一并挪进锁里——"是不是空的"与时钟开关必须在同一个临界区内定下来。段边界不需要额外强制采样：活跃段外没有字节流动，累计值本身就是准的。

采样队列 `(Ms, Bytes)` 的 `Ms` 从墙钟改为这条虚拟时间轴，10 秒窗口也在虚拟轴上度量。

效果：

- 压缩期虚拟时间不前进 → 停顿两侧的采样在窗口里是**连着的** → 速度既不被稀释，也不会出现「整批淘汰 → 报 0 → 猛跳」。
- 卡住时虚拟时间照走 → 窗口内字节增量为 0 → 速度一路掉向 0。

**不选**的替代方案：保留墙钟时间戳，算速度时把窗口内的空闲区间扣掉。需要维护一张空闲区间表，还要处理被窗口切成半截的区间，结果与虚拟时钟等价而实现复杂得多。

### 2. 心跳

卡住时没有事件，虚拟时钟走了也没人去重算。加一个 `System.Threading.Timer`，周期 1 秒，**只在活跃段内跑**：空→非空时 `Change(1s, 1s)`，非空→空时 `Change(Timeout.Infinite, Timeout.Infinite)`。回调只做一件事——`PublishIfDue(force: false)`，200 ms 节流照旧。

实现落地时又加了两层防护，都不是本节最初设想的那"只做一件事"：回调先检查 `_completed`，
已经收尾的阶段不会被迟到的回调再补一条快照（见下一节）；回调本身包一层 `try/catch` 把异常吞掉——
它跑在线程池定时器线程上，没有调用方能接住抛出的异常，顶到运行时手里默认行为是打掉整个进程，
而进度上报只是锦上添花，不该有资格拖累正在跑的备份/还原/校验。

这样纯压缩期一个多余的快照都不发，界面保留停顿前的最后一个速度值（含义是「最近一段上传时的速度」；旁边的 `uploading=0 / preparing=1` 已经说清了当下没在传）。

`StageTracker` 实现 `IDisposable`，`Complete()` 里停表。异常路径下漏掉 `Dispose` 也不会泄漏定时器回调：只要 `EndItem` 是成对调用的（三处都在 `finally` 里），表在最后一条流结束时就已经停了。

### 3. 开关与适用范围

构造函数新增可选参数，默认 `false`——保持从不调 `BeginItem` 的阶段原样。对那些阶段虚拟时钟会永远停在 0，速度将恒为 0，属于必须避免的回归。

| 阶段 | 位置 | 开关 | 理由 |
|---|---|---|---|
| Uploading | `BackupOrchestrator.cs:313` | `true` | 压一箱几十秒、传几秒 |
| Restoring | `RestoreOrchestrator.cs:259` | `true` | 下载与解压交替 |
| Verifying | `BackupChecker.cs:286` | `true` | 下载后解压重算 hash |
| Scanning / Diffing / LoadingIndex / Metadata / Local / Orphans / Cloud | — | `false` | 从不 `BeginItem` |

`Cloud` 阶段只做 HEAD 请求后 `Advance`，不登记在途项，因此留在 `false`。

三个开了 `true` 的阶段现在都名副其实地"边传边报"：`VolumeBlobIO.DownloadAsync` 新增一个可选的
`Func<IProgress<long>>?` 进度回调**工厂**参数，挂到 SDK `DownloadToAsync` 的 `BlobDownloadToOptions.
ProgressHandler` 上，字节和 Uploading 一样经 `ItemProgress()` 返回的 `DeltaProgress` 逐笔进
`AddBytes`。Restoring（`RestoreOrchestrator.cs`）与 Verifying（`BackupChecker.cs`）的 `BeginItem`/
`EndItem` 窗口也相应收窄：`BeginItem` 仍在拿到并发闸门之后打，但 `EndItem(blobName, 0)` 提前到
下载本身结束（成功或失败）就调用——`0` 是因为字节已经在下载过程中经进度回调计过了，收尾不需要、
也不能再补一次，否则就是双计。下载之后的解压、重算 hash、写盘不再计入在途窗口，虚拟测速时钟
量的因此真的是"网线上有多快"，不再把本地 CPU 时间也算进分母。

工厂而不是单个 `IProgress<long>`：SDK 报的是**本次** `DownloadToAsync` 调用内的累计字节，
`DeltaProgress` 把累计转增量是按"这一个实例自己的基线"算的（回退即视为重新开始）。多卷下载若
共用一个实例，后一卷从 0 起步的累计有可能被那个基线错误地记账——每卷调一次工厂拿一个全新实例，
与 `VolumeUploadScope.RunAsync` 里"每卷各要一个 `ItemProgress()`"是同一个道理，`VolumeBlobIO.cs`
里 `DownloadAsync` 方法头的注释和 `VolumeBlobIOTests.
DownloadAsync_Calls_Progress_Factory_Once_Per_Volume_With_A_Fresh_Instance` 分别是这条约束的
文档和 mutation 验证过的回归测试。

此前这一节描述的"锯齿"（一组活的字节整块砸在 `EndItem` 那一瞬、心跳采样跨窗口边界时读数忽高忽低）
到这里就不再是 Restoring / Verifying 的已知限制——字节随下载进度连续入账，不再有"一整块"这回事。
`DeadWeightCompactor.cs` 里那个不带 tracker 的调用点（死重压实，不接进度、不登记在途）维持原样，
`progress` 参数默认 `null` 时 `DownloadAsync` 完全不挂回调，行为与改动前一致。

### 4. 可测性

10 秒窗口不能靠真等。给 `StageTracker` 加一个 internal 时间源（`Func<long>`，默认取 `Stopwatch`）和一个 internal `Tick()`；生产代码里的定时器回调只调 `Tick()`。测试注入假时钟后完全确定，不依赖 `Task.Delay`。

## 测试

1. **压缩空档不进分母**：传 1 MB 用 1 秒 → 无在途流地推进 30 秒 → 再传 1 MB 用 1 秒。速度约 1 MB/s，既不是 500 KB/s，也不是 0。
2. **卡住体现为掉速**：登记在途流，字节不动，推进 10 秒以上并 `Tick()`。速度掉到 0。
3. **无回归**：开关 `false` 的阶段（Scanning/Diffing）速度算法与现在逐字节一致。
4. **心跳只在活跃段跑**：没有在途流时 `Tick()` 不产出新快照。
5. 现有 `StageProgressTests` / `BackupProgressDetailTests` 全绿。

## 不改的东西

- 前端。`BackupConfigsPage` 照旧渲染 `bytesPerSecond`，含义变了但形状没变。
- `Eta()` 与 `EstimatedRemaining`。
- `preparing` / `queued` / `ActiveItems` 的口径。

## 2026-07-31 追加：压完到开传之间那一段

上面那三个口径确实一个都没动，但它们**加起来不等于总数**——这一轮补的就是差额。

现场是这样一屏，而且一连几分钟纹丝不动：

```
Uploading: 5,345 of 6,378 objects · nothing on the wire right now · 1 preparing · 1,031 queued
195.3 GB / 331.6 GB original (58%) · +4.2 GB uploaded in unfinished objects · 100.0 MB ready to upload
```

`5,345 + 1 + 1,031 = 6,377`。少的那一件正是卡住的那个：它压完了（`100.0 MB ready to upload` 就是它，`_stagedBytes` 只在整件压完移入暂存区后才增加），卷已经部分落云（`+4.2 GB`），可它既不在 `preparing`（那只数拿到压缩锁的）、不在 `queued`（那只数还没被领走或在排压缩锁的）、也不在 `ActiveItems`（那数的是在途的**卷**）。屏幕上没有任何一栏在说它，只能靠把几屏截图排在一起做减法才发现得了它存在。

### 恒等式

```
processed + preparing + queued + uploading ≡ total
```

`uploading` 取 `inWork - _inStaging`——手上的件减去还在暂存段的件。**不用** `_inUpload`（`BeginUpload`/`EndUpload` 那一对）：它从 `UploadStagedBlobAsync` 才开始算，而压完到那里之间还隔着预约协调与云端 HEAD，一件活能在那儿卡上几分钟却不在它里面。用它当口径，账在最需要对得上的时候恰好对不上，而账对不上正是这一栏存在的理由。这个减法把那段空隙一并算了进来，恒等式于是不依赖任何调用位置。

### 卡在哪一段要分开说

`UploadWait` 三档，因为三段的处置完全不同：

| | 等什么 | 单位 | 标记处 |
|---|---|---|---|
| `Peer` | 同批同内容的首个上传者传完**整件** | 件 | `LocalDedupResolver.ResolveAsync`、`RunState.ClaimCloudUpload` 的等待侧 |
| `Slot` | 全局上传闸门的额度 | **卷**（闸门按卷排队） | `VolumeUploadScope.RunAsync` |
| `Cloud` | 云端存在性/元数据 HEAD | 件 | `ResolveDataRefAsync` |

两处要点：

- **`BeginWait`/`EndWait` 强制发布，不受 200ms 节流约束。** 等待期间本调用方不再产生任何事件，而心跳只在有流在传时才跑（`Tick()` 里那条虚拟时钟短路）。零流在传时被节流吞掉的那一次发布没有任何后续补偿，界面就冻在旧快照上直到等待结束——那正是这一栏要说明的那几分钟。
- **闸门那一路先试 `gate.Wait(0)`。** 闸门空着时随手就拿到，标记它等于给每一卷平白加一次强制发布，一件大活上千卷就是上千次。只有真排上队才报。补 `ct.ThrowIfCancellationRequested()`：`Wait(0)` 不看取消令牌，而它替下来的 `WaitAsync(ct)` 是看的。

### 这一轮没做

`ActiveItems.length` 在界面上仍显示为 `N uploading`，单位是**卷**却和件数摆在同一行，加起来会超过总数（实测 `5,346 + 5 + 1,031 = 6,382 > 6,378`）。`X uploaded (N% of original)` 的分母是 `workDone` 而不是那一行左边的 `workTotal`，`of original` 这个标签指错了数。两处都已确认，本轮按要求未动。
