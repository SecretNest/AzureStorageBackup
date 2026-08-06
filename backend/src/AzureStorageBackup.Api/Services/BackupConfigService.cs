using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

public class BackupConfigService(AppDbContext db) : IBackupConfigService
{
    /// <summary>按名字排序。<c>COLLATE NOCASE</c> 不是可有可无的润色：SQLite 的默认排序是
    /// 按字节码点比的，大写字母全部排在小写字母前面（"Zoo" 会跑到 "apple" 前面），
    /// 在界面上看就是「没按字母排」。</summary>
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
    /// 更新配置。基础字段（AccountId/ContainerName/LocalRoot/IndexTier/DataTier）与密码创建后锁定
    /// （§4.5）：本地权威状态（TrackedInfoStore/LocalIndexCache）按 账户+container 键控，改这些字段会与云端/本地
    /// 索引失步。检测到变更时抛 <see cref="InvalidOperationException"/>，端点映射为 400。
    ///
    /// <para>
    /// LocalRoot 另有一条带校验的专用通道 <see cref="ChangeLocalRootAsync"/>（挂载点搬家用）。
    /// **这里的检查不因此放松**：常规编辑路径继续拒绝改根，否则一次顺手的改名保存就能悄悄换掉根，
    /// 绕开那条通道的全部防呆。
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

        // 密码创建后不可更改（设计决策 8）。空 = 保留原值；非空一律拒绝，重设走专用端点。
        if (!string.IsNullOrEmpty(update.PasswordProtected))
            throw new InvalidOperationException("Password cannot be changed after creation; leave it empty.");

        existing.Name = update.Name;
        existing.Description = update.Description;
        existing.IgnoreRules = update.IgnoreRules;
        existing.DontCompressRules = update.DontCompressRules;
        existing.DontGroupRules = update.DontGroupRules;
        existing.CrossDirGroupRules = update.CrossDirGroupRules;
        // 范围可改（不属于锁定的基础字段），改后下次备份生效。
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

        // 只动这一个字段。ScopeRules 尤其不能顺手改写：它是相对根的坐标，
        // 新根下是同一份数据时，规则原样继续正确命中。
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

    // 手动 reset 与「成功自清」同实现（决策 2）。
    public Task ResetStatusAsync(int id, CancellationToken ct = default) => SetNormalAsync(id, ct);
}
