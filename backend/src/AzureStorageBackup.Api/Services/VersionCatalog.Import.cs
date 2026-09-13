using System.Runtime.CompilerServices;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The write side of the catalog: a version goes in as a merge against the rows current at its predecessor, so what
/// is written is the version's changes, not its entries. See docs/storage-format.md, "One row per path per change".
/// </summary>
public sealed partial class VersionCatalog
{
    /// <summary>Rows that cover the version being imported, in path order. <c>id &lt;= @maxId</c> keeps the rows the
    /// import itself inserts out of its own cursor: SQLite makes no promise about whether a cursor sees rows added
    /// to the table it is walking, and a new <c>[N, next)</c> row seen again would be closed as "gone from N".</summary>
    private const string SelectCoveringSql = $"""
        SELECT id, version_from, version_to, unrecoverable, path_key, {EntryRowMapper.Columns} FROM entries
        WHERE version_from <= @v AND version_to > @v AND id <= @maxId ORDER BY path_key
        """;
    private const string SelectCoveringDirsSql =
        "SELECT id, version_from, version_to, path, path_key FROM dirs WHERE version_from <= @v AND version_to > @v AND id <= @maxId ORDER BY path_key";
    private const string MaxEntryIdSql = "SELECT COALESCE(MAX(id), 0) FROM entries";
    private const string MaxDirIdSql = "SELECT COALESCE(MAX(id), 0) FROM dirs";

    private const string InsertEntrySql = $"""
        INSERT INTO entries (version_from, version_to, parent, path_fold, path_key, {EntryRowMapper.Columns}, unrecoverable)
        VALUES (@version_from, @version_to, @parent, @path_fold, @path_key, {EntryRowMapper.Parameters}, @unrecoverable)
        """;
    private const string CloseEntrySql = "UPDATE entries SET version_to=@v WHERE id=@id";
    private const string StartEntryAtSql = "UPDATE entries SET version_from=@v WHERE id=@id";
    private const string DeleteEntrySql = "DELETE FROM entries WHERE id=@id";

    private const string InsertDirSql =
        "INSERT INTO dirs (version_from, version_to, path, parent, path_key) VALUES (@version_from, @version_to, @path, @parent, @path_key)";
    private const string CloseDirSql = "UPDATE dirs SET version_to=@v WHERE id=@id";
    private const string StartDirAtSql = "UPDATE dirs SET version_from=@v WHERE id=@id";
    private const string DeleteDirSql = "DELETE FROM dirs WHERE id=@id";
    private const string CopyDirSql =
        "INSERT INTO dirs (version_from, version_to, path, parent, path_key) SELECT @from, @to, path, parent, path_key FROM dirs WHERE id=@id";

    private const string CreateStageSql = $"""
        CREATE TEMP TABLE IF NOT EXISTS import_stage (seq INTEGER PRIMARY KEY, path_key BLOB NOT NULL, {EntryRowMapper.ColumnDefinitions});
        CREATE INDEX IF NOT EXISTS temp.import_stage_key ON import_stage (path_key);
        DELETE FROM import_stage;
        """;
    private const string InsertStageSql = $"INSERT INTO import_stage (seq, path_key, {EntryRowMapper.Columns}) VALUES (@seq, @path_key, {EntryRowMapper.Parameters})";
    private const string SelectStageSql = $"SELECT {EntryRowMapper.Columns} FROM import_stage ORDER BY path_key, seq";

    /// <summary>Records the order the entries were handed over in, which is the order the version has to be written
    /// back out in. One row per <em>path</em>, at its first <c>seq</c>: the merge keeps the first of a repeated path
    /// and records the rest as duplicates, so an order that named a path twice would serialize more entries than the
    /// version has. Grouped rather than correlated per row — the staging table is indexed by <c>path_key</c>, not by
    /// <c>path</c>, so a subquery per row would be a scan per row. The gaps a duplicate leaves in <c>seq</c> do not
    /// matter: the table is read back in <c>seq</c> order, not by position.</summary>
    private const string RecordStageOrderSql =
        "INSERT INTO entry_order (version, seq, path) SELECT @v, MIN(seq), path FROM import_stage GROUP BY path";

    private const string ClearStageSql = "DELETE FROM import_stage";

    /// <summary>How often <c>onEntries</c> hears from an import, in rows. Coarse enough to cost nothing against the
    /// merge itself, fine enough that a million-row version moves the line a hundred times.</summary>
    internal const int EntryProgressEvery = 10_000;

