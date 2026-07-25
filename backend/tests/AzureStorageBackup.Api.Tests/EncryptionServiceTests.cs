using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;

namespace AzureStorageBackup.Api.Tests;

public class EncryptionServiceTests
{
    private static EncryptionService CreateSut()
        => new(new EphemeralDataProtectionProvider());

    // 注：曾有一条 Encrypt_Then_Decrypt_Returns_Original——生产侧的 Decrypt 移除后，
    // 它只是绕 TestSecrets.Reveal 又跑了一遍 TryDecrypt，与下面那条完全重复，已删。

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
        // 另一个 provider = 另一套密钥环，等价于 /keys 丢失后重新生成
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
