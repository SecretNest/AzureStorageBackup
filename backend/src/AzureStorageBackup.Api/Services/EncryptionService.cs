using Microsoft.AspNetCore.DataProtection;

namespace AzureStorageBackup.Api.Services;

public class EncryptionService : IEncryptionService
{
    private readonly IDataProtector _protector;

    public EncryptionService(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("AzureStorageBackup.Secrets.v1");

    public string Encrypt(string plaintext) => _protector.Protect(plaintext);

    public string Decrypt(string ciphertext) => _protector.Unprotect(ciphertext);
}
