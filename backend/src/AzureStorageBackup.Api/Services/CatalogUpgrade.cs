using System.Runtime.CompilerServices;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Converts a format-1 catalog (one row per version per path) to format 2 (one row per path per change) in place:
/// the old tables are renamed aside, the v2 schema is created, and every version is merged in ascending order
/// through the same import the run uses, one transaction per version, with an <c>upgrade_done</c> row per version
/// so a conversion killed halfway resumes from the first version not yet in. The last thing written is the format
/// stamp; a file without it is resumed, never trusted. Reads are sequential over the old rows; the cost is the old
/// row count, once — minutes to tens of minutes for a history of millions of rows.
/// </summary>
internal static class CatalogUpgrade
{
    /// <summary>
    /// <c>versions</c> goes aside with the rest. It is the one old table the v2 schema would otherwise have shared,
    /// and sharing it would cost the conversion its whole point: the merge bounds a version's rows at the next
    /// version the table already lists (<see cref="VersionCatalog.NextPresentAsync"/>), so with every old version
    /// still listed, version <c>k</c>'s rows would end at <c>k+1</c>, version <c>k+1</c> would find nothing current
    /// to compare against, and the result would be the format-1 shape written into the format-2 tables — correct,
    /// and not one row smaller. Aside, the new table lists exactly the versions converted so far, which is what makes
    /// each version a merge against the one before it.
    /// <para>
    /// The renames carry no <c>IF EXISTS</c> on purpose: format 1's own schema pass created all six tables on every
    /// write open, so a file this runs on has all six. A file missing one is not a format-1 catalog at all, and
    /// failing the whole begin transaction on it is the right answer — the store's recovery rebuilds it from the
    /// cloud rather than converting half a layout.
    /// </para>
    /// </summary>
    private const string BeginSql = """
        DROP INDEX IF EXISTS entries_seq; DROP INDEX IF EXISTS entries_parent; DROP INDEX IF EXISTS entries_fold;
        DROP INDEX IF EXISTS entries_path_key; DROP INDEX IF EXISTS entries_storage;
        DROP INDEX IF EXISTS entries_content; DROP INDEX IF EXISTS entries_ref; DROP INDEX IF EXISTS entries_head;
        DROP INDEX IF EXISTS dirs_parent;
        ALTER TABLE versions RENAME TO v1_versions;
        ALTER TABLE entries RENAME TO v1_entries;
        ALTER TABLE dirs RENAME TO v1_dirs;
        ALTER TABLE empty_dirs RENAME TO v1_empty_dirs;
        ALTER TABLE unrecoverable RENAME TO v1_unrecoverable;
        ALTER TABLE import_issues RENAME TO v1_import_issues;
        CREATE TABLE upgrade_done (version INTEGER PRIMARY KEY);
        """;
    private const string EndSql = """
        DROP TABLE v1_versions; DROP TABLE v1_entries; DROP TABLE v1_dirs; DROP TABLE v1_empty_dirs;
        DROP TABLE v1_unrecoverable; DROP TABLE v1_import_issues;
        DROP TABLE upgrade_done;
        """;
    private const string PendingVersionsSql =
        "SELECT version, identity, entry_count FROM v1_versions WHERE version NOT IN (SELECT version FROM upgrade_done) ORDER BY version";
    private const string TotalRowsSql = "SELECT COALESCE(SUM(entry_count), 0) FROM v1_versions";
    private const string DoneRowsSql = "SELECT COALESCE(SUM(v.entry_count), 0) FROM v1_versions v JOIN upgrade_done d ON d.version = v.version";
    private const string V1EntriesSql = $"SELECT {EntryRowMapper.Columns} FROM v1_entries WHERE version=@v ORDER BY path_key";
    private const string V1SeqOrderSql = "SELECT seq, path, path_key FROM v1_entries WHERE version=@v ORDER BY seq";
    private const string V1EmptyDirsSql = "SELECT path FROM v1_empty_dirs WHERE version=@v ORDER BY seq";
    private const string V1UnrecoverableSql = "SELECT path FROM v1_unrecoverable WHERE version=@v ORDER BY seq";
    private const string CopyIssuesSql = "INSERT OR IGNORE INTO import_issues (version, path, issue) SELECT version, path, issue FROM v1_import_issues WHERE version=@v";
    private const string InsertOrderSql = "INSERT INTO entry_order (version, seq, path) VALUES (@v, @seq, @path)";
    private const string MarkDoneSql = "INSERT INTO upgrade_done (version) VALUES (@v)";

    /// <summary>On a thread of its own, like the import and the index rebuild it is made of: Microsoft.Data.Sqlite is
    /// synchronous underneath, and a conversion holds whatever thread it starts on for as long as the history is
    /// big — a pool worker gone for tens of minutes next to the upload workers' own blocking work.</summary>
    public static Task RunAsync(VersionCatalog catalog, IProgress<CatalogUpgradeProgress>? progress, CancellationToken ct) =>
        VersionCatalog.OffThePoolAsync(() => ConvertAsync(catalog, progress, ct), ct);

    private static async Task ConvertAsync(VersionCatalog catalog, IProgress<CatalogUpgradeProgress>? progress, CancellationToken ct)
    {
        var connection = catalog.Connection;
        if (!CatalogSql.HasTable(connection, "v1_entries"))
        {
            // First time in: put the old tables aside and lay the v2 schema beside them. One transaction: a kill
            // here leaves either the old layout or the whole in-progress layout, never half.
            await using var begin = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = begin;
                command.CommandText = BeginSql;
                await command.ExecuteNonQueryAsync(ct);
            }
            CatalogSql.EnsureSchema(connection, begin);
            await begin.CommitAsync(ct);
        }

