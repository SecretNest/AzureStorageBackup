using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;

namespace AzureStorageBackup.Api.Tests;

public class EncryptionServiceTests
{
    private static EncryptionService CreateSut()
        => new(new EphemeralDataProtectionProvider());

    // Note: there used to be an Encrypt_Then_Decrypt_Returns_Original. Once the production Decrypt was
    // removed it merely ran TryDecrypt again via TestSecrets.Reveal, duplicating the case below, so it was deleted.

    [Fact]
    public void Encrypt_Produces_Ciphertext_Different_From_Plaintext()
    {
        var sut = CreateSut();
        const string original = "super-secret-key==";

        var cipher = sut.Encrypt(original);

        Assert.NotEqual(original, cipher);
    }

    [Fact]
    public void TryDecrypt_Returns_True_And_Plaintext_For_Own_Ciphertext()
    {
        var sut = CreateSut();
        var cipher = sut.Encrypt("super-secret-key==");

        var ok = sut.TryDecrypt(cipher, out var plain);

        Assert.True(ok);
        Assert.Equal("super-secret-key==", plain);
    }

    [Fact]
    public void TryDecrypt_Returns_False_When_Keyring_Cannot_Decrypt()
    {
        // A different provider = a different key ring, equivalent to /keys being lost and regenerated
        var written = new EncryptionService(new EphemeralDataProtectionProvider());
        var cipher = written.Encrypt("super-secret-key==");
        var sut = CreateSut();

        var ok = sut.TryDecrypt(cipher, out var plain);

        Assert.False(ok);
        Assert.Equal(string.Empty, plain);
    }

    [Fact]
    public void TryDecrypt_Returns_False_For_Garbage_Input()
    {
        var sut = CreateSut();

        var ok = sut.TryDecrypt("not-a-ciphertext", out _);

        Assert.False(ok);
    }
}
