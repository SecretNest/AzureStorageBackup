# M4 — 备份引擎设计

> 对应 PRD 第 3 章、backup-feature-design.md。本文件是 M4 的详细设计，供 review。
> 已确认前提：密码=加密（单开关）、symlink 默认跳过、变更判定用 hash、Tier 无 Smart（Hot/Cool/Cold[/Archive]）。

## 1. 核心概念

- **备份（Backup）**：一个 container 内的一份备份，由 `(Account, Container)` 标识（每 container 最多一个，PRD 1.3）。含配置、多个版本、索引、数据。
- **版本（Version）**：每次执行备份产生一个不可变版本。版本引用文件清单（第二级索引）。
- **信息记录文件**：container 内的权威元数据 blob，保存配置 + 版本列表 + 分组包元数据。跨设备恢复的唯一真相源（PRD 1.5）。平时不读取（PRD 1.7），仅导入/检查时读。
- **本地缓存**：本地 SQLite 缓存备份状态（上一版本索引、pack 元数据），加速对比，避免每次读 container。恢复时从信息文件重建。

> **实现更新（以代码为准，2026-07）**：
> - hash 用 **XxHash128**（`xxh128:` 前缀），本文档 §2/§3.2 中出现的 `sha256:` 示例均按 §13.2 读作 `xxh128:`。
> - 信息文件与第二级索引为**紧凑二进制序列化**（§13.4，`IndexSerializer` BinaryWriter），blob 名仍字面保留 `.json[.enc]` 后缀；§3.1/§3.2 的 JSON 示例仅描述**逻辑结构**，非磁盘字节格式。
> - data blob 与 pack 均可**分卷**：多卷时实际 blob 名为 `data/{hash}.001/.002…`、`packs/{id}.7z.001/.002…`（单卷用基名），读写/清理经 `VolumeBlobIO` 按分卷族处理。
> - **分卷数记入版本文件**（§7）：单文件 blob 记在索引条目 `StorageRef.Volumes`，pack 记在 `PackInfo.Volumes`（压实会改，随信息文件更新，不改版本索引）。检查据此核验全部分卷存在，检测 Azure 端误删/丢失；多卷**倒序上传**（.001 最后写）作为「整族齐全」提交标记。
> - **hash 碰撞避让**：data blob 元数据存原始长度 + headHash；去重时须元数据一致才跳过，否则判为碰撞、改用备用名 `data/{hash}~1/~2…` 并报 UnrecoverableError。索引 `StorageRef.Ref` 记实际名，还原/检查/清理据此。（残余「同 hash+同长度+同 headHash」碰撞概率可忽略，不再下载内容比对。）
> - **加密备份密钥化寻址（防指纹识别）**：加密备份的 data blob 名改为 `data/{HMAC(key, fullHash)[:16]}`（key = HKDF(password, `BackupMeta.KdfSalt`)），碰撞元数据改为不透明 `v = HMAC(key, fullHash|len|head)`，不泄露长度/头部。未授权者即使能列 container 也无法用公开 hash 反推「是否备份过某文件」。去重照常（同内容→同地址）。非加密备份仍明文寻址。仅编排器创建 blob 时用密钥；还原/检查/清理用索引里记录的实际地址。残余泄露：blob 数量与大小。见 `BlobAddressScheme`。
> - **原始文件直传（PRD 3.3.2，`StorageRef.Raw`）**：单文件 data blob 若**命中不压缩列表(store-only) + 无密码 + 单卷内(≤VolumeBytes)**，则直接把原文件拷到待上传区、上传**原始字节**（不走 7z 封装），`StorageRef.Raw=true`；raw 属性同时记入 blob 元数据(`raw=1`)，去重时以既有 blob 为准（同内容不同 don't-compress 状态也正确）。还原直接写回、深度检查直接重算 hash，均不解压。因单文件 blob 内容寻址去重可被多路径引用，还原/检查对同一 blob 复制/校验给**每个**引用条目。加密（keyed）备份永不 raw。见 `BackupOrchestrator.CopyRawAsync`。
> - **计划任务遇忙碌跳过（`BackupBusyTracker`）**：备份按 账户/container 标识忙碌态；备份/还原/检查任一操作期间标记忙碌。计划任务（`TaskDispatcher`）目标忙碌 → 记 Warning 报警并跳过该目标，不打断在执行的任务；HTTP 备份/还原忙碌则拒绝并发，手动检查忙碌返回 409。
> - **去重碰撞加固：三段 hash**。data blob 碰撞元数据由「长度 + 头 4KB hash」增加「尾 4KB hash」（`IFileHasher.TailHashAsync`）。误去重需 fullHash(128 位全文件)+长度+头+尾 同时相同，实际不可能——无需逐字节全文件比对。`TailHash` 一并存入索引条目（`IndexEntry.TailHash`，序列化 format 2）。加密备份的不透明校验 `v` 也纳入尾部。
> - **分级检查 + 本地修复 + 不可恢复标记 + 还原替代**（PRD 2.3 扩展）：
>   - **检查双轴**（`CheckOptions`，替换旧 `deep` 布尔）：云端 `CloudCheckLevel`（不查 / 元数据比对本地缓存 / 存在+尺寸（默认，HEAD 比对索引里存的 `VolumeSizes`，免下载识破截断/错包）/ 内容（下载重算 hash，Archive 可选活化 tier））；本地 `LocalCheckLevel`（不查 / 存在+尺寸+权限 / 内容 hash（默认，＝可从本地修复的判据））。结果 `CheckReport` 按文件给 `CloudState`/`LocalState`/`Repairable`。计划任务的 Check 也带这两级（`ScheduledTask.Check*Level`）。
>   - **每分卷尺寸入索引**：`StorageRef.VolumeSizes` / `PackInfo.VolumeSizes`（序列化 format 3/2），供上面「存在+尺寸」级。
>   - **从本地修复**（`BackupRepairer`，显式动作）：对云端坏掉的 blob，从本地文件（hash 校验）重压并**完整替换**（先删旧全部分卷）；单文件 blob 更新所有引用版本的尺寸/卷数（去重共享），pack 按所有版本存活成员整体重压。修不了（本地删/hash 变）→ 该文件在相关版本 `VersionIndex.UnrecoverablePaths` 标记不可恢复。归档内 mtime 不重要（展示用索引元数据，还原重设时间/权限）。
>   - **还原替代**（`RestoreRequest.Substitutions` path→版本）：不可恢复文件由用户逐个（可批量、就近优先）选另一版本替代；未指定的不可恢复文件跳过（不报错）。候选由 `GET /file-versions?path=` 给出。
> - **单文件 blob 去重纯本地化（自建备份零云端读，`LocalDedupResolver`）**：自建备份的本地缓存已含每个 blob 的内容身份（fullHash+长度+头+尾）与存储信息（ref/raw/分卷数），故备份时**不发云端 HEAD**判断去重/碰撞：
>   - 跨版本：从保留版本索引建「内容身份 → 既有 blob」映射直接命中。
>   - 同一次备份内：运行内预约表（每 ref 一个 `TaskCompletionSource`）协调——同内容后到者等首个上传者完成，拿到相同 (ref, raw, 分卷数)（顺带修一个潜在竞态：同内容但不压缩设置不同的两文件曾各写各的 raw 标志、还原时损坏）；不同内容撞同址避让到 …~N；上传失败则令等待者一并失败，绝不去重到未成功写入的 blob。
>   - **权威判定**：本地有状态（`TrackedInfoStore.HasLocalAsync`）或全新无版本时启用本地解析；**导入未同步**的备份回退到云端存在性检查（`ResolveDataRefAsync`）。
>   - **行为取舍**（与「尽量不读云端」一致）：备份信任本地索引＝云端真相，不再自动重传被外部误删的 blob——该漂移交由**检查(Check)**发现。

