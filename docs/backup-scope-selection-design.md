# 备份范围选择（根目录下的子集）— 设计

2026-08-03

## 问题

一个备份配置目前只能是"根目录下的全部内容，减去忽略规则命中的部分"。想只备份根下的
几个子目录，唯一的办法是把不要的部分**用 gitignore 语法写进忽略规则**——用户得先知道
根下有什么，再把它们逐条敲成文本，改一次范围就要重新敲一次。

需要的是：新建备份选好根目录后，可以取消"备份全部"，展开一棵树逐个勾选；这份范围
以后还能回来增删。

## 已确认的语义（四条）

1. **子树语义**：勾选一个目录 = 一条持续生效的规则，日后该目录下新增的文件自动纳入。
   存的是**边界**，不是文件清单。
2. **与忽略规则正交**：被忽略规则命中的条目在树里照常显示、照常可勾、勾选状态照常存；
   备份时忽略规则仍独立剔除它。以后改了忽略规则，旧的勾选自动生效。
3. **大目录分页**：每次加载一批，底部 `Load more` 继续。目录级勾选不需要展开就能做。
4. **移出范围 = 视为删除**：与改忽略规则的行为完全一致。新版本不再包含它们，旧版本
   依然保留（可从旧版本还原，直到保留策略把旧版本清掉）。保存时在界面上明确警告。

## 现状

- `LocalFileScanner.ScanDirectory` 遍历本地根时只有一层过滤：`IgnoreRuleSet`
  （`LocalFileScanner.cs:133`）。目录命中忽略时直接 `continue`，**不再下降**。
- `/api/system/browse` 是现成的本地目录懒加载列举，隐藏文件本来就一并返回，
  受 `PathBoundary` 约束，指向根外的软链标 `OutsideRoot`。每目录硬上限 2000 项后截断。
- `BackupConfig.LocalRoot` 在常规编辑路径上仍然锁定（`BackupConfigService.cs:46`），但另有一条
  带校验的专用迁移通道（`docs/change-local-root-design.md`，挂载点搬家用）。范围规则的相对
  基准因此**不是绝对稳定**的：换根后规则原文保留、不做改写，新根下是同一份数据时继续正确；
  用户强行迁到结构不同的目录树时，规则可能命中变空或部分失效，后果与手工改窄范围一致
  （见本文语义 4），不损坏数据。
- `RestoreDialog.tsx` 已有一棵懒加载树 + 三态勾选，但数据源是**云端版本索引**
  （有限已知全集，三态靠数已加载的后代文件算）。

## 为什么不复用 `IgnoreRuleSet`

把范围翻译成 gitignore 规则存进另一个字段，后端几乎零改动——但走不通。
`LocalFileScanner.cs:136` 在目录命中忽略时不再下降，于是"排除 `docs/`，但重新包含
`docs/2026/`"这条路失效，而这正是本功能最常见的一步操作。用它就得同时改扫描器的下降
逻辑，省下的改动又还了回去，还额外背上两套规则互相干扰的排查成本。

存完整白名单文件清单同样排除：与子树语义直接冲突（新文件不会自动纳入），且一个 50 万
文件的根会把配置表撑成几十 MB。

## 设计

### 1. 数据模型：`BackupConfig.ScopeRules`

```csharp
/// <summary>备份范围（本设计）。null/空 = 根下全部内容（默认）。
/// **不可继承**——范围是这个备份自己的事，全局默认没有意义，因此不进
/// ResolvedBackupSettings，直接从 config 取。</summary>
public string? ScopeRules { get; set; }
```

其它规则字段的 `null` 表示"继承全局"，这个字段的 `null` 表示"全部包含"。这处不同是
故意的，实现时不要顺手把它塞进继承体系。

**文本格式**，每行一条，符号 + 空格 + 相对 `LocalRoot` 的路径（`/` 分隔，无首尾斜杠）：

```
-
+ photos
+ docs/2026
- docs/2026/tmp
```

