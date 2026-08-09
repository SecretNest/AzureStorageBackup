using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The unique index behind "a container can have only one backup config" was added after the fact, and the database it has to
/// land on may already be holding duplicates — the very product of the bug this fix plugs. A plain CREATE UNIQUE INDEX would fail,
/// and a failed migration means the app does not start; the user is on a NAS with no command line, so that amounts to backups for the whole device grinding to a halt.
/// <para>
/// So the migration has to move the duplicates aside itself, and **must not delete a single one**: those duplicate configs point at real
/// cloud data, and deleting the local record does not make the cloud data go away, it only makes it invisible to the user forever. Moving
/// aside is done by changing ContainerName to a name Azure will never accept (it contains a dot), so it can no longer touch any real
/// container while still showing up in the UI exactly as it was — along with a LastError explaining why, leaving the user to decide what to do with it.
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
        Assert.Equal(3, rows.Count); // not a single one lost

        // The earliest one is the untouched winner: it is the real owner of that container.
        Assert.Equal("shared", rows[0].ContainerName);
        Assert.Equal(BackupStatus.Normal, rows[0].Status);
        Assert.Null(rows[0].LastError);

        foreach (var moved in rows.Skip(1))
        {
            Assert.NotEqual("shared", moved.ContainerName);
            // Azure rejects any name containing a dot, so this config can never touch a real container again.
            Assert.Contains(".", moved.ContainerName, StringComparison.Ordinal);
            Assert.Contains("shared", moved.ContainerName, StringComparison.Ordinal);
            Assert.Equal(BackupStatus.Error, moved.Status);
            Assert.NotNull(moved.LastError);
            // Spell out who is holding it and what state this config is now in — otherwise the UI just inexplicably grows an extra error.
            Assert.Contains("First", moved.LastError!, StringComparison.Ordinal);
        }
    }

    /// <summary>A database with no duplicates (the vast majority) must come through the migration byte-identical.</summary>
    [Fact]
    public async Task A_Clean_Database_Passes_Through_Untouched()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = await MigratedToJustBeforeAsync(connection);

        await db.Database.ExecuteSqlRawAsync(InsertSql(1, "photos", "Photos"));
        await db.Database.ExecuteSqlRawAsync(InsertSql(1, "docs", "Docs"));
        // The same container name under a different account is legal and must not be moved aside as a duplicate.
        await db.Database.ExecuteSqlRawAsync(InsertSql(2, "photos", "Other"));

        await db.Database.MigrateAsync();

        var rows = await db.BackupConfigs.OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(["photos", "docs", "photos"], rows.Select(r => r.ContainerName));
        Assert.All(rows, r => Assert.Equal(BackupStatus.Normal, r.Status));
        Assert.All(rows, r => Assert.Null(r.LastError));
    }
}
