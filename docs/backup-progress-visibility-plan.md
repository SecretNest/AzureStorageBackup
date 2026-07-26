# 备份进度可见性 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让备份进度在刷新页面后仍可见，并让定时任务触发的备份第一次拥有进度。

**Architecture:** 后端把 `BackupRunner` 拆成「抢锁 + 即发即忘」的 `Start` 与「调用方已持锁」的 `RunTrackedAsync`，调度器改走后者，从而与界面按钮共用同一段执行体。前端删掉活在闭包里的轮询循环，改为由服务端权威状态驱动的统一轮询。

**Tech Stack:** .NET 10 / ASP.NET Core Minimal API / xUnit；React 19 + TypeScript + Vite。

设计文档：[backup-progress-visibility-design.md](backup-progress-visibility-design.md)

## Global Constraints

- **界面文案一律英文**。文档与代码注释用中文。
- **`frontend/package.json` 不得新增依赖。**
- **不引入前端测试框架。** 前端验证只有 `cd frontend && npm run build && npm run lint`。
- **不引入 SSE / WebSocket**，轮询即可。
- **进度只存内存，不落库**，不新增表、不新增迁移。
- **不改动调度器「忙碌 → 跳过并记 Warning 与操作日志」的分支**（`TaskDispatcher.cs:29-36`）。它发生在抢锁阶段，早于本轮涉及的执行阶段，且其可见行为是既有契约。
- **Check 不在修复范围**：`POST /backup-configs/{id}/check` 是同步端点，服务端无可查状态。不要为它编造轮询。
- 后端测试：`cd backend && dotnet test`。基线 **570 passed, 0 failed, 0 skipped**。
- 每个任务结束提交一次。

---

### Task 1: 拆分 BackupRunner 的两个入口

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs:34-101`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupRunnerTrackedTests.cs`

**Interfaces:**
- Consumes: `BackupBusyTracker`（`Services/` 下，方法 `TryAcquire(int accountId, string container, string activity)` 与 `Release(int accountId, string container)`）
- Produces:
  - `BackupRunState Start(int configId)` —— 签名与行为不变
  - `Task<BackupRunState> RunTrackedAsync(int configId, CancellationToken ct)` —— 新增。**不抢也不释放忙碌锁**，调用方须已持有该 (account, container) 的锁。返回终态的 `BackupRunState`，且该状态已登记进 `_runs`，`Get(configId)` 可查到

