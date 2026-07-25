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
        var account = new Account { AccountKeyProtected = enc.Encrypt("the-key==") };

        Assert.Equal("the-key==", sut.RevealAccountKey(account));
    }

    [Fact]
    public void RevealAccountKey_Throws_When_Undecryptable()
    {
        var (sut, _) = Create();
        var foreign = new EncryptionService(new EphemeralDataProtectionProvider());
        var account = new Account { Id = 7, Name = "prod", AccountKeyProtected = foreign.Encrypt("the-key==") };

        var ex = Assert.Throws<SecretUnavailableException>(() => sut.RevealAccountKey(account));
        Assert.Contains("prod", ex.Message);
    }

    /// <summary>
    /// 账户密钥是必填项，没有「未设置即 null」的语义（与代理密码/备份密码相反）：
    /// 空串（新建实体的默认值、或历史遗留的空行）必须同样抛出，**不得**回退成空密钥去连云——
    /// 那会拿一个空的 SharedKey 去签名，失败在 Azure 侧、消息与真实原因无关。
    /// </summary>
    [Fact]
    public void RevealAccountKey_Throws_When_Not_Set()
    {
        var (sut, _) = Create();

        Assert.Throws<SecretUnavailableException>(
            () => sut.RevealAccountKey(new Account { Id = 5, Name = "no-key" }));       // 默认值（空串）
        Assert.Throws<SecretUnavailableException>(
            () => sut.RevealAccountKey(new Account { Id = 5, Name = "no-key", AccountKeyProtected = "" }));
    }

    [Fact]
    public void RevealProxyPassword_Returns_Null_When_Not_Set()
    {
        var (sut, _) = Create();

        Assert.Null(sut.RevealProxyPassword(new Account { ProxyPasswordProtected = null }));
        Assert.Null(sut.RevealProxyPassword(new Account { ProxyPasswordProtected = "" }));
    }

    [Fact]
    public void RevealBackupPassword_Returns_Null_For_Unencrypted_Backup()
    {
        var (sut, _) = Create();

        Assert.Null(sut.RevealBackupPassword(new BackupConfig { PasswordProtected = null }));
        Assert.Null(sut.RevealBackupPassword(new BackupConfig { PasswordProtected = "" }));
    }

    [Fact]
    public void RevealBackupPassword_Throws_When_Undecryptable()
    {
        var (sut, _) = Create();
        var foreign = new EncryptionService(new EphemeralDataProtectionProvider());
        var config = new BackupConfig { Id = 3, Name = "docs", PasswordProtected = foreign.Encrypt("pw") };

        var ex = Assert.Throws<SecretUnavailableException>(() => sut.RevealBackupPassword(config));
        Assert.Contains("docs", ex.Message);
    }
}