## 2. 存储布局（container 内 blob 组织）

```
azurestoragebackup.index.json          # 信息记录文件（非加密版；内容为二进制，见上）
azurestoragebackup.index.json.enc      # 加密版（与非加密二选一；两者都在用非加密，PRD 1.6）
indexes/v{N}.json[.enc]                # 第二级：每版本一个文件级索引（二进制）
data/{xxh128}[.001,.002,...]           # 数据 blob，按内容哈希寻址（天然去重）；大 blob 分卷
packs/{packId}.7z[.001,.002,...]       # 分组/分卷 7z 包
```

**两级索引**（PRD 特别说明 B）：
- 第一级 = 信息记录文件里的 `versions[]`（版本号、时间、第二级索引引用、统计）——小，每次备份只追加+更新。
- 第二级 = `indexes/v{N}.json`——该版本全部文件清单。新版本只写新的第二级，不改旧的（除非分组死重导致，见 §6）。

避免单一巨型索引反复重写；索引文件本身也压缩、加密（PRD 特别说明 B）。

## 3. 数据模型

### 3.1 信息记录文件 schema（草案）
```jsonc
{
  "schemaVersion": 1,
  "backup": {
    "name": "...", "description": "...",
    "sourceRootHint": "/data/photos",     // 仅提示；恢复时用户重新指定
    "encrypted": true,
    "createdAt": "...",
    "settings": { /* 本备份生效的设置（默认值的解析结果快照） */ }
  },
  "versions": [
    { "version": 1, "createdAt": "...", "indexBlob": "indexes/v1.json.enc",
      "stats": { "files": 1200, "bytes": 3.4e9, "changedFiles": 12, "changedBytes": 5e7 } }
  ],
  "packs": {
    "p0001": { "blob": "packs/p0001.7z", "members": ["sha_a","sha_b"], "originalBytes": 900000, "deadBytes": 0 }
  }
}
```