- [ ] **Step 1: 写失败测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/BackupRunnerTrackedTests.cs`：

```csharp
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// RunTrackedAsync 供调度器使用：调用方已持有忙碌锁，本方法不得再抢。
/// 若它照 Start 那样抢锁，每一次定时备份都会立刻失败——这正是本轮要修的缺陷。
/// </summary>
[Trait("Category", "Integration")]
public class BackupRunnerTrackedTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<(int AccountId, int ConfigId, string Container)> SeedAsync()
    {
        var container = "run-" + Guid.NewGuid().ToString("N")[..8];
        var acctRes = await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "runner-" + Guid.NewGuid().ToString("N")[..8],
            Description: null,
            BlobEndpoint: "http://127.0.0.1:10000/devstoreaccount1",
            Region: AzureRegion.Global,
            AccountKey: "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null));
        acctRes.EnsureSuccessStatusCode();
        var acct = await acctRes.Content.ReadFromJsonAsync<AccountResponse>();

        var cfgRes = await _client.PostAsJsonAsync("/api/backup-configs", new
        {
            AccountId = acct!.Id,
            ContainerName = container,
            Name = "runner-test",
            LocalRoot = Path.Combine(Path.GetTempPath(), "asb-runner-" + Guid.NewGuid().ToString("N")[..8]),
            IndexTier = StorageTier.Hot,
            DataTier = StorageTier.Hot,
        });
        cfgRes.EnsureSuccessStatusCode();
        var cfg = await cfgRes.Content.ReadFromJsonAsync<BackupConfigResponse>();
        return (acct.Id, cfg!.Id, container);
    }

    [Fact]
    public async Task RunTrackedAsync_Does_Not_Acquire_The_Busy_Lock()
    {
        var (accountId, configId, container) = await SeedAsync();
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();

        // 模拟调度器：调用方先持锁。
        Assert.True(busy.TryAcquire(accountId, container, "BackingUp"));
        try
        {
            var state = await runner.RunTrackedAsync(configId, CancellationToken.None);

            // 本地根不存在，备份多半失败——那没关系。要断言的是它没有
            // 因为「抢不到忙碌锁」而失败，因为那说明它抢了本不该抢的锁。
            Assert.DoesNotContain("busy", state.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }

    [Fact]
    public async Task RunTrackedAsync_Registers_State_For_Polling()
    {
        var (accountId, configId, container) = await SeedAsync();
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();

        Assert.True(busy.TryAcquire(accountId, container, "BackingUp"));
        try
        {
            await runner.RunTrackedAsync(configId, CancellationToken.None);
        }
        finally
        {
            busy.Release(accountId, container);
        }

        // 这条钉住界面能看到定时备份：状态必须留在 runner 里供 GET 端点查询。
        Assert.NotNull(runner.Get(configId));
    }

    [Fact]
    public async Task Start_Still_Acquires_The_Busy_Lock()
    {
        var (accountId, configId, container) = await SeedAsync();
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();

        // 别人已持锁 → Start 必须失败并说明忙碌，行为与改动前一致。
        Assert.True(busy.TryAcquire(accountId, container, "Checking"));
        try
        {
            var state = runner.Start(configId);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (state.Status == RunStatus.Running && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            Assert.Equal(RunStatus.Failed, state.Status);
            Assert.Contains("busy", state.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }
}
```

需要 `using System.Net.Http.Json;`。若 `TestWebAppFactory` 未暴露 `Services`，用 `factory.Services`（`WebApplicationFactory<T>` 的内置属性）即可。

- [ ] **Step 2: 跑测试确认失败**

Run: `cd backend && dotnet test --filter FullyQualifiedName~BackupRunnerTrackedTests`
Expected: 编译失败，`'BackupRunner' does not contain a definition for 'RunTrackedAsync'`

- [ ] **Step 3: 实现**

把 `BackupRunner.cs` 中 `Start`、`RunAsync` 两个成员替换为下列三个。执行体从 `RunAsync` 原样搬出，只把抢锁/释放锁那一层拿掉：

```csharp
    /// <summary>界面用：抢忙碌锁并在后台跑。同一配置已在运行则返回现有状态。</summary>
    public BackupRunState Start(int configId)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new BackupRunState();
            _runs[configId] = state;
            _ = Task.Run(() => RunOwningLockAsync(configId, state));
            return state;
        }
    }

    /// <summary>
    /// 调度器用：调用方**已持有**该 (account, container) 的忙碌锁
    /// （TaskDispatcher.DispatchAsync 在进入执行前就抢了）。本方法不抢也不释放，
    /// 只负责执行并把状态登记进 _runs 供 GET 端点轮询。
    ///
    /// 锁的归属由「调用哪个方法」表达，而不是由一个布尔参数表达：布尔值传错一次，
    /// 不是每次定时备份都拒跑，就是锁根本没人持有，而两种都不会在编译期暴露。
    /// </summary>
    public async Task<BackupRunState> RunTrackedAsync(int configId, CancellationToken ct)
    {
        BackupRunState state;
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            state = new BackupRunState();
            _runs[configId] = state;
        }

        await RunCoreAsync(configId, state, ct);
        return state;
    }

    /// <summary>Start 的执行体：抢锁 → 跑 → 释放。</summary>
    private async Task RunOwningLockAsync(int configId, BackupRunState state)
    {
        int accountId;
        string container;
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var config = await sp.GetRequiredService<IBackupConfigService>().GetAsync(configId)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            accountId = config.AccountId;
            container = config.ContainerName;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
            return;
        }

        // 标记该备份忙碌（供计划任务检测），已忙碌则拒绝并发操作。
        if (!busy.TryAcquire(accountId, container, "BackingUp"))
        {
            state.Error = "This backup is busy with another operation.";
            state.Status = RunStatus.Failed;
            return;
        }

        try
        {
            await RunCoreAsync(configId, state, CancellationToken.None);
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }

    /// <summary>两个入口共用的执行体。**不碰忙碌锁**——锁由调用方负责。</summary>
    private async Task RunCoreAsync(int configId, BackupRunState state, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var configs = sp.GetRequiredService<IBackupConfigService>();

            var config = await configs.GetAsync(configId, ct)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            var account = await sp.GetRequiredService<IAccountService>().GetAsync(config.AccountId, ct)
                ?? throw new InvalidOperationException($"Account {config.AccountId} not found.");
            var settings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync(ct);
            var password = sp.GetRequiredService<ISecretReader>().RevealBackupPassword(config);

            var result = await sp.GetRequiredService<BackupOrchestrator>().RunAsync(
                BackupRequestMapper.From(config, account, password, settings), new StateProgress(state), ct);
            state.Version = result.Version;
            state.Status = RunStatus.Completed;

            await configs.WriteStatusAsync(configId, error: null, sp.GetService<ILogger<BackupRunner>>());
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
            // 原 scope 可能已随异常释放（`using var scope` 在 try 块退出时释放）：另开一个写状态。
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .WriteStatusAsync(configId, ex.Message, scope.ServiceProvider.GetService<ILogger<BackupRunner>>());
        }
    }
