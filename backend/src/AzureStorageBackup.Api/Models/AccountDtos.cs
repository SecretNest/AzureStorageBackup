using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Models;

/// <summary>账户响应体。刻意不含 AccountKey/ProxyPassword，避免敏感信息外泄。</summary>
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
    bool SecretsUnavailable)
{
    /// <summary>账户密钥必填，密钥环 Lost 时必然解不开，故 SecretsUnavailable 原样取自 keyringLost。</summary>
    public static AccountResponse From(Account a, bool keyringLost = false) => new(
        a.Id, a.Name, a.Description, a.BlobEndpoint, a.Region,
        a.UseProxy, a.ProxyMode, a.ProxyHost, a.ProxyPort, a.ProxyUsername, a.CreatedAt,
        keyringLost);
}

/// <summary>创建/更新账户请求体。更新时 AccountKey/ProxyPassword 为空表示保留原值。</summary>
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
    /// <summary>请求体里的凭据是明文；落到实体上时立即加密（设计 §3.1：实体只持密文）。空值保持空值，不加密。</summary>
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
