# 备份流水线化与流式压缩/校验（设计 + 计划）

> 2026-07-27 拟定。**尚未开工**，代码零改动。本文自足：换一个 session 直接照此推进即可。

## 1. 问题

当前一次备份严格串行走三段：`Scanning` → `Diffing`（全部跑完）→ `Plan` → `Uploading`。由此产生三笔可去掉的浪费：

1. **首次备份期间网络全程闲置**。46,624 个文件的哈希要走几小时，这几小时里一个字节都没上云。Plan 被当成全局屏障，但它其实不需要。
2. **每个新增文件被完整读两遍**：`BackupDiffer` 算 `FullHash` 一遍，7z 压缩时再读一遍（外加 head/tail 两小段）。
3. **检查（Content 级）把整个 pack 解压到磁盘再逐个算 hash**（`BackupChecker.cs:328`、`339`），临时空间与磁盘写全是白付的。

## 2. 已实测确认的事实

以下都在本机用镜像里同款 7-Zip 实测过，**不要在实施时重新怀疑**：

```bash
# stdin 流式压缩 与 密码 / 头加密 / 分卷 可以同时工作
7z a -si'data.bin' -pPW -mhe=on -v1m out.7z < big.bin        # exit 0，产出 .001/.002/.003

# 流式解压，内容与源文件逐字节一致
7z x -so -pPW out.7z.001 | sha256sum                          # hash 与源文件相同

# -si 的条目名保留完整相对路径，且能按该名精确取出单个成员
echo -n hello | 7z a -si'dir/sub/file.txt' -pPW -mhe=on nested.7z
7z l -slt -pPW nested.7z | grep ^Path                         # Path = dir/sub/file.txt
7z x -so -pPW nested.7z 'dir/sub/file.txt'                    # hello
```

**结论：流式改造与现有归档格式完全兼容**——归档内条目名仍是相对 `LocalRoot` 的完整路径，还原与检查的成员定位逻辑一个字都不用改。

**陷阱（必须在实现里防）**：`7z x -so <归档> <不存在的成员>` 输出为空且**退出码 0**。与本项目已经踩过的「7z 丢成员时用退出码 1 静默通过」是同一类坑。凡流式读取，一律自行核对读到的字节数与索引记录的 `Length`，以及算出的 hash，**不得以退出码作为通过依据**。

另一条来自代码的事实：`GroupingPlanner.Plan` 的三条分类判定只读 `Path` 与 `Length`（`Length >= SingleFileThresholdBytes` / `DontGroup` / `CrossDirGroup` 的路径匹配），`PlannedFile.FullHash` 仅用于生成 `data/{hash}` 的内容地址。**所以扫描一结束，全部文件的归类就已经确定，不需要等 Diffing。**

## 3. 明确不做的事

- **不引入 LZMA SDK 或任何新 NuGet 包。** LZMA SDK 只提供裸 LZMA 算法，不含 `.7z` 容器、不含 AES-256 + 头加密、不含分卷、不含多成员归档。改用它等于自研备份存档格式，并失去「任何人拿一份 7-Zip 就能打开这份备份」这条最后退路——对备份工具这不是锦上添花。CLI 已经提供了全部所需的流式能力（见 §2），没有理由付这个代价。
- **不改归档格式**，不改索引/信息文件格式。
- **pack（多小文件合并）不做流式压缩。** `-si` 一次只能接一个流，多成员合并用不上；而 pack 装的都是 <5 MB 的小文件，第二遍读大概率仍在 page cache 里，双读的实际代价小。

## 4. 分期

四期各自独立可上线，按顺序推进。每期结束都走完整流程：后端全量测试（Azurite 起着）→ 前端 build+lint（涉及时）→ 合并 main → 推送 → 跑 Docker workflow。

---

### 第 1 期：检查改为流式比对

**改什么**

- `IFileCompressor` 新增按成员流式读取的能力，例如
  `Task ExtractMemberToAsync(string firstVolumePath, string entryName, string? password, Stream destination, CancellationToken ct)`。