单独一个 `-` 是根规则。根没有祖先，其隐含默认是"包含"，所以由不变式 1，根规则只可能是
`-`——`+` 的根规则是冗余的，写入时不落盘。

**判定** `IsInScope(path)`：沿路径逐级向上找**最长匹配前缀**的规则，命中即定；
一条都没有则为"包含"。

**两条写入不变式**，规则集因此永远最小、永远不失控增长：

1. 每条规则的判定必须与它最近的祖先规则**相反**——相同即冗余，写入时不落盘。
2. 写入一条规则时，删除所有以它为严格前缀的更深规则——它们已被覆盖。

勾选一个节点因此只是"写一条规则 + 清掉被它覆盖的规则"：一次性、局部、无回路。

### 2. 两个白送的推论

这两条是懒加载与三态能同时成立的原因，也是整个设计的承重点：

- **某目录显示为 `indeterminate` ⟸ 规则集里存在以它为严格前缀的规则。**
  由不变式 1，更深规则必与其最近祖先判定相反，所以"存在更深规则"意味着该子树内部有
  分歧。**不需要加载任何子节点**就能算出三态——否则懒加载和三态只能二选一。

  这是**单向**的，有一个真实的边角：`- docs` + `+ docs/a` + `+ docs/b`，而 `docs` 下
  恰好只有 `a`、`b` 两个子目录时，实际效果是全选，界面却显示灰选。不加载子节点就无从
  知道 `a`、`b` 是否穷尽了 `docs`——这是懒加载的固有代价，`RestoreDialog` 那棵树有同样
  的限制。取灰选是保守且诚实的一侧：它如实反映"这里有明确规则在起作用"，而不会把
  "部分选中"错报成"全选"。备份结果不受影响，只是显示。
- **扫描器不能沿用"目录不在范围就不下降"**：被排除的目录下面可能还有 `+` 规则要重新
  包含。因此需要第二个方法 `MayContainIncluded(dir)` = 自身在范围内 **或** 存在以它为
  前缀的 `+` 规则。

### 3. `Services/ScopeRuleSet.cs`（新增，纯逻辑无 IO）

与 `IgnoreRuleSet` 平级但**不复用**：那套是 glob 匹配 + 最后规则胜出，这套是精确路径 +
最长前缀胜出，混在一起只会让两边都变复杂。

```csharp
public sealed class ScopeRuleSet
{
    public static ScopeRuleSet All { get; }           // 空规则集 = 全部包含
    public static ScopeRuleSet Parse(string? text);   // null/空 → All；非法行跳过不抛

    public bool IsInScope(string relativePath);       // 最长前缀匹配
    public bool MayContainIncluded(string dirPath);   // 子树里还有没有 + 规则
    public bool IsPartial(string dirPath);            // 三态：存在更深规则

    public ScopeRuleSet With(string path, bool included);  // 维护两条不变式，返回新实例
    public override string ToString();                     // 回写文本
}
```

内部结构 `SortedDictionary<string, bool>`（Ordinal）。`IsInScope` 逐级切分路径查表，
O(depth)。`MayContainIncluded` / `IsPartial` 线性扫描规则集找前缀——规则是用户一次次点
出来的边界，几十条封顶，线性扫描比二分查找更简单也更快；这里刻意不做区间索引。

Ordinal 序下祖先必排在后代之前（严格前缀在字符串比较中恒小于其扩展），规范化时因此可以
一遍顺序遍历就把冗余规则清干净。

### 4. `LocalFileScanner` 集成

唯一动到备份主链路的地方。`ScanOptions` 加
`ScopeRuleSet Scope { get; init; } = ScopeRuleSet.All;`，在现有 ignore 判断
（`LocalFileScanner.cs:133`）**之后**插入：

```csharp
if (isDirectory && !isSymlink)
{
    // 目录被排除、且子树里也没有任何重新包含的规则 → 整棵剪掉，不下降。
    // 只判 IsInScope 是不够的：被排除的目录下面可能还有 + 规则。
    if (!scope.MayContainIncluded(relative))
        continue;
    ScanDirectory(...);   // 见下：keptChildren 不能无条件 ++
    continue;
}
if (!scope.IsInScope(relative))
    continue;             // 文件不在范围
```

