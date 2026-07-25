using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 测试用的共享密钥环。实体里的凭据字段一律是密文（设计 §3.1），测试构造样本时用
/// <see cref="Protect"/> 加密，被测代码经 <see cref="Reader"/> 取明文——与生产同一条路径。
/// 无状态且线程安全，可被并行的测试类共用。
/// </summary>
internal static class TestSecrets
{
    public static readonly IEncryptionService Encryption =
        new EncryptionService(new EphemeralDataProtectionProvider());

    public static readonly ISecretReader Reader = new SecretReader(Encryption);

    public static string Protect(string plaintext) => Encryption.Encrypt(plaintext);
}
