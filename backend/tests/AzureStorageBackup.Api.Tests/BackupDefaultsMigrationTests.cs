using AzureStorageBackup.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 迁移前 VolumeBytes 的 null 表示「关闭分卷」，迁移后 null 表示「继承」。
/// 若不改写，每一份明确关掉分卷的备份都会在升级后突然开始跟随全局设置。
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

        // 迁移到本轮之前的那一版，插入一份「关闭分卷」的配置，再迁到最新。
        var migrations = db.Database.GetMigrations().ToList();
        var target = migrations[migrations.IndexOf(
            migrations.First(m => m.EndsWith("MakeBackupDefaultsInheritable", StringComparison.Ordinal))) - 1];

        await db.Database.GetService<IMigrator>().MigrateAsync(target);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO BackupConfigs (AccountId, ContainerName, Name, LocalRoot, IndexTier, DataTier, " +
            "IncludeSymlinks, MaxVersions, MaxAgeDays, RetentionMode, SingleFileThresholdBytes, " +
            "GroupCapBytes, VolumeBytes, VerboseLogging, CreatedAt, Status) " +
            "VALUES (1, 'c', 'n', '/tmp', 0, 3, 0, 100, 180, 0, 5242880, 104857600, NULL, 0, '2026-01-01', 0);");

        await db.Database.MigrateAsync();

        var volumeBytes = await db.BackupConfigs.Select(c => c.VolumeBytes).SingleAsync();
        Assert.Equal(0, volumeBytes);
    }
}
