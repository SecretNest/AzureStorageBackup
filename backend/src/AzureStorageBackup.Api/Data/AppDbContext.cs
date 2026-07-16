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
    public DbSet<BackupJob> BackupJobs => Set<BackupJob>();
    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BackupJob>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.SourcePath).IsRequired();
            entity.Property(e => e.ContainerName).IsRequired().HasMaxLength(63);
        });

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
    }
}
