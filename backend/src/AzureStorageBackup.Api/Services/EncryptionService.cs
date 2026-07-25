using Microsoft.AspNetCore.DataProtection;

namespace AzureStorageBackup.Api.Services;

public class EncryptionService : IEncryptionService
{
    private readonly IDataProtector _protector;

    public EncryptionService(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("AzureStorageBackup.Secrets.v1");

    public string Encrypt(string plaintext) => _protector.Protect(plaintext);

    public string Decrypt(string ciphertext) => _protector.Unprotect(ciphertext);

    public bool TryDecrypt(string ciphertext, out string plaintext)
    {
        try
        {
            plaintext = _protector.Unprotect(ciphertext);
            return true;
        }
        catch (Exception)
        {
            // 密钥环换过、密文被截断或根本不是密文——一律视为不可用
            plaintext = string.Empty;
            return false;
        }
    }
}
