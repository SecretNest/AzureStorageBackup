using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>The persistent status (Normal/Error): failure sets Error, the next success clears it back to Normal, and a manual reset clears it (§4.2, decision 2).</summary>
public sealed class BackupConfigStatusTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly BackupConfigService _sut;

    public BackupConfigStatusTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
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
    };

    [Fact]
    public async Task New_Config_Defaults_To_Normal()
    {
        var created = await _sut.CreateAsync(Sample());

        Assert.Equal(BackupStatus.Normal, created.Status);
        Assert.Null(created.LastError);
        Assert.Null(created.LastErrorAt);
    }

    [Fact]
    public async Task Failure_Sets_Error_Success_Clears_To_Normal_Reset_Clears()
    {
        var created = await _sut.CreateAsync(Sample());
        var id = created.Id;

        await _sut.SetErrorAsync(id, "boom");
        var c1 = await _sut.GetAsync(id);
        Assert.Equal(BackupStatus.Error, c1!.Status);
        Assert.Equal("boom", c1.LastError);
        Assert.NotNull(c1.LastErrorAt);

        await _sut.SetNormalAsync(id); // success clears it (decision 2)
        var c2 = await _sut.GetAsync(id);
        Assert.Equal(BackupStatus.Normal, c2!.Status);
        Assert.Null(c2.LastError);
        Assert.Null(c2.LastErrorAt);

        await _sut.SetErrorAsync(id, "again");
        await _sut.ResetStatusAsync(id); // manual reset
        var c3 = await _sut.GetAsync(id);
        Assert.Equal(BackupStatus.Normal, c3!.Status);
        Assert.Null(c3.LastError);
        Assert.Null(c3.LastErrorAt);
    }

    [Fact]
    public async Task SetError_On_Missing_Config_Is_NoOp()
    {
        // A non-existent id must not throw (a status write can still fire after the config has been deleted).
        await _sut.SetErrorAsync(999, "boom");
        await _sut.SetNormalAsync(999);
        await _sut.ResetStatusAsync(999);
    }
}
