using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class MakeBackupDefaultsInheritable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This column used to mean null = volume splitting off; after the change null = inherit. Rewrite the existing nulls to 0 first,
            // otherwise every backup that had explicitly turned splitting off would suddenly start following the global setting after the upgrade.
            migrationBuilder.Sql("UPDATE BackupConfigs SET VolumeBytes = 0 WHERE VolumeBytes IS NULL;");

            // These three columns were nullable to begin with and are not on the AlterColumn list below, which is exactly why the first review missed them —
            // but their semantics flip identically to VolumeBytes: null used to mean no rules, after the change null means inherit.
            // Without the rewrite, every backup that had explicitly said "no rules" would suddenly start applying the global rules after the upgrade,
            // making the matched files disappear from new versions, with no log line and no hint in the UI.
            migrationBuilder.Sql("UPDATE BackupConfigs SET IgnoreRules       = '' WHERE IgnoreRules       IS NULL;");
            migrationBuilder.Sql("UPDATE BackupConfigs SET DontCompressRules = '' WHERE DontCompressRules IS NULL;");
            migrationBuilder.Sql("UPDATE BackupConfigs SET DontGroupRules    = '' WHERE DontGroupRules    IS NULL;");

            migrationBuilder.AlterColumn<bool>(
                name: "VerboseLogging",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<long>(
                name: "SingleFileThresholdBytes",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "RetentionMode",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "MaxVersions",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<int>(
                name: "MaxAgeDays",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<bool>(
                name: "IncludeSymlinks",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<long>(
                name: "GroupCapBytes",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "VerboseLogging",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "SingleFileThresholdBytes",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "RetentionMode",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "MaxVersions",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "MaxAgeDays",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<bool>(
                name: "IncludeSymlinks",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "GroupCapBytes",
                table: "BackupConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.Sql("UPDATE BackupConfigs SET VolumeBytes = NULL WHERE VolumeBytes = 0;");

            // The rollback symmetric to the above: turn the empty string that means "explicitly no rules" back into null.
            migrationBuilder.Sql("UPDATE BackupConfigs SET IgnoreRules       = NULL WHERE IgnoreRules       = '';");
            migrationBuilder.Sql("UPDATE BackupConfigs SET DontCompressRules = NULL WHERE DontCompressRules = '';");
            migrationBuilder.Sql("UPDATE BackupConfigs SET DontGroupRules    = NULL WHERE DontGroupRules    = '';");
        }
    }
}
