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
            // 记录生成的 SQL，供 Probe_Query_For_Account_Is_Ordered_By_Id_In_Sql 断言用。
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
        await new KeyringProbe(_db, NewKeyring()).EvaluateAsync();
        _db.ChangeTracker.Clear();

        // 新密钥环 = /keys 丢失后重新生成
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
        // Id 2 用另一套密钥环写入；判定必须只看 Id 最小的那条（Id 1），故为 Healthy
        _db.Accounts.Add(AddAccount(good, 1, "first"));
        _db.Accounts.Add(AddAccount(NewKeyring(), 2, "second"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        // 只关心探测查询本身产生的 SQL，清掉建表/插入产生的噪音。
        _sqlLog.Clear();

        Assert.Equal(KeyringStatus.Healthy, await new KeyringProbe(_db, good).EvaluateAsync());

        // SQLite 的 INTEGER PRIMARY KEY 就是 rowid，无 ORDER BY 的全表扫描也会按 rowid(=Id)
        // 升序返回结果——仅靠上面的 Healthy 断言无法区分"探测是否显式排序"（设计 §决策4
        // 要求确定性）：即使把 KeyringProbe.cs 里的 OrderBy 全删掉，这条断言也会照样通过。
        // 因此直接断言生成的 SQL 带 ORDER BY：删掉对应的 OrderBy 会让这条断言失败，
        // 而不会被 SQLite 的物理存储顺序悄悄掩盖。
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
}
