using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class SplitRuleListsByCaseSensitivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultCrossDirGroupRulesCaseInsensitive",
                table: "GlobalSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultDontCompressRulesCaseInsensitive",
                table: "GlobalSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultDontGroupRulesCaseInsensitive",
                table: "GlobalSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultIgnoreRulesCaseInsensitive",
                table: "GlobalSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CrossDirGroupRulesCaseInsensitive",
                table: "BackupConfigs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DontCompressRulesCaseInsensitive",
                table: "BackupConfigs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DontGroupRulesCaseInsensitive",
                table: "BackupConfigs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IgnoreRulesCaseInsensitive",
                table: "BackupConfigs",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultCrossDirGroupRulesCaseInsensitive",
                table: "GlobalSettings");

            migrationBuilder.DropColumn(
                name: "DefaultDontCompressRulesCaseInsensitive",
                table: "GlobalSettings");

            migrationBuilder.DropColumn(
                name: "DefaultDontGroupRulesCaseInsensitive",
                table: "GlobalSettings");

            migrationBuilder.DropColumn(
                name: "DefaultIgnoreRulesCaseInsensitive",
                table: "GlobalSettings");

            migrationBuilder.DropColumn(
                name: "CrossDirGroupRulesCaseInsensitive",
                table: "BackupConfigs");

            migrationBuilder.DropColumn(
                name: "DontCompressRulesCaseInsensitive",
                table: "BackupConfigs");

            migrationBuilder.DropColumn(
                name: "DontGroupRulesCaseInsensitive",
                table: "BackupConfigs");

            migrationBuilder.DropColumn(
                name: "IgnoreRulesCaseInsensitive",
                table: "BackupConfigs");
        }
    }
}
