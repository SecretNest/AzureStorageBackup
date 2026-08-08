# unfinished 字节的归属，与进度两行合一（2026-08-09）

## 现场

一轮 3 TB 备份（11,004 件），屏幕上几分钟纹丝不动：

```
Uploading: 6,636 of 11,004 objects · nothing on the wire right now · 1 object starting upload · 1 object preparing · 4,366 objects queued · 3.2 MB/s · ~1d 10h left
1.8 TB / 3.0 TB original (61%) · 1.8 TB uploaded (100% of original) · +2.0 GB uploaded in unfinished objects · 100.0 MB ready to upload
```

件数账是平的（`6,636 + 1 + 1 + 4,366 = 11,004`），可字节这一侧读不通。最自然的读法是"一件活传了 2.0 GB，还剩最后 100 MB 卡住了"——而这个读法在代码里根本没有对应物：一族卷的上传循环里，卷与卷之间不做任何检查（拿闸门额度 → 一次条件 `PUT` → 删本地文件），所有的核对都在**第一卷起飞之前**。

追下去发现三个独立的毛病，各修各的。

## 一、`unfinished` 是个只加的标量，失败路径上抵不平

原来的账：`EndItem` 每卷加卷的标称大小，`SetTransferred` 在件级销账时按 `state.UploadedBytes` 的增量整体减。正常路径上两边精确抵消（加的和减的都是 `FileInfo.Length`）。

**失败路径上不抵**：一族卷传掉几卷之后整件抛出，那几卷已经加了进去，而 `state.AddUploaded` 在 `VolumeBlobIO.UploadAsync` **之后**才执行——压根没跑。重试成功后只按新的那一次扣一遍，第一次的量就此永久留在这一栏里。`Math.Max(0, …)` 只挡负数，挡不住这个方向。

实测残留 2.0 GB。旧版本的重试单位是**一整个 pack 池**（按组重试是 `b5d2fc2`），一次抖动退回第 1 组，攒得更快。

### 决策：换成按 blobRef 分条的账本

`Dictionary<string, UnfinishedFamily>`，每条有明确的一生：

| 动作 | 时机 | 做什么 |
|---|---|---|
| `BeginUpload(owner)` | `UploadAsync` 之前 | 开条。**开条即重置**——重试走同一个 key，上一次的量当场抹掉 |
| `EndItem(volume)` | 每卷传完 | 加到 `flow.Owner` 那一条名下 |
| `ConfirmUpload(owner)` | `state.AddUploaded` 之后 | 标记云端已确认整族 |
| `SetTransferred(total)` | 件级销账 | 把已确认的条目**整条删掉**（不是减一个数） |
| `EndUpload(owner)` | `finally` | 没确认过的就地作废 |

**key 取 blobRef，不取 ticket。** 票是每次 `UploadAsync` 现领的（`VolumeBlobIO.cs` 里 `scope.NextTicket()`），重试就换一张，拿它当键就找不回上一次那条。blobRef 两条路上都跨重试不变：单文件的 ref 是内容 hash，pack 的包号在重试**之外**领（那本身是为了让重压的产出盖回同一族卷）。

**`ConfirmUpload` 与 `EndUpload` 分开**，而不是在 `finally` 里一律清账：整族传完到件级销账之间还隔着写索引、写 journal 那一截，此刻清掉的话这批字节会在两栏之间凭空消失一会儿，而它们确实已经在云上了。分开之后失败与成功用同一句 `finally`，区别只在有没有被确认过。

`owner` 为 null 的卷（下载侧、不走上传闸门的修复路径）不进这本账——那些路径本来就走 `_transferredByItem == false` 的分支。上传侧漏传 owner 时宁可少报一卷，也不造一条没人认领、没人删得掉的账：那正是原来那个漂移的形状。

## 二、还在核对的归档被算成 "ready to upload"

