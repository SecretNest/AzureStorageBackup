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
                // 0 == SevenZipCpuPriority.Lowest，也正是新库的默认值——枚举就是照着这一点排的，
                // 所以这里不必像 AddOverlapDiffAndUpload 那样另给 defaultValue 去纠正脚手架。
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
