using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The sweep that fires when a check report retires ("在drop或者repair了所有文件时去做一次清理"). During the
/// remediation hold — a report awaiting repair — every automatic orphan sweep stands down (the sweep judges
/// by exact volume names, and a suspended repair's replacement volumes can outnumber the recorded family);
/// the moment the report is dropped, manually or by a fully-successful repair, the container gets the full
/// collection it was owed. Fire-and-forget and busy-gated: if the container is occupied, it simply skips —
/// the next unheld backup's tail sweep collects instead, so nothing is ever owed twice or lost.
/// </summary>
public sealed class OrphanSweeper(
    IServiceScopeFactory scopes, BackupBusyTracker busy, ILogger<OrphanSweeper>? logger = null)
{
    public void Kick(int configId)
    {
        _ = Task.Run(() => SweepAsync(configId));
    }

    private async Task SweepAsync(int configId)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var config = await sp.GetRequiredService<IBackupConfigService>().GetAsync(configId);
            if (config is null)
                return;
            var account = await sp.GetRequiredService<IAccountService>().GetAsync(config.AccountId);
            if (account is null)
                return;
            if (!busy.TryAcquire(account.Id, config.ContainerName, "CleaningUp"))
            {
                logger?.LogInformation(
                    "Post-report sweep for {Container} skipped: busy — the next backup's tail sweep collects instead",
                    config.ContainerName);
                return;
            }
            try
            {
                var settings = await sp.GetRequiredService<IGlobalSettingsService>().GetAsync();
                var password = sp.GetRequiredService<ISecretReader>().RevealBackupPassword(config);
                using var lease = sp.GetRequiredService<StagingArea>().AcquireLease();
                var cleanup = await sp.GetRequiredService<RetentionCleaner>().CleanupAsync(
                    account, config.ContainerName, password,
                    BackupRequestMapper.CleanupOf(config, settings), default, lease, sweepOrphans: true);
                if (!cleanup.IsEmpty)
                    await sp.GetRequiredService<IOperationLog>().AppendAsync(
                        OperationLogLevel.Info, $"sweep:{account.Id}/{config.ContainerName}",
                        BackupSummary.FormatRetention(cleanup), default, durable: true);
            }
            finally
            {
                busy.Release(account.Id, config.ContainerName);
            }
        }
        catch (Exception ex)
        {
            // Best effort by design: the ordinary post-backup sweep is the backstop.
            logger?.LogWarning(ex, "Post-report sweep for config {ConfigId} did not complete", configId);
        }
    }
}
