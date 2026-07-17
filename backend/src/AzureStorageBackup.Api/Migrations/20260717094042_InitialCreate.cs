using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    BlobEndpoint = table.Column<string>(type: "TEXT", nullable: false),
                    Region = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountKey = table.Column<string>(type: "TEXT", nullable: false),
                    UseProxy = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProxyMode = table.Column<int>(type: "INTEGER", nullable: false),
                    ProxyHost = table.Column<string>(type: "TEXT", nullable: true),
                    ProxyPort = table.Column<int>(type: "INTEGER", nullable: true),
                    ProxyUsername = table.Column<string>(type: "TEXT", nullable: true),
                    ProxyPassword = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BackupConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    LocalRoot = table.Column<string>(type: "TEXT", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: true),
                    IndexTier = table.Column<int>(type: "INTEGER", nullable: false),
                    DataTier = table.Column<int>(type: "INTEGER", nullable: false),
                    IgnoreRules = table.Column<string>(type: "TEXT", nullable: true),
                    DontCompressRules = table.Column<string>(type: "TEXT", nullable: true),
                    DontGroupRules = table.Column<string>(type: "TEXT", nullable: true),
                    IncludeSymlinks = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxVersions = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAgeDays = table.Column<int>(type: "INTEGER", nullable: false),
                    RetentionMode = table.Column<int>(type: "INTEGER", nullable: false),
                    SingleFileThresholdBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    GroupCapBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    VolumeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    VerboseLogging = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CachedVersionIndexes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    Container = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    IdentityTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Bytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedVersionIndexes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GlobalSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DefaultIndexTier = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultDataTier = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultMaxVersions = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultMaxAgeDays = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultRetentionMode = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultSingleFileThresholdBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DefaultGroupCapBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DefaultVolumeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    RepackDownloadHot = table.Column<bool>(type: "INTEGER", nullable: false),
                    RepackDownloadCool = table.Column<bool>(type: "INTEGER", nullable: false),
                    RepackDownloadCold = table.Column<bool>(type: "INTEGER", nullable: false),
                    RepackDownloadArchive = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultIncludeSymlinks = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultIgnoreRules = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultDontCompressRules = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultDontGroupRules = table.Column<string>(type: "TEXT", nullable: true),
                    UploadConcurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    DownloadConcurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    LogEphemeralMaxAgeDays = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultVerboseLogging = table.Column<bool>(type: "INTEGER", nullable: false),
                    RetryBackoffSeconds = table.Column<string>(type: "TEXT", nullable: false),
                    RetryMaxTotalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    DeadWeightThresholdPercent = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalBackupStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    Container = table.Column<string>(type: "TEXT", nullable: false),
                    InfoBytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ETag = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalBackupStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    Ephemeral = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    BodyTemplate = table.Column<string>(type: "TEXT", nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", nullable: true),
                    Events = table.Column<int>(type: "INTEGER", nullable: false),
                    ProxyUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TargetKind = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    TaskType = table.Column<int>(type: "INTEGER", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CheckCloudLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckLocalLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckRehydrateTier = table.Column<int>(type: "INTEGER", nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupMembers_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedVersionIndexes_AccountId_Container_Version",
                table: "CachedVersionIndexes",
                columns: new[] { "AccountId", "Container", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_GroupId",
                table: "GroupMembers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalBackupStates_AccountId_Container",
                table: "LocalBackupStates",
                columns: new[] { "AccountId", "Container" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogEntries_Timestamp",
                table: "LogEntries",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "BackupConfigs");

            migrationBuilder.DropTable(
                name: "CachedVersionIndexes");

            migrationBuilder.DropTable(
                name: "GlobalSettings");

            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "LocalBackupStates");

            migrationBuilder.DropTable(
                name: "LogEntries");

            migrationBuilder.DropTable(
                name: "NotificationConfigs");

            migrationBuilder.DropTable(
                name: "ScheduledTasks");

            migrationBuilder.DropTable(
                name: "Groups");
        }
    }
}
