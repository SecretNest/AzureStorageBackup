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
/// 一个 Azure Storage Account 配置。敏感字段（AccountKeyProtected、ProxyPasswordProtected）
/// 在应用层与库中**均为密文**，解密只经 ISecretReader（设计 §3.1）。
/// </summary>
public class Account
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public string BlobEndpoint { get; set; } = string.Empty;
    public AzureRegion Region { get; set; } = AzureRegion.Global;

    /// <summary>账户密钥密文。取明文用 ISecretReader.RevealAccountKey。</summary>
    public string AccountKeyProtected { get; set; } = string.Empty;

    // 代理
    public bool UseProxy { get; set; }
    public ProxyMode ProxyMode { get; set; } = ProxyMode.Independent;
    public string? ProxyHost { get; set; }
    public int? ProxyPort { get; set; }
    public string? ProxyUsername { get; set; }

    /// <summary>代理密码密文。取明文用 ISecretReader.RevealProxyPassword。</summary>
    public string? ProxyPasswordProtected { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
