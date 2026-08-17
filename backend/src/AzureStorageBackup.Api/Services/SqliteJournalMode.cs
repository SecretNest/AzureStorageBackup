using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Puts the application database into write-ahead logging at startup.
/// <para>
/// SQLite's default DELETE journal gives the whole database to one writer at a time, readers included. This
/// application runs the opposite shape: a backup writes continuously for hours (run state, per-file logs,
/// stats) while the scheduler, the UI's few-second poll and the log cleaner read the same file. Under DELETE
/// that produces <c>SQLITE_BUSY: database is locked</c> — and not after patiently waiting: when a connection
/// holding a read lock tries to upgrade while another already holds the write lock, SQLite returns busy
/// **immediately**, without consulting the busy handler, because waiting cannot resolve that particular
/// standoff. No timeout setting can help there; the journal mode is the fix.
/// </para>
/// <para>
/// Nor is a timeout the fix for the ordinary case. A backup's write has to get past the UI's few-second
/// poll, and under DELETE a reader shuts the writer out for as long as it reads; retrying just walks the
/// clock down until it gives up, which is the failure the operator sees. Measured, a fresh connection
/// reports <c>PRAGMA busy_timeout = 0</c> — Microsoft.Data.Sqlite does not arm SQLite's own busy handler and
/// retries at the ADO layer within the connection's default timeout instead. That retry is left as it is:
/// under WAL a reader no longer blocks the writer at all, so the contention it was papering over is gone.
/// </para>
/// <para>
/// The mode is recorded in the database header, so one call per process start is enough and connections
/// opened later inherit it — no per-connection interceptor, no change to how the DbContext is registered.
/// </para>
/// <para>
/// WAL needs shared memory and therefore a local filesystem. Pointed at a network share the PRAGMA quietly
/// leaves the old mode in place instead of failing, which is why this returns the mode that actually took
/// effect rather than assuming success — the caller logs it, so "why is it still locking up" has an answer
/// on line one of the log instead of being invisible.
/// </para>
/// </summary>
public static class SqliteJournalMode
{
    /// <returns>The journal mode now in effect, lowercased (<c>wal</c> on success).</returns>
    public static string Enable(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        return ((string?)command.ExecuteScalar() ?? "unknown").ToLowerInvariant();
    }
}
