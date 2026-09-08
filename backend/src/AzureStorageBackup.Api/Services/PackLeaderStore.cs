using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// "Who saw this content first" for one backup run, kept in a SQLite file instead of a dictionary.
/// <para>
/// This is the half of <see cref="PackAliasTable"/> that is <b>not</b> bounded by duplicates: every packed member
/// asks the question, so a first backup of 200 000 small files puts 200 000 rows in here even when not one of them
/// is a duplicate. Task 23's benchmark measured what that cost on the managed heap — ~56 MB of live data per
/// 100 000 files, surviving a forced collection — which is why the map moved out of memory and the run's file
/// count now costs page cache instead.
/// </para>
/// <para>
/// A file of its own, <c>{runId}.aliases.db</c> beside the run's <c>work.db</c>, rather than a table inside it: the
/// claim path holds one long write transaction that only commits every <see cref="CommitEvery"/> claims, and a
/// writer that parks on a table for thousands of rows at a time has no business sharing a database with the work
/// db's single writer task, whose whole design is that nothing else queues behind it.
/// </para>
/// <para>
/// Exclusively owned by the single-threaded diff, exactly as the dictionary it replaces was — one connection, no
/// locking, and no second thread may call <see cref="ClaimAsync"/>.
/// </para>
/// </summary>
public sealed class PackLeaderStore : IAsyncDisposable
{
    /// <summary>How many claims share one transaction. A commit per claim would fsync-free but still rewrite the
    /// database header once per small file; committing in batches makes the whole run a few hundred commits. The
    /// batch is invisible to correctness because every read goes through the <b>same connection</b>, which sees its
    /// own uncommitted rows — a duplicate arriving inside the batch window still finds its leader.</summary>
    private const int CommitEvery = 2_000;

    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _insert;
    private readonly SqliteCommand _select;
    private SqliteTransaction? _transaction;
    private int _sinceCommit;
    private bool _disposed;

    private PackLeaderStore(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;

        // Two prepared statements reused for the whole run: this is called once per packed small file, so a command
        // built per call would be one parse of the same SQL per file.
        _insert = connection.CreateCommand();
        _insert.CommandText = "INSERT OR IGNORE INTO pack_leaders (content_key, path) VALUES (@key, @path);";
        _insert.Parameters.AddWithValue("@key", "");
        _insert.Parameters.AddWithValue("@path", "");

        _select = connection.CreateCommand();
        _select.CommandText = "SELECT path FROM pack_leaders WHERE content_key = @key;";
        _select.Parameters.AddWithValue("@key", "");
    }

    /// <summary>The scratch file's path. Deleted by <see cref="DisposeAsync"/>.</summary>
    public string Path { get; }

    /// <summary>
    /// Creates the file and its schema. Anything already at that name is deleted first rather than reused, for the
    /// same reason <see cref="RunWorkDbFactory.CreateAsync"/> does it: a leftover from a run that shared this id
    /// would seed the new run with somebody else's leaders, and an alias pointing at a path this run never packed
    /// is an index entry pointing at content that is not there.
    /// </summary>
    public static async Task<PackLeaderStore> CreateAsync(string path, CancellationToken ct)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        RunWorkDb.Delete(path);

        // CatalogSql's builder rather than a local one, for its Pooling=false: a pooled connection collected without
        // being disposed hands its live handle to the next Open of the same connection string, and the symptom is a
        // "SQLite Error 0: 'not an error'" from an unrelated statement (see the application database's own history).
        var connection = new SqliteConnection(CatalogSql.ConnectionString(path, readOnly: false));
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            // journal_mode=OFF and synchronous=OFF because this file is scratch twice over: it is deleted at the end
            // of the run, and a crash mid-run ends the run, so there is nothing for a journal or an fsync to protect
            // — and with no rollback journal a commit is a write to the page cache and nothing else. 16 MiB of page
            // cache (negative = KiB) keeps the hot part of the index resident; the table is small (a ~124-char key
            // and a path per row) and the access pattern is one probe per file, so this is about right and it is a
            // sixteenth of what work.db asks for.
            // WITHOUT ROWID: the primary key *is* the whole row's identity, so the extra rowid index would double the
            // write cost for nothing.
            command.CommandText = """
                PRAGMA journal_mode=OFF;
                PRAGMA synchronous=OFF;
                PRAGMA cache_size=-16384;
                CREATE TABLE IF NOT EXISTS pack_leaders (
                  content_key TEXT PRIMARY KEY,
                  path TEXT NOT NULL) WITHOUT ROWID;
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return new PackLeaderStore(connection, path);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Registers <paramref name="path"/> as the leader of <paramref name="contentKey"/> and returns <c>null</c>, or
    /// — if someone got there first — leaves the table alone and returns that leader's path.
    /// <para>
    /// The <c>INSERT OR IGNORE</c>'s own row count answers the question, so only a genuine duplicate pays for the
    /// <c>SELECT</c>: on a first backup, where nearly every file is its own leader, the common path is one insert.
    /// </para>
    /// <para>
    /// Synchronous underneath, and deliberately so: Microsoft.Data.Sqlite's async methods run synchronously anyway
    /// (SQLite has no async I/O), so a <see cref="Task"/> here would buy a state machine and no concurrency. The
    /// signature is a <see cref="ValueTask{TResult}"/> because the caller is on the diff's async path and because
    /// this is where a future off-thread implementation would go, not because the work suspends today.
    /// </para>
    /// </summary>
    public ValueTask<string?> ClaimAsync(string contentKey, string path, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        _transaction ??= _connection.BeginTransaction();
        _insert.Transaction = _transaction;
        _insert.Parameters["@key"].Value = contentKey;
        _insert.Parameters["@path"].Value = path;

        string? leader = null;
        if (_insert.ExecuteNonQuery() == 0)
        {
            _select.Transaction = _transaction;
            _select.Parameters["@key"].Value = contentKey;
            leader = (string?)_select.ExecuteScalar();
        }

        if (++_sinceCommit >= CommitEvery)
            Commit();

        return ValueTask.FromResult(leader);
    }

    /// <summary>Ends the open batch. Nothing in a run needs this — the same connection reads its own uncommitted
    /// rows, and <see cref="DisposeAsync"/> commits the tail — but a test that counts the rows from a second
    /// connection does.</summary>
    internal void Flush() => Commit();

    private void Commit()
    {
        if (_transaction is null)
            return;

        _transaction.Commit();
        _transaction.Dispose();
        _transaction = null;
        _sinceCommit = 0;
    }

    /// <summary>Commits the tail of the last batch, closes the connection and deletes the file. The commit is not
    /// for durability (nobody reads this file after the run) but so that the connection closes with no transaction
    /// open, which is the difference between "closed" and "rolled back with journal_mode=OFF", a state SQLite
    /// explicitly does not define.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { Commit(); }
        catch (SqliteException) { /* the file is about to be deleted; failing the run over it would waste the run */ }

        _insert.Dispose();
        _select.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);

        RunWorkDb.Delete(Path);
        // journal_mode=OFF leaves no companion behind, but a pragma that failed to take would; one syscall.
        RunWorkDb.Delete(Path + "-journal");
    }
}
