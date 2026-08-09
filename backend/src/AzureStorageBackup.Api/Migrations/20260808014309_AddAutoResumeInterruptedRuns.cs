using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoResumeInterruptedRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoResumeInterruptedRuns",
                table: "GlobalSettings",
                type: "INTEGER",
                nullable: false,
                // Existing rows must get true: the default behaviour is "continue by itself after a
                // restart", while the scaffolding writes the CLR default of false. That would leave every
                // already-installed instance off after upgrading and every new install on — a difference
                // only discovered the day a restart fails to continue. Same precedent as
                // AddOverlapDiffAndUpload.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoResumeInterruptedRuns",
                table: "GlobalSettings");
        }
    }
}
