using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>密钥环健康判定（设计 §3.2 的四分支表）。Scoped——持有 AppDbContext。</summary>
public sealed class KeyringProbe(AppDbContext db, IEncryptionService encryption)
{
    public const string CanaryPlaintext = "canary.v1";

    public async Task<KeyringStatus> EvaluateAsync(CancellationToken ct = default)
    {
        var canary = await db.KeyringCanaries.AsNoTracking().OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        if (canary is not null)
        {
            if (encryption.TryDecrypt(canary.Ciphertext, out var value) && value == CanaryPlaintext)
                return KeyringStatus.Healthy;

            // 哨兵解不开，但库里已经没有任何解不开的密文了（用户放弃恢复、把账户与加密配置删光，
            // 或凭据在别处已被换成当前密钥环的密文）。此时没有任何可重设的凭据，若继续判 Lost，
            // 进程将永久卡死：/api/health/ready 恒 503、调度器全跳过、所有动作 409，而横幅只会写
            // 「0 credentials need to be re-entered」——用户没有任何出口。重建哨兵放行。
            // 这同时保证「Lost 且待重设数为 0」的状态不可达（配合 §3.3 的逐条计数）。
            if (!await AllStoredSecretsReadableAsync(ct))
                return KeyringStatus.Lost;

            await WriteCanaryAsync(ct);
            return KeyringStatus.Healthy;
        }

        // 无 canary 行：可能是升级上来的老库，也可能是全新库。
        // 必须先拿现存密文探一次——否则「升级时密钥环已丢失」会被漏检且此后永远检测不出。
        var probe = await db.Accounts.AsNoTracking().OrderBy(a => a.Id)
            .Select(a => a.AccountKeyProtected).FirstOrDefaultAsync(ct);

        if (string.IsNullOrEmpty(probe))
            probe = await db.BackupConfigs.AsNoTracking().OrderBy(c => c.Id)
                .Where(c => c.PasswordProtected != null && c.PasswordProtected != "")
                .Select(c => c.PasswordProtected).FirstOrDefaultAsync(ct);

        if (!string.IsNullOrEmpty(probe) && !encryption.TryDecrypt(probe, out _))
            return KeyringStatus.Lost;

        await WriteCanaryAsync(ct);
        return KeyringStatus.Healthy;
    }

    /// <summary>
    /// 库中三族密文（账户密钥、代理密码、备份密码）是否全部能被当前密钥环解开。
    /// 一条密文都没有时为 true。<see cref="KeyringRecovery"/> 的完成判定与上面的
    /// 「哨兵已陈旧但无密文残留」分支共用此扫描——两处必须同口径，否则会出现
    /// 「翻不回 Healthy 又无凭据可重设」的死局（设计 §3.4）。
    /// </summary>
    public async Task<bool> AllStoredSecretsReadableAsync(CancellationToken ct = default)
    {
        var accountKeys = await db.Accounts.AsNoTracking()
            .Select(a => a.AccountKeyProtected).ToListAsync(ct);
        var proxyPasswords = await db.Accounts.AsNoTracking()
            .Where(a => a.ProxyPasswordProtected != null && a.ProxyPasswordProtected != "")
            .Select(a => a.ProxyPasswordProtected!).ToListAsync(ct);
        var backupPasswords = await db.BackupConfigs.AsNoTracking()
            .Where(c => c.PasswordProtected != null && c.PasswordProtected != "")
            .Select(c => c.PasswordProtected!).ToListAsync(ct);

        return accountKeys.Concat(proxyPasswords).Concat(backupPasswords)
            .All(cipher => !SecretAvailability.Unreadable(encryption, cipher));
    }

    /// <summary>写入（或重建）哨兵。恢复流程全部完成后调用。</summary>
    public async Task WriteCanaryAsync(CancellationToken ct = default)
    {
        var existing = await db.KeyringCanaries.ToListAsync(ct);
        if (existing.Count > 0)
            db.KeyringCanaries.RemoveRange(existing);

        db.KeyringCanaries.Add(new KeyringCanary
        {
            Ciphertext = encryption.Encrypt(CanaryPlaintext),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}
