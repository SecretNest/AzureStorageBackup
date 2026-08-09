namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The ciphertext cannot be decrypted with the current Data Protection keyring (usually /keys was lost or replaced).
/// Throwing it means the operation must not continue — falling back to an empty password or to the raw ciphertext is forbidden.
/// <para>
/// Deliberately **only** a message constructor: the sole throw site <see cref="SecretReader"/> sits downstream of
/// <see cref="IEncryptionService.TryDecrypt"/>, where the underlying CryptographicException has already been swallowed
/// (and logged at Debug), so by the time we get here there is no innerException at all. Having one would require
/// TryDecrypt to report failures by throwing — precisely the bypass <see cref="IEncryptionService"/> explicitly rules out.
/// Hence no (message, inner) overload: it could only ever be dead code with no callers.
/// </para>
/// </summary>
public sealed class SecretUnavailableException(string message) : Exception(message);
