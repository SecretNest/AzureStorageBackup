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
                // 既有行必须拿到 true：默认行为是重叠跑，而脚手架按 CLR 默认写的是 false，
                // 那会让所有已经装好的实例在升级后**静默退回**串行执行。
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