    /// <summary>Imports a version's index straight off the stream it was downloaded as. Two passes: the lists at the
    /// tail first (the unrecoverable flag is part of a row and has to be known while the entries stream), then the
    /// entries, merged against the rows current at the previous version. Requires a seekable stream.</summary>
    /// <param name="onEntries">Called with the running row count every <see cref="EntryProgressEvery"/> rows, on the
    /// importing thread; null when nobody is watching.</param>
    public Task ImportVersionAsync(int version, long identity, IndexStreamReader reader, CancellationToken ct, Action<long>? onEntries = null) =>
        OffThePoolAsync(() => ImportFromReaderAsync(version, identity, reader, onEntries, ct), ct);

    private async Task ImportFromReaderAsync(int version, long identity, IndexStreamReader reader, Action<long>? onEntries, CancellationToken ct)
    {
        if (!reader.Input.CanSeek)
            throw new NotSupportedException("A catalog import needs a seekable index stream: the tail lists are read before the entries.");

        // Pass 1: skip the entries (checking their order on the way) and read the lists behind them.
        var ordered = true;
        byte[]? last = null;
        foreach (var entry in reader.Entries())
        {
            var key = CatalogSql.PathKey(entry.Path);
            if (last is not null && Compare(key, last) < 0)
                ordered = false;
            last = key;
        }
        var emptyDirs = reader.ReadEmptyDirs();
        var unrecoverable = reader.ReadUnrecoverable();

        // Pass 2: the entries, from the start — of the index, which is not necessarily the start of the stream (a
        // cached .idx file carries its own header in front of it).
        reader.Input.Position = reader.Start;
        using var second = new IndexStreamReader(reader.Input);
        await ImportCoreAsync(version, identity, second.EntryCount, ToAsync(second.Entries()), emptyDirs, unrecoverable, ordered, onEntries, ct);
    }

    /// <summary>Imports a version from an in-memory enumeration. The entries need not be ordered: they are staged
    /// and merged in path order, and their own order is kept as the version's serialization order when it differs.</summary>
    public Task ImportVersionAsync(int version, long identity, int entryCount, IAsyncEnumerable<IndexEntry> entries,
        IReadOnlyList<string> emptyDirs, IReadOnlyList<string> unrecoverable, CancellationToken ct) =>
        OffThePoolAsync(() => ImportCoreAsync(version, identity, entryCount, entries, emptyDirs, unrecoverable, ordered: false, onEntries: null, ct), ct);

    /// <param name="ordered">Whether <paramref name="entries"/> come in ascending <see cref="CatalogSql.PathKey"/>
    /// order. If not, they are staged into a temp table, merged from it in path order, and their given order is
    /// recorded in <c>entry_order</c> so <see cref="SerializeVersionAsync"/> reproduces it.</param>
    private Task ImportCoreAsync(int version, long identity, int expected, IAsyncEnumerable<IndexEntry> entries,
        IReadOnlyList<string> emptyDirs, IReadOnlyList<string> unrecoverable, bool ordered, Action<long>? onEntries, CancellationToken ct) =>
        RunInTransactionAsync(
            () => MergeVersionAsync(version, identity, expected, entries, emptyDirs, unrecoverable, ordered, onEntries, ct), ct);

    /// <summary>The merge without a transaction of its own, for a caller that already holds one: the conversion of a
    /// format-1 file, which puts a version's rows and its bookkeeping in one transaction so it can resume at a version
    /// boundary. Its entries come off the old table in path order, so no staging pass is needed.</summary>
    internal Task ImportOrderedInTransactionAsync(int version, long identity, int entryCount, IAsyncEnumerable<IndexEntry> entries,
        IReadOnlyList<string> emptyDirs, IReadOnlyList<string> unrecoverable, Action<long>? onEntries, CancellationToken ct) =>
        MergeVersionAsync(version, identity, entryCount, entries, emptyDirs, unrecoverable, ordered: true, onEntries, ct);

