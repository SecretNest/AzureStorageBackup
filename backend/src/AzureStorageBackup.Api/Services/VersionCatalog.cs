using System.Globalization;
using System.Runtime.CompilerServices;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>One retained version as the catalog knows it. <c>Identity</c> is the version's identity stamp (the value
/// that tells "the v3 I imported" from "the v3 that has since been rewritten"), so a stale catalog can be spotted
/// without re-downloading the index.</summary>
public sealed record CatalogVersionInfo(int Version, long Identity, int EntryCount, DateTimeOffset ImportedAt);

/// <summary>A dedup hit: content that is already in the cloud as a single-file blob, and everything the caller needs to point an entry at it.</summary>
public sealed record CatalogBlobHit(string Ref, bool Raw, int Volumes, IReadOnlyList<long> VolumeSizes);

/// <summary>Which content occupies a blob ref, and whether the version that owns it declares it damaged (an occupied name holding broken bytes).</summary>
public sealed record CatalogRefOwner(string FullHash, long Length, string? HeadHash, string? TailHash, bool Damaged);

/// <summary>Content that already sits inside an existing pack: which pack, and under which name inside the archive.</summary>
public sealed record CatalogPackMember(string PackId, string EntryName, string? TailHash);

/// <summary>One direct child of a directory. <c>HasChildren</c> tells the frontend whether the node is expandable
/// without making it fetch a level it may never open; a file carries its <c>Entry</c>, a directory has none.</summary>
public sealed record CatalogChild(string Name, bool IsDir, bool HasChildren, IndexEntry? Entry);

/// <summary>A change repair or a check wants made to one entry of one version. A null field means "leave it alone" —
/// which is why <c>UnreadableAt</c> cannot be cleared through a patch, only set.</summary>
public sealed record CatalogPatch(int Version, string Path, DateTimeOffset? UnreadableAt, bool? Unrecoverable, StorageRef? Storage);

/// <summary>
/// A container's <c>catalog.db</c>: every retained version's index entries, plus the secondary indexes dedup,
/// browsing, retention and repair need, in place of holding one <see cref="VersionIndex"/> per version in memory.
/// <para>
/// The file is a cache, not a source of truth — the index blobs in the cloud are — so it can be deleted and rebuilt,
/// which is what lets it run with <c>synchronous=NORMAL</c> and no migration story.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> It wraps a single <see cref="SqliteConnection"/> and every reader streams off it, so two
/// concurrent callers would interleave on one connection. Callers that need parallelism open one catalog each (the
/// orchestrator opens two: one for the diff cursor, one for dedup lookups) — cheap, since the file is shared and only
/// the handle is per-instance.
/// </para>
/// </summary>
public sealed partial class VersionCatalog : IAsyncDisposable
{
    private const string SelectVersionSql = "SELECT version, identity, entry_count, imported_at FROM versions WHERE version=@v";
    private const string SelectVersionsSql = "SELECT version, identity, entry_count, imported_at FROM versions ORDER BY version";
    private const string SelectEntryCountSql = "SELECT entry_count FROM versions WHERE version=@v";

    private const string UpsertVersionSql = """
        INSERT INTO versions (version, identity, entry_count, imported_at) VALUES (@v, @identity, @count, @at)
          ON CONFLICT (version) DO UPDATE SET identity=@identity, entry_count=@count, imported_at=@at
        """;

    private const string DeleteVersionRowsSql = """
        DELETE FROM entries WHERE version=@v;
        DELETE FROM dirs WHERE version=@v;
        DELETE FROM empty_dirs WHERE version=@v;
        DELETE FROM unrecoverable WHERE version=@v;
        DELETE FROM import_issues WHERE version=@v;
        """;

    private const string DeleteVersionSql = "DELETE FROM versions WHERE version=@v";

    // OR IGNORE, not OR REPLACE: a duplicate path inside one version keeps the first row (see ImportCoreAsync).
    private const string InsertEntrySql =
        $"INSERT OR IGNORE INTO entries (version, seq, parent, path_fold, {EntryRowMapper.Columns}) " +
        $"VALUES (@version, @seq, @parent, @path_fold, {EntryRowMapper.Parameters})";