### 3.2 第二级索引 schema（indexes/v{N}.json）
```jsonc
{
  "version": 1,
  "entries": [
    { "path": "sub/a.txt", "kind": "file",
      "length": 123, "mtime": "...", "permissions": "0644",
      "headHash": "sha256:...", "fullHash": "sha256:...",
      "storage": { "kind": "blob", "ref": "data/{fullHash}" } },
    { "path": "sub/small.txt", "kind": "file", "length": 40,
      "headHash": "...", "fullHash": "...",
      "storage": { "kind": "pack", "ref": "p0001", "entryName": "sub/small.txt" } }
  ],
  "emptyDirs": ["sub/empty1", "sub/empty2"]   // 空文件夹（备份需包含，还原需创建）
}
```
- 权限、mtime、length、hash 均记录（PRD 特别说明 A）。
- symlink：默认跳过；若用户选包含，则 `kind:"symlink"` + `target` 字段。

### 3.3 本地状态（SQLite，新增表）

> **已实现（2026-07，`CachedVersionIndex` 表 + `LocalIndexCache`）**：
> - 缓存**版本索引**（大）：按 (AccountId, Container, Version) 存序列化索引字节。版本索引写入即不可变，故命中即有效；
>   `IdentityTicks`=备份创建时间戳，用于识别 container 删后重建（版本号复用但内容不同）→ 不匹配即失效重下。
> - **信息文件本地权威（`LocalBackupState` 表 + `TrackedInfoStore`）**：信息文件也不再每次从云端读——它可能落 Cold、读内容有取回费。本地存序列化副本 + 云端 ETag；备份写入用 `If-Match` 乐观并发检测外部改动（多机/container 重建），冲突则清本地状态并报错、下次重同步。仅本地无副本时（首次/导入前）才读云端并回填。**净效果：除导入/深度检查/重 pack 外，备份对信息文件与版本索引都零云端读。**（单用户假设：不处理真正的并发多写；ETag 把该罕见情形变成干净的中止+重同步而非丢历史。）
> - 命中/回填：编排器 diff 读上一版本索引走缓存、写完新版本回填缓存；保留清理读保留版本索引走缓存、退役版本从缓存移除；
>   **导入时下载全部版本索引入缓存**——之后（除导入/深度检查/重 pack 外）备份/清理平时不再下载云端版本索引与数据。
> - 缓存存的是**解密后**的索引元数据（路径/hash）；与密钥化寻址的威胁模型一致（攻击者只有云端 list 权限，本机是可信端、源文件本就在此）。
>
> 原设计草案（未采用其字段布局，仅保留意图参考）：
> - `LocalBackupState`：AccountId, ContainerName, LastVersion, LastIndexCacheJson, UpdatedAt。
> - `PackState`：packId, members, originalBytes, deadBytes。
> - 本地缓存是优化；权威在 container 信息文件。

## 4. 备份流程（状态机）

```
Scan → Diff → Plan(group/dedup) → Compress → Upload → WriteIndex → Finalize → Cleanup
```

1. **Scan**：遍历本地根，应用 gitignore 忽略规则（§5），产出条目（path/kind/length/mtime/permissions）。收集空文件夹。
2. **Diff**（PRD 特别说明 A）：对比上一版本索引：
   - length 不同 → 变更，需处理。
   - length 同、mtime 或权限不同 → 先比 **headHash**（文件头部一小段，默认 4 KB，可配）：不同 → 变更；相同 → 再比 **fullHash**；fullHash 不同 → 变更；相同 → 仅更新索引元数据（不重传）。
   - 上版本有、本次无 → 删除（新版本排除）。
