using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public class KeyringRecoveryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly IEncryptionService _current = new EncryptionService(new EphemeralDataProtectionProvider());
    private readonly IKeyringHealth _health = new KeyringHealth();

    public KeyringRecoveryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _health.Set(KeyringStatus.Lost);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private KeyringRecovery Sut() => new(_health, new KeyringProbe(_db, _current));

    private static string Stale(string v) =>
        new EncryptionService(new EphemeralDataProtectionProvider()).Encrypt(v);

    [Fact]
    public async Task Does_Not_Flip_While_A_Secret_Is_Still_Undecryptable()
    {
        _db.Accounts.Add(new Account { Name = "a", BlobEndpoint = "https://a.blob.core.windows.net", AccountKeyProtected = _current.Encrypt("k") });
        _db.Accounts.Add(new Account { Name = "b", BlobEndpoint = "https://b.blob.core.windows.net", AccountKeyProtected = Stale("k") });
        await _db.SaveChangesAsync();

        Assert.False(await Sut().TryCompleteAsync());
        Assert.Equal(KeyringStatus.Lost, _health.Status);
    }

    [Fact]
    public async Task Flips_To_Healthy_And_Rebuilds_Canary_When_All_Readable()
    {
        _db.Accounts.Add(new Account { Name = "a", BlobEndpoint = "https://a.blob.core.windows.net", AccountKeyProtected = _current.Encrypt("k") });
        _db.BackupConfigs.Add(new BackupConfig { Name = "docs", ContainerName = "c", LocalRoot = "/d", PasswordProtected = _current.Encrypt("pw") });
        await _db.SaveChangesAsync();

        Assert.True(await Sut().TryCompleteAsync());
        Assert.Equal(KeyringStatus.Healthy, _health.Status);
        Assert.Equal(1, await _db.KeyringCanaries.CountAsync());
    }

    [Fact]
    public async Task Unencrypted_Backup_Configs_Do_Not_Block_Recovery()
    {
        _db.BackupConfigs.Add(new BackupConfig { Name = "plain", ContainerName = "c", LocalRoot = "/d", PasswordProtected = null });
        await _db.SaveChangesAsync();

        Assert.True(await Sut().TryCompleteAsync());
        Assert.Equal(KeyringStatus.Healthy, _health.Status);
    }

    /// <summary>三族密文之一：代理密码。回归覆盖——若查询被误删，本用例会拿掉才能捕获。</summary>
    [Fact]
    public async Task Does_Not_Flip_While_A_Proxy_Password_Is_Still_Undecryptable()
    {
        _db.Accounts.Add(new Account
        {
            Name = "a", BlobEndpoint = "https://a.blob.core.windows.net",
            AccountKeyProtected = _current.Encrypt("k"),
            ProxyPasswordProtected = Stale("proxy-pw"),
        });
        await _db.SaveChangesAsync();

        Assert.False(await Sut().TryCompleteAsync());
        Assert.Equal(KeyringStatus.Lost, _health.Status);
    }

    /// <summary>三族密文之一：备份密码。回归覆盖——若查询被误删，本用例会拿掉才能捕获。</summary>
    [Fact]
    public async Task Does_Not_Flip_While_A_Backup_Password_Is_Still_Undecryptable()
    {
        _db.Accounts.Add(new Account { Name = "a", BlobEndpoint = "https://a.blob.core.windows.net", AccountKeyProtected = _current.Encrypt("k") });
        _db.BackupConfigs.Add(new BackupConfig { Name = "docs", ContainerName = "c", LocalRoot = "/d", PasswordProtected = Stale("backup-pw") });
        await _db.SaveChangesAsync();

        Assert.False(await Sut().TryCompleteAsync());
        Assert.Equal(KeyringStatus.Lost, _health.Status);
    }
}
