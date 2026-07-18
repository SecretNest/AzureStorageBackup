using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessingMaxAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProcessingMaxAttempts",
                table: "GlobalSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProcessingMaxAttempts",
                table: "GlobalSettings");
        }
    }
}
