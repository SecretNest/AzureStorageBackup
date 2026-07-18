namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 备份持久状态写入的共享助手（§4.2 决策 2）。成功→Normal（自清 Error），失败→Error+消息。
/// **best-effort**：写状态本身失败不掩盖/误判一次已确定的运行结果，但会记 Warning 留诊断痕迹
/// （原先五处各自 try/catch 静默吞——见 BackupRunner/RestoreRunner/RepairRunner/TaskDispatcher/check）。
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
