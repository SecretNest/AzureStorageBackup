using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AzureStorageBackup.Api.Data;

/// <summary>
/// 设计时（`dotnet ef migrations`）构造 <see cref="AppDbContext"/>：仅用于生成迁移的 schema，不跑 Web 主机。
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=ef-design.db")
            .Options;
        return new AppDbContext(options);
    }
}
