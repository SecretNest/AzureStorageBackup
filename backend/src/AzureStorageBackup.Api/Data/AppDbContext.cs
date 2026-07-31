using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Data;

/// <summary>
/// 应用数据上下文（SQLite）。敏感字段在库与实体中均为密文，不在此处加解密（设计 §3.1）。
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<BackupConfig> BackupConfigs => Set<BackupConfig>();
    public DbSet<NotificationConfig> NotificationConfigs => Set<NotificationConfig>();
    public DbSet<LogEntry> LogEntries => Set<LogEntry>();
    public DbSet<GlobalSettings> GlobalSettings => Set<GlobalSettings>();
    public DbSet<CachedVersionIndex> CachedVersionIndexes => Set<CachedVersionIndex>();
    public DbSet<LocalBackupState> LocalBackupStates => Set<LocalBackupState>();
    public DbSet<KeyringCanary> KeyringCanaries => Set<KeyringCanary>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.BlobEndpoint).IsRequired();
            // 属性名带 Protected 后缀，列名保持原样（无 schema 变更）。
            entity.Property(e => e.AccountKeyProtected).IsRequired().HasColumnName("AccountKey");
            entity.Property(e => e.ProxyPasswordProtected).HasColumnName("ProxyPassword");
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
            e.Property(x => x.PasswordProtected).HasColumnName("Password"); // 密文落库，列名不变
            // 一个 container 只能挂一条备份。两条配置写同一个地方，就是两套互不知情的版本号
            // 与索引互相覆盖，各自的数据 blob 在对方的保留清理里被当成孤儿删掉。端点会先查一次
            // 并给出说得清的 409；这条索引兜住绕过端点的写入与并发挤进那个窗口的第二条。
            e.HasIndex(x => new { x.AccountId, x.ContainerName }).IsUnique();
        });

        modelBuilder.Entity<NotificationConfig>(e => e.HasKey(x => x.Id));
        modelBuilder.Entity<GlobalSettings>(e => e.HasKey(x => x.Id));

        modelBuilder.Entity<LogEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Source).IsRequired();
            // DateTimeOffset 在 SQLite 上无法翻译范围比较：落库为 UtcTicks（可比较、可排序）。
            e.Property(x => x.Timestamp).HasConversion(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));
            e.HasIndex(x => x.Timestamp);
        });

        modelBuilder.Entity<CachedVersionIndex>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Container).IsRequired();
            e.HasIndex(x => new { x.AccountId, x.Container, x.Version }).IsUnique();
        });

        modelBuilder.Entity<LocalBackupState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Container).IsRequired();
            e.HasIndex(x => new { x.AccountId, x.Container }).IsUnique();
        });

        modelBuilder.Entity<KeyringCanary>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Ciphertext).IsRequired();
        });
    }
}