```

若 `IBackupConfigService.GetAsync` / `IGlobalSettingsService.GetAsync` 的可选 `CancellationToken` 参数签名与上面不符，以接口定义为准调整，不要改接口。

- [ ] **Step 4: 跑测试确认通过**

Run: `cd backend && dotnet test --filter FullyQualifiedName~BackupRunnerTrackedTests`
Expected: PASS，3 个用例全绿

- [ ] **Step 5: 跑全量**

Run: `cd backend && dotnet test`
Expected: 全绿，基线 570 之上只增不减

- [ ] **Step 6: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupRunnerTrackedTests.cs
git commit -m "refactor: split the backup runner into lock-owning and caller-owns-lock entry points"
```

---

### Task 2: 调度器改走 RunTrackedAsync

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs:100-107`（`ScheduledTaskType.Backup` 分支）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/ScheduledBackupProgressTests.cs`

**Interfaces:**
- Consumes: `Task<BackupRunState> BackupRunner.RunTrackedAsync(int configId, CancellationToken ct)` 与 `BackupRunState? BackupRunner.Get(int configId)`（Task 1）
- Produces: 无新公开 API

- [ ] **Step 1: 写失败测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/ScheduledBackupProgressTests.cs`：

```csharp
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 定时备份此前绕开 BackupRunner 直接调 BackupOrchestrator，进度回调传的是 null，
/// 因此界面永远查不到它的状态。而定时备份恰恰是常态。
/// </summary>
[Trait("Category", "Integration")]
public class ScheduledBackupProgressTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task A_Scheduled_Backup_Leaves_State_The_UI_Can_Poll()
    {
        var container = "sched-" + Guid.NewGuid().ToString("N")[..8];
        var acctRes = await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "sched-" + Guid.NewGuid().ToString("N")[..8],
            Description: null,
            BlobEndpoint: "http://127.0.0.1:10000/devstoreaccount1",
            Region: AzureRegion.Global,
            AccountKey: "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null));
        acctRes.EnsureSuccessStatusCode();
        var acct = await acctRes.Content.ReadFromJsonAsync<AccountResponse>();

        var cfgRes = await _client.PostAsJsonAsync("/api/backup-configs", new
        {
            AccountId = acct!.Id,
            ContainerName = container,
            Name = "sched-test",
            LocalRoot = Path.Combine(Path.GetTempPath(), "asb-sched-" + Guid.NewGuid().ToString("N")[..8]),
            IndexTier = StorageTier.Hot,
            DataTier = StorageTier.Hot,
        });
        cfgRes.EnsureSuccessStatusCode();
        var cfg = await cfgRes.Content.ReadFromJsonAsync<BackupConfigResponse>();

        var taskRes = await _client.PostAsJsonAsync("/api/tasks", new
        {
            TargetKind = TaskTargetKind.Backup,
            AccountId = acct.Id,
            ContainerName = container,
            GroupId = (int?)null,
            TaskType = ScheduledTaskType.Backup,
            CronExpression = "0 2 * * *",
            Enabled = true,
        });
        taskRes.EnsureSuccessStatusCode();
        var task = await taskRes.Content.ReadFromJsonAsync<ScheduledTaskResponse>();

        // 立即执行该计划任务，走的是调度器的分发路径。
        (await _client.PostAsync($"/api/tasks/{task!.Id}/run", null)).EnsureSuccessStatusCode();

        // 备份多半会失败（本地根不存在），但**必须留下可轮询的状态**。
        // 修复前这里是 null：调度器根本没经过 BackupRunner。
        var runner = factory.Services.GetRequiredService<BackupRunner>();
        Assert.NotNull(runner.Get(cfg!.Id));
    }
}
```

需要 `using System.Net.Http.Json;`。`ScheduledTaskResponse` 与「立即执行」端点的实际名称以 `Endpoints/TaskEndpoints.cs` 为准——先读该文件确认路由与响应类型，再照实调整这两处。若立即执行是异步返回，在断言前轮询等待至多 10 秒。

- [ ] **Step 2: 跑测试确认失败**

Run: `cd backend && dotnet test --filter FullyQualifiedName~ScheduledBackupProgressTests`
Expected: FAIL，`Assert.NotNull` 失败——因为调度器没经过 runner

- [ ] **Step 3: 实现**

把 `TaskDispatcher.cs` 的 `ScheduledTaskType.Backup` 分支替换为：

```csharp
                case ScheduledTaskType.Backup:
                    // 与界面按钮走同一条执行体，这样定时备份也有进度可查。
                    // 用 RunTrackedAsync 而非 Start：DispatchAsync 已为该目标持有忙碌锁，
                    // Start 会再抢一次并必然失败，把每一次定时备份都变成「busy」。
                    var backupState = await sp.GetRequiredService<BackupRunner>()
                        .RunTrackedAsync(config.Id, ct);
                    // 执行体吞掉异常、只把失败写进 state，所以这里必须显式抛出，
                    // 否则下方 catch 不会触发，失败会被 WriteStatusAsync(null) 记成成功。
                    if (backupState.Status == RunStatus.Failed)
                        throw new InvalidOperationException(backupState.Error ?? "Backup failed.");
                    break;
