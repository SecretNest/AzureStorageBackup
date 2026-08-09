using Microsoft.AspNetCore.DataProtection;

namespace AzureStorageBackup.Api.Services;

public class EncryptionService : IEncryptionService
{
    private readonly IDataProtector _protector;
    private readonly ILogger<EncryptionService>? _logger;

    /// <param name="logger">
    /// Nullable: DI can resolve <see cref="ILogger{TCategoryName}"/> (registered as a singleton, see
    /// Program.cs), while a unit test constructing this directly can omit it and get no logging.
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
            // A replaced key ring, truncated ciphertext, or something that was never ciphertext — all
            // treated as unavailable.
            // This is the hot path while Lost, probing record by record, and it is hit in normal operation
            // too, so only a Debug-level trace is left: returning false silently would leave a genuine key
            // ring failure with no clue at all.
            _logger?.LogDebug(ex, "Data protection could not decrypt a stored secret; treating it as unavailable.");
            plaintext = string.Empty;
            return false;
        }
    }
}
