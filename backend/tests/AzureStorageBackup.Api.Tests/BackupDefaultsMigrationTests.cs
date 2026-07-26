using AzureStorageBackup.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 迁移前 VolumeBytes 的 null 表示「关闭分卷」，迁移后 null 表示「继承」。
/// 若不改写，每一份明确关掉分卷的备份都会在升级后突然开始跟随全局设置。
///
/// IgnoreRules / DontCompressRules / DontGroupRules 三列语义翻转完全相同：
/// 迁移前 null = 无规则，迁移后 null = 继承。这三列本来就是 nullable，不出现在
/// AlterColumn 名单里，正因如此才在最初的审查中被漏掉——这里连同 VolumeBytes
/// 一起用同一份迁移前记录来钉住，不能只测 VolumeBytes 这一列。
/// </summary>
public class BackupDefaultsMigrationTests
{
    [Fact]
    public async Task Existing_Null_VolumeBytes_Becomes_Zero_Not_Inherit()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);

        // 迁移到本轮之前的那一版，插入一份「关闭分卷、无任何规则」的配置，再迁到最新。
        var migrations = db.Database.GetMigrations().ToList();
        var target = migrations[migrations.IndexOf(
            migrations.First(m => m.EndsWith("MakeBackupDefaultsInheritable", StringComparison.Ordinal))) - 1];

        await db.Database.GetService<IMigrator>().MigrateAsync(target);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO BackupConfigs (AccountId, ContainerName, Name, LocalRoot, IndexTier, DataTier, " +
            "IncludeSymlinks, MaxVersions, MaxAgeDays, RetentionMode, SingleFileThresholdBytes, " +
            "GroupCapBytes, VolumeBytes, VerboseLogging, IgnoreRules, DontCompressRules, DontGroupRules, " +
            "CreatedAt, Status) " +
            "VALUES (1, 'c', 'n', '/tmp', 0, 3, 0, 100, 180, 0, 5242880, 104857600, NULL, 0, NULL, NULL, NULL, " +
            "'2026-01-01', 0);");

        await db.Database.MigrateAsync();

        var row = await db.BackupConfigs
            .Select(c => new { c.VolumeBytes, c.IgnoreRules, c.DontCompressRules, c.DontGroupRules })
            .SingleAsync();
        Assert.Equal(0, row.VolumeBytes);
        Assert.Equal(string.Empty, row.IgnoreRules);
        Assert.Equal(string.Empty, row.DontCompressRules);
        Assert.Equal(string.Empty, row.DontGroupRules);
    }
}