3. **Plan**：对变更文件决定分组/单文件（§6），死重压实检查。
4. **Compress**：7z 压缩/加密/分卷，经临时区状态机（§7）；处理后重校验（§9）。
5. **Upload**：并发上传 data/pack blob，设置 Tier（索引=索引 Tier，数据=数据 Tier），重试退避（PRD 4.1）。
6. **WriteIndex**：写第二级 `indexes/v{N}.json`（先上传，成功）。
7. **Finalize**：原子更新信息记录文件（§8）——先写新内容到临时 blob，成功后覆盖，避免网络失败导致整体损坏（PRD 特别说明 C）。
8. **Cleanup**：按保留策略清理超期版本及其独占数据（§10）。

进度反馈（PRD 备份设计 §2）：百分比 + 变更文件数/尺寸（未压缩、分组前，删除不计）。

## 5. gitignore 规则引擎（统一组件）

三处复用（忽略 3.3.1 / 不压缩 3.3.2.2 / 不分组 3.3.3.2），语法一致（gitignore 风格，支持否定 `!` 特例）。
- 输入：规则集 + 相对路径 → 命中判定。
- 单一实现，三处各持一份规则集。

## 6. 分组打包与死重压实（PRD 3.3.3）

- **分组**：同一目录（不含子目录）的小文件合并成一个 7z pack，减少 blob 数。
  - 尺寸限制（默认 5M）：超过者不入组，单文件处理。仅对新增文件生效。
  - 不分组列表（gitignore 语法）：命中者单文件处理。
  - 单组上限（默认 100M，压缩前）。
- **死重压实**（默认 30%）：pack 内文件被删/变更后旧数据留存；当死重比例（原始尺寸）> 阈值，pack 中**仍有效**的文件重新参与处理（按当前尺寸限制/不分组列表重新决定分组），旧 pack 在本次备份完成后删除。
  - **死重判定**：仅当所有有效版本都不再引用该文件，才算死重（§10 保留策略影响）。

