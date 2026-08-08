# 上传速度时钟 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 `StageProgress.BytesPerSecond` 只统计「网线上至少有一条流开着」的时间——压缩/排队的空转不进分母，流开着却不动的卡顿进分母把速度压下去。

**Architecture:** 在 `StageTracker` 内维护一条只在活跃段前进的虚拟时间轴，测速采样改用它打时间戳（10 秒窗口也在这条轴上度量）；活跃段内挂一个 1 秒心跳，让卡住的流也能被重新采样。由构造参数开关，只对会登记在途项的三个阶段启用。

**Tech Stack:** .NET 10 / C# 13、xunit（`SkippableFact` 用于集成测试）、`System.Threading.Timer`、`System.Threading.Lock`。

设计文档：[progress-display-design.md](progress-display-design.md)（本文件描述的那一轮已并入其中；下文出现的 `docs/upload-speed-clock-design.md` 是当时的实现记录，保留原样）。

## Global Constraints

- 目标框架 `net10.0`；`Nullable` 与 `ImplicitUsings` 均为 enable。
- 代码注释用中文，写「为什么」而不是「做了什么」；界面文案一律英文。
- 提交信息英文，`type(scope): 小写祈使句` 开头，正文说明动机，结尾带 `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`。
- 测试程序集已通过 `src/AzureStorageBackup.Api/AssemblyInfo.cs` 的 `InternalsVisibleTo("AzureStorageBackup.Api.Tests")` 看得见 internal 成员，不必为测试把成员改成 public。
- 全量测试命令：`cd backend && dotnet test AzureStorageBackup.slnx`。需要 Azurite 或 7z 的集成测试在缺环境时自动 Skip，不算失败。
- 分支 `speed-clock-runs-only-while-uploading`，做完合并回 `main` 并删分支（仓库只留 main 一条线）。

## 文件结构

| 文件 | 职责 | 本次变动 |
|---|---|---|
| `backend/src/AzureStorageBackup.Api/Services/StageProgress.cs` | `StageProgress` 记录 + `StageTracker` 累加/节流/测速 | 全部实现改动集中在这里 |
| `backend/tests/AzureStorageBackup.Api.Tests/StageProgressTests.cs` | `StageTracker` 单测 | 新增 4 个测试 |
| `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs:311` | 备份编排 | 一行：开开关 |
| `backend/src/AzureStorageBackup.Api/Services/RestoreOrchestrator.cs:257` | 还原编排 | 一行：开开关 |
| `backend/src/AzureStorageBackup.Api/Services/BackupChecker.cs:62-63,283` | 检查编排 | `Track` 帮手加参数，`Verifying` 开开关 |
| `docs/upload-speed-clock-design.md` | 设计文档 | 修正一处与实现不符的措辞 |

`StageProgress.cs` 目前 328 行、一个记录 + 一个跟踪器，职责单一，不拆。

---

### Task 1: 虚拟上传时钟

只让测速的时间轴在有在途项时前进。本任务不含心跳——卡住时速度还不会掉，那是 Task 2。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/StageProgress.cs:72-88`（构造签名与字段）、`:191`（`BeginItem`）、`:241-249`（`EndItem`）、`:261-294`（`PublishIfDue`）、`:160-165`（`BeginWork`）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/StageProgressTests.cs`

**Interfaces:**
- Consumes: 无
- Produces:
  - `StageTracker(string stage, int total, Action<StageProgress> publish, bool speedWhileInFlight = false)`
  - `internal Func<long>? Clock { get; init; }` — 测试注入的毫秒时间源，null 时走内部 `Stopwatch`

- [ ] **Step 1: 写失败的测试**

追加到 `backend/tests/AzureStorageBackup.Api.Tests/StageProgressTests.cs` 类内末尾：

