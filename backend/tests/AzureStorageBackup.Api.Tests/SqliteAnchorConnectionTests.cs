using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The application database is opened without pooling, so every DbContext scope closes a real connection — and
/// SQLite treats the closing of the <em>last</em> open connection specially: it takes an exclusive lock on the file,
/// checkpoints the WAL into the database and deletes the -wal. Under that lock every newcomer, readers included,
/// waits — WAL's "readers never block" holds only while the WAL exists. On a disk that a compressor is saturating
/// (ZFS: the checkpoint's fsync waits for the whole transaction group) that close ran past the 30-second command
/// timeout on 2026-09-17, and a backup died reading <c>GlobalSettings</c> with <c>database is locked</c> while
/// nothing was writing at all.
/// <para>
/// The anchor is one connection the process keeps open for its lifetime, so no scope's close is ever the last
/// close: the exclusive-lock checkpoint never runs, and the WAL is kept in bounds by the ordinary passive
/// auto-checkpoint on the committing connection, which never locks anyone out.
/// </para>
/// </summary>
public sealed class SqliteAnchorConnectionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly string _connectionString;

    public SqliteAnchorConnectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-anchor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "app.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString();
        SqliteJournalMode.Enable(_connectionString);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteAndClose()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS t(id INTEGER PRIMARY KEY, b BLOB); INSERT INTO t(b) VALUES (zeroblob(65536));";
        cmd.ExecuteNonQuery();
    }

    /// <summary>The control: this is what SQLite does on its own, and what the anchor exists to prevent.</summary>
    [Fact]
    public void Without_an_anchor_the_last_connection_to_close_checkpoints_and_deletes_the_wal()
    {
        WriteAndClose();
        Assert.False(File.Exists(_dbPath + "-wal"));
    }

    [Fact]
    public void With_an_anchor_open_a_closing_writer_is_not_the_last_connection_and_leaves_the_wal_in_place()
    {
        using var anchor = SqliteAnchorConnection.Open(_connectionString);
        WriteAndClose();
        Assert.True(File.Exists(_dbPath + "-wal"));
    }

    /// <summary>
    /// The anchor must hold the file open without holding a read snapshot: a pinned snapshot would stop every
    /// checkpoint from completing and let the WAL grow for as long as the process lives. A TRUNCATE checkpoint
    /// from another connection completing with nothing left behind is the proof — and it is also the operation
    /// a future "quiesce for backup" step would run.
    /// </summary>
    [Fact]
    public void An_idle_anchor_does_not_pin_a_snapshot_so_a_checkpoint_can_drain_the_wal()
    {
        using var anchor = SqliteAnchorConnection.Open(_connectionString);
        WriteAndClose();

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0)); // busy = 0: the checkpoint ran to completion
        Assert.Equal(0, new FileInfo(_dbPath + "-wal").Length);
    }

    [Fact]
    public void Disposing_the_anchor_releases_the_file()
    {
        var anchor = SqliteAnchorConnection.Open(_connectionString);
        anchor.Dispose();
        // Now the next writer really is the last connection again, and SQLite's own close behaviour is back.
        WriteAndClose();
        Assert.False(File.Exists(_dbPath + "-wal"));
    }

    /// <summary>
    /// Pinned on the hosted application rather than on the class alone: the guarantee is that <em>production</em>
    /// holds an anchor for as long as the process runs, whatever else the registration does.
    /// </summary>
    [Fact]
    public async Task The_hosted_application_keeps_an_anchor_open_so_a_scope_closing_leaves_the_wal_in_place()
    {
        using var factory = new TestWebAppFactory();
        string dbPath;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dbPath = new SqliteConnectionStringBuilder(db.Database.GetDbConnection().ConnectionString).DataSource;
            db.LogEntries.Add(new Models.LogEntry { Timestamp = DateTimeOffset.UtcNow, Source = "test", Message = "anchor" });
            await db.SaveChangesAsync();
        }
        Assert.True(File.Exists(dbPath + "-wal"), "the scope's close was the last close: no anchor is held by the host");
    }
}
