using AzureStorageBackup.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Before the migration, null on VolumeBytes meant "volumes off"; after it, null means "inherit".
/// Without a rewrite, every backup that had explicitly turned volumes off would suddenly start following the global setting after the upgrade.
///
/// The three columns IgnoreRules / DontCompressRules / DontGroupRules flip meaning in exactly the same way:
/// before the migration null = no rules, after it null = inherit. Those three were nullable already, so they do not show up in
/// the AlterColumn list — which is exactly why they were missed in the original review. They are pinned down here together with
/// VolumeBytes using the same pre-migration row; testing the VolumeBytes column alone is not enough.
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

        // Migrate to the version just before this round, insert a config with "volumes off, no rules at all", then migrate to the latest.
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