**必须点明的坑**：`keptChildren` 决定一个目录会不会被记为 `EmptyDirs`
（`LocalFileScanner.cs:177`）。一个只是"路过"的目录（自身被排除，只为了下降到子树里
某个重新包含的目录）不该算作保留的子项。`ScanDirectory` 因此要返回"子树里是否真的留下
了东西"，只在为真时 `keptChildren++`。否则 `- docs` + `+ docs/2026` 会让 `docs` 被写成
空目录，还原时凭空重建出来。

### 5. 范围内为空的兜底

若范围把所有文件都剔光，备份会扫出零条目，diff 判成"全部删除"，写出一个空版本。这不是
数据丢失（旧版本还在），但一定是误操作。`BackupOrchestrator` 在扫描结果为空且
`ScopeRules` 非空时**直接失败**并给出明确消息，不安静地写一个空版本。

### 6. 其它后端接线

- `BackupConfigResponse` / `BackupConfigRequest` 加字段。
- `BackupConfigService.UpdateAsync` 加一行赋值（不属于锁定的基础字段，可改）。
- `BackupRequestMapper.From`：`Scan = new ScanOptions { ..., Scope = ScopeRuleSet.Parse(config.ScopeRules) }`。
- EF 迁移 `AddBackupScopeRules`。

### 7. browse 接口加分页

现有实现先收集再排序，截断发生在收集阶段，所以截断后的排序是错的、也没法分页。
改为分别枚举 `EnumerateDirectories` 与 `EnumerateFiles`（`isDir` 因此免费得到，
不用 stat），各自排序后拼接、按 `offset` / `limit` 切片，**只对当前页**的项做 stat 取
`Length` / `Mtime`。响应加 `Total` 与 `Offset`。

旧的 `MaxBrowseEntries` 截断语义保留给不传分页参数的调用方，`PathBrowser` 不受影响。

### 8. 前端 `components/ScopeTree.tsx`（新增）

不复用 `RestoreDialog` 那棵树：那棵的数据源是云端版本索引，这棵是活的文件系统
（无限、会变，三态靠规则集算）。两者只有外观像，内核相反，合并只会让两边都变脆。
视觉上共用同一套行样式。

状态只有三份，真相只有一份：

```
rules      — ScopeRuleSet 的前端镜像（唯一真相，保存时序列化成文本）
children   — Record<path, Entry[]>，懒加载缓存，纯展示
expanded   — Set<path>，纯展示
```

节点的勾选状态**永远从 `rules` 现算，不存**：

```ts
isPartial(path) ? 'indeterminate' : isInScope(path) ? 'checked' : 'unchecked'
```

**因此不会死循环**：点击只调用一次 `rules.with(path, !checked)` 就结束。父节点的状态在
下一次渲染时现算，子节点也是。没有"子改父 → 父改子"的传播回路，因为根本没有传播这个
动作。需求里那四条级联规则全部是这个模型的推论，不需要单独实现：

| 需求 | 由什么推出 |
|---|---|
| 父选中 → 包含所有子 | 子树无更深规则 → 子全部显示为选中 |
| 父取消 → 取消所有子 | 同上，反向 |
| 子全选 → 父显示选中 | 那些子规则被不变式 1 判为冗余而清除 |
| 否则父灰选 | 存在更深规则 → `isPartial` 为真 |

### 9. 规则集逻辑的两份实现

`ScopeRuleSet` 的判定与写入用 TypeScript 再实现一遍（`lib/scopeRules.ts`，约 60 行）。
这是**有意的重复**：走 API 意味着每点一个复选框就要一次往返，一棵树点几十下就是几十次
请求。代价是两份实现必须行为一致——用**同一份 JSON 夹具**（C# 与 TS 测试各自读）钉住，
行为分叉时两边同时红。