    private const string InsertDirSql = "INSERT OR IGNORE INTO dirs (version, path, parent) VALUES (@v, @path, @parent)";
    private const string InsertEmptyDirSql = "INSERT OR IGNORE INTO empty_dirs (version, path, seq) VALUES (@v, @path, @seq)";
    private const string InsertIssueSql = "INSERT OR IGNORE INTO import_issues (version, path, issue) VALUES (@v, @path, @issue)";

    private const string MarkUnrecoverableSql = """
        INSERT OR IGNORE INTO unrecoverable (version, path, seq) VALUES (@v, @path, @seq);
        UPDATE entries SET unrecoverable=1 WHERE version=@v AND path=@path;
        """;

    /// <summary>Appends at the end of the version's list, reproducing the <c>List.Add</c> order the check and repair
    /// flows write their unrecoverable paths in — that order is part of the index's bytes.</summary>
    private const string AddUnrecoverableSql = """
        INSERT OR IGNORE INTO unrecoverable (version, path, seq)
          VALUES (@v, @path, (SELECT COALESCE(MAX(seq), -1) + 1 FROM unrecoverable WHERE version=@v));
        UPDATE entries SET unrecoverable=1 WHERE version=@v AND path=@path;
        """;

    private const string ClearUnrecoverableSql = """
        DELETE FROM unrecoverable WHERE version=@v AND path=@path;
        UPDATE entries SET unrecoverable=0 WHERE version=@v AND path=@path;
        """;

    private const string PatchUnreadableSql =
        "UPDATE entries SET unreadable_ticks=@unreadable_ticks, unreadable_offset=@unreadable_offset WHERE version=@v AND path=@path";

    private const string PatchStorageSql =
        "UPDATE entries SET storage_kind=@storage_kind, storage_ref=@storage_ref, entry_name=@entry_name, " +
        "volumes=@volumes, raw=@raw, volume_sizes=@volume_sizes WHERE version=@v AND path=@path";

    private const string SelectEntriesBySeqSql = $"SELECT {EntryRowMapper.Columns} FROM entries WHERE version=@v ORDER BY seq";
    private const string SelectEmptyDirsSql = "SELECT path FROM empty_dirs WHERE version=@v ORDER BY seq";
    private const string SelectUnrecoverableSql = "SELECT path FROM unrecoverable WHERE version=@v ORDER BY seq";

    private readonly SqliteConnection _connection;

    /// <summary>The statements below run inside the import/patch transaction; Microsoft.Data.Sqlite refuses a command
    /// that does not name the connection's pending transaction, and the class is single-threaded, so one field is
    /// enough to hand it to every helper without threading it through their signatures.</summary>
    private SqliteTransaction? _transaction;

    private VersionCatalog(string path, SqliteConnection connection)
    {
        Path = path;
        _connection = connection;
    }

    /// <summary>The catalog file's path (a container's <c>catalog.db</c>).</summary>
    public string Path { get; }

