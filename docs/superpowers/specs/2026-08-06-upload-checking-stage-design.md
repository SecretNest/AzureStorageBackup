# 上传阶段的 "checking" 档位

2026-08-06

## 起因

界面上出现过这样一屏，持续半分钟纹丝不动：

```
Uploading: 686 of 11,004 objects · 1 object starting upload · 10,317 objects queued · 3.8 MB/s · ~5d 3h left
160.3 GB / 3.0 TB original (5%) · 159.7 GB uploaded (100% of original) · +118.0 MB uploaded in unfinished objects · 100.0 MB ready to upload
```

这个快照是自洽的（`686 + 0 preparing + 10,317 queued + 1 uploading = 11,004`，正是 `StageProgress.cs` 里那个恒等式），它说的是：一条流都没在网线上、没人在等闸门、没人在等同批预约、没人占着压缩锁，只有一件活离开了压缩/暂存段。

而"半分钟不刷新"本身是一条硬信息：上传阶段的心跳只在有在途流时才跑，最后一条流 `EndItem` 之后心跳就停了，此后界面只有靠 `BeginItem`/`EndItem`/字节回调/`BeginPacking`/`BeginWait`/`Advance` 才会更新。半分钟一次都没更新，说明那件活正停在一段**既不推字节也不登记等待**的代码里。

这样的代码段有四处，它们全都被算进 `uploading` 那一栏，于是屏幕上一律写成 "starting upload"——而它们既没在 starting，也没在 upload。

## 改什么

从 `uploading` 里拆出一栏 `checking`，覆盖这四段：

| 位置 | 段 | 为什么慢 |
|---|---|---|
| `BackupOrchestrator.cs` `ProbeForDedupAsync` | 单文件去重预筛，整读算三段 hash | 一个几 GB 的文件在 NAS 上读一遍就是几十秒 |
| `BackupOrchestrator.cs` pack 装箱前 `TryStat` | 逐成员 stat | 一箱几百个成员 |
| `BackupOrchestrator.cs` pack 压缩后重校验 | 逐成员 stat + 变化成员整读算 FullHash | 同上，且撞上大成员要整读 |
| `BackupOrchestrator.cs` `ClearLeftoverVolumesAsync` | 加密多卷上传前列举云端清残留 | 网络往返，卷多时可观 |

**不覆盖** pack 上传后的 `RecordPack` + 逐成员 `LogFileAsync`：只在开了 verbose logging 时才慢，先不进这一栏。

## 为什么不并进 preparing

`preparing` 现在的定义是"占着全局压缩锁"，按锁的定义只会是 0 或 1，这条不变量在代码里被依赖着（`StageProgress.cs` 的 `BeginPacking` 注释）。把读盘活混进来就破了它，而且屏幕上分不清是在压还是在读盘。

## 设计

### 后端

`StageProgress` record 末尾追加 `int Checking = 0`（追加在末尾，不动现有位置参数的次序）。

`StageTracker` 加 `_inChecking`，配 `BeginChecking()` / `EndChecking()`，形状照抄 `BeginPacking`：进 `_gate`、`Interlocked` 增减、`PublishIfDue(force: false)`。200ms 节流照旧生效。

**不开心跳**：这几段期间没有任何新读数，每秒推一条一模一样的快照没有意义。进出各推一次就够——界面从 "1 object starting upload" 变成 "1 object checking files" 再变回去，卡在哪一段就说得出来了。

四处调用点各套一层 `try/finally`。`finally` 是必须的而不是防御性习惯：`BeginPacking` 在这个项目里已经栽过一次（加了没配对，那一栏会在余下的运行里卡在虚高的数字上），注释就写在 `StagingArea.StageAsync` 里。

`ProbeForDedupAsync` 与 `ClearLeftoverVolumesAsync` 需要多收一个 `StageTracker` 参数；两个调用点都已经有 `uploadTracker` 在手。`ClearLeftoverVolumesAsync` 的登记放在方法内的早退 `return` 之后——不加密或单卷时它什么都不做，不该在屏幕上闪一下。

### 恒等式

这四段都在 `_inStaging == 0` 时跑，所以 `checking ⊆ uploading`，`processed + preparing + queued + uploading ≡ total` 不受影响。拆的是显示，不是账。

### 前端

`StageProgress` 接口加 `checking: number`（后端 record 直接序列化，没有中间 DTO）。

`stalled` 改成 `max(0, uploading - waitingOnPeer - checking)`——这两栏是从同一个 `uploading` 里拆出来的，不减就会重复计数。

时间轴位置（那一行按逆时间轴排，越接近"字节已经上了网线"的排越前）：

```
… waiting for an upload slot · N objects starting upload · N objects checking files · N preparing · N queued
```

`checking` 排在 `starting upload` 之后、`preparing` 之前。

措辞 **"N objects checking files"**，不写成 "in checking files"：整行每一项都是 `N objects <现在分词>` 的形状（`starting upload`、`downloading`、`waiting for an upload slot`、`preparing`），插进一个 `in` 会成为整行唯一的介词结构；而 `in` + 动名词在英文里偏公文腔。语义上 "1 object is checking files" 与已有的 "1 object is starting upload" 是同一种拟人，屏幕上已经统一了。

云端清残留那一段严格说查的是云上的卷而不是本地文件，措辞上归到同一个词里——单独给它一栏不值当，一个词能说清"这件活正在核对，不在传"。

## 测试

- `StageProgressTests`：计数增减、发布时机、异常路径不泄漏（`finally` 配对）
- `BackupProgressDetailTests`：字段透出到 detail
- 前端：`checking > 0` 时渲染出这一栏，且 `starting upload` 相应减掉

## 不做

- 不给 checking 期间开心跳（没有新读数可报）
- 不在 checking 期间调 `Touch` 报当前文件名：上传阶段是多线程的，`_current` 只有一个，几条线程会互相覆盖
- 不覆盖上传后的 `RecordPack` + `LogFileAsync`