### 10. 行的渲染与入口

行 = 复选框 + 名称 + 展开箭头（目录）+ 徽标。两种徽标，都只是标注：

- `ignored` —— 命中当前忽略规则。照常显示、可勾、状态照常存，备份时忽略规则仍独立剔除
  它。徽标带 tooltip 说明这一点，否则用户会以为勾了就会传。
- `outside root` —— 指向根外的软链，沿用 browse 已有的 `outsideRoot`，灰显不可勾。

隐藏文件不做任何过滤——browse 本来就一并返回，这条是"不做什么"。

分页：每目录首屏 500 项，底部 `Load more (showing 500 of 12,431)`，已加载的不清空。

入口：`BackupConfigsPage` 表单里 `LocalRoot` 字段下方加复选框
`Back up everything in this folder`（默认勾选）。取消勾选时展开树，第一层就是根目录
那一个节点，**初始为选中态**（等价于当前行为），用户从这里开始剔除——首次配置最常见的
是"基本都要，除了几个目录"；若初始全不选，用户得从零勾起。编辑已有配置时从
`ScopeRules` 反序列化。保存随配置表单一起提交，不单独开端点。

**保存时的警告**：若这次编辑把某些先前在范围内的路径移出了范围，保存前弹一句确认——
按语义 4，那些文件下次备份会被当作删除处理。判断依据是新旧规则集的差异，不扫文件系统。

## 错误处理

| 场景 | 处置 |
|---|---|
| 展开没权限的目录 | browse 已返回 403，行内显示 `Could not be read`，节点仍可勾选——范围规则不要求目录当下可读 |
| 目录在勾选后被删除 | 规则留在集合里，扫描时匹配不到任何东西，无害。**不做**自动清理：目录可能只是暂时挂载不上，清掉等于擦掉用户的意图 |
| 规则文本被手工改坏 | `Parse` 跳过无法识别的行而不抛，与 `IgnoreRuleSet` 对空行/注释的处置一致。保存路径由 UI 生成，不产生非法行 |
| 范围把所有文件剔光 | 备份直接失败并给出明确消息，不写空版本（§5） |
| 范围内的目录读不开 | 走现有 `UnreadablePath` 那条路，与本功能无关，不改 |

## 测试

1. **`ScopeRuleSetTests`（C#）**——最长前缀匹配；`MayContainIncluded` 在多层交替
   `+/-` 下的正确性；两条写入不变式（写冗余规则不落盘、写规则清掉更深规则）；
   `Parse` / `ToString` 往返。
2. **共享夹具**——判定与写入用例表落成一份 JSON，C# 与 TS 测试各自读同一份（§9）。
3. **`LocalFileScannerTests` 新增**——建真实临时目录树验证：整棵子树排除；排除目录下
   重新包含子目录（必须真的下降进去）；**只是路过的目录不进 `EmptyDirs`**（§4 那个坑，
   单独一条测试钉死）；范围与 ignore 同时生效时两者独立。
4. **前端 `ScopeTree`**——三态渲染；点击后规则集的变化；懒加载不触发额外规则计算；
   分页加载不丢已有勾选。

不额外测：browse 分页的切片边界只做一条基本用例（现有 browse 已覆盖枚举与异常路径）。

## 改动清单

**后端**：新增 `Services/ScopeRuleSet.cs`；改 `LocalFileScanner`、`BackupConfig`、
`BackupConfigDtos`、`BackupConfigService`、`BackupRequestMapper`、`BackupOrchestrator`
（空范围兜底）、`SystemEndpoints`（分页）；加迁移 `AddBackupScopeRules`。

**前端**：新增 `components/ScopeTree.tsx`、`lib/scopeRules.ts`；改
`pages/BackupConfigsPage.tsx`、`api/browse.ts`。

## 不在本设计范围内

- 还原与检查不受范围影响——范围只作用于备份扫描。
- 范围不写入云端信息文件，与 `LocalRoot`、忽略规则一样是本地设备配置。
