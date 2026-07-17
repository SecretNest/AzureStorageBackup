using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>一次修复运行的内存状态。</summary>
public sealed class RepairRunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;
    public RepairReport? Report { get; set; }
    public string? Error { get; set; }
}

public sealed record RepairRunResponse(
    string Status, IReadOnlyList<string>? Repaired, IReadOnlyList<string>? Unrecoverable, string? Error)
{
    public static RepairRunResponse From(RepairRunState s) => new(
        s.Status.ToString(), s.Report?.Repaired, s.Report?.Unrecoverable, s.Error);
}

/// <summary>
/// 后台修复运行器：跑 <see cref="BackupRepairer"/>，状态存内存供轮询。
/// **持有 <see cref="BackupBusyTracker"/> 到完成**——修复改 blob/索引且涉及去重共享，必须独占，
/// 期间该备份不能做备份/检查/其它修复（用户要求）。目标忙碌时直接失败。
/// </summary>
public sealed class RepairRunner(IServiceScopeFactory scopes, BackupBusyTracker busy)
{
    private readonly Dictionary<int, RepairRunState> _runs = [];
    private readonly Lock _lock = new();

    public RepairRunState Start(int configId, int? version, CloudCheckLevel cloud, StorageTier? rehydrate)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new RepairRunState();
            _runs[configId] = state;
            _ = Task.Run(() => RunAsync(configId, version, cloud, rehydrate, state));
            return state;
        }
    }

    public RepairRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    private async Task RunAsync(int configId, int? version, CloudCheckLevel cloud, StorageTier? rehydrate, RepairRunState state)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var config = await sp.GetRequiredService<IBackupConfigService>().GetAsync(configId)
                ?? throw new InvalidOperationException($"Backup config {configId} not found.");
            var account = await sp.GetRequiredService<IAccountService>().GetAsync(config.AccountId)
                ?? throw new InvalidOperationException($"Account {config.AccountId} not found.");
            var settings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync();

            if (!busy.TryAcquire(account.Id, config.ContainerName))
            {
                state.Error = "This backup is busy with another operation.";
                state.Status = RunStatus.Failed;
                return;
            }
            try
            {
                var options = new CheckOptions
                {
                    Cloud = cloud,
                    RehydrateTier = rehydrate is { } t ? BackupRequestMapper.MapTier(t) : null,
                };
                state.Report = await sp.GetRequiredService<BackupRepairer>().RepairAsync(
                    account, config.ContainerName, string.IsNullOrEmpty(config.Password) ? null : config.Password,
                    config.LocalRoot, version, options, BackupRequestMapper.MapTier(config.DataTier),
                    config.VolumeBytes is > 0 ? config.VolumeBytes : settings.DefaultVolumeBytes);
                state.Status = RunStatus.Completed;
            }
            finally
            {
                busy.Release(account.Id, config.ContainerName);
            }
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
        }
    }
}
