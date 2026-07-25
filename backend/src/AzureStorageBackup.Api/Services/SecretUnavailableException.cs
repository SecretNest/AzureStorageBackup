namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 密文无法用当前 Data Protection 密钥环解密（通常是 /keys 丢失或被替换）。
/// 抛出即表示该操作不可继续——禁止回退到空密码或原密文。
/// </summary>
public sealed class SecretUnavailableException(string message) : Exception(message);
