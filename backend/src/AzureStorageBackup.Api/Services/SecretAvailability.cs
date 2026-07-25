using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 单条记录的密文可解性（设计 §3.3）。
///
/// 恢复流程必然经过中间态：全局状态仍是 <c>Lost</c>（还有别的密文没重设），但部分记录
/// 已经重设成功。若待重设计数与逐条标记沿用全局状态，已修好的记录会永远显示待重设，
/// 而顺序依赖（账户先于备份密码）又依赖该计数放行下一步——恢复流程就此死锁。
/// 因此 <c>Lost</c> 期间必须逐条试解。记录数很少，与 <see cref="KeyringProbe"/> 的完成判定同量级。
/// </summary>
public static class SecretAvailability
{
    /// <summary>密文非空且当前密钥环解不开 → 需要重设。空密文没有密钥可丢，不算。</summary>
    public static bool Unreadable(IEncryptionService encryption, string? ciphertext) =>
        !string.IsNullOrEmpty(ciphertext) && !encryption.TryDecrypt(ciphertext, out _);

    /// <summary>账户：密钥或代理密码任一解不开即需重设——reset-secrets 一次性重设两者，
    /// 且完成判定（<see cref="KeyringProbe.AllStoredSecretsReadableAsync"/>）两者都查。</summary>
    public static bool Unreadable(IEncryptionService encryption, Account account) =>
        Unreadable(encryption, account.AccountKeyProtected)
        || Unreadable(encryption, account.ProxyPasswordProtected);

    /// <summary>备份配置：只有加密备份才有密文可丢。</summary>
    public static bool Unreadable(IEncryptionService encryption, BackupConfig config) =>
        Unreadable(encryption, config.PasswordProtected);
}
