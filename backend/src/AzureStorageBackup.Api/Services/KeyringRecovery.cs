using AzureStorageBackup.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 恢复完成判定（设计 §3.4）：所有密文都能用当前密钥环解开时，重建 canary 并翻回 Healthy。
/// 不可在首条重设成功时就翻转——彼时其余记录仍解不开。
/// </summary>
public sealed class KeyringRecovery(
    AppDbContext db, IEncryptionService encryption, IKeyringHealth health, KeyringProbe probe)
{
    public async Task<bool> TryCompleteAsync(CancellationToken ct = default)
    {
        var accountKeys = await db.Accounts.AsNoTracking()
            .Select(a => a.AccountKeyProtected).ToListAsync(ct);
        var proxyPasswords = await db.Accounts.AsNoTracking()
            .Where(a => a.ProxyPasswordProtected != null && a.ProxyPasswordProtected != "")
            .Select(a => a.ProxyPasswordProtected!).ToListAsync(ct);
        var backupPasswords = await db.BackupConfigs.AsNoTracking()
            .Where(c => c.PasswordProtected != null && c.PasswordProtected != "")
            .Select(c => c.PasswordProtected!).ToListAsync(ct);

        foreach (var cipher in accountKeys.Concat(proxyPasswords).Concat(backupPasswords))
        {
            if (string.IsNullOrEmpty(cipher))
                continue;
            if (!encryption.TryDecrypt(cipher, out _))
                return false;
        }

        await probe.WriteCanaryAsync(ct);
        health.Set(KeyringStatus.Healthy);
        return true;
    }
}
