using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>Keyring health verdict (the four-branch table in design §3.2). Scoped — it holds an AppDbContext.</summary>
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

            // The canary will not decrypt, but the database no longer holds any undecryptable ciphertext (the user gave up on
            // recovery and deleted every account and encrypted config, or the credentials were replaced elsewhere with
            // ciphertext from the current keyring). There is nothing left to re-enter, so staying Lost would wedge the
            // process forever: /api/health/ready stuck at 503, the scheduler skipping everything, every action 409, while
            // the banner reads only "0 credentials need to be re-entered" — no way out for the user. Rebuild the canary and let it through.
            // This also makes the state "Lost with 0 pending" unreachable (together with the per-record counting in §3.3).
            if (!await AllStoredSecretsReadableAsync(ct))
                return KeyringStatus.Lost;

            await WriteCanaryAsync(ct);
            return KeyringStatus.Healthy;
        }

        // No canary row: this could be an old database that was just upgraded, or a brand new one.
        // Probe an existing ciphertext first — otherwise "the keyring was already lost at upgrade time" is missed, and never detected again.
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
    /// Whether all three families of ciphertext in the database (account keys, proxy passwords, backup passwords) decrypt with the current keyring.
    /// True when there is no ciphertext at all. <see cref="KeyringRecovery"/>'s completion check and the
    /// "canary is stale but no ciphertext is left" branch above share this scan — the two must use the same criteria, or you land in the
    /// dead end of "cannot get back to Healthy, and no credentials left to re-enter" (design §3.4).
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

    /// <summary>Write (or rebuild) the canary. Called once the recovery flow is fully complete.</summary>
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
