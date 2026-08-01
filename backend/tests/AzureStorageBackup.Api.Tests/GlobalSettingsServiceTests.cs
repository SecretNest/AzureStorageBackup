using System.Diagnostics;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
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

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
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

    [Fact]
    public async Task GetAsync_Normalizes_Zero_Migrated_Columns_To_Defaults()
    {
        // 模拟迁移遗留行：新列 SQL 默认 0。
        _db.GlobalSettings.Add(new Models.GlobalSettings { StagedLimitBytes = 0, ProcessingMaxAttempts = 0 });
        await _db.SaveChangesAsync();

        var s = await _sut.GetAsync();
        Assert.Equal(2L * 1024 * 1024 * 1024, s.StagedLimitBytes); // 2GB 默认
        Assert.Equal(5, s.ProcessingMaxAttempts);
    }

    [Fact]
    public async Task Upsert_Persists_SevenZipPriority()
    {
        var s = await _sut.GetAsync();
        s.SevenZipPriority = SevenZipCpuPriority.Normal;
        await _sut.UpsertAsync(s);
        Assert.Equal(SevenZipCpuPriority.Normal, (await _sut.GetAsync()).SevenZipPriority);
    }

    [Fact]
    public async Task GetAsync_Defaults_SevenZipPriority_To_Lowest()
    {
        Assert.Equal(SevenZipCpuPriority.Lowest, (await _sut.GetAsync()).SevenZipPriority);
    }

    [Fact]
    public async Task GetAsync_Leaves_Migrated_Zero_SevenZipPriority_As_Lowest()
    {
        // 迁移给既有行填的是 0。枚举正是照着这一点排的（Lowest = 0），所以这一行**不该**
        // 被上面那段"读到 0 就换回默认值"的规范化碰到——它读出来本就已经是默认值。
        _db.GlobalSettings.Add(new Models.GlobalSettings { SevenZipPriority = 0 });
        await _db.SaveChangesAsync();

        Assert.Equal(SevenZipCpuPriority.Lowest, (await _sut.GetAsync()).SevenZipPriority);
    }

    [Theory]
    [InlineData(SevenZipCpuPriority.Lowest, ProcessPriorityClass.Idle)]
    [InlineData(SevenZipCpuPriority.BelowNormal, ProcessPriorityClass.BelowNormal)]
    [InlineData(SevenZipCpuPriority.Normal, ProcessPriorityClass.Normal)]
    public void Maps_To_ProcessPriorityClass(SevenZipCpuPriority priority, ProcessPriorityClass expected)
        => Assert.Equal(expected, priority.ToProcessPriorityClass());

    [Fact]
    public void Maps_Unknown_Value_To_Lowest()
    {
        // 数据库里存了个我们不认识的档位（降级、手改）：认不出来时压慢一点是小事，
        // 把机器卡住不是——所以 default 分支必须倒向最低，而不是 Normal。
        Assert.Equal(ProcessPriorityClass.Idle, ((SevenZipCpuPriority)99).ToProcessPriorityClass());
    }
}
