using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AzureStorageBackup.Api.Data;

/// <summary>
/// Design-time construction of <see cref="AppDbContext"/> (for `dotnet ef migrations`): used only to produce the migration schema, never to run the web host.
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