> **实现说明（以代码为准）**：
> - `GroupingPlanner` 对本次全部变更文件（Added **与** Modified）统一套用尺寸阈值/不分组列表。
> - **死重压实已接入清理管线**（`DeadWeightCompactor` + `RetentionCleaner`，2026-07-17）：采用**原地重压**而非「重新参与规划」——
>   pack 死重比例超阈值（默认 30%，`GlobalSettings.DeadWeightThresholdPercent`）时，下载该 pack→解压→**仅保留仍有效成员**重压→覆盖同 packId blob（删旧分卷）。
>   因 pack 按 `packId+entryName` 引用、有效成员 entryName 不变，**无需改写任何版本索引**（比 §6 原始「重新决定分组」更简单且避免跨版本改索引）。仅在版本退役时触发（死重只在此时增加）。
> - **成员内容来源：本地优先**。重压时先看仍有效成员在**本地是否有相同内容**（须 hash 确认，即便长度/时间/权限相同）：有则直接用本地、**无需下载**。仅本地缺失的成员才需从云端取回旧 pack 解压补齐。
>   - 因此**全部有效成员本地可得时，Archive tier 的 pack 也能压实**（不读云端）。
>   - 本地缺失成员时，是否下载云端 pack 由**按数据 tier 的开关**决定：`GlobalSettings.RepackDownload{Hot,Cool,Cold,Archive}`（默认 真/真/真/**假**——Archive 关，避免高成本取回/rehydrate）。不允许下载则**放弃该 pack 的重打包**（保留死重、记 `DeadBytes` 以便观测）。
>   - 先按存在性判断本地缺失、再做 hash 比对（短路优化）。见 `DeadWeightCompactor`。

## 7. 临时区状态机（PRD 3.3.2.4）

两个目录：
- **压缩临时文件夹**（compress-temp）：7z 的输出目标。
- **压缩后临时区**（staged-temp，默认上限 1GB）：压缩结果移入，供上传。

规则：
- 压缩先输出到 compress-temp → 完成后**移动**整套分卷到 staged-temp（避免压缩中分卷被改动）。
- **压缩全局非并发**（跨备份也不并发）——单一压缩队列/锁。
- staged-temp 未达上限 → 分发下一个压缩任务；已超量 → 暂停新压缩，直到上传腾出空间。
- 允许「新加入的一个压缩结果导致暂时超限」。
- 上传完成即从 staged-temp 删除。

## 8. 索引/信息文件原子性（PRD 特别说明 C）

- 数据/pack blob：内容寻址，先传数据再改索引；重复传等价（幂等）。
- 第二级索引：新版本写新文件，不覆盖旧。
- 信息记录文件更新：写到临时 blob → 校验 → 覆盖正式名（或用 blob 版本/ETag 乐观并发）。网络失败时旧文件仍完整。

## 9. 处理后重校验与反复保护（PRD 特别说明 D）

- 每个文件处理后重查 mtime/权限 → 变则重算 hash → hash 变则重处理。
- 反复达阈值（默认 5，env 可配）→ 报警，以当前版本保存，停重试。
- 分组文件：压缩后对组内原始文件重校验；变更的移出分组，放当前目录下一个分组，无则单文件。
- 收尾检查：全部处理结束、上传索引前再校验一遍，已报警的跳过。

## 10. 版本保留与清理（PRD 3.2、9）

- 保留策略：最大版本数（默认 100）+ 最长时间（默认 180 天），超量判断方式（两者都到/任一到/仅版本/仅时间）。
- 清理时机：备份完成时 + 计划任务 Cleanup。
- 删除版本 → 删其第二级索引 + 不再被任何有效版本引用的数据 blob/pack。
  - **分卷清理（以代码为准）**：`RetentionCleaner` 在比对引用时把 data blob 名 `data/{hash}.NNN` 归一化回基名，pack 按 `packId` 归组枚举 `packs/` 前缀，确保「删除时整个分卷族一起删、被引用的分卷不会被误删」（§7）。

## 11. 前端：新建备份流程（PRD 备份设计 §1）

两步向导：
1. 基础信息（创建后不可改，除名字/描述）：账户+container（可新建 container）、本地根路径、名字、描述、密码（可选=加密）、索引 Tier、数据 Tier。
2. 基于默认值的本备份设置（逐项或勾选「使用默认」）：忽略/压缩/分组规则、版本保留、并发、symlink（默认跳过）、执行记录保留。
完成后「立即备份」或「暂不执行」。

## 12. 子任务拆分（建议分阶段实现，每阶段可独立验证）

- **M4a — 扫描与索引基础**：gitignore 引擎、本地扫描、索引 schema + 序列化（含加密）、信息文件读写 + 原子更新、本地状态缓存。验证：对一个目录产出/读取索引往返（Azurite）。
- **M4b — 对比引擎**：版本 diff（length/mtime/权限/hash），仅元数据变更只更新索引。验证：改动文件→正确识别变更/仅元数据/删除。
- **M4c — 压缩与临时区**：7z 封装（压缩/加密/分卷）、临时区状态机（非并发、超量阻塞、先临时后移动）、处理后重校验。验证：压缩产出分卷 + 临时区调度。
- **M4d — 分组与死重**：分组打包、死重压实。验证：小文件合并、死重触发重组。
- **M4e — 上传与保留**：并发上传 + 重试退避 + Tier、版本保留清理。验证：上传到 Azurite、清理旧版本。
- **M4f — 编排器与前端**：串联全流程 + 进度反馈、新建备份向导。验证：端到端跑一次真实备份（Azurite）。

## 13. 决策（已定，2026-07-16）

1. **7z 实现**：用**官方 7-Zip**——backend Dockerfile 装 Debian `7zip` 包（提供 `7zz`）。命令：`7zz a -p{pwd} -mhe=on -v{size} out.7z ...`（AES-256 + 头加密 + 分卷），解包 `7zz x out.7z.001`。若 apt 版本过旧则改用官网二进制。
2. **hash 算法**：**XxHash128**（fullHash 与 headHash 均用；`xxh128:` 前缀，16 字节）。原定 SHA-256，后改为非加密的 XxHash128——更快、更短（索引体积减半），128 位对个人备份规模的内容寻址去重碰撞概率可忽略。**不用 CRC**：CRC 碰撞率太高，作去重键会丢数据。
3. **去重 / 变更检测**：整文件级去重（去重键 = fullHash）。变更检测用**两级 hash**——索引存 headHash（头部一小段，默认 4 KB，可配）+ fullHash；Diff 先比 headHash 快速预筛，相同再比 fullHash。分块（CDC）去重留后续优化。
4. **索引序列化**：**紧凑自定义二进制** + 7z 压缩。原定 JSON，后改为二进制以减小体积——hash 存 16 字节裸字节（而非 `xxh128:`+hex 文本）、枚举/时间/长度定宽编码。`IndexSerializer` 公开 API 不变（字节数组往返），备份/还原/blob 存储透明。
5. **检查「本地文件存在」**：与 §4 Diff 一致（length → mtime/权限 → headHash → fullHash）。
6. **前端进度**：先用轮询（`GET /api/backups/{id}/progress`），简单可靠；后续可升级 SSE。