```csharp
    /// <summary>
    /// 备份上传的节奏是「压一箱几十秒 → 传几秒」。测速窗口过去按墙钟打时间戳，于是同一条网线
    /// 量出来的数字随停顿长短而变：停顿短于窗口被稀释，长于窗口则老采样被整批淘汰、当场报 0。
    /// 速度要回答的是"网线上有多快"，压缩那几十秒就不该进分母。
    /// </summary>
    [Fact]
    public void Compression_Stalls_Do_Not_Dilute_The_Upload_Speed()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 2, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        // 第一卷：1 MB 用掉 1 秒。
        tracker.BeginItem("v1");
        var first = tracker.ItemProgress();
        now += 1_000;
        first.Report(1 << 20);
        tracker.EndItem("v1", 0);

        // 压缩 30 秒——一条流都没开着。这 30 秒不该进分母。
        now += 30_000;

        // 第二卷：又是 1 MB 用掉 1 秒。
        tracker.BeginItem("v2");
        var second = tracker.ItemProgress();
        now += 1_000;
        second.Report(1 << 20);
        tracker.EndItem("v2", 0);

        // 2 MB / 2 秒在网线上 ≈ 1 MB/s。被 30 秒摊薄的话是 64 KB/s，老采样被淘汰的话是 0。
        Assert.InRange(seen[^1].BytesPerSecond, 900_000L, 1_150_000L);
    }

    /// <summary>
    /// 开关默认关：扫描、差分这些阶段从不登记在途项，虚拟时钟对它们会永远停在 0，
    /// 速度将恒为 0。它们必须原样走墙钟。
    /// </summary>
    [Fact]
    public void Stages_Without_In_Flight_Items_Keep_The_Wall_Clock_Speed()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 2, seen.Add) { Clock = () => now };

        tracker.Advance(1 << 20);
        now += 1_000;
        tracker.Advance(1 << 20);

        Assert.InRange(seen[^1].BytesPerSecond, 900_000L, 1_150_000L);
    }
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `cd backend && dotnet test AzureStorageBackup.slnx --filter "FullyQualifiedName~StageProgressTests"`

Expected: 编译失败——`StageTracker` 没有 `speedWhileInFlight` 参数，也没有 `Clock` 属性。（编译不过就是本步要的"红"；补齐签名后 `Compression_Stalls...` 会以 `BytesPerSecond` 实为 `0` 失败。）

- [ ] **Step 3: 改构造签名、加时钟字段**

`StageProgress.cs:72` 的类声明改为：

```csharp
/// <param name="speedWhileInFlight">测速的分母是否只算「至少有一条在途项开着」的时间。
/// 会登记在途项的阶段（上传/还原/校验）置 true：它们的节奏是「压一箱几十秒 → 传几秒」，
/// 拿墙钟当分母量出来的既不是传输速度也不是墙钟吞吐。从不调 <see cref="BeginItem"/> 的阶段
/// （扫描/差分/本地检查）必须保持 false——虚拟时钟对它们永远不走，速度会恒为 0。</param>
public sealed class StageTracker(
    string stage, int total, Action<StageProgress> publish, bool speedWhileInFlight = false) : IDisposable
```

在 `private long _workStartMs = -1;`（`:107`）之后追加字段：

```csharp
    // 测速用的时间轴：只在 _active 非空时前进（speedWhileInFlight 为 true 时）。
    // 压缩期它冻着，于是停顿两侧的采样在窗口里是连着的——速度既不被空转稀释，
    // 也不会出现"老采样整批超龄 → 当场报 0 → 压完猛跳"。
    private long _activeMs;
    // 当前活跃段的起点；-1 = 当下一条流都没开。
    private long _activeSince = -1;

    /// <summary>测试注入的毫秒时间源。10 秒测速窗口不可能靠真等来验，注入之后整个跟踪器
    /// 在时间上完全确定。生产为 null，走内部的 <see cref="Stopwatch"/>。</summary>
    internal Func<long>? Clock { get; init; }

    private long NowMs() => Clock?.Invoke() ?? _clock.ElapsedMilliseconds;

    /// <summary>测速用的时刻。开了开关的阶段走"有流才走"的虚拟轴，其余照走墙钟。</summary>
    private long SpeedNow(long now) =>
        speedWhileInFlight ? _activeMs + (_activeSince >= 0 ? now - _activeSince : 0) : now;
