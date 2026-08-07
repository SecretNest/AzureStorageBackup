# 同一轮备份内的跨箱打包成员去重

2026-08-07

## 起因

单文件 blob（≥ `SingleFileThresholdBytes`，默认 5 MB）一直是内容寻址的：同一份内容在一次运行内只存一次，后到者连压都不压（`PlaceBlobAsync` 的 `ProbeForDedupAsync` 一命中就返回），同批同内容还有预约表协调（`LocalDedupResolver.ResolveAsync` 的 `_run`）。

打包的小文件没有这一层。它有两条既得的去重：

- **同一箱内**：7z 的 solid 归档字典跨成员匹配，重复内容几乎不占字节。
- **跨版本**：`LocalDedupResolver.TryFindPackMember` 查既有保留版本的索引，命中就让新条目直接指过去，不压、不传、不装箱。

缺的是**同一轮之内、跨箱**那一段。`_packMembers` 只在 `LocalDedupResolver.Build` 里从传入的历史 `VersionIndex` 构建，本轮新封的箱不进这张表。于是首次备份、或一次新增大量重复小文件的备份里，同内容一旦被分进不同的箱，就实打实地各存一份——不同箱之间压缩不共享字典，省不下来。

## 判据

四项严格相等：`fullHash` + `length` + `headHash` + `tailHash`。任一项不同或缺失即不参与。

与 `TryFindPackMember` 完全一致，理由照抄那里的注释：判据要么是四项要么不是，为兼容开个口子等于在最不该含糊的地方（"这份内容是不是同一份"）留一档说不清的语义。

四项对 pack 候选一定齐全——`Added`/`Modified` 都由 `ContentIdentityAsync` 一遍读算出（`BackupDiffer.cs:230`、`258`、`273`）。缺项的只有未变更条目，而它们根本不进这条路。

## 设计

### 新增 `PackAliasTable`

`Services/PackAliasTable.cs`。不塞进 `LocalDedupResolver`：后者持有的是只读的 prior 表加运行内 blob 预约，语义已经满了；`BackupOrchestrator.cs` 也已经 1600 行开外。单独一个小类可以脱离 Azurite 做纯单测。

```csharp
/// 本轮内、跨箱的打包成员去重。diff 单线程独占，不加锁
/// （与 dirPending/crossPending 同一条约束）。
public sealed class PackAliasTable
{
    private readonly Dictionary<string, string> _leaderByContent;          // 四项内容身份 → leader 路径
    private readonly Dictionary<string, List<PlannedAlias>> _aliasesByLeader;
}

public sealed record PlannedAlias(
    string Path, long Length, string FullHash, string HeadHash, string TailHash);
```

内容身份的拼法从 `LocalDedupResolver.ContentKey` 提成 `public static` 共用，保证两条路同源。

### 装箱决定点

`BackupOrchestrator.cs:567-580`，现有 `TryFindPackMember` 那一段之后多一档：

```
既有 pack 命中（跨版本，现有逻辑）  → 直接写 StorageRef，file = null
    ↓ 未命中
本轮别名表命中                      → 登记为别名，file = null，不入箱
    ↓ 未命中
登记自己为 leader                   → 照旧入箱
```

别名分支与既有 pack 命中分支**收场完全一样**（`file = null`），走的是"这一条没有变更"那条既有路径：目录计数照常递减、封箱时机不受影响、不占上传槽位、不必销账。

因此消费者侧——`ProcessPackAsync`、`RecordPack`、`CompressPackTolerantAsync`、`UploadStagedPackAsync`——**一行都不改**。

顺序上不会与跨版本去重冲突：leader 若命中既有 pack，后来的同内容文件用同一张 `_packMembers`、同一套四项判据，也会命中第一档，根本不会走到别名表。所以进别名表的 leader 一定是"本轮新装箱的"。

空文件在 `IsEmptyFile` 那里就已经 `file = null`，进不到这段；`klass.Category != FileCategory.SingleFile` 保证只对成组的做。

### 收尾回填

插入点在 `BackupOrchestrator.cs:714-721`，`await Task.WhenAll(consumers)` 之后、`BuildEntries` 之前。那时一切都停了，`storageByPath` / `overrides` / `postDiffUnreadable` 全部落定，判断可以是纯同步的。

对每个 leader：

```
storageByPath[leader] 是 { Kind: "pack" }
  且 leader ∉ overrides
  且 leader ∉ postDiffUnreadable
      → 把这个 StorageRef 整个复制给它的每个别名
否则
      → 这些别名全部判为悬空
```

