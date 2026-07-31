using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 「一个 container 只能有一条备份配置」这条唯一索引是补加的，而它要落到的库里可能已经躺着
/// 重复——那正是这次修复要堵的 bug 的产物。直接 CREATE UNIQUE INDEX 会失败，而迁移失败
/// 就是应用起不来；用户在 NAS 上，拿不到命令行，那等于整台设备的备份停摆。
/// <para>
/// 所以迁移必须自己把重复挪开，而且**一条都不许删**：重复的那些配置指着真实的云端数据，
/// 删掉本地记录不会让云端数据消失，只会让用户再也看不见它。挪开的做法是把 ContainerName
/// 改成一个 Azure 根本不接受的名字（带点），这样它既不会再碰任何真实 container，又原样留在
/// 界面上——连同一条说明为什么的 LastError，让用户自己决定怎么处置。
/// </para>
/// </summary>
public class DuplicateConfigMigrationTests
{
    private const string MigrationName = "EnforceOneBackupPerContainer";

    private static async Task<AppDbContext> MigratedToJustBeforeAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);

        var migrations = db.Database.GetMigrations().ToList();
        var target = migrations[migrations.IndexOf(
            migrations.First(m => m.EndsWith(MigrationName, StringComparison.Ordinal))) - 1];
        await db.Database.GetService<IMigrator>().MigrateAsync(target);
        return db;
    }

    private static string InsertSql(int accountId, string container, string name) =>
        "INSERT INTO BackupConfigs (AccountId, ContainerName, Name, LocalRoot, IndexTier, DataTier, " +
        "IncludeSymlinks, MaxVersions, MaxAgeDays, RetentionMode, SingleFileThresholdBytes, " +
        "GroupCapBytes, VolumeBytes, VerboseLogging, IgnoreRules, DontCompressRules, DontGroupRules, " +
        "CreatedAt, Status) " +
        $"VALUES ({accountId}, '{container}', '{name}', '/data/{name}', 0, 3, 0, 100, 180, 0, 5242880, " +
        "104857600, 0, 0, '', '', '', '2026-01-01', 0);";

    [Fact]
    public async Task Existing_Duplicates_Are_Set_Aside_Rather_Than_Dropped()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = await MigratedToJustBeforeAsync(connection);

        await db.Database.ExecuteSqlRawAsync(InsertSql(1, "shared", "First"));
        await db.Database.ExecuteSqlRawAsync(InsertSql(1, "shared", "Second"));
        await db.Database.ExecuteSqlRawAsync(InsertSql(1, "shared", "Third"));

        await db.Database.MigrateAsync();

        var rows = await db.BackupConfigs.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(3, rows.Count); // 一条都没丢

        // 最早的那条是原样的赢家：它才是那个 container 真正的主人。
        Assert.Equal("shared", rows[0].ContainerName);
        Assert.Equal(BackupStatus.Normal, rows[0].Status);
        Assert.Null(rows[0].LastError);

        foreach (var moved in rows.Skip(1))
        {
            Assert.NotEqual("shared", moved.ContainerName);
            // 带点的名字 Azure 一概不收，所以这条配置绝无可能再动到任何真实 container。
            Assert.Contains(".", moved.ContainerName, StringComparison.Ordinal);
            Assert.Contains("shared", moved.ContainerName, StringComparison.Ordinal);
            Assert.Equal(BackupStatus.Error, moved.Status);
            Assert.NotNull(moved.LastError);
            // 说清是谁占着、以及这条配置现在处在什么状态——否则界面上只是莫名其妙多了个错。
            Assert.Contains("First", moved.LastError!, StringComparison.Ordinal);
        }
    }

    /// <summary>没有重复的库（绝大多数）迁过去必须一个字节都不变。</summary>
    [Fact]
    public async Task A_Clean_Database_Passes_Through_Untouched()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = await MigratedToJustBeforeAsync(connection);

        await db.Database.ExecuteSqlRawAsync(InsertSql(1, "photos", "Photos"));
        await db.Database.ExecuteSqlRawAsync(InsertSql(1, "docs", "Docs"));
        // 不同账户下的同名 container 是合法的，不能被当成重复挪走。
        await db.Database.ExecuteSqlRawAsync(InsertSql(2, "photos", "Other"));

        await db.Database.MigrateAsync();

        var rows = await db.BackupConfigs.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(["photos", "docs", "photos"], rows.Select(r => r.ContainerName));
        Assert.All(rows, r => Assert.Equal(BackupStatus.Normal, r.Status));
        Assert.All(rows, r => Assert.Null(r.LastError));
    }
}
