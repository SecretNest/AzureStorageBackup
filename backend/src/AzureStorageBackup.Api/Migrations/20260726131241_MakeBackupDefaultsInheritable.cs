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
            // 该列原先 null = 关闭分卷；改造后 null = 继承。先把现有的 null 改写为 0，
            // 否则每一份明确关闭了分卷的备份都会在升级后突然开始跟随全局设置。
            migrationBuilder.Sql("UPDATE BackupConfigs SET VolumeBytes = 0 WHERE VolumeBytes IS NULL;");

            // 这三列本来就是 nullable，不在下面 AlterColumn 的名单里，因此最初审查时被漏掉——
            // 但它们的语义翻转和 VolumeBytes 一模一样：原先 null = 没有规则，改造后 null = 继承。
            // 不改写的话，每一份此前明确「无规则」的备份，升级后会突然开始套用全局规则，
            // 导致匹配到的文件从新版本里消失，且没有任何日志或界面提示。
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

            // 与上面对称的回滚：把「明确无规则」的空串还原回 null。
            migrationBuilder.Sql("UPDATE BackupConfigs SET IgnoreRules       = NULL WHERE IgnoreRules       = '';");
            migrationBuilder.Sql("UPDATE BackupConfigs SET DontCompressRules = NULL WHERE DontCompressRules = '';");
            migrationBuilder.Sql("UPDATE BackupConfigs SET DontGroupRules    = NULL WHERE DontGroupRules    = '';");
        }
    }
}
