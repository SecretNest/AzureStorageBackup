using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
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
        _db = new AppDbContext(options);
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
        Assert.Equal("second", all[0].Message); // newest first
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
    public async Task Trim_Deletes_Old_Ephemeral_But_Keeps_Durable()
    {
        var now = DateTimeOffset.UtcNow;
        _db.LogEntries.Add(new LogEntry { Timestamp = now.AddDays(-40), Level = OperationLogLevel.Debug, Source = "s", Message = "old-ephemeral", Ephemeral = true });
        _db.LogEntries.Add(new LogEntry { Timestamp = now.AddDays(-40), Level = OperationLogLevel.Error, Source = "s", Message = "old-durable", Ephemeral = false });
        _db.LogEntries.Add(new LogEntry { Timestamp = now, Level = OperationLogLevel.Debug, Source = "s", Message = "new-ephemeral", Ephemeral = true });
        await _db.SaveChangesAsync();

        await _sut.TrimAsync(maxAgeDays: 14, now);

        var kept = (await _sut.QueryAsync(null, null, null, null, 100)).Select(x => x.Message).ToHashSet();
        Assert.DoesNotContain("old-ephemeral", kept);      // expired ephemeral → deleted
        Assert.Contains("old-durable", kept);              // durable is unaffected by age
        Assert.Contains("new-ephemeral", kept);            // ephemeral still within the window is kept
    }

    [Fact]
    public async Task Append_Defaults_Info_To_Ephemeral_And_Warning_To_Durable()
    {
        await _sut.AppendAsync(OperationLogLevel.Info, "s", "info");
        await _sut.AppendAsync(OperationLogLevel.Warning, "s", "warn");
        await _sut.AppendAsync(OperationLogLevel.Info, "s", "forced-durable", durable: true);

        var all = await _db.LogEntries.ToListAsync();
        Assert.True(all.Single(e => e.Message == "info").Ephemeral);
        Assert.False(all.Single(e => e.Message == "warn").Ephemeral);
        Assert.False(all.Single(e => e.Message == "forced-durable").Ephemeral);
    }

    [Fact]
    public async Task PurgeBefore_Deletes_All_Before_Cutoff()
    {
        var now = DateTimeOffset.UtcNow;
        _db.LogEntries.Add(new LogEntry { Timestamp = now.AddDays(-2), Level = OperationLogLevel.Error, Source = "s", Message = "old", Ephemeral = false });
        _db.LogEntries.Add(new LogEntry { Timestamp = now, Level = OperationLogLevel.Error, Source = "s", Message = "new", Ephemeral = false });
        await _db.SaveChangesAsync();

        await _sut.PurgeBeforeAsync(now.AddDays(-1));

        Assert.Equal("new", Assert.Single(await _sut.QueryAsync(null, null, null, null, 100)).Message);
    }

    /// <summary>§5.3: source carries the account dimension ("{op}:{accountId}/{container}").
    /// DeleteForContainerAsync matches accountId+container exactly (the ":{accountId}/{container}" suffix):
    /// when deleting the logs of one container under one account, logs of the same container under other
    /// accounts, and logs of other containers under the same account, must all survive (BackupConfig has a
    /// unique index on (AccountId, ContainerName), so two accounts may own containers of the same name —
    /// a container-only match would delete across accounts, which rules it out).</summary>
    [Fact]
    public async Task DeleteForContainer_Removes_Only_That_Accounts_Container_Logs()
    {
        await _sut.AppendAsync(OperationLogLevel.Info, "backup:3/photos", "a", durable: true);
        await _sut.AppendAsync(OperationLogLevel.Info, "check:3/photos", "b", durable: true);
        await _sut.AppendAsync(OperationLogLevel.Info, "restore:3/photos", "c", durable: true);
        await _sut.AppendAsync(OperationLogLevel.Info, "backup:3/docs", "d", durable: true);      // same account, different container → kept
        await _sut.AppendAsync(OperationLogLevel.Info, "backup:5/photos", "e", durable: true);    // different account, same container name → kept
        await _sut.AppendAsync(OperationLogLevel.Info, "check:5/photos", "f", durable: true);     // different account, same container name → kept

        await _sut.DeleteForContainerAsync(3, "photos");

        var kept = (await _sut.QueryAsync(null, null, null, null, 100)).Select(e => e.Message).ToHashSet();
        Assert.Equal(new HashSet<string> { "d", "e", "f" }, kept); // only account 3's photos logs were deleted
    }

    /// <summary>Legacy rows left over from before the format change ("{op}:{container}", no account dimension) deliberately get no fallback match —
    /// the project is not live yet, and a tiny number of orphaned old logs beats any risk of deleting across accounts (see the DeleteForContainerAsync comment).</summary>
    [Fact]
    public async Task DeleteForContainer_Does_Not_Match_Legacy_Account_Less_Format()
    {
        await _sut.AppendAsync(OperationLogLevel.Info, "backup:photos", "legacy", durable: true);

        await _sut.DeleteForContainerAsync(3, "photos");

        var kept = await _sut.QueryAsync(null, null, null, null, 100);
        Assert.Equal("legacy", Assert.Single(kept).Message); // not deleted
    }

    /// <summary>Regression lock (§5.5): DeleteForContainerAsync matches on the source suffix and does not distinguish level/Ephemeral,
    /// so ephemeral Debug/verbose logs are swept away together with durable ones when a config is deleted — no orphaned diagnostic logs are left behind.</summary>
    [Fact]
    public async Task DeleteForContainer_Removes_All_Levels_Including_Debug()
    {
        await _sut.AppendAsync(OperationLogLevel.Debug, "backup:3/c", "verbose file x");
        await _sut.AppendAsync(OperationLogLevel.Warning, "backup:3/c", "done");

        await _sut.DeleteForContainerAsync(3, "c");

        var remaining = await _sut.QueryAsync(null, "backup:3/c", null, null, 100);
        Assert.Empty(remaining);
    }
}
