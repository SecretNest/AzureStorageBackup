namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 敏感信息的可逆加密（账户 key、代理密码、信息文件密码）。
/// 由 ASP.NET Core Data Protection 支撑，密钥环持久化到本地卷。
/// </summary>
public interface IEncryptionService
{
    string Encrypt(string plaintext);
    string Decrypt(string ciphertext);
}
