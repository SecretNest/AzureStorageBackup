using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public sealed class OperationLogServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly OperationLogService _sut;

    public OperationLogServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options, new EncryptionService(new EphemeralDataProtectionProvider()));
        _db.Database.EnsureCreated();
        _sut = new OperationLogService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Append_Then_Query_Returns_Newest_First()
    {
        await _sut.AppendAsync(OperationLogLevel.Info, "backup:a", "first");
        await _sut.AppendAsync(OperationLogLevel.Error, "backup:a", "second");

        var all = await _sut.QueryAsync(null, null, null, null, 100);

        Assert.Equal(2, all.Count);
        Assert.Equal("second", all[0].Message); // 最新在前
    }

    [Fact]
    public async Task Filters_By_Minimum_Level()
    {
        await _sut.AppendAsync(OperationLogLevel.Info, "s", "i");
        await _sut.AppendAsync(OperationLogLevel.Warning, "s", "w");
        await _sut.AppendAsync(OperationLogLevel.Error, "s", "e");

        var warnPlus = await _sut.QueryAsync(OperationLogLevel.Warning, null, null, null, 100);

        Assert.Equal(2, warnPlus.Count);
        Assert.DoesNotContain(warnPlus, x => x.Message == "i");
    }

    [Fact]
    public async Task Filters_By_Source()
    {
        await _sut.AppendAsync(OperationLogLevel.Info, "backup:a", "a1");
        await _sut.AppendAsync(OperationLogLevel.Info, "backup:b", "b1");

        var onlyA = await _sut.QueryAsync(null, "backup:a", null, null, 100);

        Assert.Equal("a1", Assert.Single(onlyA).Message);
    }

    [Fact]
    public async Task Filters_By_Time_Range()
    {
        await _sut.AppendAsync(OperationLogLevel.Info, "s", "m");
        var all = await _sut.QueryAsync(null, null, null, null, 100);
        var ts = all[0].Timestamp;

        var future = await _sut.QueryAsync(null, null, ts.AddMinutes(1), null, 100);
        Assert.Empty(future);

        var since = await _sut.QueryAsync(null, null, ts.AddMinutes(-1), null, 100);
        Assert.Single(since);
    }

    [Fact]
    public async Task Respects_Limit()
    {
        for (var i = 0; i < 5; i++)
            await _sut.AppendAsync(OperationLogLevel.Info, "s", $"m{i}");

        Assert.Equal(3, (await _sut.QueryAsync(null, null, null, null, 3)).Count);
    }

    [Fact]
    public async Task Clear_Removes_All()
    {
        await _sut.AppendAsync(OperationLogLevel.Info, "s", "m");
        await _sut.ClearAsync();

        Assert.Empty(await _sut.QueryAsync(null, null, null, null, 100));
    }

    [Fact]
    public async Task Trim_By_Max_Entries_Keeps_Newest()
    {
        for (var i = 0; i < 5; i++)
            await _sut.AppendAsync(OperationLogLevel.Info, "s", $"m{i}");

        await _sut.TrimAsync(maxEntries: 2, maxAgeDays: null, DateTimeOffset.UtcNow);

        var kept = await _sut.QueryAsync(null, null, null, null, 100);
        Assert.Equal(2, kept.Count);
        Assert.Equal(["m4", "m3"], kept.Select(x => x.Message)); // 最新两条
    }

    [Fact]
    public async Task Trim_By_Age_Deletes_Old()
    {
        _db.LogEntries.Add(new LogEntry { Timestamp = DateTimeOffset.UtcNow.AddDays(-40), Level = OperationLogLevel.Info, Source = "s", Message = "old" });
        _db.LogEntries.Add(new LogEntry { Timestamp = DateTimeOffset.UtcNow, Level = OperationLogLevel.Info, Source = "s", Message = "new" });
        await _db.SaveChangesAsync();

        await _sut.TrimAsync(maxEntries: null, maxAgeDays: 30, DateTimeOffset.UtcNow);

        var kept = await _sut.QueryAsync(null, null, null, null, 100);
        Assert.Equal("new", Assert.Single(kept).Message);
    }

    [Fact]
    public async Task Trim_With_No_Limits_Does_Nothing()
    {
        await _sut.AppendAsync(OperationLogLevel.Info, "s", "m");
        await _sut.TrimAsync(null, null, DateTimeOffset.UtcNow);
        Assert.Single(await _sut.QueryAsync(null, null, null, null, 100));
    }
}
