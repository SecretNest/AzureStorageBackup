using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// One connection to the application database that the process keeps open for as long as it runs, so that no
/// DbContext's close is ever the <em>last</em> close.
/// <para>
/// The database is opened without pooling (see <c>Program.cs</c>), so every DbContext scope closes a real SQLite
/// connection — and SQLite treats the last connection to close specially. It takes an <b>exclusive</b> lock on
/// the database file, checkpoints whatever is left in the WAL, fsyncs the database and deletes the -wal. Under
/// that lock every newcomer waits, readers included: WAL's promise that readers never block holds only while the
/// WAL is in use. On a disk a compressor is saturating that fsync can take as long as the disk's write queue (on
/// ZFS it waits for the whole transaction group), and on 2026-09-17 it ran past the 30-second command timeout:
/// a backup died mid-run reading <c>GlobalSettings</c> with <c>SQLite Error 5: 'database is locked'</c>, while
/// nothing was writing to the database at all. Which connection's close it was is unknowable — the run's own
/// per-file settings read, the UI's poll, the scheduler's minute tick — because with pooling off any of them is
/// the last one open a dozen times a minute.
/// </para>
/// <para>
/// With this connection held, the exclusive-lock checkpoint on close never runs. The WAL is kept in bounds by
/// the ordinary passive auto-checkpoint on the committing connection instead, which copies what it can and
/// locks nobody out. The cost is that <c>-wal</c> and <c>-shm</c> stay on disk for the life of the process; the
/// anchor holds no read snapshot (it runs one statement to take its shared lock and is never used again), so a
/// checkpoint from any other connection can still drain the WAL completely — pinned by
/// <c>SqliteAnchorConnectionTests</c>.
/// </para>
/// <para>
/// Disposed by the host at shutdown, at which point its own close is the last close and SQLite's orderly
/// checkpoint-and-delete happens once, when nobody is waiting behind it.
/// </para>
/// </summary>
public sealed class SqliteAnchorConnection : IDisposable
{
    private readonly SqliteConnection _connection;

    private SqliteAnchorConnection(SqliteConnection connection) => _connection = connection;

    public static SqliteAnchorConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        // Opening alone takes no lock: SQLite's pager acquires its shared lock on the first statement that reads a
        // page, and in WAL mode keeps that lock for the connection's lifetime. Without this read the connection
        // would be open in name only and every other close would still be the last close.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master;";
        command.ExecuteScalar();
        return new SqliteAnchorConnection(connection);
    }

    public void Dispose() => _connection.Dispose();
}
