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
            return encryption.TryDecrypt(canary.Ciphertext, out var value) && value == CanaryPlaintext
                ? KeyringStatus.Healthy
                : KeyringStatus.Lost;

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
