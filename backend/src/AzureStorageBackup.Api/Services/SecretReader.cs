using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Stateless, registered as a singleton (as is <see cref="IEncryptionService"/>).</summary>
public sealed class SecretReader(IEncryptionService encryption) : ISecretReader
{
    public string RevealAccountKey(Account account) =>
        Reveal(account.AccountKeyProtected, $"account '{account.Name}' (id {account.Id}) key")!;

    public string? RevealProxyPassword(Account account) =>
        string.IsNullOrEmpty(account.ProxyPasswordProtected)
            ? null
            : Reveal(account.ProxyPasswordProtected, $"account '{account.Name}' (id {account.Id}) proxy password");

    public string? RevealBackupPassword(BackupConfig config) =>
        string.IsNullOrEmpty(config.PasswordProtected)
            ? null
            : Reveal(config.PasswordProtected, $"backup '{config.Name}' (id {config.Id}) password");

    private string? Reveal(string ciphertext, string what) =>
        encryption.TryDecrypt(ciphertext, out var plain)
            ? plain
            : throw new SecretUnavailableException(
                $"Cannot decrypt {what}: the data protection keyring cannot read it. Re-enter the credential.");
}
