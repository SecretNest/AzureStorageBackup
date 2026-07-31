using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureStorageBackup.Api.Migrations
{
    /// <inheritdoc />
    public partial class EnforceOneBackupPerContainer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 这条唯一索引要落到的库里可能已经躺着重复——那正是它要堵的 bug 的产物（从前
            // 创建/导入都不查重）。直接建索引会失败，而迁移失败就是应用起不来。
            //
            // 所以先把重复挪开，而且**一条都不许删**：那些配置指着真实的云端数据，删掉本地记录
            // 不会让云端数据消失，只会让用户再也看不见它。挪法是把 ContainerName 改成一个 Azure
            // 根本不接受的名字（带点），于是它既不可能再碰任何真实 container，又原样留在界面上，
            // 连同一条说明为什么的 LastError，由用户自己决定怎么处置。
            //
            // 赢家取每组 Id 最小的那条：先建的那条才是这个 container 真正的主人。SQLite 里 SET
            // 各表达式读的都是本行的旧值，所以下面的子查询按旧 ContainerName 仍能匹配到整组。
            migrationBuilder.Sql(
                """
                UPDATE BackupConfigs
                SET ContainerName = ContainerName || '.duplicate.' || Id,
                    Status = 1,
                    LastError = 'Set aside during upgrade: container ''' || ContainerName ||
                        ''' is already held by the backup "' ||
                        (SELECT w.Name FROM BackupConfigs w
                         WHERE w.AccountId = BackupConfigs.AccountId
                           AND w.ContainerName = BackupConfigs.ContainerName
                         ORDER BY w.Id LIMIT 1) ||
                        '". Two backups on one container overwrite each other''s version history, so this ' ||
                        'one was pointed at a name Azure will not accept and can no longer run. Nothing in ' ||
                        'the cloud was touched. Restore anything you still need through the other backup, ' ||
                        'then delete this entry.'
                WHERE Id NOT IN (SELECT MIN(Id) FROM BackupConfigs GROUP BY AccountId, ContainerName);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_BackupConfigs_AccountId_ContainerName",
                table: "BackupConfigs",
                columns: new[] { "AccountId", "ContainerName" },
                unique: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 只撤索引，不还原被挪开的名字：还原就等于把重复重新放回去，而降级之后紧接着的那次
        /// 升级又会再挪一遍（这次赢家还是同一条，被挪的却会带上第二层后缀）。挪开的配置在界面上
        /// 看得见、删得掉，交给用户处置比让迁移来回搬更安全。
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackupConfigs_AccountId_ContainerName",
                table: "BackupConfigs");
        }
    }
}