```

同时删掉该分支原先取 `IGlobalSettingsService` 并构造 `BackupRequestMapper.From(...)` 的两行——执行体已经自己做了。

若删除后 `password` 或 `account` 变量在本方法内不再被其它分支使用，编译器会报未使用警告；此时**不要**删除它们，Check 与 Cleanup 分支仍在用。确认后再动。

- [ ] **Step 4: 跑测试确认通过**

Run: `cd backend && dotnet test --filter FullyQualifiedName~ScheduledBackupProgressTests`
Expected: PASS

- [ ] **Step 5: 跑全量，重点看调度器既有测试**

Run: `cd backend && dotnet test`
Expected: 全绿。`SchedulerServiceTests`、`ScheduledTaskServiceTests`、`TaskRunEndpointsTests` 尤其不得回归——它们覆盖忙碌跳过与失败记录。若有失败，逐个说明原因再改，不要放松断言。

- [ ] **Step 6: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/TaskDispatcher.cs \
        backend/tests/AzureStorageBackup.Api.Tests/ScheduledBackupProgressTests.cs
git commit -m "fix: give scheduled backups the progress the UI has always tried to show"
```

---

### Task 3: 前端改为服务端驱动的轮询

**Files:**
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`（`load`、`run`、`pollRestore`、行内按钮，约 82-115、258-280、396-425 行）

**Interfaces:**
- Consumes: 现有 `backupConfigsApi.runStatus/restoreStatus/repairStatus`，以及 `BackupConfig.activity`（类型 `BackupActivity = 'Idle' | 'BackingUp' | 'Restoring' | 'Checking' | 'Repairing' | 'CleaningUp'`）
- Produces: 无对外接口

- [ ] **Step 1: 加入 repairs 状态并统一轮询**

在 `const [restores, setRestores] = useState<Record<number, RestoreRun>>({})` 之后加入：

```typescript
  const [repairs, setRepairs] = useState<Record<number, RepairRun>>({})
