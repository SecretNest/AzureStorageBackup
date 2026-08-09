using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureStorageBackup.Api.Tests;

public class KeyringProbeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly List<string> _sqlLog = [];

    public KeyringProbeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            // Capture the generated SQL for Probe_Query_For_Account_Is_Ordered_By_Id_In_Sql to assert on.
            .LogTo(_sqlLog.Add, LogLevel.Information)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IEncryptionService NewKeyring() =>
        new EncryptionService(new EphemeralDataProtectionProvider());

    private Account AddAccount(IEncryptionService enc, int id, string name) =>
        new() { Id = id, Name = name, BlobEndpoint = "https://x.blob.core.windows.net", AccountKeyProtected = enc.Encrypt("k") };

    [Fact]
    public async Task Fresh_Database_Is_Healthy_And_Writes_Canary()
    {
        var sut = new KeyringProbe(_db, NewKeyring());

        Assert.Equal(KeyringStatus.Healthy, await sut.EvaluateAsync());
        Assert.Equal(1, await _db.KeyringCanaries.CountAsync());
    }

    [Fact]
    public async Task Existing_Canary_That_Decrypts_Is_Healthy()
    {
        var enc = NewKeyring();
        await new KeyringProbe(_db, enc).EvaluateAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(KeyringStatus.Healthy, await new KeyringProbe(_db, enc).EvaluateAsync());
    }

    [Fact]
    public async Task Existing_Canary_That_Fails_To_Decrypt_Is_Lost()
    {
        var old = NewKeyring();
        await new KeyringProbe(_db, old).EvaluateAsync();
        // The database must still hold undecryptable ciphertext — that is the real loss scenario, the one where something actually needs re-entering.
        _db.Accounts.Add(AddAccount(old, 1, "prod"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // A new keyring = /keys was lost and regenerated
        Assert.Equal(KeyringStatus.Lost, await new KeyringProbe(_db, NewKeyring()).EvaluateAsync());
    }

    /// <summary>
    /// All-branch review Finding 3: the canary is stale, but the database no longer holds any undecryptable ciphertext
    /// (the user gave up on recovery and deleted every account and every encrypted backup config). EvaluateAsync used
    /// to rewrite the canary only when it was **missing**, so a stale canary pinned the process in Lost forever:
    /// /api/health/ready stuck at 503, the scheduler skipping everything, every action 409, while the banner said only
    /// "0 credentials need to be re-entered" — with no way out.
    /// It must rebuild the canary and go back to Healthy.
    /// </summary>
    [Fact]
    public async Task Stale_Canary_With_No_Remaining_Secrets_Rebuilds_Canary_And_Is_Healthy()
    {
        await new KeyringProbe(_db, NewKeyring()).EvaluateAsync();
        _db.ChangeTracker.Clear();
        var stale = await _db.KeyringCanaries.AsNoTracking().SingleAsync();

        var current = NewKeyring(); // the keyring regenerated after /keys was lost
        Assert.Equal(KeyringStatus.Healthy, await new KeyringProbe(_db, current).EvaluateAsync());

        var rebuilt = await _db.KeyringCanaries.AsNoTracking().SingleAsync();
        Assert.NotEqual(stale.Ciphertext, rebuilt.Ciphertext);
        Assert.True(current.TryDecrypt(rebuilt.Ciphertext, out var plain));
        Assert.Equal(KeyringProbe.CanaryPlaintext, plain);
    }

    /// <summary>Stale canary + a backup password that still will not decrypt (all accounts deleted): must still be Lost, otherwise
    /// the pass-through branch above would wave through a real loss where something genuinely needs re-entering.</summary>
    [Fact]
    public async Task Stale_Canary_With_A_Remaining_Backup_Password_Is_Still_Lost()
    {
        await new KeyringProbe(_db, NewKeyring()).EvaluateAsync();
        _db.BackupConfigs.Add(new BackupConfig
        {
            Id = 1, Name = "docs", ContainerName = "c", LocalRoot = "/data",
            PasswordProtected = NewKeyring().Encrypt("pw"),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(KeyringStatus.Lost, await new KeyringProbe(_db, NewKeyring()).EvaluateAsync());
    }

    [Fact]
    public async Task Legacy_Database_With_Readable_Secret_Is_Healthy_And_Backfills_Canary()
    {
        var enc = NewKeyring();
        _db.Accounts.Add(AddAccount(enc, 1, "prod"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(KeyringStatus.Healthy, await new KeyringProbe(_db, enc).EvaluateAsync());
        Assert.Equal(1, await _db.KeyringCanaries.CountAsync());
    }

    [Fact]
    public async Task Legacy_Database_With_Unreadable_Secret_Is_Lost_And_Writes_No_Canary()
    {
        _db.Accounts.Add(AddAccount(NewKeyring(), 1, "prod"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(KeyringStatus.Lost, await new KeyringProbe(_db, NewKeyring()).EvaluateAsync());
        Assert.Equal(0, await _db.KeyringCanaries.CountAsync());
    }

    [Fact]
    public async Task Probe_Query_For_Account_Is_Ordered_By_Id_In_Sql()
    {
        var good = NewKeyring();
        // Id 2 is written with a different keyring; the verdict must look only at the lowest Id (Id 1), hence Healthy
        _db.Accounts.Add(AddAccount(good, 1, "first"));
        _db.Accounts.Add(AddAccount(NewKeyring(), 2, "second"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        // Only the SQL from the probe query itself matters; clear the noise from table creation and inserts.
        _sqlLog.Clear();

        Assert.Equal(KeyringStatus.Healthy, await new KeyringProbe(_db, good).EvaluateAsync());

        // SQLite's INTEGER PRIMARY KEY is the rowid, so even a full scan without ORDER BY returns rows in ascending
        // rowid(=Id) order — the Healthy assertion above alone cannot tell whether "the probe sorts explicitly"
        // (design §decision 4 demands determinism): strip every OrderBy out of KeyringProbe.cs and it still passes.
        // So assert directly that the generated SQL carries ORDER BY: removing the matching OrderBy then fails this
        // assertion instead of being quietly masked by SQLite's physical storage order.
        var accountsQuery = Assert.Single(_sqlLog, s => s.Contains("FROM \"Accounts\""));
        Assert.Contains("ORDER BY", accountsQuery);
    }

    [Fact]
    public async Task Falls_Back_To_Backup_Config_When_No_Account_Exists()
    {
        _db.BackupConfigs.Add(new BackupConfig
        {
            Id = 1, Name = "docs", ContainerName = "c", LocalRoot = "/data",
            PasswordProtected = NewKeyring().Encrypt("pw"),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(KeyringStatus.Lost, await new KeyringProbe(_db, NewKeyring()).EvaluateAsync());
    }

    /// <summary>
    /// The other side of the fallback branch: an old database, no accounts, a single encrypted backup config, and its ciphertext **does** decrypt.
    /// Covering only the Lost side would let a fallback implementation that always returns Lost pass too — and that would
    /// lock up at first boot after the upgrade every old database that used backup configs without ever creating an account. Must be Healthy, and must backfill the canary.
    /// </summary>
    [Fact]
    public async Task Falls_Back_To_Backup_Config_And_Is_Healthy_When_It_Decrypts()
    {
        var enc = NewKeyring();
        _db.BackupConfigs.Add(new BackupConfig
        {
            Id = 1, Name = "docs", ContainerName = "c", LocalRoot = "/data",
            PasswordProtected = enc.Encrypt("pw"),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        Assert.Equal(KeyringStatus.Healthy, await new KeyringProbe(_db, enc).EvaluateAsync());
        Assert.Equal(1, await _db.KeyringCanaries.CountAsync());
    }

    /// <summary>
    /// Same reasoning as <see cref="Probe_Query_For_Account_Is_Ordered_By_Id_In_Sql"/> (the determinism of design §decision 4),
    /// covering the other two probe queries. SQLite's INTEGER PRIMARY KEY is the rowid, so even without ORDER BY the rows happen to come back in ascending Id order,
    /// which leaves asserting on the generated SQL as the only option — otherwise removing an OrderBy would trip no assertion at all.
    /// </summary>
    [Fact]
    public async Task Canary_Query_Is_Ordered_By_Id_In_Sql()
    {
        var good = NewKeyring();
        // Id 2 is a canary from a different keyring; the verdict must look only at the lowest Id (Id 1), hence Healthy.
        _db.KeyringCanaries.Add(new KeyringCanary
        { Id = 1, Ciphertext = good.Encrypt(KeyringProbe.CanaryPlaintext), CreatedAt = DateTimeOffset.UtcNow });
        _db.KeyringCanaries.Add(new KeyringCanary
        { Id = 2, Ciphertext = NewKeyring().Encrypt(KeyringProbe.CanaryPlaintext), CreatedAt = DateTimeOffset.UtcNow });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _sqlLog.Clear();

        // The canary decrypts → return Healthy immediately, never reaching WriteCanaryAsync (which would emit one more unordered query).
        Assert.Equal(KeyringStatus.Healthy, await new KeyringProbe(_db, good).EvaluateAsync());

        var canaryQuery = Assert.Single(_sqlLog, s => s.Contains("FROM \"KeyringCanaries\""));
        Assert.Contains("ORDER BY", canaryQuery);
    }

    [Fact]
    public async Task Backup_Config_Fallback_Query_Is_Ordered_By_Id_In_Sql()
    {
        // No accounts → take the backup-config fallback. Id 1 uses a different keyring, Id 2 the current one:
        // the result is Lost only if the lowest Id really is the one picked.
        var current = NewKeyring();
        _db.BackupConfigs.Add(new BackupConfig
        {
            Id = 1, Name = "docs", ContainerName = "c1", LocalRoot = "/data",
            PasswordProtected = NewKeyring().Encrypt("pw"),
        });
        _db.BackupConfigs.Add(new BackupConfig
        {
            Id = 2, Name = "pics", ContainerName = "c2", LocalRoot = "/data",
            PasswordProtected = current.Encrypt("pw"),
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _sqlLog.Clear();

        Assert.Equal(KeyringStatus.Lost, await new KeyringProbe(_db, current).EvaluateAsync());

        var configQuery = Assert.Single(_sqlLog, s => s.Contains("FROM \"BackupConfigs\""));
        Assert.Contains("ORDER BY", configQuery);
    }
}
