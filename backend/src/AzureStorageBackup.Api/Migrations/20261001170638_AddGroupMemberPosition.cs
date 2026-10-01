using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupMemberPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "GroupMembers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Existing members are numbered in the order they ran in until now — (AccountId, ContainerName), the
            // order GroupService sorted them into on the way in — so an upgrade changes no group's sequence; it
            // only makes that sequence visible and editable. Without this every member would sit at 0 and the
            // run order would fall to the row order, which is insertion order and never was the run order.
            migrationBuilder.Sql("""
                UPDATE GroupMembers SET Position = (
                    SELECT COUNT(*) FROM GroupMembers AS earlier
                    WHERE earlier.GroupId = GroupMembers.GroupId
                      AND (earlier.AccountId < GroupMembers.AccountId
                           OR (earlier.AccountId = GroupMembers.AccountId
                               AND earlier.ContainerName < GroupMembers.ContainerName)))
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Position",
                table: "GroupMembers");
        }
    }
}
