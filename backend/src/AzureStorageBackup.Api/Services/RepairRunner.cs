using Azure.Storage.Blobs.Models;
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
    string Status, IReadOnlyList<string>? Repaired, IReadOnlyList<string>? Unrecoverable,
    IReadOnlyList<string>? DeletedOrphans, string? Error)
{
    public static RepairRunResponse From(RepairRunState s) => new(
        s.Status.ToString(), s.Report?.Repaired, s.Report?.Unrecoverable, s.Report?.DeletedOrphans, s.Error);
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

    public RepairRunState Start(int configId, int? version, CloudCheckLevel cloud, StorageTier? rehydrate, bool cleanupOrphans)
    {
        lock (_lock)
        {
            if (_runs.TryGetValue(configId, out var existing) && existing.Status == RunStatus.Running)
                return existing;

            var state = new RepairRunState();
            _runs[configId] = state;
            _ = Task.Run(() => RunAsync(configId, version, cloud, rehydrate, cleanupOrphans, state));
            return state;
        }
    }

    public RepairRunState? Get(int configId)
    {
        lock (_lock)
            return _runs.GetValueOrDefault(configId);
    }

    private async Task RunAsync(int configId, int? version, CloudCheckLevel cloud, StorageTier? rehydrate, bool cleanupOrphans, RepairRunState state)
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

            if (!busy.TryAcquire(account.Id, config.ContainerName, "Repairing"))
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
                    // 显式转为 AccessTier?：见 BackupConfigEndpoints.cs /check 端点同处注释（真实生产 bug 修复）。
                    RehydrateTier = rehydrate is { } t ? (AccessTier?)BackupRequestMapper.MapTier(t) : null,
                    ListOrphans = cleanupOrphans,
                };
                state.Report = await sp.GetRequiredService<BackupRepairer>().RepairAsync(
                    account, config.ContainerName, sp.GetRequiredService<ISecretReader>().RevealBackupPassword(config),
                    config.LocalRoot, version, options, BackupRequestMapper.MapTier(config.DataTier),
                    config.VolumeBytes is > 0 ? config.VolumeBytes : settings.DefaultVolumeBytes,
                    // 与 BackupRequestMapper.From 取同一份规则：修好的归档要和全新备份写出的压缩方式一致。
                    BackupRequestMapper.OptionalRules(config.DontCompressRules));
                state.Status = RunStatus.Completed;
            }
            finally
            {
                busy.Release(account.Id, config.ContainerName);
            }
            await configs.WriteStatusAsync(configId, error: null, sp.GetService<ILogger<RepairRunner>>());
        }
        catch (Exception ex)
        {
            state.Error = ex.Message;
            state.Status = RunStatus.Failed;
            // 原 scope 可能已随异常释放（`using var scope` 在 try 块退出时释放）：另开一个写状态。
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .WriteStatusAsync(configId, ex.Message, scope.ServiceProvider.GetService<ILogger<RepairRunner>>());
        }
    }
}
