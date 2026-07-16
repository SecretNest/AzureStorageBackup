using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>备份配置的增删改查。加密密码加解密对调用方透明（仿 M1 Account）。</summary>
public interface IBackupConfigService
{
    Task<IReadOnlyList<BackupConfig>> ListAsync(CancellationToken ct = default);
    Task<BackupConfig?> GetAsync(int id, CancellationToken ct = default);
    Task<BackupConfig> CreateAsync(BackupConfig config, CancellationToken ct = default);
    Task<BackupConfig?> UpdateAsync(int id, BackupConfig update, CancellationToken ct = default);
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);
}
