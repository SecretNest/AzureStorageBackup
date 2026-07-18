using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>全局设置往返（§5.1）：ProcessingMaxAttempts 等字段随 Upsert 持久化。</summary>
public sealed class GlobalSettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly GlobalSettingsService _sut;

    public GlobalSettingsServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var encryption = new EncryptionService(new EphemeralDataProtectionProvider());
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options, encryption);
        _db.Database.EnsureCreated();

        _sut = new GlobalSettingsService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Upsert_Persists_ProcessingMaxAttempts()
    {
        var s = await _sut.GetAsync();
        s.ProcessingMaxAttempts = 8;
        await _sut.UpsertAsync(s);
        Assert.Equal(8, (await _sut.GetAsync()).ProcessingMaxAttempts);
    }

    [Fact]
    public async Task GetAsync_Defaults_ProcessingMaxAttempts_To_Five()
    {
        var s = await _sut.GetAsync();
        Assert.Equal(5, s.ProcessingMaxAttempts);
    }
}
