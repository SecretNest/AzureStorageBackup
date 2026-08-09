namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Reversible encryption for sensitive data (account keys, proxy passwords, info file passwords).
/// Backed by ASP.NET Core Data Protection, with the keyring persisted to a local volume.
/// <para>
/// Deliberately **does not provide** a throwing Decrypt: a failed decryption must funnel into
/// <see cref="SecretUnavailableException"/> at the choke point (<see cref="ISecretReader"/>), and a bare CryptographicException is a bypass around that constraint.
/// When "fail the assertion if it will not decrypt" semantics are needed, that is the tests' business (see TestSecrets in the test project).
/// </para>
/// </summary>
public interface IEncryptionService
{
    string Encrypt(string plaintext);

    /// <summary>Try to decrypt. Returns false when the current keyring cannot decrypt it, with plaintext set to the empty string, and never throws.</summary>
    bool TryDecrypt(string ciphertext, out string plaintext);
}
