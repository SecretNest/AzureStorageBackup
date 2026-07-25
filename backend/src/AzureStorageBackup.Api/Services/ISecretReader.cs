using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 敏感字段落库为密文；本接口是密文→明文的**唯一**读取口（设计 §3.1「咽喉处解密」）。
/// 解不开一律抛 <see cref="SecretUnavailableException"/>，不得回退。
/// </summary>
public interface ISecretReader
{
    string RevealAccountKey(Account account);
    string? RevealProxyPassword(Account account);

    /// <summary>备份密码；未加密的备份返回 null。</summary>
    string? RevealBackupPassword(BackupConfig config);
}
