using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public sealed class BackupConfigServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly BackupConfigService _sut;

    public BackupConfigServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var encryption = new EncryptionService(new EphemeralDataProtectionProvider());
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options, encryption);
        _db.Database.EnsureCreated();

        _sut = new BackupConfigService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private static BackupConfig Sample(string name = "photos") => new()
    {
        AccountId = 1,
        ContainerName = "photos",
        Name = name,
        LocalRoot = "/data/photos",
        Password = "s3cret",
        DataTier = StorageTier.Cool,
        MaxVersions = 50,
        RetentionMode = RetentionMode.BothRequired,
    };

    [Fact]
    public async Task Create_Then_Get_RoundTrips_Including_Password()
    {
        var created = await _sut.CreateAsync(Sample());

        Assert.True(created.Id > 0);
        Assert.NotEqual(default, created.CreatedAt);

        var fetched = await _sut.GetAsync(created.Id);
        Assert.Equal("photos", fetched!.Name);
        Assert.Equal("s3cret", fetched.Password); // 透明解密
        Assert.Equal(StorageTier.Cool, fetched.DataTier);
        Assert.Equal(RetentionMode.BothRequired, fetched.RetentionMode);
    }

    [Fact]
    public async Task Password_Is_Encrypted_At_Rest()
    {
        var created = await _sut.CreateAsync(Sample());

        // 直接读原始列，应为密文而非明文。
        var raw = _connection.CreateCommand();
        raw.CommandText = "SELECT Password FROM BackupConfigs WHERE Id = $id";
        raw.Parameters.AddWithValue("$id", created.Id);
        var stored = (string?)await raw.ExecuteScalarAsync();

        Assert.NotNull(stored);
        Assert.NotEqual("s3cret", stored);
    }

    [Fact]
    public async Task List_Returns_All()
    {
        await _sut.CreateAsync(Sample("a"));
        await _sut.CreateAsync(Sample("b"));

        Assert.Equal(2, (await _sut.ListAsync()).Count);
    }

    [Fact]
    public async Task Update_Changes_Fields()
    {
        var created = await _sut.CreateAsync(Sample());

        var update = Sample();
        update.Name = "renamed";
        update.MaxVersions = 10;
        var result = await _sut.UpdateAsync(created.Id, update);

        Assert.Equal("renamed", result!.Name);
        Assert.Equal(10, (await _sut.GetAsync(created.Id))!.MaxVersions);
    }

    [Fact]
    public async Task Update_Missing_Returns_Null()
    {
        Assert.Null(await _sut.UpdateAsync(999, Sample()));
    }

    [Fact]
    public async Task Delete_Removes_Config()
    {
        var created = await _sut.CreateAsync(Sample());

        Assert.True(await _sut.DeleteAsync(created.Id));
        Assert.Null(await _sut.GetAsync(created.Id));
        Assert.False(await _sut.DeleteAsync(created.Id));
    }
}
