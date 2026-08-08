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
                // 既有行必须拿到 true：默认行为是"重启后自己接上"，而脚手架按 CLR 默认写的是 false，
                // 那会让所有已经装好的实例升级后默认是关的、新装的默认是开的——而这个差别只会在
                // 某天重启没接上时才被发现。先例同 AddOverlapDiffAndUpload。
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
