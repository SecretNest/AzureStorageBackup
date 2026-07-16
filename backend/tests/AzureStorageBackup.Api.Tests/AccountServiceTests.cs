using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public class AccountServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AccountService _sut;

    public AccountServiceTests()
    {
        // in-memory SQLite：连接保持打开，库随连接存续
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var encryption = new EncryptionService(new EphemeralDataProtectionProvider());
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(options, encryption);
        _db.Database.EnsureCreated();

        _sut = new AccountService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Account SampleAccount() => new()
    {
        Name = "prod",
        Description = "primary",
        BlobEndpoint = "https://prod.blob.core.windows.net",
        Region = AzureRegion.Global,
        AccountKey = "the-secret-key==",
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Create_Then_Get_Returns_Same_AccountKey()
    {
        var created = await _sut.CreateAsync(SampleAccount());

        var fetched = await _sut.GetAsync(created.Id);

        Assert.NotNull(fetched);
        Assert.Equal("the-secret-key==", fetched!.AccountKey);
        Assert.Equal("prod", fetched.Name);
    }

    [Fact]
    public async Task Create_Persists_AccountKey_Encrypted()
    {
        var created = await _sut.CreateAsync(SampleAccount());

        // 绕过 EF converter 直接读原始列，验证落库确为密文
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT AccountKey FROM Accounts WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", created.Id);
        var raw = (string)(await cmd.ExecuteScalarAsync())!;

        Assert.NotEqual("the-secret-key==", raw);
    }

    [Fact]
    public async Task Update_Modifies_Fields()
    {
        var created = await _sut.CreateAsync(SampleAccount());

        var update = SampleAccount();
        update.Name = "renamed";
        update.Description = "changed";
        var result = await _sut.UpdateAsync(created.Id, update);

        Assert.NotNull(result);
        var fetched = await _sut.GetAsync(created.Id);
        Assert.Equal("renamed", fetched!.Name);
        Assert.Equal("changed", fetched.Description);
    }

    [Fact]
    public async Task Update_NonExistent_Returns_Null()
    {
        var result = await _sut.UpdateAsync(999, SampleAccount());
        Assert.Null(result);
    }

    [Fact]
    public async Task Delete_Removes_Account()
    {
        var created = await _sut.CreateAsync(SampleAccount());

        var deleted = await _sut.DeleteAsync(created.Id);

        Assert.True(deleted);
        Assert.Null(await _sut.GetAsync(created.Id));
    }

    [Fact]
    public async Task Delete_NonExistent_Returns_False()
    {
        Assert.False(await _sut.DeleteAsync(999));
    }

    [Fact]
    public async Task List_Returns_All_Accounts()
    {
        await _sut.CreateAsync(SampleAccount());
        await _sut.CreateAsync(SampleAccount());

        var all = await _sut.ListAsync();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task ProxyPassword_RoundTrips()
    {
        var acct = SampleAccount();
        acct.UseProxy = true;
        acct.ProxyMode = ProxyMode.Independent;
        acct.ProxyHost = "proxy.local";
        acct.ProxyPort = 8080;
        acct.ProxyUsername = "user";
        acct.ProxyPassword = "proxy-pass";

        var created = await _sut.CreateAsync(acct);
        var fetched = await _sut.GetAsync(created.Id);

        Assert.Equal("proxy-pass", fetched!.ProxyPassword);
        Assert.Equal(8080, fetched.ProxyPort);
    }
}