    /// <summary>The import's body, inside whichever transaction the caller opened.</summary>
    private async Task MergeVersionAsync(int version, long identity, int expected, IAsyncEnumerable<IndexEntry> entries,
        IReadOnlyList<string> emptyDirs, IReadOnlyList<string> unrecoverable, bool ordered, Action<long>? onEntries, CancellationToken ct)
    {
        // Re-importing a version replaces it wholesale: a repair rewrites an index in place. Its rows go the way
        // retention takes them, and what other versions reach stays.
        if (await GetVersionAsync(version, ct) is not null)
            await RemoveVersionCoreAsync(version, ct);

        var source = entries;
        if (!ordered)
        {
            var (seen, ascending) = await StageAsync(entries, ct);
            if (seen != expected)
                throw new InvalidOperationException($"Version {version} announced {expected} entries but produced {seen}.");
            // Only a version whose entries really did arrive out of path order needs its order written down;
            // for every other one the path order is the order, and the table would be a row per entry per
            // version — the shape this format exists to stop storing.
            if (!ascending)
            {
                using var order = CreateCommand(RecordStageOrderSql);
                Set(order, "@v", version);
                await order.ExecuteNonQueryAsync(ct);
            }
            source = QueryStageAsync(ct);
        }

        var kept = await MergeEntriesAsync(version, expected, source, unrecoverable, ordered, onEntries, ct);
        AddEmptyDirs(emptyDirs);
        await MergeDirsAsync(version, ct);

        using var emptyDir = CreateCommand(InsertEmptyDirSql);
        for (var i = 0; i < emptyDirs.Count; i++)
        {
            Set(emptyDir, "@v", version);
            Set(emptyDir, "@path", emptyDirs[i]);
            Set(emptyDir, "@seq", i);
            await emptyDir.ExecuteNonQueryAsync(ct);
        }

        using var list = CreateCommand(InsertUnrecoverableListSql);
        for (var i = 0; i < unrecoverable.Count; i++)
        {
            Set(list, "@v", version);
            Set(list, "@path", unrecoverable[i]);
            Set(list, "@seq", i);
            await list.ExecuteNonQueryAsync(ct);
        }

        if (!ordered)
        {
            using var clear = CreateCommand(ClearStageSql);
            await clear.ExecuteNonQueryAsync(ct);
        }

        await UpsertVersionAsync(version, identity, kept, ct);
    }

    /// <summary>The directories of the version being merged, gathered as its entries go by (every prefix of every
    /// path, and the empty directories themselves), then reconciled against the <c>dirs</c> rows covering the
    /// version. In memory: a version's directories are a small fraction of its paths.</summary>
    private readonly SortedSet<string> _mergeDirs = new(StringComparer.Ordinal);

    private async Task<int> MergeEntriesAsync(int version, int expected, IAsyncEnumerable<IndexEntry> entries,
        IReadOnlyList<string> unrecoverable, bool checkOrder, Action<long>? onEntries, CancellationToken ct)
    {
        var next = await NextPresentAsync(version, ct) ?? CatalogSql.OpenEnd;
        var flagged = new HashSet<string>(unrecoverable, StringComparer.Ordinal);
        _mergeDirs.Clear();

        long maxId;
        using (var max = CreateCommand(MaxEntryIdSql))
            maxId = Convert.ToInt64(await max.ExecuteScalarAsync(ct));

        using var covering = CreateCommand(SelectCoveringSql);
        Set(covering, "@v", version);
        Set(covering, "@maxId", maxId);
        await using var rows = (SqliteDataReader)await covering.ExecuteReaderAsync(ct);
        var haveRow = await rows.ReadAsync(ct);

        using var insert = CreateCommand(InsertEntrySql);
        using var close = CreateCommand(CloseEntrySql);
        using var start = CreateCommand(StartEntryAtSql);
        using var delete = CreateCommand(DeleteEntrySql);
        using var copy = CreateCommand(CopyRowSql);

        var seen = 0;
        var kept = 0;
        byte[]? lastKey = null;
        await foreach (var entry in entries.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            seen++;
            if (onEntries is not null && seen % EntryProgressEvery == 0)
                onEntries(seen);

            var key = CatalogSql.PathKey(entry.Path);
            if (lastKey is not null)
            {
                var order = Compare(key, lastKey);
                if (order == 0)
                {
                    // The version names the same path twice: the first wins, the loss is recorded (it makes the
                    // version's serialization no longer byte-identical), exactly as the old primary key did.
                    await RecordIssueAsync(version, entry.Path, "duplicate", ct);
                    continue;
                }
                if (order < 0 && checkOrder)
                    throw new InvalidOperationException(
                        $"Version {version}'s entries are not in ascending ordinal path order: '{entry.Path}' came after the previous path.");
            }
            lastKey = key;
            AddDirsOf(entry.Path);

            // Everything current before this path is gone from the version.
            while (haveRow && Compare((byte[])rows["path_key"], key) < 0)
            {
                await CloseRowAsync(rows, version, next, close, start, delete, copy, ct);
                haveRow = await rows.ReadAsync(ct);
            }

            var flag = flagged.Contains(entry.Path);
            if (haveRow && Compare((byte[])rows["path_key"], key) == 0)
            {
                var current = EntryRowMapper.Read(rows);
                var currentFlag = rows.GetInt64(rows.GetOrdinal("unrecoverable")) != 0;
                if (!EntryRowMapper.SameEntry(current, entry) || currentFlag != flag)
                {
                    // The replacement reaches no further than the row it replaces: a row that ended at a
                    // since-removed version says the path stopped there, and the new entry must not claim the
                    // versions between, or re-importing that version would find the path present and unchanged.
                    var end = Math.Min(next, rows.GetInt32(2));
                    await CloseRowAsync(rows, version, next, close, start, delete, copy, ct);
                    await InsertRowAsync(insert, version, end, entry, flag, key, ct);
                }
                haveRow = await rows.ReadAsync(ct);
            }
            else
            {
                await InsertRowAsync(insert, version, next, entry, flag, key, ct);
            }

            kept++;
        }

        while (haveRow)
        {
            ct.ThrowIfCancellationRequested();
            await CloseRowAsync(rows, version, next, close, start, delete, copy, ct);
            haveRow = await rows.ReadAsync(ct);
        }

        if (seen != expected)
            throw new InvalidOperationException($"Version {version} announced {expected} entries but produced {seen}.");

        return kept;
    }

