namespace AzureStorageBackup.Api.Models;

/// <summary>Azure partition; decides the endpoint suffix.</summary>
public enum AzureRegion
{
    Global = 0,
    China = 1,
    UsGov = 2
}

/// <summary>Where the proxy comes from: a custom standalone proxy, or inherited from the docker environment variables.</summary>
public enum ProxyMode
{
    Independent = 0,
    DockerEnv = 1
}

/// <summary>
/// One Azure Storage Account configuration. The sensitive fields (AccountKeyProtected, ProxyPasswordProtected)
/// are **ciphertext in both the application layer and the database**; decryption goes only through ISecretReader (design §3.1).
/// </summary>
public class Account
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public string BlobEndpoint { get; set; } = string.Empty;
    public AzureRegion Region { get; set; } = AzureRegion.Global;

    /// <summary>Account key ciphertext. Use ISecretReader.RevealAccountKey to get the plaintext.</summary>
    public string AccountKeyProtected { get; set; } = string.Empty;

    // Proxy
    public bool UseProxy { get; set; }
    public ProxyMode ProxyMode { get; set; } = ProxyMode.Independent;
    public string? ProxyHost { get; set; }
    public int? ProxyPort { get; set; }
    public string? ProxyUsername { get; set; }

    /// <summary>Proxy password ciphertext. Use ISecretReader.RevealProxyPassword to get the plaintext.</summary>
    public string? ProxyPasswordProtected { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