- `SevenZipCli` 需要一个**不把 stdout 缓冲成字符串**的运行模式：现在 `RunAsync` 用 `ReadToEndAsync`，流式读取必须直接暴露 `proc.StandardOutput.BaseStream`，否则一个大成员会被整个读进内存，比落盘还糟。新增 `RunStreamingAsync`，把 §2 那条取消时杀进程树的逻辑一并复用（不要复制一份）。
- `BackupChecker.cs:328`、`339` 两处 `ExtractAsync(firstVolume, extractDir, …)` 改为逐成员流式喂给 hasher，删掉 `extractDir` 的建立与清理。

**验收要点**

- 加密（`-mhe=on`）+ 分卷的归档，逐成员流式取出并比对通过。
- **成员不存在时必须报「校验失败」而不是「通过」**（针对 §2 的陷阱，专门写一个用例：伪造一份缺成员的归档，断言检查报告把它标为损坏）。
- 读到的字节数与索引记录的 `Length` 不符时同样判失败。

**收益**：省掉整包解压落盘与那份临时空间；Content 级检查在小磁盘的 NAS 上不再受临时区大小限制。

---

### 第 2 期：单文件 blob 的流式 hash + 压缩

针对 `≥ SingleFileThresholdBytes`（默认 5 MB）与命中 `DontGroup` 的文件——首次备份的字节大头。

**改什么**

- `SevenZipCli` 支持写 stdin（现在 `proc.StandardInput.Close()` 是立即关闭的）：把源流拷进 `proc.StandardInput.BaseStream`，拷完再关。写入侧的 `IOException`/取消必须原样传出，**不得吞**（否则一次半截的压缩会被当成成功）。
- `IFileCompressor` 新增流式压缩：源 `Stream` + 条目名 + 输出路径 + 密码 + 分卷 + storeOnly。条目名取 `file.Path`（相对 `LocalRoot`），与现有格式一致（§2 已验证）。
- 在同一遍读取里同时喂三个 hasher：`head`（前 N 字节）、`full`、`tail`（**后 N 字节需要环形缓冲**，这是流式化 `TailHashAsync` 唯一的实现难点）。
- `BackupOrchestrator.ProcessFileAsync`（约 400 行起）的 `UploadNewAsync` 改走流式路径；`raw` 直传分支（storeOnly && 无密码 && 不分卷）同样流式——边读边写临时文件边算三个 hash。

**去重顺序反转的处理**

现在是「先算 hash → 发现 `data/{hash}` 已存在 → 整个压缩+上传都跳过」。流式后要压完才知道名字，重复内容会白压一遍。折中：先读头部算 `head` hash 做预筛（本地权威索引里有 head 索引），**只有命中候选**才退回「先全 hash 再判去重」的老路；没有候选就直接流式压。首次备份没有任何候选，因此走的全是快路径。

**顺带删掉一类竞态**

现在 hash 在压缩之前算，压缩时文件可能已经变了，因此有一整套处理期重校验（变化的成员以稳定后的 hash 重新入队、反复变化达阈值降级为单文件）。流式之后**hash 算的就是压缩进去的那份字节**，二者不可能不一致——单文件 blob 这条路径上的重校验分支可以删除。**pack 路径的重校验必须保留**（那里仍是先 hash 后压）。

**验收要点**

- 流式产出的归档，还原后与源文件逐字节一致；加密、分卷、storeOnly、raw 直传四种组合都要覆盖。
- 压缩进行中源文件被改写：断言备份记录的 hash 与归档内容一致（这正是本期消掉的那类竞态，要有用例钉住）。
- 读不开的文件仍走 `postDiffUnreadable`，不产生 blob、不中断整轮备份。
- 取消：写 stdin 的过程中取消，进程树被杀、临时文件被清理（复用第 1 期已有的杀进程树逻辑）。

---

### 第 3 期：扫描后即分类 + 流水线化

**改什么**

