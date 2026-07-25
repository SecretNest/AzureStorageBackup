using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;

namespace AzureStorageBackup.Api.Tests;

public class SecretReaderTests
{
    private static (SecretReader Sut, IEncryptionService Enc) Create()
    {
        var enc = new EncryptionService(new EphemeralDataProtectionProvider());
        return (new SecretReader(enc), enc);
    }

    [Fact]
    public void RevealAccountKey_Returns_Plaintext()
    {
        var (sut, enc) = Create();
        var account = new Account { AccountKey = enc.Encrypt("the-key==") };

        Assert.Equal("the-key==", sut.RevealAccountKey(account));
    }

    [Fact]
    public void RevealAccountKey_Throws_When_Undecryptable()
    {
        var (sut, _) = Create();
        var foreign = new EncryptionService(new EphemeralDataProtectionProvider());
        var account = new Account { Id = 7, Name = "prod", AccountKey = foreign.Encrypt("the-key==") };

        var ex = Assert.Throws<SecretUnavailableException>(() => sut.RevealAccountKey(account));
        Assert.Contains("prod", ex.Message);
    }

    [Fact]
    public void RevealProxyPassword_Returns_Null_When_Not_Set()
    {
        var (sut, _) = Create();

        Assert.Null(sut.RevealProxyPassword(new Account { ProxyPassword = null }));
        Assert.Null(sut.RevealProxyPassword(new Account { ProxyPassword = "" }));
    }

    [Fact]
    public void RevealBackupPassword_Returns_Null_For_Unencrypted_Backup()
    {
        var (sut, _) = Create();

        Assert.Null(sut.RevealBackupPassword(new BackupConfig { Password = null }));
        Assert.Null(sut.RevealBackupPassword(new BackupConfig { Password = "" }));
    }

    [Fact]
    public void RevealBackupPassword_Throws_When_Undecryptable()
    {
        var (sut, _) = Create();
        var foreign = new EncryptionService(new EphemeralDataProtectionProvider());
        var config = new BackupConfig { Id = 3, Name = "docs", Password = foreign.Encrypt("pw") };

        var ex = Assert.Throws<SecretUnavailableException>(() => sut.RevealBackupPassword(config));
        Assert.Contains("docs", ex.Message);
    }
}
