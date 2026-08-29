using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSuspendedRepair : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SuspendedRepairs",
                columns: table => new
                {
                    BackupConfigId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PathsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Cloud = table.Column<int>(type: "INTEGER", nullable: false),
                    RehydrateTier = table.Column<int>(type: "INTEGER", nullable: true),
                    CleanupOrphans = table.Column<bool>(type: "INTEGER", nullable: false),
                    SuspendedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuspendedRepairs", x => x.BackupConfigId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SuspendedRepairs");
        }
    }
}
