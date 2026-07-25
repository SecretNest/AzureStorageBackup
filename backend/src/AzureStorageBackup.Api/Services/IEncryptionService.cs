namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 敏感信息的可逆加密（账户 key、代理密码、信息文件密码）。
/// 由 ASP.NET Core Data Protection 支撑，密钥环持久化到本地卷。
/// <para>
/// 刻意**不提供**会抛异常的 Decrypt：解密失败必须在咽喉处（<see cref="ISecretReader"/>）
/// 收口成 <see cref="SecretUnavailableException"/>，裸抛 CryptographicException 是绕过该约束的旁路。
/// 需要「解不开就判失败」的断言语义时，那是测试的事（见测试项目的 TestSecrets）。
/// </para>
/// </summary>
public interface IEncryptionService
{
    string Encrypt(string plaintext);

    /// <summary>尝试解密。当前密钥环解不开时返回 false，plaintext 为空串，不抛异常。</summary>
    bool TryDecrypt(string ciphertext, out string plaintext);
}