三个否决条件对应 leader 走岔的三条真实路径：

| 条件 | 含义 |
|---|---|
| `overrides[leader]` 有值 | 内容在压缩窗口里变过（`ProcessPackAsync:1364` 写下了新 hash）。**别名的内容已经不等于 leader 的内容了**——这是本设计的正确性红线。 |
| `postDiffUnreadable[leader]` 有值 | leader 第二次也读不开，就地降级，不产生任何 blob。 |
| `storageByPath[leader]` 不是 pack 或缺失 | 变大到超阈值改走单文件 blob（`newLen >= threshold` 那一支），或整组一起读不开。 |

判断只看**最终态**，不追踪中间过程。所以不存在"diff 线程刚挂上一个别名、消费者已经把 leader 判死"的竞态——把回填放到收尾的全部意义就在这里。也因此不需要任何新的并发原语。

### 悬空别名的重跑

leader 走岔时，别名文件自己通常好好的，不该被连累。把它们重新跑一遍：第一个自然成为新 leader。

此时 `uploadGate`、`stagingLease`、`uploadScope` 都还在作用域内没释放，直接复用：

```csharp
// 按压法分两组——一箱只能有一种压法，这一刀必须在装箱之前落（与 crossPending 同一个理由）。
// storeOnly 按**别名自己的路径**算，与 OnChangeAsync 里装箱时同一个写法：规则按路径匹配，
// 别名和 leader 分属不同目录，压法完全可能不同。
// GroupingPlanner.SplitByCompressibility 是 private，不为这一处放开它——
// 那个纯函数还要保证"组内仍是 ordinal 路径序"才能与规划器对上，而这里是收尾的零散重跑，
// 用不着那条约束，照 OnChangeAsync 的两行分流即可。
foreach (var (storeOnly, pool) in orphans.ToLookup(
             a => packOptions.DontCompress?.MatchesFileOrAncestorDir(a.Path) ?? false))
    await ProcessPackAsync(request, pool, storeOnly, /* …原样透传… */);
```

`ProcessPackAsync` 自带 `GroupIsFull` 分组（不会撑爆 argv）、自带 `changed` 处理、自带 `queue.Add` 回炉，不需要额外机制。别名文件自己在这期间被改写或读不开，同样由它现成的路径接住。

**悬空别名之间不再互相去重**，各存各的。这条路要求 leader 恰好在压缩窗口内被改写或读不开，本来就罕见；罕见路径上多存几份，换收尾逻辑保持线性、好读、好测。

### 执行顺序

除装箱决定点（`:567-580` 多一档）之外，这是整个设计里唯一要动的既有代码：

```
  await Task.WhenAll(consumers);
+ 回填 → 悬空重跑 → 若跑过，重新 uploadTracker.SetTotal(totalItems) + reporter.Settle(totalItems)
  uploadTracker.Complete();      ← 必须留在重跑之后
  var total = totalItems; var uploaded = uploadedItems;
  BuildEntries(...)
```

`SetTotal` 可以重复调（`StageProgress.cs:335` 就是赋值加强制发布），所以重跑涨出来的槽位补得进去。

`RecordUnreadableWarningsAsync` 留在原位（consumers join 之前）不动：它的入参是 `scan` 和 `diff`，本来就不含 `postDiffUnreadable`，重跑不影响它。

### 进度计数的配对

这个项目在"恰好一次"上栽过几次（`onItem` 那一大段注释），单独说清：

- 别名**不** `Enqueue`、**不** `ReportItem`。两边都是零，配对天然平衡——它确实没有对应任何一件活。
- 悬空重跑走正常配对（`ProcessPackAsync` 内部的 `onItem(bytes)` 保证"每组恰好一次"），只是 total 的分母要在跑完后重新发布一次。
- 界面表现：无悬空时与今天**完全一致**；有悬空时收尾阶段分母向上跳一次然后走完。比"100% 却还在跑"诚实。

## 对既有备份的只读保证

