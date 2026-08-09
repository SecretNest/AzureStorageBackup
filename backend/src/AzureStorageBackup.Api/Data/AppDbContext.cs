using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Data;

/// <summary>
/// The application data context (SQLite). Sensitive fields are ciphertext both in the database and on the entities; nothing is encrypted or decrypted here (design §3.1).
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
            // The property names carry a Protected suffix while the column names stay as they were (no schema change).
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
            e.Property(x => x.PasswordProtected).HasColumnName("Password"); // ciphertext in the database, column name unchanged
            // One container can carry only one backup. Two configs writing to the same place means two sets of version numbers and
            // indexes that know nothing of each other overwriting one another, each one's data blobs deleted as orphans by the other's
            // retention cleanup. The endpoint checks first and returns a 409 that explains itself; this index catches writes that bypass the endpoint and a second config that squeezes into that window concurrently.
            e.HasIndex(x => new { x.AccountId, x.ContainerName }).IsUnique();
        });

        modelBuilder.Entity<NotificationConfig>(e => e.HasKey(x => x.Id));
        modelBuilder.Entity<GlobalSettings>(e => e.HasKey(x => x.Id));

        modelBuilder.Entity<LogEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Source).IsRequired();
            // SQLite cannot translate range comparisons on DateTimeOffset: stored as UtcTicks (comparable and sortable).
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
