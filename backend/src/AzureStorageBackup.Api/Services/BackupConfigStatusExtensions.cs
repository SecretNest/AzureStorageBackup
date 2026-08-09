namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The shared helper for writing a backup's persistent status (§4.2, decision 2). Success → Normal
/// (clearing Error), failure → Error plus a message.
/// **Best-effort**: a failure to write the status must not mask or misreport an already-determined run
/// result, but it logs a Warning so the diagnostic trace survives (five sites used to swallow it in their
/// own try/catch — BackupRunner, RestoreRunner, RepairRunner, TaskDispatcher and check).
/// </summary>
public static class BackupConfigStatusExtensions
{
    public static async Task WriteStatusAsync(
        this IBackupConfigService configs, int configId, string? error,
        ILogger? logger = null, CancellationToken ct = default)
    {
        try
        {
            if (error is null)
                await configs.SetNormalAsync(configId, ct);
            else
                await configs.SetErrorAsync(configId, error, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to persist backup status for config {ConfigId}", configId);
        }
    }
}
