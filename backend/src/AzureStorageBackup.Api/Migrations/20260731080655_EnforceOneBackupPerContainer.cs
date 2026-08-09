using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class EnforceOneBackupPerContainer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The database this unique index has to land in may already be holding duplicates — the very product of the bug it plugs
            // (create/import never checked for duplicates before). Creating the index outright fails, and a failed migration means the app will not start.
            //
            // So move the duplicates aside first, and **delete not a single one**: those configs point at real cloud data; deleting the
            // local record does not make the cloud data disappear, it only makes the user unable to ever see it again. Moving one aside
            // means rewriting ContainerName into a name Azure flatly refuses (it has a dot in it), so it can never touch a real container
            // again while staying visible in the UI as-is, together with a LastError explaining why, for the user to deal with as they choose.
            //
            // The winner is the smallest Id in each group: the one created first is this container's true owner. In SQLite every expression
            // in a SET reads this row's old values, so the subquery below still matches the whole group by the old ContainerName.
            migrationBuilder.Sql(
                """
                UPDATE BackupConfigs
                SET ContainerName = ContainerName || '.duplicate.' || Id,
                    Status = 1,
                    LastError = 'Set aside during upgrade: container ''' || ContainerName ||
                        ''' is already held by the backup "' ||
                        (SELECT w.Name FROM BackupConfigs w
                         WHERE w.AccountId = BackupConfigs.AccountId
                           AND w.ContainerName = BackupConfigs.ContainerName
                         ORDER BY w.Id LIMIT 1) ||
                        '". Two backups on one container overwrite each other''s version history, so this ' ||
                        'one was pointed at a name Azure will not accept and can no longer run. Nothing in ' ||
                        'the cloud was touched. Restore anything you still need through the other backup, ' ||
                        'then delete this entry.'
                WHERE Id NOT IN (SELECT MIN(Id) FROM BackupConfigs GROUP BY AccountId, ContainerName);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_BackupConfigs_AccountId_ContainerName",
                table: "BackupConfigs",
                columns: new[] { "AccountId", "ContainerName" },
                unique: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Only the index is dropped; the names that were moved aside are not restored. Restoring them would put the duplicates straight
        /// back, and the very next upgrade after the downgrade would move them aside again (same winner, but the moved-aside one would
        /// pick up a second layer of suffix). A moved-aside config is visible and deletable in the UI, and leaving it to the user is safer than having the migration shuttle it back and forth.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackupConfigs_AccountId_ContainerName",
                table: "BackupConfigs");
        }
    }
}