1. 别名表只从**本轮** `FileChange` 构建，从不读写 previous index。老索引一个字节都不动。
2. 写下的引用形状是 `{Kind="pack", Ref=packId, EntryName=leaderPath}`，与 `RecordPack` 从前写的、以及跨版本去重写的**逐字节相同**。索引 schema 不变，无新字段。
3. 未变更条目一律沿用 `CarriedStorage`（`BackupOrchestrator.cs:1620`），根本不进这条路。**既有引用一条都不解除**，所以既有包的 `deadBytes` 分毫不动。
4. 消费方全部不改，逐处核实过：
   - `RetentionCleaner.cs:113` 按 `Storage.Ref` 收集存活包；`:120` 按 `EntryName` 归组存活成员——注释里那句"同内容不同路径去重成同 fullHash 但仍是两个成员，不可用 hash 作 key"正是为这个形状写的。
   - `DeadWeightCompactor.cs:44-48` 的 `liveBytes` 按 EntryName 去重，`OriginalBytes` 只算实际成员（`RecordPack` 的 `members.Sum`）。两边口径一致，`liveBytes ≤ OriginalBytes` 恒成立，`deadBytes` 算不出负数，压实阈值不会被虚假触发。
   - `RestoreOrchestrator.cs:485` 各条目从 `extractDir/EntryName` 各自复制到自己的 Path，源文件不动。
   - `BackupChecker.cs:481` 逐条查 `actual[entryName]`，两条 entry 查同一项、内容相同都过；`CompletedSegments == files.Count` 里的 `files` 是归档列举出的成员，别名不进去。
   - `BackupRepairer.cs:266` 按 `(packId, EntryName)` 收集，同上。
5. **同一版本索引内两条条目指向同一 `(packId, EntryName)`** 这个形状，跨版本去重上线时就已经能产生（v2 里新文件指向 v1 包的成员，而那个成员自己的条目在 v2 里沿用旧值）。本设计不引入新形状，只是让它变常见。
6. **不追溯**。历史里已有的重复不合并，各自活到自己的版本退役为止。合并它们需要重写老包，那是对已备份数据的破坏性操作，不做。

### 别名让成员更难死，不是更容易死

`liveByPack[packId][entryName]` 按 EntryName 归组，只要**任何一条**引用它的路径还在，该成员就算存活。多一条别名等于多一份"钉住"。`LocalDedupResolver.cs:101-103` 表明这是有意的：

> 取最先遇到的（版本从旧到新传入）：引用聚到老包上，它就更不容易在死重压实里被重写。

推论：**leader 那个路径的文件被删掉之后，别名仍然要能还原**。那时 `liveByPack` 里那个 entryName 由别名条目独自提供，包不删、成员不死、`extractDir/leaderEntryName` 照样取得到。这条链每一环都核过，但它是本特性最容易被将来某次重构悄悄踩坏的地方，必须有测试钉死（见 T3）。

## 测试

### 纯单测（`PackAliasTableTests`，不需要 Azurite）

- 四项全等才合并；任一项不同即不合并；任一项缺失即不参与
- 同一 leader 挂多个别名的登记与查询
- 回填判定：三个否决条件各自单独生效

### 集成测试（沿用 `PackMemberDedupTests` 的搭法，需要 Azurite）

| # | 钉住什么 |
|---|---|
| T1 | 跨箱生效：同内容小文件分散到会落进不同箱的位置 → 归档里只有一个成员，第二条 entry 指向 leader 的 EntryName |
| T2 | 两条路径都还原出正确内容，各自的 mtime/权限独立正确 |
| **T3** | **leader 路径的文件被删除后别名仍能还原**：v1 有 A + B(别名)，v2 删掉 A，保留清理跑完 → v2 还原 B 成功 |
| T4 | 保留清理不错删：v1 退役、v2 仍引用该包 → 包保留 |
| T5 | 死重压实：`deadBytes` 不为负、阈值不被虚假触发；压实重写包之后别名仍可还原 |
| **T6** | **悬空回炉**：leader 在压缩窗口内被改写 → 别名**不**指向 leader 的新内容，而是自己被正确备份（逐字节校验还原结果） |
| T7 | Check 通过：备份完跑一次检查，两条 entry 都判健康 |
| T8 | 进度计数收敛：无悬空时 `totalItems` / `uploadedItems` 的行为与今天完全一致 |

T3 和 T6 是这个特性的两根支柱——一根管"别名不会被错删"，一根管"别名不会指向错内容"。

Azurite 必须起，否则这批集成测试会静默跳过（`npx azurite --skipApiVersionCheck`）。

## 不做

- 不追溯合并历史版本里已有的重复。
- 不为未变更条目补算 `tailHash`（`BackupDiffer.cs:47-49` 已经算过这笔账：50 万小文件在 NAS 机械盘上接近一小时，换来的加固边际价值极小）。
- 不给悬空别名之间再做一层去重。
- 不改单文件 blob 那条路——它本来就没有这个缺口。
