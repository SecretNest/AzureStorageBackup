using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

public class BackupConfigService(AppDbContext db) : IBackupConfigService
{
    /// <summary>Ordered by name. <c>COLLATE NOCASE</c> is not optional polish: SQLite's default ordering
    /// compares byte code points, so every uppercase letter sorts ahead of every lowercase one ("Zoo" lands before "apple"),
    /// which on screen simply looks like "not sorted alphabetically".</summary>
    public async Task<IReadOnlyList<BackupConfig>> ListAsync(CancellationToken ct = default) =>
        await db.BackupConfigs.AsNoTracking()
            .OrderBy(c => EF.Functions.Collate(c.Name, "NOCASE")).ToListAsync(ct);

    public async Task<BackupConfig?> GetAsync(int id, CancellationToken ct = default) =>
        await db.BackupConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<BackupConfig?> FindAsync(int accountId, string containerName, CancellationToken ct = default) =>
        await db.BackupConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AccountId == accountId && c.ContainerName == containerName, ct);

    public async Task<BackupConfig> CreateAsync(BackupConfig config, CancellationToken ct = default)
    {
        if (config.CreatedAt == default)
            config.CreatedAt = DateTimeOffset.UtcNow;

        db.BackupConfigs.Add(config);
        await db.SaveChangesAsync(ct);
        return config;
    }

    /// <summary>
    /// Update a config. The base fields (AccountId/ContainerName/LocalRoot/IndexTier/DataTier) and the password are locked
    /// after creation (§4.5): the local authoritative state (TrackedInfoStore/LocalIndexCache) is keyed by account+container, so changing these fields
    /// desynchronizes it from the cloud/local index. Throws <see cref="InvalidOperationException"/> when a change is detected; the endpoint maps that to 400.
    ///
    /// <para>
    /// LocalRoot has its own validated channel, <see cref="ChangeLocalRootAsync"/> (for moving the mount point).
    /// **That does not relax the check here**: the regular edit path still refuses to change the root, otherwise one casual rename-and-save could quietly swap the root out,
    /// bypassing every safeguard on that channel.
    /// </para>
    /// </summary>
    public async Task<BackupConfig?> UpdateAsync(int id, BackupConfig update, CancellationToken ct = default)
    {
        var existing = await db.BackupConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (existing is null)
            return null;

        if (existing.AccountId != update.AccountId
            || existing.ContainerName != update.ContainerName
            || existing.LocalRoot != update.LocalRoot
            || existing.IndexTier != update.IndexTier
            || existing.DataTier != update.DataTier)
            throw new InvalidOperationException("Base fields cannot be changed after creation.");

        // The password cannot be changed after creation (design decision 8). Empty = keep the existing value; anything non-empty is refused, and resets go through the dedicated endpoint.
        if (!string.IsNullOrEmpty(update.PasswordProtected))
            throw new InvalidOperationException("Password cannot be changed after creation; leave it empty.");

        existing.Name = update.Name;
        existing.Description = update.Description;
        existing.IgnoreRules = update.IgnoreRules;
        existing.DontCompressRules = update.DontCompressRules;
        existing.DontGroupRules = update.DontGroupRules;
        existing.CrossDirGroupRules = update.CrossDirGroupRules;
        // Scope is editable (it is not one of the locked base fields); a change takes effect on the next backup.
        existing.ScopeRules = update.ScopeRules;
        existing.IncludeSymlinks = update.IncludeSymlinks;
        existing.MaxVersions = update.MaxVersions;
        existing.MaxAgeDays = update.MaxAgeDays;
        existing.RetentionMode = update.RetentionMode;
        existing.SingleFileThresholdBytes = update.SingleFileThresholdBytes;
        existing.GroupCapBytes = update.GroupCapBytes;
        existing.VolumeBytes = update.VolumeBytes;
        existing.VerboseLogging = update.VerboseLogging;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    /// <inheritdoc />
    public async Task<BackupConfig?> ChangeLocalRootAsync(int id, string newRoot, CancellationToken ct = default)
    {
        var existing = await db.BackupConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (existing is null)
            return null;

        // Touch this one field only. ScopeRules in particular must not be rewritten along the way: they are coordinates relative to the root,
        // so when the new root holds the same data the rules keep matching correctly, unchanged.
        existing.LocalRoot = newRoot;
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        var existing = await db.BackupConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (existing is null)
            return false;

        db.BackupConfigs.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task SetErrorAsync(int id, string message, CancellationToken ct = default)
    {
        var existing = await db.BackupConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (existing is null)
            return;

        existing.Status = BackupStatus.Error;
        existing.LastError = message;
        existing.LastErrorAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetNormalAsync(int id, CancellationToken ct = default)
    {
        var existing = await db.BackupConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (existing is null)
            return;

        existing.Status = BackupStatus.Normal;
        existing.LastError = null;
        existing.LastErrorAt = null;
        await db.SaveChangesAsync(ct);
    }

    // Manual reset shares its implementation with "auto-clear on success" (decision 2).
    public Task ResetStatusAsync(int id, CancellationToken ct = default) => SetNormalAsync(id, ct);
}