    /// <summary>
    /// The row no longer describes the version being imported. A row that started before it is closed at it; if
    /// it also reached past the next retained version, that tail is re-inserted under its own id, since those
    /// versions still see the old entry. A row that started exactly at this version (left by an earlier import of
    /// the same version, kept because a later version reaches it) is moved to start at the next version, or
    /// deleted when nothing past this version reaches it.
    /// </summary>
    private static async Task CloseRowAsync(SqliteDataReader row, int version, int next,
        SqliteCommand close, SqliteCommand start, SqliteCommand delete, SqliteCommand copy, CancellationToken ct)
    {
        var id = row.GetInt64(0);
        var from = row.GetInt32(1);
        var to = row.GetInt32(2);
        if (from == version)
        {
            if (to > next)
            {
                Set(start, "@id", id);
                Set(start, "@v", next);
                await start.ExecuteNonQueryAsync(ct);
            }
            else
            {
                Set(delete, "@id", id);
                await delete.ExecuteNonQueryAsync(ct);
            }
            return;
        }

        Set(close, "@id", id);
        Set(close, "@v", version);
        await close.ExecuteNonQueryAsync(ct);
        if (to > next)
        {
            Set(copy, "@id", id);
            Set(copy, "@from", next);
            Set(copy, "@to", to);
            await copy.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertRowAsync(SqliteCommand insert, int version, int next, IndexEntry entry, bool flag, byte[] key, CancellationToken ct)
    {
        Set(insert, "@version_from", version);
        Set(insert, "@version_to", next);
        Set(insert, "@parent", ParentOf(entry.Path));
        Set(insert, "@path_fold", entry.Path.ToUpperInvariant());
        Set(insert, "@path_key", key);
        Set(insert, "@unrecoverable", flag ? 1 : 0);
        EntryRowMapper.Bind(insert, entry);
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every directory prefix of a path ("a/b/c.txt" → "a", "a/b"), which is what makes browsing one level
    /// at a time a single indexed lookup instead of a scan over every path in the version.</summary>
    private void AddDirsOf(string path)
    {
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            if (slash > 0)   // a leading slash would make an empty directory name
                _mergeDirs.Add(path[..slash]);
    }

    /// <summary>The same merge for directories: the set gathered by <see cref="MergeEntriesAsync"/> plus the empty
    /// directories, against the <c>dirs</c> rows covering the version. Directories carry no content, so "same"
    /// is "present on both sides".</summary>
    private async Task MergeDirsAsync(int version, CancellationToken ct)
    {
        var next = await NextPresentAsync(version, ct) ?? CatalogSql.OpenEnd;
        long maxId;
        using (var max = CreateCommand(MaxDirIdSql))
            maxId = Convert.ToInt64(await max.ExecuteScalarAsync(ct));

        using var covering = CreateCommand(SelectCoveringDirsSql);
        Set(covering, "@v", version);
        Set(covering, "@maxId", maxId);
        await using var rows = (SqliteDataReader)await covering.ExecuteReaderAsync(ct);
        var haveRow = await rows.ReadAsync(ct);

        using var insert = CreateCommand(InsertDirSql);
        using var close = CreateCommand(CloseDirSql);
        using var start = CreateCommand(StartDirAtSql);
        using var delete = CreateCommand(DeleteDirSql);
        using var copy = CreateCommand(CopyDirSql);

        foreach (var dir in _mergeDirs)
        {
            ct.ThrowIfCancellationRequested();
            var key = CatalogSql.PathKey(dir);
            while (haveRow && Compare((byte[])rows["path_key"], key) < 0)
            {
                await CloseRowAsync(rows, version, next, close, start, delete, copy, ct);
                haveRow = await rows.ReadAsync(ct);
            }
            if (haveRow && Compare((byte[])rows["path_key"], key) == 0)
            {
                haveRow = await rows.ReadAsync(ct);
                continue;
            }
            Set(insert, "@version_from", version);
            Set(insert, "@version_to", next);
            Set(insert, "@path", dir);
            Set(insert, "@parent", ParentOf(dir));
            Set(insert, "@path_key", key);
            await insert.ExecuteNonQueryAsync(ct);
        }

        while (haveRow)
        {
            await CloseRowAsync(rows, version, next, close, start, delete, copy, ct);
            haveRow = await rows.ReadAsync(ct);
        }

        _mergeDirs.Clear();
    }

    /// <summary>Empty directories are browsable nodes too: their prefixes and themselves join the directory set
    /// before the directory merge. Called by <see cref="MergeVersionAsync"/> before <see cref="MergeDirsAsync"/>.</summary>
    private void AddEmptyDirs(IReadOnlyList<string> emptyDirs)
    {
        foreach (var dir in emptyDirs)
        {
            AddDirsOf(dir);
            _mergeDirs.Add(dir);
        }
    }

    /// <summary>Copies the entries into the staging table so the merge can read them in path order, and says how many
    /// there were and whether they already arrived in that order — the second answer is free here, next to the key
    /// each row is stored under, and decides whether the version needs an <c>entry_order</c> table at all.</summary>
    private async Task<(int Seen, bool Ascending)> StageAsync(IAsyncEnumerable<IndexEntry> entries, CancellationToken ct)
    {
        using (var create = CreateCommand(CreateStageSql))
            await create.ExecuteNonQueryAsync(ct);
        using var insert = CreateCommand(InsertStageSql);
        var seq = 0;
        var ascending = true;
        byte[]? lastKey = null;
        await foreach (var entry in entries.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            var key = CatalogSql.PathKey(entry.Path);
            if (lastKey is not null && Compare(key, lastKey) < 0)
                ascending = false;
            lastKey = key;
            Set(insert, "@seq", seq++);
            Set(insert, "@path_key", key);
            EntryRowMapper.Bind(insert, entry);
            await insert.ExecuteNonQueryAsync(ct);
        }
        return (seq, ascending);
    }

    private async IAsyncEnumerable<IndexEntry> QueryStageAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var command = CreateCommand(SelectStageSql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return EntryRowMapper.Read(reader);
    }

    public async Task RemoveVersionAsync(int version, CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        _transaction = transaction;
        try
        {
            await RemoveVersionCoreAsync(version, ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _transaction = null;
        }
    }

    /// <summary>Inside a transaction: the version row goes first, so "reachable" below is judged against what
    /// remains; then every entry and directory row nothing remaining reaches; then the per-version tables.</summary>
    private async Task RemoveVersionCoreAsync(int version, CancellationToken ct)
    {
        using (var drop = CreateCommand(DeleteVersionSql))
        {
            Set(drop, "@v", version);
            await drop.ExecuteNonQueryAsync(ct);
        }
        var max = await MaxPresentAsync(ct);
        using (var unreachable = CreateCommand(DeleteUnreachableEntriesSql))
        {
            Set(unreachable, "@max", max);
            await unreachable.ExecuteNonQueryAsync(ct);
        }
        // The version was the newest, so rows may now start above the maximum; retiring an older one cannot leave
        // any, and this pair has no index to work with.
        if (version > max)
        {
            using var above = CreateCommand(DeleteRowsAboveMaxSql);
            Set(above, "@max", max);
            await above.ExecuteNonQueryAsync(ct);
        }
        using var rest = CreateCommand(DeletePerVersionRowsSql);
        Set(rest, "@v", version);
        await rest.ExecuteNonQueryAsync(ct);
    }

    private static int Compare(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    /// <summary>Adapts the synchronous stream reader to the merge, so both entry sources share one code path.</summary>
    private static async IAsyncEnumerable<IndexEntry> ToAsync(IEnumerable<IndexEntry> entries)
    {
        await Task.CompletedTask;
        foreach (var entry in entries)
            yield return entry;
    }
}
