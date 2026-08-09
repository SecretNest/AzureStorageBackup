using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOverlapDiffAndUpload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OverlapDiffAndUpload",
                table: "GlobalSettings",
                type: "INTEGER",
                nullable: false,
                // Existing rows must get true: the default behaviour is to overlap, while the scaffolding
                // writes the CLR default of false — which would **silently revert** every already-installed
                // instance to serial execution after an upgrade.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OverlapDiffAndUpload",
                table: "GlobalSettings");
        }
    }
}
