using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Sensitive fields are stored as ciphertext, and this interface is the **only** way to read plaintext
/// back (design §3.1, "decrypt at the chokepoints").
/// A failed decryption always throws <see cref="SecretUnavailableException"/>; there is no fallback.
/// </summary>
public interface ISecretReader
{
    string RevealAccountKey(Account account);
    string? RevealProxyPassword(Account account);

    /// <summary>The backup password; null for an unencrypted backup.</summary>
    string? RevealBackupPassword(BackupConfig config);
}
