using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class DropStagingFairShare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StagingFairShare",
                table: "GlobalSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StagingFairShare",
                table: "GlobalSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
