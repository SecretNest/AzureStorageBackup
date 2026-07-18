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
    public async Task Trim_Deletes_Old_Ephemeral_But_Keeps_Durable()
    {
        var now = DateTimeOffset.UtcNow;
        _db.LogEntries.Add(new LogEntry { Timestamp = now.AddDays(-40), Level = OperationLogLevel.Debug, Source = "s", Message = "old-ephemeral", Ephemeral = true });
        _db.LogEntries.Add(new LogEntry { Timestamp = now.AddDays(-40), Level = OperationLogLevel.Error, Source = "s", Message = "old-durable", Ephemeral = false });
        _db.LogEntries.Add(new LogEntry { Timestamp = now, Level = OperationLogLevel.Debug, Source = "s", Message = "new-ephemeral", Ephemeral = true });
        await _db.SaveChangesAsync();

        await _sut.TrimAsync(maxAgeDays: 14, now);

        var kept = (await _sut.QueryAsync(null, null, null, null, 100)).Select(x => x.Message).ToHashSet();
        Assert.DoesNotContain("old-ephemeral", kept);      // 超期短存 → 删
        Assert.Contains("old-durable", kept);              // 长存不受年龄影响
        Assert.Contains("new-ephemeral", kept);            // 未超期短存保留
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

    /// <summary>§5.3：source 携带 account 维度（"{op}:{accountId}/{container}"）。
    /// DeleteForContainerAsync 按 accountId+container 精确匹配（":{accountId}/{container}" 后缀）：
    /// 删除某 account 下某 container 的日志时，同一 container 的其他 account 日志、以及同 account 下
    /// 其他 container 的日志都必须保留（BackupConfig 在 (AccountId, ContainerName) 唯一索引，
    /// 两个 account 可有同名 container——container-only 匹配会跨 account 误删，故不可用）。</summary>
    [Fact]
    public async Task DeleteForContainer_Removes_Only_That_Accounts_Container_Logs()
    {
        await _sut.AppendAsync(OperationLogLevel.Info, "backup:3/photos", "a", durable: true);
        await _sut.AppendAsync(OperationLogLevel.Info, "check:3/photos", "b", durable: true);
        await _sut.AppendAsync(OperationLogLevel.Info, "restore:3/photos", "c", durable: true);
        await _sut.AppendAsync(OperationLogLevel.Info, "backup:3/docs", "d", durable: true);      // 同 account，不同 container → 保留
        await _sut.AppendAsync(OperationLogLevel.Info, "backup:5/photos", "e", durable: true);    // 不同 account，同名 container → 保留
        await _sut.AppendAsync(OperationLogLevel.Info, "check:5/photos", "f", durable: true);     // 不同 account，同名 container → 保留

        await _sut.DeleteForContainerAsync(3, "photos");

        var kept = (await _sut.QueryAsync(null, null, null, null, 100)).Select(e => e.Message).ToHashSet();
        Assert.Equal(new HashSet<string> { "d", "e", "f" }, kept); // 仅 account 3 的 photos 日志被删
    }

    /// <summary>改版前遗留的旧格式行（"{op}:{container}"，无 account 维度）故意不再兜底匹配——
    /// 项目尚未上线，宁可留极少量孤儿旧日志，也不能有跨 account 误删风险（见 DeleteForContainerAsync 注释）。</summary>
    [Fact]
    public async Task DeleteForContainer_Does_Not_Match_Legacy_Account_Less_Format()
    {
        await _sut.AppendAsync(OperationLogLevel.Info, "backup:photos", "legacy", durable: true);

        await _sut.DeleteForContainerAsync(3, "photos");

        var kept = await _sut.QueryAsync(null, null, null, null, 100);
        Assert.Equal("legacy", Assert.Single(kept).Message); // 未被删除
    }
}
