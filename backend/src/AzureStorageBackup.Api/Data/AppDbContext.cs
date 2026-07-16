using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AzureStorageBackup.Api.Data;

/// <summary>
/// 应用数据上下文（SQLite）。敏感字段通过 ValueConverter 在落库边界自动加解密。
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options, IEncryptionService encryption)
    : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<BackupConfig> BackupConfigs => Set<BackupConfig>();
    public DbSet<NotificationConfig> NotificationConfigs => Set<NotificationConfig>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 敏感字段落库加密：应用层读写明文，Provider 边界自动加解密。
        var encrypt = new ValueConverter<string, string>(
            v => encryption.Encrypt(v),
            v => encryption.Decrypt(v));
        var encryptNullable = new ValueConverter<string?, string?>(
            v => v == null ? null : encryption.Encrypt(v),
            v => v == null ? null : encryption.Decrypt(v));

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.BlobEndpoint).IsRequired();
            entity.Property(e => e.AccountKey).IsRequired().HasConversion(encrypt);
            entity.Property(e => e.ProxyPassword).HasConversion(encryptNullable);
        });

        modelBuilder.Entity<Group>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.HasMany(x => x.Members).WithOne().HasForeignKey(m => m.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GroupMember>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ContainerName).IsRequired();
        });

        modelBuilder.Entity<ScheduledTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.CronExpression).IsRequired();
        });

        modelBuilder.Entity<BackupConfig>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(200);
            e.Property(x => x.ContainerName).IsRequired();
            e.Property(x => x.LocalRoot).IsRequired();
            e.Property(x => x.Password).HasConversion(encryptNullable); // 加密落库
        });

        modelBuilder.Entity<NotificationConfig>(e => e.HasKey(x => x.Id));
    }
}