```

在 `useEffect(load, [])` 之后加入统一轮询。**它取代闭包里的循环**：状态来自服务端，因此刷新页面、换标签页、或备份由定时任务发起，看到的都一样。

```typescript
  // 列表每 5 秒刷一次：纯本地查询（配置行 + 内存中的 activity），不连云。
  useEffect(() => {
    const t = setInterval(load, 5000)
    return () => clearInterval(t)
  }, [])

  // 有活跃项时，只对活跃的那几份、且只拉该 activity 对应的那一个端点。
  // 全空闲时不发这些请求。
  useEffect(() => {
    const active = configs.filter((c) => c.activity !== 'Idle')
    if (active.length === 0) return

    let cancelled = false
    const tick = async () => {
      await Promise.all(
        active.map(async (c) => {
          try {
            if (c.activity === 'BackingUp') {
              const s = await backupConfigsApi.runStatus(c.id)
              if (!cancelled) setRuns((r) => ({ ...r, [c.id]: s }))
            } else if (c.activity === 'Restoring') {
              const s = await backupConfigsApi.restoreStatus(c.id)
              if (!cancelled) setRestores((r) => ({ ...r, [c.id]: s }))
            } else if (c.activity === 'Repairing') {
              const s = await backupConfigsApi.repairStatus(c.id)
              if (!cancelled) setRepairs((r) => ({ ...r, [c.id]: s }))
            }
            // Checking 与 CleaningUp 没有状态端点：只显示徽章，不拉进度。
          } catch {
            // 单次轮询失败不值得打断整页，下一拍会重试。
          }
        }),
      )
    }

    const t = setInterval(tick, 1000)
    void tick()
    return () => {
      cancelled = true
      clearInterval(t)
    }
  }, [configs])
