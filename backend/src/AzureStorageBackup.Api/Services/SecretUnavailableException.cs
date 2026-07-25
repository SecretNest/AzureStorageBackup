namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 密文无法用当前 Data Protection 密钥环解密（通常是 /keys 丢失或被替换）。
/// 抛出即表示该操作不可继续——禁止回退到空密码或原密文。
/// </summary>
public sealed class SecretUnavailableException : Exception
{
    public SecretUnavailableException(string message) : base(message)
    {
    }

    /// <summary>保留底层原因（通常是 CryptographicException）供诊断；对外文案仍只用 message。</summary>
    public SecretUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
