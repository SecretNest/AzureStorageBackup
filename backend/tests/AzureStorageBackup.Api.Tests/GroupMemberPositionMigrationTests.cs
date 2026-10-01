using AzureStorageBackup.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Until Position existed, a group's members ran in (AccountId, ContainerName) order — GroupService sorted them into it
/// on the way in. The migration numbers existing members in that same order, so an upgrade changes no group's
/// sequence; it only makes the sequence visible and editable. The rows are inserted out of that order on purpose, so
/// that falling back to row order would show up as a failure.
/// </summary>
public class GroupMemberPositionMigrationTests
{
    [Fact]
    public async Task Existing_Members_Are_Numbered_In_The_Order_They_Ran_In()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);

        var migrations = db.Database.GetMigrations().ToList();
        var target = migrations[migrations.IndexOf(
            migrations.First(m => m.EndsWith("AddGroupMemberPosition", StringComparison.Ordinal))) - 1];
        await db.Database.GetService<IMigrator>().MigrateAsync(target);

        await db.Database.ExecuteSqlRawAsync("INSERT INTO Groups (Id, Name, CreatedAt) VALUES (1, 'g1', '2026-01-01'), (2, 'g2', '2026-01-01');");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO GroupMembers (GroupId, AccountId, ContainerName) VALUES " +
            "(1, 2, 'a'), (1, 1, 'z'), (1, 1, 'b'), " + // ran as 1/b, 1/z, 2/a
            "(2, 1, 'b'), (2, 1, 'a');");             // ran as 1/a, 1/b — numbered per group, not across

        await db.Database.MigrateAsync();

        var g1 = await db.GroupMembers.Where(m => m.GroupId == 1).OrderBy(m => m.Position)
            .Select(m => $"{m.AccountId}/{m.ContainerName}").ToListAsync();
        Assert.Equal(["1/b", "1/z", "2/a"], g1);
        var g2 = await db.GroupMembers.Where(m => m.GroupId == 2).OrderBy(m => m.Position)
            .Select(m => new { m.Position, m.ContainerName }).ToListAsync();
        Assert.Equal([0, 1], g2.Select(m => m.Position));
        Assert.Equal(["a", "b"], g2.Select(m => m.ContainerName));
    }
}