```

`RepairRun` 若尚未从 `../api/backupConfigs` 导出，补上导入。

- [ ] **Step 2: 删掉闭包里的循环**

`run` 改为只负责发起——轮询由上一步的机制接手：

```typescript
  const run = async (c: BackupConfig) => {
    setError(null)
    try {
      const state = await backupConfigsApi.run(c.id)
      setRuns((r) => ({ ...r, [c.id]: state }))
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }
```

`pollRestore` 同样去掉循环，只写入首个状态：

```typescript
  const pollRestore = (id: number, state: RestoreRun) => {
    setRestores((r) => ({ ...r, [id]: state }))
    load()
  }
```

它的调用点若是 `await pollRestore(...)`，去掉 `await` 或保留皆可（函数已非 async 时 `await` 无害），但需确认调用点不再依赖它「等到完成才返回」——若有依赖，改为不等待并在报告中说明。

- [ ] **Step 3: 行内按钮改用服务端 activity**

把 `Run` 按钮那一段（约 400-409 行）替换为：

```tsx
                  <button
                    type="button"
                    className="btn-ghost"
                    onClick={() => run(c)}
                    disabled={keyringLost || c.activity !== 'Idle'}
                    title={keyringLostHint}
                  >
                    {c.activity === 'BackingUp' ? 'Backing up…' : 'Backup'}
                  </button>{' '}
```

两处改动的理由：文案 `Run` 与同一行的 `Restore`、`Check/Repair` 并列时不表意，改为 `Backup`；禁用条件从组件本地的 `runs[c.id]` 改为服务端的 `c.activity`，这样定时任务正在跑时按钮也会正确禁用。

Restore 按钮的 `disabled` 同样从 `restores[c.id]?.status === 'Running'` 改为 `c.activity !== 'Idle'`。Check/Repair 按钮若有类似条件，一并改。

- [ ] **Step 4: 行上显示修复进度**

行内进度目前渲染在第 434-435 行：

```tsx
                  {runs[c.id] && <RunStatus run={runs[c.id]} />}
                  {restores[c.id] && <RestoreStatus run={restores[c.id]} />}
```

在其后补一行：

```tsx
                  {repairs[c.id] && <RepairStatus run={repairs[c.id]} />}
```

并在 `RunStatus`（约 871 行）旁新增同形的组件。三态与 `RunStatus` 一致，只是修复没有 version：

```tsx
function RepairStatus({ run }: { run: RepairRun }) {
  if (run.status === 'Failed')
    return <div className="text-danger">Repair failed: {run.error}</div>
  if (run.status === 'Completed')
    return <div className="text-ok">Repair completed</div>
  return <div className="text-faint">Repairing…</div>
}
```

若 `RepairRun` 带有可展示的进度或计数字段，按 `RunStatus` 处理 `run.progress` 的同样方式加进来；没有就保持上面这样。先读 `frontend/src/api/backupConfigs.ts` 里 `RepairRun` 的定义再决定，不要凭空假设字段名。

**`CheckModal` 内部的轮询保持不变**：它在修复完成后会自动再跑一次 check 并展示报告，把那套逻辑提到页面级需要重构整个模态框，超出本轮范围。代价是模态框开着时该配置会被轮询两次（页面级一次、模态框一次），每秒一次的本地查询，可接受。这是有意的取舍，不是遗漏。

- [ ] **Step 5: 构建与 lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 均通过

- [ ] **Step 6: 确认闭包循环已不存在**

Run: `cd frontend && grep -n "while (.*status === 'Running')" src/pages/BackupConfigsPage.tsx`
Expected: 只剩 `CheckModal` 内 `runRepair` 的那一处（Step 4 有意保留）。若 `run` 或 `pollRestore` 里还有，说明没删干净。

- [ ] **Step 7: 提交**

```bash
git add frontend/src/pages/BackupConfigsPage.tsx
git commit -m "fix: drive run state from the server so it survives a refresh"
```

---

### Task 4: 全量验证

- [ ] **Step 1: 后端全量**

Run: `cd backend && dotnet test`
Expected: 全绿，记录数量与 570 基线对比

- [ ] **Step 2: 前端**

Run: `cd frontend && npm run build && npm run lint`
Expected: 均通过

- [ ] **Step 3: 确认没有为 Check 编造轮询**

Run: `cd frontend && grep -n "checkStatus\|'Checking'" src/pages/BackupConfigsPage.tsx src/api/backupConfigs.ts`
Expected: 没有任何 `checkStatus` 之类的调用。`'Checking'` 只应出现在徽章显示与轮询的排除逻辑中。后端没有该端点，编造一个只会是 404。

- [ ] **Step 4: 手工核对（人工）**

前端无自动化测试。需人工确认：发起备份后刷新页面，进度仍在；定时任务触发的备份在界面上能看到百分比；全部空闲时浏览器开发者工具的网络面板只有每 5 秒一次的列表请求，没有持续的状态请求；离开该页面后请求停止。

- [ ] **Step 5: 最终提交**

```bash
git add -A
git commit -m "chore: verify progress visibility across refresh and scheduled runs"
```

若无改动则不提交空提交。

---

## 交付说明

前端无自动化测试，本轮亦不引入（设计文档 §5）。Task 3 的轮询启停、卸载清理、刷新后恢复只能人工核对。

**Check 刷新后仍会丢失**，这是本轮唯一没有修好的操作，原因是服务端缺少可查状态而非前端没去查（设计 §3.4）。汇报时须说明，不要让它被误认为一并修好了。
