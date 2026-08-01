# 7-Zip CPU 优先级（全局设置）

## 背景

压缩与解压是这个程序唯一会把 CPU 吃满的动作。它跑在 NAS 上，而 NAS 上还有别的东西在跑
（媒体库、相册索引、别人的容器）。备份是背景工作，慢一点没人会注意；把机器卡住会。

7z 的线程数已经可以经 `Backup__SevenZipMethodArgs`（`-mmt=N`）调，但那是个环境变量，改一次
要重启容器，而且限线程只降**并行度**，不降**争抢时的排队权重**——单线程满载一样能让界面卡顿。
优先级是另一个旋钮：让 7z 只吃别人不要的那部分 CPU。

## 设置项

`GlobalSettings` 新增：

```csharp
public enum SevenZipCpuPriority { Lowest = 0, BelowNormal = 1, Normal = 2 }

public SevenZipCpuPriority SevenZipPriority { get; set; } = SevenZipCpuPriority.Lowest;
```

映射到 `ProcessPriorityClass`：

| 设置值 | ProcessPriorityClass | Linux nice |
|---|---|---|
| `Lowest`（默认） | `Idle` | 19 |
| `BelowNormal` | `BelowNormal` | 10 |
| `Normal` | `Normal` | 0 |

不提供"高于正常"。在 Linux 上提升优先级需要特权，而让压缩抢在 Web 界面前面，对一个
背景备份程序来说只有坏处。

**`Lowest` 必须是枚举的 0**。EF 迁移给既有行填的就是 0，这样老库升级后天然落在"最低"
上——与默认值一致。反面教材是 `StagedLimitBytes` / `ProcessingMaxAttempts`：它们的
"合法默认"不是 0，于是 `GlobalSettingsService.GetAsync` 里至今留着一段"读到 0 就换成
默认值"的补丁。把 `Lowest` 定成 0 就不用再欠这笔账。

## 生效路径

`SevenZipCli.RunAsync` / `RunStreamingAsync` 各加一个可选参数：

```csharp
Func<ProcessPriorityClass>? priority = null
```

在 `Process.Start` 返回后立即取值并设到进程上。`SevenZipCompressor` 与
`SevenZipArchiveCodec` 构造时收下这个委托、转交给每一次调用；`Program.cs` 提供它的实现，
写法照抄 `StagingArea` 的 `Limit()`——开一个 scope 读一次 `GlobalSettings`。

**取委托而不取值**，是为了让 Settings 里改完保存后，下一个 7z 进程就按新档跑，不必重启容器。
每次 7z 调用多一次 SQLite 单例表读；`StagingArea.Limit()` 调用更频繁，这个代价已经验证过。

作用面因此是全部 7z 进程：备份压缩（含流式）、还原解压、深度检查、修复、死重压实、索引编解码。

### 两处必须写进注释的坑

**设置失败一律吞掉。** 进程可能在这几微秒内就已退出（`InvalidOperationException`），平台也
可能拒绝（`Win32Exception`）。优先级调不动不是压缩失败，绝不能因此让一次备份炸掉。

**Linux 的 nice 是每线程属性。** `setpriority(PRIO_PROCESS, pid)` 只落在主线程上，7z 的
LZMA 工作线程会继承**创建它们的那个线程**当时的 nice 值。我们在 `Process.Start` 刚返回时
就设置，那时 7z 还在动态链接、解析参数，工作线程尚未创建，因此实践上全部继承。最坏情况
（我们输掉这个竞态）也只是部分线程没降下来，不影响正确性，只影响效果。

## 界面

Settings → Global 区，一个下拉：

- Label：`7-Zip CPU priority`
- 选项：`Lowest (default)` / `Below normal` / `Normal`
- 说明（英文，同 UI 语言约定）：Compression and extraction are the most CPU-hungry things
  this app does. Lowest keeps them out of the way of everything else on the machine — they
  only get the CPU nobody else wants. Raise it if backups are the reason you bought the machine.

## 测试

- `GlobalSettingsService` round-trip：Upsert 存得下、Get 读得回。
- 老行兼容：列值为 0 的既有行读出来是 `Lowest`，且不被 `GetAsync` 的规范化逻辑改写。
- 映射：三档各自映射到预期的 `ProcessPriorityClass`。
- 真跑一次 7z 压缩并传入 `Lowest`，断言压缩照常成功——覆盖"设优先级这一步不会把正常路径搞坏"。
  不回读 `PriorityClass` 断言：进程可能已退出，那是竞态。

## 改动清单

后端：`Models/GlobalSettings.cs`、新 EF 迁移、`Services/GlobalSettingsService.cs`、
`Services/SevenZipCli.cs`、`Services/SevenZipCompressor.cs`、`Services/SevenZipArchiveCodec.cs`、
`Program.cs`。

前端：`api/settings.ts`、`pages/SettingsPage.tsx`。

测试：`GlobalSettingsServiceTests` / `SettingsEndpointsTests` / `SevenZipCompressorTests` 增补。
