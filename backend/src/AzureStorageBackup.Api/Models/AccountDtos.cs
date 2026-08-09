using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Models;

/// <summary>Account response body. Deliberately carries no AccountKey/ProxyPassword, so nothing sensitive leaks out.</summary>
public record AccountResponse(
    int Id,
    string Name,
    string? Description,
    string BlobEndpoint,
    AzureRegion Region,
    bool UseProxy,
    ProxyMode ProxyMode,
    string? ProxyHost,
    int? ProxyPort,
    string? ProxyUsername,
    DateTimeOffset CreatedAt,
    bool SecretsUnavailable,
    /// <summary>Names of the backups using this account (sorted by name). Non-empty means it cannot be deleted — the UI disables delete and explains why.</summary>
    IReadOnlyList<string> UsedByBackups)
{
    /// <summary>
    /// <paramref name="secretsUnavailable"/> must be passed according to whether this account's ciphertext actually decrypts
    /// (see <see cref="SecretAvailability"/>), never as the global Lost status — in the middle of a recovery, an account that
    /// has already been reset must stop showing as "needs re-entry".
    /// </summary>
    /// <param name="usedByBackups">
    /// Names of the backups using this account. Empty by default = nothing is using it; if the caller could not obtain the usage
    /// info, do **not** paper over it with the default, because that makes the UI show an undeletable account as deletable.
    /// </param>
    public static AccountResponse From(
        Account a, bool secretsUnavailable = false, IReadOnlyList<string>? usedByBackups = null) => new(
        a.Id, a.Name, a.Description, a.BlobEndpoint, a.Region,
        a.UseProxy, a.ProxyMode, a.ProxyHost, a.ProxyPort, a.ProxyUsername, a.CreatedAt,
        secretsUnavailable, usedByBackups ?? []);
}

/// <summary>Create/update account request body. On update, a blank AccountKey/ProxyPassword means keep the existing value.</summary>
public record AccountRequest(
    string Name,
    string? Description,
    string BlobEndpoint,
    AzureRegion Region,
    string? AccountKey,
    bool UseProxy,
    ProxyMode ProxyMode,
    string? ProxyHost,
    int? ProxyPort,
    string? ProxyUsername,
    string? ProxyPassword)
{
    /// <summary>Credentials arrive in the request body as plaintext and are encrypted the moment they land on the entity (design §3.1: entities only ever hold ciphertext). Empty stays empty and is not encrypted.</summary>
    public Account ToAccount(IEncryptionService encryption) => new()
    {
        Name = Name,
        Description = Description,
        BlobEndpoint = BlobEndpoint,
        Region = Region,
        AccountKeyProtected = string.IsNullOrEmpty(AccountKey) ? string.Empty : encryption.Encrypt(AccountKey),
        ProxyPasswordProtected = string.IsNullOrEmpty(ProxyPassword) ? null : encryption.Encrypt(ProxyPassword),
        UseProxy = UseProxy,
        ProxyMode = ProxyMode,
        ProxyHost = ProxyHost,
        ProxyPort = ProxyPort,
        ProxyUsername = ProxyUsername,
    };
}

/// <summary>Credential reset request. AccountKey is required; a blank ProxyPassword means clear the proxy password.</summary>
public record ResetAccountSecretsRequest(string AccountKey, string? ProxyPassword);
