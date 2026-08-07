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
    bool SecretsUnavailable,
    /// <summary>占用这个账户的备份名（按名字排序）。非空即不可删——界面据此禁用删除并说明原因。</summary>
    IReadOnlyList<string> UsedByBackups)
{
    /// <summary>
    /// <paramref name="secretsUnavailable"/> 必须按该账户密文的实际可解性传入
    /// （见 <see cref="SecretAvailability"/>），不能直接传全局 Lost 状态——恢复中间态里
    /// 已重设成功的账户必须停止显示「待重设」。
    /// </summary>
    /// <param name="usedByBackups">
    /// 占用这个账户的备份名。默认空＝没有占用；调用方拿不到占用信息时**不要**用默认值蒙混，
    /// 那会让界面把一个不可删的账户显示成可删的。
    /// </param>
    public static AccountResponse From(
        Account a, bool secretsUnavailable = false, IReadOnlyList<string>? usedByBackups = null) => new(
        a.Id, a.Name, a.Description, a.BlobEndpoint, a.Region,
        a.UseProxy, a.ProxyMode, a.ProxyHost, a.ProxyPort, a.ProxyUsername, a.CreatedAt,
        secretsUnavailable, usedByBackups ?? []);
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

/// <summary>凭据重设请求。AccountKey 必填；ProxyPassword 为空表示清空代理密码。</summary>
public record ResetAccountSecretsRequest(string AccountKey, string? ProxyPassword);
