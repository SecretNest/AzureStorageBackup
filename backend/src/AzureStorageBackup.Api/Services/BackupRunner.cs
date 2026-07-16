using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

public enum RunStatus
{
    Running,
    Completed,
    Failed,
}

/// <summary>一次备份运行的内存状态（前端轮询用）。</summary>
public sealed class BackupRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;
    public BackupProgress? Progress { get; set; }
    public int? Version { get; set; }
    public string? Error { get; set; }
}

public sealed record BackupRunResponse(string Status, BackupProgress? Progress, int? Version, string? Error)
{
    public static BackupRunResponse From(BackupRunState s) =>
        new(s.Status.ToString(), s.Progress, s.Version, s.Error);
}

/// <summary>
/// 后台备份运行器：按配置 id 在后台跑 BackupOrchestrator，进度存内存供轮询。
/// 同一配置正在运行时不重复启动。压缩全局非并发由单例 StagingArea 保证。
/// </summary>
public sealed class BackupRunner(IServiceScopeFactory scopes)
{
    private readonly Dictionary<int, BackupRunState> _runs = [];
    private readonly Lock _lock = new();

    public BackupRunState Start(int configId)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new BackupRunState();
            _runs[configId] = state;
            _ = Task.Run(() => RunAsync(configId, state));
            return state;
        }
    }

    public BackupRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    private async Task RunAsync(int configId, BackupRunState state)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var configs = sp.GetRequiredService<IBackupConfigService>();
            var accounts = sp.GetRequiredService<IAccountService>();
            var orchestrator = sp.GetRequiredService<BackupOrchestrator>();

            var config = await configs.GetAsync(configId)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            var account = await accounts.GetAsync(config.AccountId)
                ?? throw new InvalidOperationException($"Account {config.AccountId} not found.");

            var result = await orchestrator.RunAsync(
                BuildRequest(config, account), new StateProgress(state), CancellationToken.None);

            state.Version = result.Version;
            state.Status = RunStatus.Completed;
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
        }
    }

    private static BackupRequest BuildRequest(BackupConfig config, Account account) => new()
    {
        Account = account,
        Container = config.ContainerName,
        LocalRoot = config.LocalRoot,
        Name = config.Name,
        Description = config.Description,
        Password = string.IsNullOrEmpty(config.Password) ? null : config.Password,
        IndexTier = MapTier(config.IndexTier),
        DataTier = MapTier(config.DataTier),
        Options = new BackupEngineOptions
        {
            Ignore = new IgnoreRuleSet(SplitLines(config.IgnoreRules)),
            DontCompress = OptionalRules(config.DontCompressRules),
            DontGroup = OptionalRules(config.DontGroupRules),
            Scan = new ScanOptions { IncludeSymlinks = config.IncludeSymlinks },
            Plan = new PlanOptions
            {
                SingleFileThresholdBytes = config.SingleFileThresholdBytes,
                GroupCapBytes = config.GroupCapBytes,
            },
            Retention = new RetentionPolicy
            {
                MaxVersions = config.MaxVersions,
                MaxAgeDays = config.MaxAgeDays,
                Mode = config.RetentionMode,
            },
        },
    };

    private static AccessTier MapTier(StorageTier tier) => tier switch
    {
        StorageTier.Cool => AccessTier.Cool,
        StorageTier.Cold => AccessTier.Cold,
        StorageTier.Archive => AccessTier.Archive,
        _ => AccessTier.Hot,
    };

    private static IgnoreRuleSet? OptionalRules(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new IgnoreRuleSet(SplitLines(text));

    private static IEnumerable<string> SplitLines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private sealed class StateProgress(BackupRunState state) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => state.Progress = value;
    }
}
