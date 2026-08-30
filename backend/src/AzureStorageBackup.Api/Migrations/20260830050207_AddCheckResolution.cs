using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Resolution",
                table: "LastCheckRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UnrepairedCount",
                table: "LastCheckRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Resolution",
                table: "LastCheckRuns");

            migrationBuilder.DropColumn(
                name: "UnrepairedCount",
                table: "LastCheckRuns");
        }
    }
}