    /// <summary>Opens (and, for a writer, creates) the catalog. A read-only open neither creates the file nor touches
    /// the schema, so a reader can never resurrect a catalog somebody just deleted.</summary>
    public static async Task<VersionCatalog> OpenAsync(string path, bool readOnly, CancellationToken ct)
    {
        var connection = new SqliteConnection(CatalogSql.ConnectionString(path, readOnly));
        try
        {
            await connection.OpenAsync(ct);
            CatalogSql.ApplyPragmas(connection, readOnly);
            if (!readOnly)
            {
                // A garbage or truncated file often trips over ApplyPragmas' own statements (SQLITE_NOTADB) before
                // this ever runs, but a file that merely has a corrupt page deep inside it looks fine until
                // something reads that page — which quick_check forces to happen now, rather than mid-import.
                // Read-only opens skip it: a reader that finds a corrupt page fails the query that touches it, and
                // is never the one to delete and recreate the file (VersionCatalogStore reserves that to a writer).
                await QuickCheckAsync(connection, ct);
                CatalogSql.EnsureSchema(connection);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return new VersionCatalog(path, connection);
    }

    /// <summary>Runs <c>PRAGMA quick_check</c> and turns anything other than a single "ok" row into the same
    /// <see cref="SqliteException"/> shape SQLite itself raises for a corrupt file (<c>SQLITE_CORRUPT</c>), so
    /// <see cref="VersionCatalogStore"/> has one error code to catch regardless of which check caught the damage.</summary>
    private static async Task QuickCheckAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        var ok = await reader.ReadAsync(ct) && reader.GetString(0) == "ok";
        if (!ok)
            throw new SqliteException("Catalog failed PRAGMA quick_check.", 11 /* SQLITE_CORRUPT */);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    // ---- versions -------------------------------------------------------------------------------------------

    public async Task<CatalogVersionInfo?> GetVersionAsync(int version, CancellationToken ct)
    {
        using var command = Command(SelectVersionSql);
        Set(command, "@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<IReadOnlyList<CatalogVersionInfo>> ListVersionsAsync(CancellationToken ct)
    {
        using var command = Command(SelectVersionsSql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        var versions = new List<CatalogVersionInfo>();
        while (await reader.ReadAsync(ct))
            versions.Add(ReadVersion(reader));
        return versions;
    }

    /// <summary>Imports a version's index straight off the stream it was downloaded as. One transaction: either the
    /// version is wholly in the catalog or it is not there at all, so an interrupted import cannot leave half a
    /// version for dedup to trust.</summary>
    public Task ImportVersionAsync(int version, long identity, IndexStreamReader reader, CancellationToken ct) =>
        // The empty-dirs and unrecoverable sections sit after the last entry in the stream, so they can only be read
        // once the entries have been consumed — hence the callback rather than two arguments.
        ImportCoreAsync(version, identity, reader.EntryCount, ToAsync(reader.Entries()),
            () => (reader.ReadEmptyDirs(), reader.ReadUnrecoverable()), ct);

    /// <summary>Imports the version a finishing run just produced, straight from the emission order, without building an index object first.</summary>
    public Task ImportVersionAsync(int version, long identity, int entryCount, IAsyncEnumerable<IndexEntry> entries,
        IReadOnlyList<string> emptyDirs, IReadOnlyList<string> unrecoverable, CancellationToken ct) =>
        ImportCoreAsync(version, identity, entryCount, entries, () => (emptyDirs, unrecoverable), ct);

    private async Task ImportCoreAsync(int version, long identity, int expected, IAsyncEnumerable<IndexEntry> entries,
        Func<(IReadOnlyList<string> EmptyDirs, IReadOnlyList<string> Unrecoverable)> tail, CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        _transaction = transaction;
        try
        {
            // Re-importing a version replaces it wholesale: a repair rewrites an index in place, and merging the old
            // rows with the new ones would leave entries no version references any more.
            await DeleteVersionRowsAsync(version, ct);

            using var insert = Command(InsertEntrySql);
            using var dir = Command(InsertDirSql);
            var seen = 0;
            var kept = 0;
            await foreach (var entry in entries.WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();
                Set(insert, "@version", version);
                Set(insert, "@seq", seen++);
                Set(insert, "@parent", ParentOf(entry.Path));
                Set(insert, "@path_fold", entry.Path.ToUpperInvariant());
                EntryRowMapper.Bind(insert, entry);
                if (await insert.ExecuteNonQueryAsync(ct) != 1)
                {
                    // The primary key cannot hold the same path twice. The first one wins (it is the one every
                    // earlier in-memory reader would have found first too), and the loss is recorded rather than
                    // swallowed, because it makes this version's serialization no longer byte-identical.
                    await RecordIssueAsync(version, entry.Path, "duplicate", ct);
                    continue;
                }

                kept++;
                await InsertAncestorsAsync(dir, version, entry.Path, ct);
            }

            if (seen != expected)
                throw new InvalidOperationException($"Version {version} announced {expected} entries but produced {seen}.");

            var (emptyDirs, unrecoverable) = tail();
            using var emptyDir = Command(InsertEmptyDirSql);
            for (var i = 0; i < emptyDirs.Count; i++)
            {
                Set(emptyDir, "@v", version);
                Set(emptyDir, "@path", emptyDirs[i]);
                Set(emptyDir, "@seq", i);
                await emptyDir.ExecuteNonQueryAsync(ct);
                // The empty dir has to become a browsable node itself, and InsertAncestors only inserts a path's
                // ancestors — so it is handed a notional child of the empty dir.
                await InsertAncestorsAsync(dir, version, emptyDirs[i] + "/x", ct);
            }

            using var mark = Command(MarkUnrecoverableSql);
            for (var i = 0; i < unrecoverable.Count; i++)
            {
                Set(mark, "@v", version);
                Set(mark, "@path", unrecoverable[i]);
                Set(mark, "@seq", i);
                await mark.ExecuteNonQueryAsync(ct);
            }

            await UpsertVersionAsync(version, identity, kept, ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _transaction = null;
        }
    }

    public async Task RemoveVersionAsync(int version, CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        _transaction = transaction;
        try
        {
            await DeleteVersionRowsAsync(version, ct);
            using var command = Command(DeleteVersionSql);
            Set(command, "@v", version);
            await command.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _transaction = null;
        }
    }

    /// <summary>
    /// Writes the version back out in the frozen cloud format (§3.2), entry by entry in <c>seq</c> order, so a version
    /// imported from the cloud and written back is the same bytes.
    /// </summary>
    /// <param name="patches">Changes to apply on the way out without having been stored yet: repair writes the fixed
    /// index to the cloud first and only records it in the catalog once that upload succeeded.</param>
    public async Task SerializeVersionAsync(int version, Stream output, IReadOnlyList<CatalogPatch>? patches, CancellationToken ct)
    {
        var byPath = patches?.Where(p => p.Version == version).ToDictionary(p => p.Path, StringComparer.Ordinal);
        var count = await EntryCountAsync(version, ct)
            ?? throw new InvalidOperationException($"Version {version} is not in the catalog.");

        using var writer = new IndexStreamWriter(output);
        writer.WriteHeader(version, count);
        await foreach (var entry in QueryEntriesAsync(SelectEntriesBySeqSql, version, ct))
            writer.WriteEntry(byPath is not null && byPath.TryGetValue(entry.Path, out var patch) ? Apply(entry, patch) : entry);

        writer.WriteEmptyDirs(await EmptyDirsAsync(version, ct));

        var unrecoverable = new List<string>(await UnrecoverableAsync(version, ct));
        if (byPath is not null)
        {
            foreach (var patch in byPath.Values)
            {
                // Appended at the end, never sorted in: the list's order is part of the bytes, and the flows that
                // produce these patches append too.
                if (patch.Unrecoverable == true && !unrecoverable.Contains(patch.Path))
                    unrecoverable.Add(patch.Path);
                else if (patch.Unrecoverable == false)
                    unrecoverable.Remove(patch.Path);
            }
        }

        writer.WriteUnrecoverable(unrecoverable);
    }

    private static IndexEntry Apply(IndexEntry entry, CatalogPatch patch) =>
        entry with { UnreadableAt = patch.UnreadableAt ?? entry.UnreadableAt, Storage = patch.Storage ?? entry.Storage };

    /// <summary>Records repair's / the check's findings: one transaction, so the <c>entries.unrecoverable</c> flag and
    /// the <c>unrecoverable</c> table can never disagree about the same path.</summary>
    public async Task ApplyPatchesAsync(IReadOnlyList<CatalogPatch> patches, CancellationToken ct)
    {
        if (patches.Count == 0)
            return;

        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        _transaction = transaction;
        try
        {
            using var unreadable = Command(PatchUnreadableSql);
            using var storage = Command(PatchStorageSql);
            using var add = Command(AddUnrecoverableSql);
            using var clear = Command(ClearUnrecoverableSql);
            foreach (var patch in patches)
            {
                ct.ThrowIfCancellationRequested();
                if (patch.UnreadableAt is { } at)
                {
                    Set(unreadable, "@v", patch.Version);
                    Set(unreadable, "@path", patch.Path);
                    Set(unreadable, "@unreadable_ticks", at.UtcTicks);
                    Set(unreadable, "@unreadable_offset", (int)at.Offset.TotalMinutes);
                    await unreadable.ExecuteNonQueryAsync(ct);
                }

                if (patch.Storage is not null)
                {
                    Set(storage, "@v", patch.Version);
                    Set(storage, "@path", patch.Path);
                    EntryRowMapper.BindStorage(storage, patch.Storage);
                    await storage.ExecuteNonQueryAsync(ct);
                }

                if (patch.Unrecoverable is { } flag)
                {
                    var command = flag ? add : clear;
                    Set(command, "@v", patch.Version);
                    Set(command, "@path", patch.Path);
                    await command.ExecuteNonQueryAsync(ct);
                }
            }

            await transaction.CommitAsync(ct);
        }
        finally
        {
            _transaction = null;
        }
    }

    public Task<IReadOnlyList<string>> EmptyDirsAsync(int version, CancellationToken ct) =>
        StringsAsync(SelectEmptyDirsSql, version, ct);

    public Task<IReadOnlyList<string>> UnrecoverableAsync(int version, CancellationToken ct) =>
        StringsAsync(SelectUnrecoverableSql, version, ct);

    // ---- plumbing -------------------------------------------------------------------------------------------

    private SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _transaction;
        return command;
    }

    /// <summary>The catalog's own parameters (version, path, seq, …) go through the mapper's setter too, so every
    /// command in the class reuses its parameters across rows the same way.</summary>
    private static void Set(SqliteCommand command, string name, object? value) => EntryRowMapper.Set(command, name, value);

    private static CatalogVersionInfo ReadVersion(SqliteDataReader reader) => new(
        reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2),
        DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    private async Task DeleteVersionRowsAsync(int version, CancellationToken ct)
    {
        using var command = Command(DeleteVersionRowsSql);
        Set(command, "@v", version);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task UpsertVersionAsync(int version, long identity, int entryCount, CancellationToken ct)
    {
        using var command = Command(UpsertVersionSql);
        Set(command, "@v", version);
        Set(command, "@identity", identity);
        // The stored count is what was actually inserted, not what the source announced: it becomes the header of the
        // next serialization, and a header that promises more entries than there are rows produces an unreadable index.
        Set(command, "@count", entryCount);
        Set(command, "@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task RecordIssueAsync(int version, string path, string issue, CancellationToken ct)
    {
        using var command = Command(InsertIssueSql);
        Set(command, "@v", version);
        Set(command, "@path", path);
        Set(command, "@issue", issue);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Inserts every directory prefix of <paramref name="path"/> ("a/b/c.txt" → "a", "a/b"), which is what
    /// makes browsing one level at a time a single indexed lookup instead of a scan over every path in the version.</summary>
    private static async Task InsertAncestorsAsync(SqliteCommand command, int version, string path, CancellationToken ct)
    {
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
        {
            if (slash == 0)
                continue;   // a leading slash would make an empty directory name

            var dir = path[..slash];
            Set(command, "@v", version);
            Set(command, "@path", dir);
            Set(command, "@parent", ParentOf(dir));
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    internal static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private async Task<int?> EntryCountAsync(int version, CancellationToken ct)
    {
        using var command = Command(SelectEntryCountSql);
        Set(command, "@v", version);
        return await command.ExecuteScalarAsync(ct) is long count ? (int)count : null;
    }

    private async Task<IReadOnlyList<string>> StringsAsync(string sql, int version, CancellationToken ct)
    {
        using var command = Command(sql);
        Set(command, "@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        var values = new List<string>();
        while (await reader.ReadAsync(ct))
            values.Add(reader.GetString(0));
        return values;
    }

    /// <summary>Streams entries off a reader — never a materialized list, because a version can hold a million of them.</summary>
    private async IAsyncEnumerable<IndexEntry> QueryEntriesAsync(
        string sql, int version, [EnumeratorCancellation] CancellationToken ct)
    {
        using var command = Command(sql);
        Set(command, "@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return EntryRowMapper.Read(reader);
    }

    /// <summary>Adapts the synchronous stream reader to the one import path, so both entry sources share the transaction and duplicate handling.</summary>
    private static async IAsyncEnumerable<IndexEntry> ToAsync(IEnumerable<IndexEntry> entries)
    {
        await Task.CompletedTask;
        foreach (var entry in entries)
            yield return entry;
    }
}
