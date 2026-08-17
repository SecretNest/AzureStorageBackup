using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The database runs under one long-lived writer (a backup writes run state, logs and stats continuously)
/// alongside several readers (the scheduler, the UI polling every few seconds, the log cleaner). Under
/// SQLite's default DELETE journal that combination produces <c>SQLITE_BUSY: database is locked</c>, and not
/// after a wait: when a connection holding a read lock tries to upgrade while another already holds the
/// write lock, SQLite returns immediately without consulting the busy handler, because waiting cannot
/// resolve it. WAL removes that class — readers and the writer proceed at the same time.
/// </summary>
public sealed class SqliteJournalModeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _connectionString;

    public SqliteJournalModeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-wal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _connectionString = "Data Source=" + Path.Combine(_dir, "app.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string ReadJournalMode()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return ((string?)cmd.ExecuteScalar() ?? "").ToLowerInvariant();
    }

    [Fact]
    public void Enable_Switches_The_Database_To_Wal()
    {
        Assert.Equal("wal", SqliteJournalMode.Enable(_connectionString));
    }

    /// <summary>
    /// The mode is written into the database header, so every later connection inherits it — which is why
    /// setting it once at startup is enough and no per-connection interceptor is needed.
    /// </summary>
    [Fact]
    public void The_Mode_Persists_For_Connections_Opened_Afterwards()
    {
        SqliteJournalMode.Enable(_connectionString);
        Assert.Equal("wal", ReadJournalMode());
    }

    /// <summary>Every process start calls this, so it has to be a no-op on a database already in WAL.</summary>
    [Fact]
    public void Enabling_Twice_Is_Harmless()
    {
        SqliteJournalMode.Enable(_connectionString);
        Assert.Equal("wal", SqliteJournalMode.Enable(_connectionString));
    }
}
