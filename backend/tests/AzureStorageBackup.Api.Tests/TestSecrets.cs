using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Shared keyring for tests. Credential fields on entities are always ciphertext (design §3.1), so tests build their samples
/// through <see cref="Protect"/> and the code under test gets the plaintext via <see cref="Reader"/> — the same path as production.
/// Stateless and thread-safe, so parallel test classes can share it.
/// </summary>
internal static class TestSecrets
{
    public static readonly IEncryptionService Encryption =
        new EncryptionService(new EphemeralDataProtectionProvider());

    public static readonly ISecretReader Reader = new SecretReader(Encryption);

    public static string Protect(string plaintext) => Encryption.Encrypt(plaintext);

    /// <summary>
    /// Decryption helper for assertions: returns the plaintext if it decrypts, fails the test outright if it does not.
    /// <para>
    /// Production deliberately keeps only <see cref="IEncryptionService.TryDecrypt"/> — "a failed decryption must funnel into
    /// <see cref="SecretUnavailableException"/>" is a project constraint, and a Decrypt that throws a bare CryptographicException
    /// is a bypass around it that should not be kept alive just for the sake of test assertions (F1).
    /// </para>
    /// </summary>
    public static string Reveal(IEncryptionService encryption, string ciphertext)
    {
        Assert.True(encryption.TryDecrypt(ciphertext, out var plaintext),
            "Ciphertext could not be decrypted with the current keyring.");
        return plaintext;
    }

    /// <summary>
    /// Encrypt with a throwaway keyring unrelated to the host under test: this simulates the old ciphertext left in the database
    /// after <c>/keys</c> is lost, which the current keyring cannot decrypt. Flipping <see cref="IKeyringHealth"/> alone cannot
    /// produce such ciphertext — the stored values would still decrypt, and the per-record trial decryption would (correctly) report that nothing needs re-entering.
    /// </summary>
    public static string Stale(string plaintext) =>
        new EncryptionService(new EphemeralDataProtectionProvider()).Encrypt(plaintext);
}