        // The same bracket EnsureVersionsAsync takes for a migration, and for the same reason: every version the
        // loop converts would otherwise insert its rows into three indexes keyed by hash, ref and length, at random
        // over a history that grows under it — a random page read per row per index (see CatalogSql.GlobalIndexNames
        // for the measurement). Dropped for the whole conversion and sorted back once at the end. On a resumed
        // conversion the drop is a no-op, and a conversion that is killed leaves a file without them that only the
        // next conversion touches, since it is not stamped.
        await catalog.DropGlobalIndexesAsync(ct);

        var total = await ScalarAsync(connection, TotalRowsSql, ct);
        var done = await ScalarAsync(connection, DoneRowsSql, ct);
        foreach (var (version, identity, count) in await PendingAsync(connection, ct))
        {
            ct.ThrowIfCancellationRequested();
            var booked = 0L;
            progress?.Report(new CatalogUpgradeProgress(version, done, total, VersionDone: false));

            await catalog.RunInTransactionAsync(async () =>
            {
                var emptyDirs = await StringsAsync(catalog, V1EmptyDirsSql, version, ct);
                var unrecoverable = await StringsAsync(catalog, V1UnrecoverableSql, version, ct);
                await catalog.ImportOrderedInTransactionAsync(version, identity, count, V1Entries(catalog, version, ct), emptyDirs, unrecoverable,
                    n =>
                    {
                        var landed = Math.Min(n, count);
                        if (landed > booked) { done += landed - booked; booked = landed; }
                        progress?.Report(new CatalogUpgradeProgress(version, done, total, VersionDone: false));
                    }, ct);
                await RecordOrderIfNotPathOrderAsync(catalog, version, ct);
                await ExecAsync(catalog, CopyIssuesSql, version, ct);
                await ExecAsync(catalog, MarkDoneSql, version, ct);
            }, ct);

            done += count - booked;
            progress?.Report(new CatalogUpgradeProgress(version, done, total, VersionDone: true));
        }

        // The other half of the bracket, before the stamp: a file that reads as converted has its indexes.
        await catalog.RebuildGlobalIndexesAsync(ct);

        await using (var end = (SqliteTransaction)await connection.BeginTransactionAsync(ct))
        {
            using var command = connection.CreateCommand();
            command.Transaction = end;
            command.CommandText = EndSql;
            await command.ExecuteNonQueryAsync(ct);
            CatalogSql.MarkCurrent(connection, end);
            await end.CommitAsync(ct);
        }

        // Reclaim the old rows' pages. VACUUM needs temp room for a copy of the file, and it is the one step here
        // that can fail on a catalog that is already converted, stamped and correct: no temp room (SQLITE_FULL), a
        // temp volume it cannot open (SQLITE_CANTOPEN), a NAS I/O error (SQLITE_IOERR), a second writer
        // (SQLITE_BUSY). Every one of them is answered the same way, because the alternative is not answering it
        // at all: an exception escaping here is wrapped as a CatalogUpgradeException by the store, and its recovery
        // would delete the finished catalog and re-download the whole history to rebuild what is already on disk.
        // So the failure is recorded, the store logs it, and the file stays large and correct until the next
        // VACUUM — a later conversion's, or an operator's. Cancellation is not caught: it is not a VACUUM failure,
        // and the file is stamped, so the next open finds the work done.
        try
        {
            using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM";
            await vacuum.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex)
        {
            // logged by the store, which knows the path
            catalog.VacuumSkipped = ex.Message;
        }
    }

    /// <summary>A version whose <c>seq</c> order is not its path order (pre-M4 builds) gets an order table, so it
    /// serializes as it came. The check is one pass over the version's rows in seq order.</summary>
    private static async Task RecordOrderIfNotPathOrderAsync(VersionCatalog catalog, int version, CancellationToken ct)
    {
        var order = new List<(int Seq, string Path)>();
        var inPathOrder = true;
        byte[]? last = null;
        using (var command = catalog.CreateCommand(V1SeqOrderSql))
        {
            command.Parameters.AddWithValue("@v", version);
            await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = (byte[])reader["path_key"];
                if (last is not null && key.AsSpan().SequenceCompareTo(last) < 0)
                    inPathOrder = false;
                last = key;
                order.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }
        if (inPathOrder)
            return;
        using var insert = catalog.CreateCommand(InsertOrderSql);
        foreach (var (seq, path) in order)
        {
            EntryRowMapper.Set(insert, "@v", version);
            EntryRowMapper.Set(insert, "@seq", seq);
            EntryRowMapper.Set(insert, "@path", path);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async IAsyncEnumerable<IndexEntry> V1Entries(VersionCatalog catalog, int version, [EnumeratorCancellation] CancellationToken ct)
    {
        using var command = catalog.CreateCommand(V1EntriesSql);
        command.Parameters.AddWithValue("@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return EntryRowMapper.Read(reader);
    }

    private static async Task<List<(int Version, long Identity, int Count)>> PendingAsync(SqliteConnection connection, CancellationToken ct)
    {
        var pending = new List<(int, long, int)>();
        using var command = connection.CreateCommand();
        command.CommandText = PendingVersionsSql;
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            pending.Add((reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2)));
        return pending;
    }

    private static async Task<List<string>> StringsAsync(VersionCatalog catalog, string sql, int version, CancellationToken ct)
    {
        var values = new List<string>();
        using var command = catalog.CreateCommand(sql);
        command.Parameters.AddWithValue("@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            values.Add(reader.GetString(0));
        return values;
    }

    private static async Task ExecAsync(VersionCatalog catalog, string sql, int version, CancellationToken ct)
    {
        using var command = catalog.CreateCommand(sql);
        command.Parameters.AddWithValue("@v", version);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
}