```

- [ ] **Step 4: 让 `BeginItem` / `EndItem` 维护活跃段**

`:191` 的 `BeginItem` 整体替换为：

```csharp
    /// <summary>登记一个在途的传输对象。上传阶段登记的是**卷**（<c>data/xxx.007</c>），
    /// 不是件——界面上那个 "N uploading" 要回答的是"网线上现在有几条流"。
    /// <para>
    /// 空→非空这一下同时开启测速时钟：在此之前的压缩与排队不算进速度的分母。
    /// 集合的增删挪进锁里，是为了让"是不是空的"与时钟开关在同一个临界区内定下来。
    /// </para></summary>
    public void BeginItem(string item)
    {
        lock (_gate)
        {
            if (!_active.TryAdd(item, 0))
                return;
            if (speedWhileInFlight && _activeSince < 0)
                _activeSince = NowMs();
        }
    }
```

`:241` 的 `EndItem` 整体替换为：

```csharp
    /// <summary>一个在途项结束：移出在途集合并累加字节，**不计数**。
    /// 计数归 <see cref="Advance"/> 专管——上传的槽位计数有"恰好一次"的精确约束
    /// （一个 pack 可能因成员变化被重压多次，却始终只占 total 里的一个槽位），
    /// 在这里顺手加一次就会重复计数，进度条会冲过 100%。
    /// <para>最后一条流收工时把这一段活跃时长落账，测速时钟就此停下，直到下一条流开起来。</para></summary>
    public void EndItem(string item, long bytes)
    {
        lock (_gate)
        {
            if (_active.TryRemove(item, out _) && speedWhileInFlight && _active.IsEmpty && _activeSince >= 0)
            {
                _activeMs += NowMs() - _activeSince;
                _activeSince = -1;
            }
            _bytes += bytes;
            PublishIfDue(force: false);
        }
    }
