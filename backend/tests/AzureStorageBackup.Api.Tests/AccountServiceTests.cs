using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
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
        // In-memory SQLite: the connection stays open and the database lives as long as it does
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new AppDbContext(options);
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
        AccountKeyProtected = TestSecrets.Protect("the-secret-key=="),
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Create_Then_Get_Keeps_Key_Ciphertext_That_Reader_Reveals()
    {
        var created = await _sut.CreateAsync(SampleAccount());

        var fetched = await _sut.GetAsync(created.Id);

        Assert.NotNull(fetched);
        // The entity always holds ciphertext; plaintext is only obtained through ISecretReader (design §3.1).
        Assert.NotEqual("the-secret-key==", fetched!.AccountKeyProtected);
        Assert.Equal("the-secret-key==", TestSecrets.Reader.RevealAccountKey(fetched));
        Assert.Equal("prod", fetched.Name);
    }

    /// <summary>
    /// Pins the **column name**: the entity property is AccountKeyProtected while the stored column must
    /// still be the historical AccountKey (no schema change).
    /// This no longer proves "encryption" — the ciphertext was written by this test through CreateAsync, so
    /// the assertion can only show it differs from the plaintext.
    /// </summary>
    [Fact]
    public async Task Create_Writes_To_The_Legacy_AccountKey_Column()
    {
        var created = await _sut.CreateAsync(SampleAccount());

        // Read the raw column directly (a wrong column name finds no table or column and fails the test)
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
        var second = SampleAccount();
        second.Name = "prod-2";
        second.BlobEndpoint = "https://prod2.blob.core.windows.net"; // one endpoint, one record
        await _sut.CreateAsync(second);

        var all = await _sut.ListAsync();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task ProxyPassword_RoundTrips_As_Ciphertext()
    {
        var acct = SampleAccount();
        acct.UseProxy = true;
        acct.ProxyMode = ProxyMode.Independent;
        acct.ProxyHost = "proxy.local";
        acct.ProxyPort = 8080;
        acct.ProxyUsername = "user";
        acct.ProxyPasswordProtected = TestSecrets.Protect("proxy-pass");

        var created = await _sut.CreateAsync(acct);
        var fetched = await _sut.GetAsync(created.Id);

        Assert.NotNull(fetched);
        Assert.NotEqual("proxy-pass", fetched!.ProxyPasswordProtected);
        Assert.Equal("proxy-pass", TestSecrets.Reader.RevealProxyPassword(fetched));
        Assert.Equal(8080, fetched.ProxyPort);
    }

    /// <summary>The operator's ruling on the endpoint-alias hazard: one endpoint, one account record. Two
    /// records for one real storage account would defeat the per-container serialization (the busy tracker
    /// keys on the local record id), letting a cleanup delete what a concurrent backup is uploading.
    /// Normalized comparison, so a cosmetic case/slash variation cannot slip past; an edit may keep its own
    /// endpoint but may not steal another record's.</summary>
    [Fact]
    public async Task An_Account_Aliasing_An_Existing_Endpoint_Is_Refused()
    {
        var first = await _sut.CreateAsync(SampleAccount());

        var alias = SampleAccount();
        alias.Name = "another name";
        alias.BlobEndpoint = first.BlobEndpoint.ToUpperInvariant() + "/";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.CreateAsync(alias));

        var second = SampleAccount();
        second.Name = "second";
        second.BlobEndpoint = "https://other.blob.core.windows.net";
        second = await _sut.CreateAsync(second);
        second.BlobEndpoint = first.BlobEndpoint;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.UpdateAsync(second.Id, second));

        second.BlobEndpoint = "https://other.blob.core.windows.net";
        second.Description = "edited";
        Assert.NotNull(await _sut.UpdateAsync(second.Id, second));
    }

}
