using Microsoft.AspNetCore.DataProtection;

namespace AzureStorageBackup.Api.Services;

public class EncryptionService : IEncryptionService
{
    private readonly IDataProtector _protector;
    private readonly ILogger<EncryptionService>? _logger;

    /// <param name="logger">
    /// 可空：DI 能解析出 <see cref="ILogger{TCategoryName}"/>（单例注册，见 Program.cs）；
    /// 单元测试直接 new 时省略即可，不写日志。
    /// </param>
    public EncryptionService(IDataProtectionProvider provider, ILogger<EncryptionService>? logger = null)
    {
        _protector = provider.CreateProtector("AzureStorageBackup.Secrets.v1");
        _logger = logger;
    }

    public string Encrypt(string plaintext) => _protector.Protect(plaintext);

    public bool TryDecrypt(string ciphertext, out string plaintext)
    {
        try
        {
            plaintext = _protector.Unprotect(ciphertext);
            return true;
        }
        catch (Exception ex)
        {
            // 密钥环换过、密文被截断或根本不是密文——一律视为不可用。
            // 这是 Lost 时逐条试解的热路径，正常也会命中，故只留 Debug 级痕迹：
            // 默默返回 false 会让真实的密钥环故障一点线索都不留（F6）。
            _logger?.LogDebug(ex, "Data protection could not decrypt a stored secret; treating it as unavailable.");
            plaintext = string.Empty;
            return false;
        }
    }
}