```

- [ ] **Step 5: 采样改用虚拟时间轴**

`PublishIfDue`（`:261`）开头到 speed 计算这一段替换为：

```csharp
    private void PublishIfDue(bool force)
    {
        var now = NowMs();
        if (!force && now - _lastPublishMs < ThrottleMs)
            return;
        _lastPublishMs = now;

        // 节流用墙钟（它管的是"多久刷一次界面"），测速用虚拟轴（它管的是"这些字节花了多少传输时间"）。
        var tick = SpeedNow(now);
        _samples.Enqueue((tick, _bytes));
        while (_samples.Count > 1 && tick - _samples.Peek().Ms > SpeedWindowMs)
            _samples.Dequeue();

        long speed = 0;
        if (_samples.Count > 1)
        {
            var oldest = _samples.Peek();
            var spanMs = tick - oldest.Ms;
            if (spanMs > 0)
                speed = (_bytes - oldest.Bytes) * 1000 / spanMs;
        }
```

同时把 `BeginWork`（`:164`）里的时间读取换成同一个来源，注入假时钟时 ETA 才跟着一起确定：

```csharp
        Interlocked.CompareExchange(ref _workStartMs, NowMs(), -1);
```

以及 `Eta` 的调用点保持传墙钟 `now`（`PublishIfDue` 末尾的 `Eta(now)` 不动）——剩余时间本来就该把压缩时间算进去。

- [ ] **Step 6: 加上 `IDisposable` 的空壳**

类已声明实现 `IDisposable`，Task 1 先给一个不做事的实现，Task 2 再填心跳的清理：

```csharp
    /// <summary>停掉心跳定时器（Task 2 起有实际内容）。</summary>
    public void Dispose() { }
```

- [ ] **Step 7: 跑测试确认通过**

Run: `cd backend && dotnet test AzureStorageBackup.slnx --filter "FullyQualifiedName~StageProgressTests"`
Expected: 全部 PASS，含新增两个。

- [ ] **Step 8: 跑全量测试**

Run: `cd backend && dotnet test AzureStorageBackup.slnx`
Expected: 全绿（缺 Azurite/7z 的集成测试显示为 Skipped，不算失败）。

- [ ] **Step 9: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/StageProgress.cs backend/tests/AzureStorageBackup.Api.Tests/StageProgressTests.cs
git commit -F - <<'EOF'
perf(progress): stop the speed clock while nothing is on the wire

The 10s window was timestamped off the wall clock, so a compression stall
either diluted the number (stall under 10s) or evicted the whole window and
excluded itself outright (stall over 10s). Same wire, same transfer, and the
figure swung between half speed and full speed depending on how big the box
being compressed happened to be.

Timestamp the samples on a clock that only advances while at least one
transfer is registered in flight. The stall freezes it, so the samples on
either side sit next to each other in the window and the number means one
thing: how fast the wire is when something is actually on it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 2: 活跃段内的心跳

卡住的流（字节不动）不触发任何事件，速度会冻在卡住前的数字上。加一个只在活跃段内跑的 1 秒心跳把窗口推下去。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/StageProgress.cs`（`BeginItem`、`EndItem`、`Complete`、`Dispose`，并新增 `Tick`/`Heartbeat`/`StopHeartbeat`）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/StageProgressTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `Clock`、`_activeSince`、`SpeedNow`
- Produces: `internal void Tick()` — 心跳的一拍；无活跃段时什么都不做

- [ ] **Step 1: 写失败的测试**

追加到 `StageProgressTests.cs` 类内末尾：

```csharp
    /// <summary>
    /// 流开着却一个字节都不动（网络卡死、SDK 没触发重试）时，没有任何事件会触发上报，
    /// 界面就冻在卡住前的数字上——最该看出问题的时候反而看不出来。
    /// 活跃段内的心跳负责把测速窗口推下去，让速度自己掉到 0。
    /// </summary>
    [Fact]
    public void A_Stuck_Stream_Drags_The_Speed_Down_Instead_Of_Freezing_It()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        tracker.BeginItem("v1");
        var bytes = tracker.ItemProgress();
        now += 1_000;
        bytes.Report(4 << 20);
        now += 1_000;
        bytes.Report(8 << 20);   // 累计值：又是 4 MB
        Assert.True(seen[^1].BytesPerSecond > 0, "流通着的时候要看得见速度");

        // 流还挂着，字节不动。心跳每秒一拍。
        for (var i = 0; i < 12; i++)
        {
            now += 1_000;
            tracker.Tick();
        }

        Assert.Equal(0, seen[^1].BytesPerSecond);
    }

    /// <summary>
    /// 纯压缩期一条流都没开：那段时间不进分母，也就没有任何新东西可报。
    /// 心跳必须闭嘴，否则几十秒一箱的压缩会刷出一串内容完全相同的快照。
    /// </summary>
    [Fact]
    public void The_Heartbeat_Stays_Silent_While_Nothing_Is_On_The_Wire()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        tracker.BeginWork();   // 领了活，但还在压缩：一条流都没开
        seen.Clear();

        for (var i = 0; i < 5; i++)
        {
            now += 1_000;
            tracker.Tick();
        }

        Assert.Empty(seen);
    }
```

- [ ] **Step 2: 跑测试确认它失败**

Run: `cd backend && dotnet test AzureStorageBackup.slnx --filter "FullyQualifiedName~StageProgressTests"`
Expected: 编译失败——`StageTracker` 没有 `Tick`。

- [ ] **Step 3: 加 `Tick` 与心跳开关**

在 `StageProgress.cs` 的常量区（`:74-75`）加：

```csharp
    private const int HeartbeatMs = 1_000;
```

在 Task 1 加的时钟字段旁边加：

```csharp
    // 只在活跃段内跑的定时器。压缩期停着，一个多余的快照都不发。
    private Timer? _heartbeat;
```

在 `Complete()`（`:252`）之前插入：

```csharp
    /// <summary>心跳的一拍：重算一次测速窗口并上报。卡住的流不产生任何事件，
    /// 没有它，速度会一直冻在卡住前的数字上。</summary>
    internal void Tick()
    {
        lock (_gate)
        {
            // 一条流都没开：这段时间本就不进分母，也没有新东西可报。
            if (speedWhileInFlight && _activeSince < 0)
                return;
            PublishIfDue(force: false);
        }
    }

    /// <summary>随活跃段启停心跳。必须在 <c>_gate</c> 内调用。
    /// 注入了时钟＝单测在手工驱动 <see cref="Tick"/>，此时不叠一个真定时器上去，结果才确定。</summary>
    private void Heartbeat(bool on)
    {
        if (Clock is not null)
            return;
        if (on)
        {
            _heartbeat ??= new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
            _heartbeat.Change(HeartbeatMs, HeartbeatMs);
        }
        else
            _heartbeat?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void StopHeartbeat()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
    }
```

- [ ] **Step 4: 在活跃段边界启停心跳**

`BeginItem` 里设 `_activeSince` 的那一句后面补一行：

```csharp
            if (speedWhileInFlight && _activeSince < 0)
            {
                _activeSince = NowMs();
                Heartbeat(on: true);
            }
```

`EndItem` 里清 `_activeSince` 的那一段同样补一行：

```csharp
            if (_active.TryRemove(item, out _) && speedWhileInFlight && _active.IsEmpty && _activeSince >= 0)
            {
                _activeMs += NowMs() - _activeSince;
                _activeSince = -1;
                Heartbeat(on: false);
            }
```

- [ ] **Step 5: 收尾时释放定时器**

`Complete()` 改为：

```csharp
    /// <summary>阶段收尾：无条件产出一次，把进度落到实处，并停掉心跳。</summary>
    public void Complete()
    {
        lock (_gate)
        {
            _current = null;
            PublishIfDue(force: true);
            StopHeartbeat();
        }
    }
```

Task 1 留的空 `Dispose` 填上：

```csharp
    /// <summary>停掉心跳。阶段收尾时 <see cref="Complete"/> 已经做过一次；异常路径漏掉也不要紧——
    /// 三处在途登记都在 <c>finally</c> 里成对调 <see cref="EndItem"/>，最后一条流一结束心跳就已停了。</summary>
    public void Dispose()
    {
        lock (_gate)
            StopHeartbeat();
    }
```

- [ ] **Step 6: 跑测试确认通过**

Run: `cd backend && dotnet test AzureStorageBackup.slnx --filter "FullyQualifiedName~StageProgressTests"`
Expected: 全部 PASS。

- [ ] **Step 7: 跑全量测试**

Run: `cd backend && dotnet test AzureStorageBackup.slnx`
Expected: 全绿。

- [ ] **Step 8: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/StageProgress.cs backend/tests/AzureStorageBackup.Api.Tests/StageProgressTests.cs
git commit -F - <<'EOF'
feat(progress): heartbeat inside a transfer so a stuck stream shows up

A stream that is open but moving no bytes fires no events at all, so nothing
recomputes the window and the readout stays frozen on whatever it said before
the stall — the one case where the number most needs to move.

Tick once a second while at least one transfer is registered, and only then:
during compression there is nothing new to say, and a stalled stream now walks
its own speed down to zero.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

### Task 3: 三个走网络的阶段开开关

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs:311`
- Modify: `backend/src/AzureStorageBackup.Api/Services/RestoreOrchestrator.cs:257`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupChecker.cs:62-63` 与 `:283`
- Modify: `docs/upload-speed-clock-design.md`

**Interfaces:**
- Consumes: Task 1 的 `speedWhileInFlight` 构造参数
- Produces: 无

不为本任务新增单测：三个 tracker 都在编排器内部创建，从外部观察不到它的构造参数，硬造一个观察点只会为测试而测试。语义本身已由 Task 1/2 的单测覆盖，这里靠既有的备份/还原/检查集成测试保证不回归。

- [ ] **Step 1: 备份的 Uploading**

`BackupOrchestrator.cs:311` 那一句改为：

```csharp
        // 速度只算"网线上有流"的那段时间：这个阶段大部分时间花在 7z 上，把压缩算进分母
        // 量出来的既不是传输速度也不是墙钟吞吐（见 StageTracker.SpeedNow）。
        var uploadTracker = new StageTracker(
            "Uploading", total: 0, reporter.ReportUpload, speedWhileInFlight: true);
```

保留它上面原有的两行注释（总数边跑边长出来的那段）。

- [ ] **Step 2: 还原的 Restoring**

`RestoreOrchestrator.cs:257` 改为：

```csharp
            var tracker = onProgress is null
                ? null
                : new StageTracker("Restoring", groups.Count, onProgress, speedWhileInFlight: true);
```

- [ ] **Step 3: 检查的 Verifying**

`BackupChecker.cs:62-63` 的帮手改为：

```csharp
    /// <summary>阶段跟踪器的构造捷径：没人要进度就一路传 null，不产生任何开销。</summary>
    /// <param name="inFlight">这个阶段会不会登记在途项。只有会的（Verifying）才让测速时钟
    /// 随流启停；不会的（本地/列举/元数据）必须走墙钟，否则虚拟时钟永不前进、速度恒为 0。</param>
    private static StageTracker? Track(
        Action<StageProgress>? onProgress, string stage, int total, bool inFlight = false) =>
        onProgress is null ? null : new StageTracker(stage, total, onProgress, inFlight);
```

`:283` 那一句改为：

```csharp
        var tracker = Track(onProgress, "Verifying", presentGroups.Count, inFlight: true);
```

其余五处 `Track(...)` 调用不动（`LoadingIndex`、`Metadata`、`Local`、`Orphans`、`Cloud`）。`Cloud` 阶段只做 HEAD 后 `Advance`，不登记在途项，必须留在默认的 false。

- [ ] **Step 4: 修正设计文档里与实现不符的一句**

`docs/upload-speed-clock-design.md` 的「1. 虚拟上传时钟」一节里，把

> 两处都在 `_gate` 锁内完成，并顺手采一个样——段边界上必须切一刀，否则跨越边界的那对采样会把边界两侧的字节混在一个时间差里。

改为

> 两处都在 `_gate` 锁内完成，`_active` 的增删也一并挪进锁里——"是不是空的"与时钟开关必须在同一个临界区内定下来。段边界不需要额外强制采样：活跃段外没有字节流动，累计值本身就是准的。

- [ ] **Step 5: 跑全量测试**

Run: `cd backend && dotnet test AzureStorageBackup.slnx`
Expected: 全绿。

- [ ] **Step 6: 前端类型检查**

Run: `cd frontend && npx tsc --noEmit`
Expected: 无输出（前端不需要改动，此步只确认早先的文案改动没破坏编译）。

- [ ] **Step 7: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs backend/src/AzureStorageBackup.Api/Services/RestoreOrchestrator.cs backend/src/AzureStorageBackup.Api/Services/BackupChecker.cs docs/upload-speed-clock-design.md
git commit -F - <<'EOF'
feat(progress): read speed off the wire for upload, restore and verify

These three are the stages that register in-flight transfers and interleave
them with local work — packing for upload, extracting for restore, hashing for
verify. The other stages never register anything, so the in-flight clock would
never advance for them and their speed would read a flat zero.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
```

---

## 收尾

- [ ] 合并回 main 并删分支：

```bash
git checkout main && git merge --no-ff speed-clock-runs-only-while-uploading && git branch -d speed-clock-runs-only-while-uploading
```

- [ ] 推送：`git push origin main`
