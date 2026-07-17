using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AzureStorageBackup.Api.Data;

/// <summary>
/// 设计时（`dotnet ef migrations`）构造 <see cref="AppDbContext"/>：仅用于生成迁移的 schema，
/// 不跑 Web 主机。加密 ValueConverter 是 string↔string、不影响列类型，故用临时数据保护 provider 即可。
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=ef-design.db")
            .Options;
        return new AppDbContext(options, new EncryptionService(new EphemeralDataProtectionProvider()));
    }
}
