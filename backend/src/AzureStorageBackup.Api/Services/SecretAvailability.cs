using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Per-record ciphertext decryptability (design §3.3).
///
/// The recovery flow inevitably passes through an intermediate state: the global status is still <c>Lost</c> (other ciphertext
/// has not been reset yet) while some records have already been reset successfully. If the pending count and the per-record flags
/// simply followed the global status, records that are already fixed would show as pending forever, while the ordering dependency
/// (accounts before backup passwords) relies on that count to release the next step — deadlocking the recovery flow.
/// So during <c>Lost</c> each record must be trial-decrypted. There are very few records, on the same order as <see cref="KeyringProbe"/>'s completion check.
/// </summary>
public static class SecretAvailability
{
    /// <summary>Non-empty ciphertext that the current keyring cannot decrypt → needs re-entry. Empty ciphertext has no key to lose, so it does not count.</summary>
    public static bool Unreadable(IEncryptionService encryption, string? ciphertext) =>
        !string.IsNullOrEmpty(ciphertext) && !encryption.TryDecrypt(ciphertext, out _);

    /// <summary>Account: needs re-entry if either the key or the proxy password fails to decrypt — reset-secrets resets both in one go,
    /// and the completion check (<see cref="KeyringProbe.AllStoredSecretsReadableAsync"/>) inspects both.</summary>
    public static bool Unreadable(IEncryptionService encryption, Account account) =>
        Unreadable(encryption, account.AccountKeyProtected)
        || Unreadable(encryption, account.ProxyPasswordProtected);

    /// <summary>Backup config: only encrypted backups have ciphertext to lose.</summary>
    public static bool Unreadable(IEncryptionService encryption, BackupConfig config) =>
        Unreadable(encryption, config.PasswordProtected);
}
