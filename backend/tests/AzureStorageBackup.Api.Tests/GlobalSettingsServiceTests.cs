using System.Diagnostics;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>Global settings round-trip (§5.1): fields such as ProcessingMaxAttempts are persisted along with Upsert.</summary>
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
        // Simulates a row left over by a migration: new columns default to 0 in SQL.
        _db.GlobalSettings.Add(new Models.GlobalSettings { StagedLimitBytes = 0, ProcessingMaxAttempts = 0 });
        await _db.SaveChangesAsync();

        var s = await _sut.GetAsync();
        Assert.Equal(2L * 1024 * 1024 * 1024, s.StagedLimitBytes); // 2GB default
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
        // The migration filled existing rows with 0. The enum is ordered around exactly that (Lowest = 0), so this row must **not**
        // be caught by the "read a 0, swap the default back in" normalization above — what it reads back already is the default.
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
        // The database holds a level we do not recognize (a downgrade, a hand edit): compressing a little slower when we cannot identify it
        // is a small matter, wedging the machine is not — so the default branch has to fall to the lowest, not to Normal.
        Assert.Equal(ProcessPriorityClass.Idle, ((SevenZipCpuPriority)99).ToProcessPriorityClass());
    }
}
