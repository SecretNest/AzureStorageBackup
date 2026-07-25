using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>无状态，注册为单例（<see cref="IEncryptionService"/> 同为单例）。</summary>
public sealed class SecretReader(IEncryptionService encryption) : ISecretReader
{
    public string RevealAccountKey(Account account) =>
        Reveal(account.AccountKey, $"account '{account.Name}' (id {account.Id}) key")!;

    public string? RevealProxyPassword(Account account) =>
        string.IsNullOrEmpty(account.ProxyPassword)
            ? null
            : Reveal(account.ProxyPassword, $"account '{account.Name}' (id {account.Id}) proxy password");

    public string? RevealBackupPassword(BackupConfig config) =>
        string.IsNullOrEmpty(config.Password)
            ? null
            : Reveal(config.Password, $"backup '{config.Name}' (id {config.Id}) password");

    private string? Reveal(string ciphertext, string what) =>
        encryption.TryDecrypt(ciphertext, out var plain)
            ? plain
            : throw new SecretUnavailableException(
                $"Cannot decrypt {what}: the data protection keyring cannot read it. Re-enter the credential.");
}
