using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUploadMemoryLimit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "UploadMemoryLimitBytes",
                table: "GlobalSettings",
                type: "INTEGER",
                nullable: false,
                // Pre-existing rows get the model default, not 0: unlike the other migrated columns, 0 is a real
                // value here ("never hold a volume in memory"), so GetAsync must not normalize it away — and this
                // is what keeps an upgraded server on the same 1 GB a fresh one starts with.
                defaultValue: 1024L * 1024 * 1024);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UploadMemoryLimitBytes",
                table: "GlobalSettings");
        }
    }
}
