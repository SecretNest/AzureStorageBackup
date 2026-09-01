using System.Diagnostics;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The regression test for the bug this whole change exists for.
///
/// <para>
/// SQLite allows exactly one writer at a time — WAL included; WAL only stops readers and the writer from shutting each
/// other out. The version index used to be stored as one row holding the whole serialized index, on the order of 100 MB
/// for a backup of half a million files, and writing it took the database's single write lock for as long as the write
/// took. On a NAS with the disk already saturated by the backup itself that was tens of seconds, during which
/// <em>every</em> other writer blocked: the scheduler's log trim (observed failing with
/// <c>SQLite Error 5: 'database is locked'</c> after 30,022 ms), and — the reason a user reported it — a config edit,
/// whose Save button simply sat there greyed out.
/// </para>
/// <para>
/// So the property worth pinning is not "the write is faster". It is that committing a version index does not need the
/// database write lock <b>at all</b>. This test states it directly: hold the write lock from another connection for
/// longer than any command timeout would tolerate, and assert that a version index still commits, promptly.
/// </para>
/// </summary>
public sealed class IndexCacheLockContentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "asb-idxlock-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;
    private readonly SqliteConnection _appConnection;
    private readonly AppDbContext _db;

    public IndexCacheLockContentionTests()
    {
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "app.db");

        // A real file, not DataSource=:memory: — an in-memory database lives on one connection, so a second connection
        // could not contend for its lock at all and the test would pass for the wrong reason.
        _appConnection = new SqliteConnection($"DataSource={_dbPath}");
        _appConnection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_appConnection).Options);
        _db.Database.EnsureCreated();

        // Production runs in WAL (Program.cs), so the test has to as well: under the default journal the block would
        // come from readers versus the writer, which is a different failure and not the one being pinned here.
        using var pragma = _appConnection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteScalar();
    }

    public void Dispose()
    {
        _db.Dispose();
        _appConnection.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a test temp dir; best effort */ }
    }

    private sealed class UnusedStore : IBackupInfoStore
    {
        public Task<VersionIndex> ReadIndexAsync(Account a, string c, string b, string? p, int v = 1, CancellationToken ct = default)
            => throw new InvalidOperationException("This test never reads through to the cloud.");
        public Task<BackupInfoFile?> ReadInfoAsync(Account a, string c, string? p, CancellationToken ct = default) => Task.FromResult<BackupInfoFile?>(null);
        public Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account a, string c, string? p, CancellationToken ct = default) => Task.FromResult<(BackupInfoFile, string)?>(null);
        public Task WriteInfoAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> WriteInfoConditionalAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t, string? e, CancellationToken ct = default) => Task.FromResult("etag");
        public Task<(string Name, int Volumes)> WriteIndexAsync(Account a, string c, int v, VersionIndex i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.FromResult(("indexes/v.bin", 1));
    }

    /// <summary>An index with enough entries to be worth writing, but small enough to keep the test quick.</summary>
    private static VersionIndex Index(int version) => new()
    {
        Version = version,
        Entries = [.. Enumerable.Range(0, 2_000).Select(i => new IndexEntry
        {
            Path = $"/data/photos/{i}.jpg", Kind = "file", Length = i, Mtime = DateTimeOffset.UnixEpoch,
            Permissions = "0644", FullHash = "xxh128:" + i.ToString("x32"),
            Storage = new StorageRef { Kind = "blob", Ref = $"data/{i}" },
        })],
    };

    [Fact]
    public async Task Version_index_commits_while_another_writer_holds_the_database_lock()
    {
        // A second connection takes the write lock and keeps it. BEGIN IMMEDIATE acquires it up front rather than on
        // first write, which is exactly the standing-writer situation a running backup used to create.
        await using var blocker = new SqliteConnection($"DataSource={_dbPath}");
        await blocker.OpenAsync();
        await using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }

        var cache = new LocalIndexCache(_db, new UnusedStore(), TestIndexFiles.New());

        var clock = Stopwatch.StartNew();
        await cache.PutAsync(1, "photos", version: 3, identityTicks: 100, Index(3), default);
        clock.Stop();

        // Before this change the call blocked on the write lock until the command timeout (30 s) and then threw
        // SqliteException 5. Anything near that means the index write is back on the database's critical path.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5),
            $"Committing a version index waited {clock.Elapsed.TotalSeconds:F1}s on another writer's lock; "
            + "it must not need the database write lock at all.");

        // And it is genuinely readable afterwards — a write that quietly went nowhere would also be fast.
        await using var rollback = blocker.CreateCommand();
        rollback.CommandText = "ROLLBACK;";
        await rollback.ExecuteNonQueryAsync();

        var read = await cache.ReadAsync(
            new Account { Id = 1, Name = "a", BlobEndpoint = "http://x", AccountKeyProtected = TestSecrets.Protect("k") },
            "photos", 3, 100, "indexes/v3.bin", null, 1, default);
        Assert.Equal(2_000, read.Entries.Count);
    }
}
