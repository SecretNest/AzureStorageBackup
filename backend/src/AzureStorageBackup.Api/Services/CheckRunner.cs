using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>一次检查运行的内存状态。</summary>
public sealed class CheckRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>最近一次跑完的报告。**跑完之后仍然留着**：用户关掉对话框再打开要能看回结果，
    /// 而一次内容级检查要把整个备份下载重算一遍 hash，重跑的代价是实打实的出站流量。</summary>
    public CheckReport? Report { get; set; }

    public string? Error { get; set; }

    /// <summary>当前阶段在做什么（在查哪个对象、已查多少、多快）。</summary>
    public StageProgress? Detail { get; set; }

    /// <summary>内部机制，不进 HTTP 契约：本次运行的取消源，供 /cancel 端点用。</summary>
    internal CancellationTokenSource Cancellation { get; } = new();
}

public sealed record CheckRunResponse(string Status, CheckReport? Report, string? Error, StageProgress? Detail)
{
    public static CheckRunResponse From(CheckRunState s) => new(s.Status.ToString(), s.Report, s.Error, s.Detail);
}

/// <summary>
/// 后台检查运行器：跑 <see cref="BackupChecker"/>，状态存内存供轮询。
/// <para>
/// 检查此前是**同步端点**：请求一直挂到检查结束。内容级检查要下载全部数据重算 hash，
/// 几百 GB 的备份要跑几小时——浏览器和反向代理都会先超时，请求断了检查也就白跑了，
/// 而且全程没有任何进度可看。改成后台运行 + 轮询之后，这两件事一起解决。
/// </para>
/// 与 <see cref="RepairRunner"/> 同形：**持有 <see cref="BackupBusyTracker"/> 到完成**
/// （检查也是对该备份的操作，期间计划任务应当跳过），目标忙碌时直接失败。
/// </summary>
public sealed class CheckRunner(IServiceScopeFactory scopes, BackupBusyTracker busy)
{
    private readonly Dictionary<int, CheckRunState> _runs = [];
    private readonly Lock _lock = new();

    public CheckRunState Start(int configId, int? version, CheckOptions options)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new CheckRunState();
            _runs[configId] = state;
            _ = Task.Run(() => RunAsync(configId, version, options, state));
            return state;
        }
    }

    public CheckRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    /// <summary>停止正在跑的那次检查。返回 false = 当前没有在跑的检查。
    /// Cancel() 在当前线程同步跑回调，故取记录用锁、取消不用（见 BackupRunner.Cancel 同处注释）。</summary>
    public bool Cancel(int configId)
    {
        CheckRunState? state;
        lock (_lock)
            state = _runs.GetValueOrDefault(configId);
        if (state is not { Status: RunStatus.Running })
            return false;
        state.Cancellation.Cancel();
        return true;
    }

    private async Task RunAsync(int configId, int? version, CheckOptions options, CheckRunState state)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var configs = sp.GetRequiredService<IBackupConfigService>();
            var config = await configs.GetAsync(configId)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            var account = await sp.GetRequiredService<IAccountService>().GetAsync(config.AccountId)
                ?? throw new InvalidOperationException($"Account {config.AccountId} not found.");
            var settings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync();

            if (!busy.TryAcquire(account.Id, config.ContainerName, "Checking"))
            {
                state.Error = "This backup is busy with another operation.";
                state.Status = RunStatus.Failed;
                return;
            }
            try
            {
                state.Report = await sp.GetRequiredService<BackupChecker>().CheckAsync(
                    account, config.ContainerName, sp.GetRequiredService<ISecretReader>().RevealBackupPassword(config),
                    version, options, config.LocalRoot, state.Cancellation.Token,
                    downloadConcurrency: settings.DownloadConcurrency > 0 ? settings.DownloadConcurrency : 5,
                    onProgress: d => state.Detail = d);
                state.Status = RunStatus.Completed;
            }
            finally
            {
                busy.Release(account.Id, config.ContainerName);
            }
            // 检查跑完（无论是否发现问题）算成功；只有异常才置 Error（决策 2）。
            await configs.WriteStatusAsync(configId, error: null, sp.GetService<ILogger<CheckRunner>>());
        }
        catch (OperationCanceledException)
        {
            // 用户按了停止：不是失败，不写 Error 状态（与 BackupRunner 同一约定，也与旧的
            // 同步 /check 端点「取消不写 Error」保持一致）。
            state.Status = RunStatus.Canceled;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
            // 原 scope 可能已随异常释放（`using var scope` 在 try 块退出时释放）：另开一个写状态。
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .WriteStatusAsync(configId, ex.Message, scope.ServiceProvider.GetService<ILogger<CheckRunner>>());
        }
    }
}
