namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 密文无法用当前 Data Protection 密钥环解密（通常是 /keys 丢失或被替换）。
/// 抛出即表示该操作不可继续——禁止回退到空密码或原密文。
/// <para>
/// 刻意**只有** message 一个构造函数：唯一抛出点 <see cref="SecretReader"/> 位于
/// <see cref="IEncryptionService.TryDecrypt"/> 的下游，底层的 CryptographicException 在那里
/// 就已被吞掉（并记 Debug 日志），到这里 innerException 根本不存在。要让它存在就得让
/// TryDecrypt 改用抛异常的方式报错——那正是 <see cref="IEncryptionService"/> 明令排除的旁路。
/// 故不留 (message, inner) 重载：它只会是一段没有调用者的死代码。
/// </para>
/// </summary>
public sealed class SecretUnavailableException(string message) : Exception(message);
