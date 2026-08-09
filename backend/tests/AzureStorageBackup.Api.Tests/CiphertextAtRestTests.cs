using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>Ciphertext at rest: the EF layer no longer decrypts, so list queries still work while the keyring is lost (design §3.1).</summary>
public class CiphertextAtRestTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public CiphertextAtRestTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Stored_Value_Stays_Ciphertext_And_Reader_Reveals_It()
    {
        var enc = new EncryptionService(new EphemeralDataProtectionProvider());
        _db.Accounts.Add(new Account
        {
            Name = "prod",
            BlobEndpoint = "https://prod.blob.core.windows.net",
            AccountKeyProtected = enc.Encrypt("the-key=="),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var loaded = await _db.Accounts.SingleAsync();

        Assert.NotEqual("the-key==", loaded.AccountKeyProtected);
        Assert.Equal("the-key==", new SecretReader(enc).RevealAccountKey(loaded));
    }

    [Fact]
    public async Task Listing_Succeeds_When_Keyring_Is_Lost()
    {
        // Write with one keyring and read with another — equivalent to restarting after /keys was lost
        var written = new EncryptionService(new EphemeralDataProtectionProvider());
        _db.Accounts.Add(new Account
        {
            Name = "prod",
            BlobEndpoint = "https://prod.blob.core.windows.net",
            AccountKeyProtected = written.Encrypt("the-key=="),
        });
        _db.BackupConfigs.Add(new BackupConfig
        {
            Name = "docs",
            ContainerName = "c",
            LocalRoot = "/data",
            PasswordProtected = written.Encrypt("pw"),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // The key regression: these two queries used to throw CryptographicException
        var accounts = await _db.Accounts.AsNoTracking().ToListAsync();
        var configs = await _db.BackupConfigs.AsNoTracking().ToListAsync();

        Assert.Single(accounts);
        Assert.Equal("prod", accounts[0].Name);
        Assert.Single(configs);
        Assert.Equal("docs", configs[0].Name);

        // But actually consuming the value must fail explicitly
        var reader = new SecretReader(new EncryptionService(new EphemeralDataProtectionProvider()));
        Assert.Throws<SecretUnavailableException>(() => reader.RevealAccountKey(accounts[0]));
    }

    private async Task<List<string>> ColumnNamesAsync(string table)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        return columns;
    }

    /// <summary>Entity properties were renamed to *Protected, but that must **not** produce a schema change: the column names must stay exactly as they were.</summary>
    [Fact]
    public async Task Column_Names_Are_Unchanged()
    {
        var accounts = await ColumnNamesAsync("Accounts");

        Assert.Contains("AccountKey", accounts);
        Assert.Contains("ProxyPassword", accounts);
        Assert.DoesNotContain("AccountKeyProtected", accounts);
        Assert.DoesNotContain("ProxyPasswordProtected", accounts);

        // The backup password likewise only had its property renamed (PasswordProtected); the column is still Password.
        var configs = await ColumnNamesAsync("BackupConfigs");

        Assert.Contains("Password", configs);
        Assert.DoesNotContain("PasswordProtected", configs);
    }
}
