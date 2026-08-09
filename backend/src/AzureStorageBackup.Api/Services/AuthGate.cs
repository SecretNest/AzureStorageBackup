using System.Security.Cryptography;
using System.Text;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Preset-password check (design §2, §4.3). The password comes from an environment variable in plaintext and **never goes
/// through Data Protection** — which is why you can still log in, and therefore run the keyring recovery flow, while the keyring is lost (design §5).
/// Singleton: the configuration is read once at construction and never changes afterwards (changing the password means changing the environment variable and restarting).
/// </summary>
public sealed class AuthGate
{
    private readonly byte[]? _expected;

    public AuthGate(IConfiguration config)
    {
        var password = config["Auth:Password"];
        _expected = string.IsNullOrEmpty(password) ? null : Encoding.UTF8.GetBytes(password);
    }

    /// <summary>Whether authentication is enabled. False when no password is configured, in which case everything is let through.</summary>
    public bool Required => _expected is not null;

    /// <summary>
    /// Verify the password. Always true when authentication is not enabled.
    /// Uses a constant-time comparison against timing side channels; a different length fails outright (a length difference cannot be hidden anyway).
    /// </summary>
    public bool Verify(string? candidate)
    {
        if (_expected is null)
            return true;
        if (string.IsNullOrEmpty(candidate))
            return false;

        var actual = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(_expected, actual);
    }
}
