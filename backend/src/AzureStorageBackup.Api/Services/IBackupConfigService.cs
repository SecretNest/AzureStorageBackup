using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>CRUD for backup configs. Encrypting/decrypting the password is transparent to callers (mirrors M1 Account).</summary>
public interface IBackupConfigService
{
    Task<IReadOnlyList<BackupConfig>> ListAsync(CancellationToken ct = default);
    Task<BackupConfig?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>Find the config for a target (account, container); null if there is none. Scheduled tasks/groups identify a backup by (AccountId, ContainerName).</summary>
    Task<BackupConfig?> FindAsync(int accountId, string containerName, CancellationToken ct = default);
    Task<BackupConfig> CreateAsync(BackupConfig config, CancellationToken ct = default);
    Task<BackupConfig?> UpdateAsync(int id, BackupConfig update, CancellationToken ct = default);

    /// <summary>
    /// Migrate the local root path (design docs/change-local-root-design.md). Changes exactly one field, LocalRoot,
    /// and nothing else — ScopeRules are coordinates relative to the root, so their meaning is unchanged after the move and they must be kept verbatim.
    /// Validation is done beforehand by the caller (the endpoint); this method only writes to the database. Returns null if the config does not exist.
    /// </summary>
    Task<BackupConfig?> ChangeLocalRootAsync(int id, string newRoot, CancellationToken ct = default);

    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Operation failed: set Error + record the message + a timestamp (§4.2 decision 2). Silently ignored if the id does not exist.</summary>
    Task SetErrorAsync(int id, string message, CancellationToken ct = default);

    /// <summary>Operation succeeded: clear back to Normal (the next successful operation of the same kind auto-clears the error). Silently ignored if the id does not exist.</summary>
    Task SetNormalAsync(int id, CancellationToken ct = default);

    /// <summary>Manual reset: same semantics as <see cref="SetNormalAsync"/>.</summary>
    Task ResetStatusAsync(int id, CancellationToken ct = default);
}