- 从 `GroupingPlanner` 拆出 `Classify(IReadOnlyList<ScannedEntry>, PlanOptions)`：只依据 `Path`+`Length` 把全部扫描条目分成三类（单文件 / 跨目录组 / 按目录组），并确定每个组的**候选成员**。`Plan` 保留（装箱仍是纯函数），输入改为「某一组内确实变更的文件」。
- 单文件类：diff 判定变更后**立刻**走第 2 期的流式压缩上传，不等任何人。
- 分组类：**整组 diff 完再装箱处理**。这是必须的——组的最终成员要等 diff 完才知道：未变的不进包、`Unreadable` 的不进包、hash 算完发现内容没变（`MetadataOnly`）的也不进包。
  - 按目录组：扫描后该目录的成员已知，因此「该目录全部 diff 完 = 可以封箱」，时机完全确定。
  - 跨目录组：diff 按扫描顺序推进，而扫描结果已按 ordinal 路径序排好（`LocalFileScanner.cs:66`），与跨目录装箱的排序**是同一个序**，所以边 diff 边填包、填满即封，结果与现在逐字节一致。
- **背压解耦**：`staged` 满时阻塞的必须只是压缩侧，不得反压回 Diffing，否则磁盘读跟着停就白改了。改成生产者/消费者 + 有界队列。
- pack 编号沿用现有的 `Interlocked` 递增 `packCounter`，不引入新的确定性要求。

**进度模型（前后端都要动）**

`StageProgress` 现在是单阶段单值，`BackupConfigsPage` 也是一行一个阶段。并行后 Diffing 与 Uploading 会同时在跑，需要同时呈现两条。后端 `BackupProgress` 挂多个 `Detail`（或 `Detail` 列表），前端 `StageDetail` 渲染多条。**注意列宽**——用户明确提过 details 会把表格撑宽，多一条更要小心。

**还要想清楚的语义**

- Diffing 还在跑时上传失败，整轮怎么收场（现在没有这个状态组合）。
- 取消：两条流都要在下一个检查点收尾，忙碌锁在两条都结束后才释放。

**验收要点**

- 同一份数据，改造前后产出的**版本索引内容一致**（pack 编号可不同，但每个文件的存储位置与 hash 必须一一对应）。
- 20 万条目规模实测：对比改造前后的耗时与内存峰值。**流水线化会让压缩读与 diff 读并发**，机械盘上可能互相拖慢——若实测确实变慢，加一个 Settings 开关（走数据库设置，不加环境变量）让用户关掉重叠。
- 现有测试里对「先 diff 完再上传」的隐含依赖要逐个复核。

---

### 第 4 期（可选）：还原流式化

`RestoreOrchestrator.cs:364`、`379` 同样可以逐成员 `-so` 直接写到目标文件，省掉一次临时落盘。

**前提**：目标路径的安全检查（不得经符号链接写出还原目标）必须在开始写之前完成——流式不能成为绕过这道检查的借口。`DeadWeightCompactor.cs:140` 需要重新打包，保持落盘不变。

## 5. 风险清单

| 风险 | 说明 | 应对 |
| --- | --- | --- |
| 磁盘争用 | 流水线后压缩读与 diff 读并发，NAS 机械盘上两股读可能互相拖慢，净收益低于「两段时长取最大值」 | 第 3 期实测；必要时加 Settings 开关 |
| `-si` 下 7z 行为差异 | stdin 不可回退，退出码/警告的表现可能与文件输入不同 | 第 2 期在真实数据上观察一轮再合并 |
| 空输出 + 退出码 0 | 流式读取时成员不存在不报错 | 一律核对字节数与 hash，见 §2 |
| 隐含依赖 | 现有测试与代码对阶段串行的假设 | 第 3 期逐个复核 |

## 6. 预期收益

- **首次备份**：大文件从「读两遍 + 等全量 diff 跑完才开始传」变成「读一遍 + 立刻开始传」，磁盘读省约一半，网络不再全程闲置。
- **增量备份**：收益小（未变的文件本来就只花一次 `stat`），但不会变慢。
- **Content 级检查**：不再需要与整个 pack 等大的临时空间。
- **正确性**：单文件 blob 上「hash 与实际压入内容不一致」的整类竞态消失。
