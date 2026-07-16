using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;

namespace AzureStorageBackup.Api.Tests;

public class EncryptionServiceTests
{
    private static EncryptionService CreateSut()
        => new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Encrypt_Then_Decrypt_Returns_Original()
    {
        var sut = CreateSut();
        const string original = "super-secret-key==";

        var cipher = sut.Encrypt(original);
        var plain = sut.Decrypt(cipher);

        Assert.Equal(original, plain);
    }

    [Fact]
    public void Encrypt_Produces_Ciphertext_Different_From_Plaintext()
    {
        var sut = CreateSut();
        const string original = "super-secret-key==";

        var cipher = sut.Encrypt(original);

        Assert.NotEqual(original, cipher);
    }
}
