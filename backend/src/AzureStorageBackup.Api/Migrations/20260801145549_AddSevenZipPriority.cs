using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSevenZipPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SevenZipPriority",
                table: "GlobalSettings",
                type: "INTEGER",
                nullable: false,
                // 0 == SevenZipCpuPriority.Lowest, which is also the default for a new database — the enum
                // was ordered precisely for this, so unlike AddOverlapDiffAndUpload there is no need to
                // supply a defaultValue to correct the scaffolding.
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SevenZipPriority",
                table: "GlobalSettings");
        }
    }
}
