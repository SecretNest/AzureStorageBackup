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
    DateTimeOffset CreatedAt)
{
    public static AccountResponse From(Account a) => new(
        a.Id, a.Name, a.Description, a.BlobEndpoint, a.Region,
        a.UseProxy, a.ProxyMode, a.ProxyHost, a.ProxyPort, a.ProxyUsername, a.CreatedAt);
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
    public Account ToAccount() => new()
    {
        Name = Name,
        Description = Description,
        BlobEndpoint = BlobEndpoint,
        Region = Region,
        AccountKey = AccountKey ?? string.Empty,
        UseProxy = UseProxy,
        ProxyMode = ProxyMode,
        ProxyHost = ProxyHost,
        ProxyPort = ProxyPort,
        ProxyUsername = ProxyUsername,
        ProxyPassword = ProxyPassword
    };
}
