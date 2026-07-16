namespace AzureStorageBackup.Api.Models;

/// <summary>Azure 分区，决定 endpoint 后缀。</summary>
public enum AzureRegion
{
    Global = 0,
    China = 1,
    UsGov = 2
}

/// <summary>代理来源：自定义独立代理 / 继承 docker 环境变量。</summary>
public enum ProxyMode
{
    Independent = 0,
    DockerEnv = 1
}

/// <summary>
/// 一个 Azure Storage Account 配置。敏感字段（AccountKey、ProxyPassword）
/// 在应用层为明文，落库时经 EF ValueConverter 自动加密。
/// </summary>
public class Account
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public string BlobEndpoint { get; set; } = string.Empty;
    public AzureRegion Region { get; set; } = AzureRegion.Global;

    /// <summary>账户密钥（应用态明文；落库加密）。</summary>
    public string AccountKey { get; set; } = string.Empty;

    // 代理
    public bool UseProxy { get; set; }
    public ProxyMode ProxyMode { get; set; } = ProxyMode.Independent;
    public string? ProxyHost { get; set; }
    public int? ProxyPort { get; set; }
    public string? ProxyUsername { get; set; }

    /// <summary>代理密码（应用态明文；落库加密）。</summary>
    public string? ProxyPassword { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