`_stagedBytes` 在 `MoveToStaged` 之后立刻记账——**这是对的**，那笔账是背压闸门，要的是"盘上此刻占了多少"，晚记一秒就可能撑爆临时盘。

但那一刻归档还要过压缩后重校验（逐成员 `stat`，变过的整读重算 hash），多卷还要先清云端残留卷。而重校验判出任何一个成员在压缩期间变过，**这份归档会被整个丢掉重压**（`changed.Count > 0` 那一支），一个字节都传不出去。管它叫 "ready to upload" 是过度承诺。

### 决策：`BeginChecking`/`EndChecking` 带上归档字节

一个计数器同时当背压闸门和界面读数，而两者对"什么时候开始算"的要求不同——背压必须在落盘那一刻记，显示应该等到核对过。拆的办法是复用已有的 `checking` 那一栏（`099dc5f` 加的件数侧），给它加字节侧：

- `BeginChecking(bytes)` / `EndChecking(bytes)` 累加到 `_checkingBytes`
- 快照里 `staged` 减掉它，另报 `CheckingBytes`
- 四个调用点里只有两个手上有归档（压缩后重校验、清云端残留卷），传实际字节；另外两个（去重预筛整读源文件、压缩前逐成员 `stat`）那时池子里一个字节都没有，传 0

三个减法互不重叠：核对整段发生在第一卷起飞之前，所以一份归档不可能同时被"在途已传"和"正在核对"两个减法碰到。

## 三、两行各排各的，没有任何东西说得出先后

件数一行、字节一行，各按各的逻辑排。于是屏幕上没有任何东西把 `+2.0 GB` 与 `100 MB ready to upload` 放在同一条轴上比较，而它们在流水线上隔着**整整一段上传**。

### 决策：合成一条逆时间轴，件数与字节交织

第一行只留**已经落定**的（原始字节分数与完成度、uploaded），第二行是完整流水线，从离完成最近的往回排：

```
queued → waiting for the archive slot → preparing → [归档落盘]
       → checking files → ready to upload → starting upload
       → waiting on peer/slot → uploading → on the cloud → 销账进第一行
```

界线是"这个数还会不会变卦"：上面的不会了，下面的都还可能重来。前缀 `In flight:` 是必要的——没有它，第一行的 `1.7 TB uploaded` 与第二行的 `+3.4 GB on the cloud` 摆在一起又成了一道谜题（两个都是已经在云上的字节，凭什么分两行）。

措辞上 `+X uploaded in unfinished objects` 缩成 `+X on the cloud`：整行已经以 "In flight" 起头，再说一遍 unfinished 是重复。

拼装逻辑提到 `frontend/src/lib/stageLines.ts`。这两行的全部难点在**顺序**与**措辞**，而字符串一旦进了 JSX 就再没有地方能断言它——顺序错了不会有任何东西报错，只会让屏幕上重新长出"这两个数到底谁先谁后"这种问不完的问题。

## 不做什么

- **不改 `_stagedBytes` 的记账时机。** 那是背压闸门，必须在落盘那一刻记。这里改的只是显示口径。
- **不给 `unfinished` 加持久化。** `uploadTracker` 是 per-run 的，这本账随运行结束而消失，不跨轮。
- **不动重试单位。** 按组重试已经在 `b5d2fc2` 做过了；这本账不依赖重试粒度，粒度只影响单次作废的规模。

## 影响

纯显示口径，不碰云上数据、索引、去重与背压。件数那条恒等式一个字都不用改（`checking` 仍是 `uploading` 的细分）。

## 测试

- `StageByteBreakdownTests`：作废的尝试不留残留（RED 报 `2000`）、一族作废不连累并发的另一族（RED 报 `1500`）、核对中的字节不算 ready、归档还不存在的核对不动字节
- `stageLines.test.ts`：整条时间轴的顺序、零值省略、核对字节与 ready 互不重叠、网线空闲时说出原因、第一行只含已落定、下载方向的措辞
