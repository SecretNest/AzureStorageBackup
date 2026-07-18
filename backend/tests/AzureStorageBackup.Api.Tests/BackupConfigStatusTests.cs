using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>持久 Status（Normal/Error）：失败置 Error，下次成功自清 Normal，手动 reset 清错（§4.2 决策 2）。</summary>
public sealed class BackupConfigStatusTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly BackupConfigService _sut;

    public BackupConfigStatusTests()
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

        await _sut.SetNormalAsync(id); // 成功自清（决策 2）
        var c2 = await _sut.GetAsync(id);
        Assert.Equal(BackupStatus.Normal, c2!.Status);
        Assert.Null(c2.LastError);
        Assert.Null(c2.LastErrorAt);

        await _sut.SetErrorAsync(id, "again");
        await _sut.ResetStatusAsync(id); // 手动 reset
        var c3 = await _sut.GetAsync(id);
        Assert.Equal(BackupStatus.Normal, c3!.Status);
        Assert.Null(c3.LastError);
        Assert.Null(c3.LastErrorAt);
    }

    [Fact]
    public async Task SetError_On_Missing_Config_Is_NoOp()
    {
        // 不存在的 id 不应抛异常（写状态点可能在 config 已被删除后仍回写）。
        await _sut.SetErrorAsync(999, "boom");
        await _sut.SetNormalAsync(999);
        await _sut.ResetStatusAsync(999);
    }
}
