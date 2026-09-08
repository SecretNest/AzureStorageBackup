using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Everything about the shape of a <c>catalog.db</c> file: how it is opened, the pragmas it runs under, its DDL, and
/// the one place an <see cref="IndexEntry"/> is written to / read from a row.
/// <para>
/// Kept out of <see cref="VersionCatalog"/> so the run's own work database (which stores the same entry columns while
/// a backup is in flight) can share the column mapping rather than grow a second copy of it — two copies of "which
/// column holds the tail hash" is exactly the kind of drift that ends with an index pointing at somebody else's
/// content.
/// </para>
/// <para>
/// Raw ADO, not EF: this database is created and deleted per container, has no migrations (it is a rebuildable cache
/// of blobs that are themselves the source of truth), and its hot paths are bulk inserts and streaming reads, which
/// a change tracker only gets in the way of.
/// </para>
/// </summary>
public static class CatalogSql
{
    /// <summary>
    /// <c>Pooling=false</c> is not a tuning knob here. A pooled <see cref="SqliteConnection"/> that is garbage
    /// collected without being disposed hands its live handle to the next Open of the same connection string, and the
    /// symptom is a <c>SQLite Error 0: 'not an error'</c> from a completely unrelated statement (see the application
    /// database's own history). A catalog is opened per container and held for the length of an operation, so pooling
    /// buys nothing to weigh against that.
    /// </summary>
    /// <param name="readOnly">A reader's open: never creates the file. It is <see cref="SqliteOpenMode.ReadWrite"/>
    /// rather than <see cref="SqliteOpenMode.ReadOnly"/> on purpose — see <see cref="ProcessPrivateSqlite"/> for why a
    /// read-only handle would bring the <c>-shm</c> file back for every connection on the catalog.</param>
    public static string ConnectionString(string path, bool readOnly) => new SqliteConnectionStringBuilder
    {
        DataSource = ProcessPrivateSqlite.DataSource(path),
        Mode = readOnly ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate,
        Pooling = false,
        Cache = SqliteCacheMode.Private,
    }.ToString();

    /// <summary>
    /// WAL, because a long import writes for minutes while the UI reads the same file; 64 MiB of page cache
    /// (<c>cache_size</c> is negative = KiB), because the dedup lookups are index probes scattered over a file that
    /// can reach hundreds of MiB; <c>synchronous=NORMAL</c>, because the catalog is rebuildable from the cloud and
    /// paying for a full fsync per transaction would double the import's cost to protect data we can re-download.
    /// Foreign keys are off — the tables are keyed by <c>version</c> and cleaned up as a set by
    /// <see cref="VersionCatalog.RemoveVersionAsync"/>, so enforcement would only cost per-row lookups.
    /// </summary>
    public static void ApplyPragmas(SqliteConnection connection, bool readOnly)
    {
        using var command = connection.CreateCommand();
        // journal_mode is a property of the file, and setting it needs write access; a read-only connection inherits
        // whatever the writer left behind.
        command.CommandText = (readOnly ? "" : "PRAGMA journal_mode=WAL;") + """
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-65536;
            PRAGMA busy_timeout=30000;
            PRAGMA foreign_keys=OFF;
            """;
        command.ExecuteNonQuery();
    }

    public static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();
    }

    /// <summary>The identity of the object an entry's content lives in: a pack and a blob may perfectly well share a
    /// ref (they are addressed in different namespaces), so the kind has to be part of the key or two unrelated objects
    /// would be downloaded as one group.</summary>
    internal static string StorageKey(StorageRef storage) =>
        storage.Kind == "pack" ? "pack:" + storage.Ref : "blob:" + storage.Ref;

    /// <summary>
    /// Folds a cursor that is <b>already ordered by storage object</b> (see
    /// <see cref="VersionCatalog.EntriesByStorageAsync"/>) into one list per object, by comparing each entry's key with
    /// the previous one. This is the streaming counterpart of <c>GroupBy</c>: a <c>GroupBy</c> cannot yield its first
    /// group until it has seen the last entry, so it has to hold the whole version in memory to answer — which is the
    /// one thing the catalog exists to stop the restore and the check from doing. Entries with no storage reference
    /// (an empty file, a symlink) are dropped: they belong to no download group, and SQLite sorts their NULL kind to
    /// the front, so they would otherwise arrive as one enormous leading "group".
    /// </summary>
    internal static async IAsyncEnumerable<List<IndexEntry>> GroupByStorageAsync(
        IAsyncEnumerable<IndexEntry> entries, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<IndexEntry>? group = null;
        string? key = null;
        await foreach (var entry in entries.WithCancellation(ct))
        {
            if (entry.Storage is null)
                continue;

            var entryKey = StorageKey(entry.Storage);
            if (group is null || !string.Equals(entryKey, key, StringComparison.Ordinal))
            {
                if (group is not null)
                    yield return group;
                group = [];
                key = entryKey;
            }

            group.Add(entry);
        }

        if (group is not null)
            yield return group;
    }

    /// <summary>UTF-16 big-endian bytes: their <c>memcmp</c> order is char-by-char order on the original string,
    /// which is exactly what <see cref="StringComparer.Ordinal"/> does. SQLite's default <c>BINARY</c> collation on
    /// a TEXT column compares UTF-8 bytes instead, and the two disagree the moment a surrogate pair appears (U+1F600
    /// is <c>F0 9F 98 80</c> in UTF-8 but <c>D83D DE00</c> in UTF-16, so UTF-8 sorts it after U+FFFF while ordinal
    /// sorts it before). The catalog's <c>entries.path_key</c> and the run's work database's <c>scan.path_key</c>
    /// both store this, and both order by it, so the diff's two cursors agree on what "next" means. Shared here
    /// rather than duplicated, because two independent implementations of "encode a path for ordering" is exactly
    /// the kind of drift that turns into a phantom deletion.</summary>
    internal static byte[] PathKey(string path) => Encoding.BigEndianUnicode.GetBytes(path);

    /// <summary>
    /// The secondary indexes are the whole point of the file: <c>entries_content</c> and <c>entries_head</c> answer
    /// dedup, <c>entries_parent</c> answers browsing, <c>entries_ref</c> answers retention and repair,
    /// <c>entries_seq</c> preserves the source order a serialized index has to be written back in, and
    /// <c>entries_path_key</c> is the order <see cref="VersionCatalog.EntriesAsync"/> streams in — see
    /// <see cref="PathKey"/> for why that is a BLOB column and not the <c>path</c> TEXT column's own collation.
    /// <c>WITHOUT ROWID</c> on the tables whose primary key is the whole row's identity saves the extra rowid index
    /// and stores entries clustered by (version, path), which is the order the diff walks.
    /// </summary>
    private const string Schema = $"""
        CREATE TABLE IF NOT EXISTS versions (
          version INTEGER PRIMARY KEY, identity INTEGER NOT NULL, entry_count INTEGER NOT NULL, imported_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS entries (
          version INTEGER NOT NULL, seq INTEGER NOT NULL, parent TEXT NOT NULL, path_fold TEXT NOT NULL,
          path_key BLOB NOT NULL, {EntryRowMapper.ColumnDefinitions}, unrecoverable INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS entries_seq      ON entries (version, seq);
        CREATE INDEX IF NOT EXISTS entries_parent   ON entries (version, parent);
        CREATE INDEX IF NOT EXISTS entries_fold     ON entries (version, path_fold);
        CREATE INDEX IF NOT EXISTS entries_path_key ON entries (version, path_key);
        CREATE INDEX IF NOT EXISTS entries_content  ON entries (full_hash, length);
        CREATE INDEX IF NOT EXISTS entries_ref      ON entries (storage_ref);
        CREATE INDEX IF NOT EXISTS entries_head     ON entries (length, head_hash);
        CREATE INDEX IF NOT EXISTS entries_storage  ON entries (version, storage_kind, storage_ref, seq);
        CREATE TABLE IF NOT EXISTS dirs (version INTEGER NOT NULL, path TEXT NOT NULL, parent TEXT NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS dirs_parent ON dirs (version, parent);
        CREATE TABLE IF NOT EXISTS empty_dirs (version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS unrecoverable (version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS import_issues (version INTEGER NOT NULL, path TEXT NOT NULL, issue TEXT NOT NULL, PRIMARY KEY (version, path, issue)) WITHOUT ROWID;
        """;
}

/// <summary>
/// The single definition of "an <see cref="IndexEntry"/> as a row": the shared column list, the parameter binding and
/// the reader. Both the catalog and the run's work database store these columns under these names, so a change to the
/// entry model lands in one place instead of silently affecting only one of the two.
/// </summary>
internal static class EntryRowMapper
{
    /// <summary>The entry's own columns, in one order, so <c>INSERT INTO … (Columns) VALUES (Parameters)</c> lines up
    /// and every <c>SELECT</c> that means to feed <see cref="Read"/> can just name this list.</summary>
    public const string Columns =
        "path, kind, length, mtime_ticks, mtime_offset, perms, head_hash, tail_hash, full_hash, target, " +
        "unreadable_ticks, unreadable_offset, storage_kind, storage_ref, entry_name, volumes, raw, volume_sizes";

    public const string Parameters =
        "@path, @kind, @length, @mtime_ticks, @mtime_offset, @perms, @head_hash, @tail_hash, @full_hash, @target, " +
        "@unreadable_ticks, @unreadable_offset, @storage_kind, @storage_ref, @entry_name, @volumes, @raw, @volume_sizes";

    /// <summary>The same columns with their declared types, for the <c>CREATE TABLE</c> side. Kept next to
    /// <see cref="Columns"/> and in the same order, so a table that stores entries cannot end up declaring a column
    /// the mapper does not know about, or missing one it does.</summary>
    public const string ColumnDefinitions =
        "path TEXT NOT NULL, kind TEXT NOT NULL, length INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL, " +
        "mtime_offset INTEGER NOT NULL, perms TEXT NOT NULL, head_hash TEXT, tail_hash TEXT, full_hash TEXT, " +
        "target TEXT, unreadable_ticks INTEGER, unreadable_offset INTEGER, storage_kind TEXT, storage_ref TEXT, " +
        "entry_name TEXT, volumes INTEGER NOT NULL DEFAULT 1, raw INTEGER NOT NULL DEFAULT 0, volume_sizes TEXT";

    /// <summary>
    /// The same three lists under a name prefix, for a table that has to hold a <em>second</em> entry per row — the
    /// run's draft keeps the previous version's entry next to the new one, so that an entry it could not read can be
    /// carried forward without going back to the catalog. Derived from the lists above rather than written out again:
    /// a hand-copied <c>prev_</c> list is a second definition of the same row, and the first column that only one of
    /// them learns about is a silent mismatch between what is written and what is read back.
    /// </summary>
    public static string PrefixedColumns(string prefix) =>
        string.Join(", ", Columns.Split(", ").Select(column => prefix + column));

    public static string PrefixedParameters(string prefix) =>
        string.Join(", ", Parameters.Split(", ").Select(parameter => "@" + prefix + parameter[1..]));

    /// <summary>The prefixed columns for the <c>CREATE TABLE</c> side, with every constraint dropped: the second
    /// entry is optional (a row whose path is new has no previous version at all), so the columns that are
    /// <c>NOT NULL</c> for an entry that exists have to be nullable for one that may not.</summary>
    public static string NullableColumnDefinitions(string prefix) =>
        string.Join(", ", ColumnDefinitions.Split(", ").Select(definition =>
        {
            var words = definition.Split(' ');   // "name TYPE [NOT NULL] [DEFAULT x]" → "prefix_name TYPE"
            return $"{prefix}{words[0]} {words[1]}";
        }));

    /// <summary>
    /// Binds <paramref name="entry"/> onto a command that may be reused for the next row: the parameter is created on
    /// the first call and only its value is replaced afterwards, which is what makes a million-entry import one
    /// prepared statement rather than a million.
    /// </summary>
    /// <param name="prefix">Names the parameters for <see cref="PrefixedParameters"/>'s column list; empty for the
    /// row's own entry.</param>
    public static void Bind(SqliteCommand command, IndexEntry entry, string prefix = "")
    {
        Set(command, $"@{prefix}path", entry.Path);
        Set(command, $"@{prefix}kind", entry.Kind);
        Set(command, $"@{prefix}length", entry.Length);
        // The mtime is split exactly the way the wire format splits it (UTC ticks + offset minutes) so that a version
        // imported from the cloud and serialized back out is byte-identical, down to the offset of a timestamp.
        Set(command, $"@{prefix}mtime_ticks", entry.Mtime.UtcTicks);
        Set(command, $"@{prefix}mtime_offset", (int)entry.Mtime.Offset.TotalMinutes);
        Set(command, $"@{prefix}perms", entry.Permissions);
        Set(command, $"@{prefix}head_hash", entry.HeadHash);
        Set(command, $"@{prefix}tail_hash", entry.TailHash);
        Set(command, $"@{prefix}full_hash", entry.FullHash);
        Set(command, $"@{prefix}target", entry.Target);
        Set(command, $"@{prefix}unreadable_ticks", entry.UnreadableAt?.UtcTicks);
        Set(command, $"@{prefix}unreadable_offset", entry.UnreadableAt is { } u ? (int)u.Offset.TotalMinutes : null);
        BindStorage(command, entry.Storage, prefix);
    }

    /// <summary>Binds an entry that may not exist: every parameter still has to be supplied, because
    /// <see cref="SqliteParameter"/> fails a statement whose parameter was never set rather than treating it as
    /// null. Only the prefixed (second) entry can be absent, which is why there is no unprefixed overload.</summary>
    public static void BindOptional(SqliteCommand command, IndexEntry? entry, string prefix)
    {
        if (entry is not null)
        {
            Bind(command, entry, prefix);
            return;
        }

        foreach (var column in Columns.Split(", "))
            Set(command, $"@{prefix}{column}", null);
    }

    /// <summary>Binds just the storage columns; repair patches one entry's storage without touching the rest of the row.</summary>
    public static void BindStorage(SqliteCommand command, StorageRef? storage, string prefix = "")
    {
        Set(command, $"@{prefix}storage_kind", storage?.Kind);
        Set(command, $"@{prefix}storage_ref", storage?.Ref);
        Set(command, $"@{prefix}entry_name", storage?.EntryName);
        Set(command, $"@{prefix}volumes", storage?.Volumes ?? 1);
        Set(command, $"@{prefix}raw", storage?.Raw == true ? 1 : 0);
        Set(command, $"@{prefix}volume_sizes", FormatVolumeSizes(storage?.VolumeSizes));
    }

    /// <summary>The write side of <see cref="ParseVolumeSizes"/>: a variable-length list as one comma-joined column,
    /// which keeps it out of the schema without a second table nothing else would ever join to. Shared, because the
    /// run's work database stores reservations and resume records with the same column.</summary>
    public static string? FormatVolumeSizes(IReadOnlyList<long>? sizes) =>
        sizes is { Count: > 0 }
            ? string.Join(',', sizes.Select(v => v.ToString(CultureInfo.InvariantCulture)))
            : null;

    /// <param name="prefix">The name prefix the columns were selected under; empty for the row's own entry, and
    /// <see cref="PrefixedColumns"/>'s prefix for a second entry stored beside it.</param>
    public static IndexEntry Read(SqliteDataReader reader, string prefix = "") => new()
    {
        Path = reader.GetString(reader.GetOrdinal($"{prefix}path")),
        Kind = reader.GetString(reader.GetOrdinal($"{prefix}kind")),
        Length = reader.GetInt64(reader.GetOrdinal($"{prefix}length")),
        Mtime = Dto(
            reader.GetInt64(reader.GetOrdinal($"{prefix}mtime_ticks")),
            reader.GetInt32(reader.GetOrdinal($"{prefix}mtime_offset"))),
        Permissions = reader.GetString(reader.GetOrdinal($"{prefix}perms")),
        HeadHash = NullableString(reader, $"{prefix}head_hash"),
        TailHash = NullableString(reader, $"{prefix}tail_hash"),
        FullHash = NullableString(reader, $"{prefix}full_hash"),
        Target = NullableString(reader, $"{prefix}target"),
        UnreadableAt = NullableLong(reader, $"{prefix}unreadable_ticks") is { } ticks
            ? Dto(ticks, (int)(NullableLong(reader, $"{prefix}unreadable_offset") ?? 0))
            : null,
        Storage = ReadStorage(reader, prefix),
    };

    /// <summary>Reads just the storage columns. Split out of <see cref="Read"/> because the run's draft asks "where
    /// did this run put that path" without wanting the rest of the row, and two readers of the same six columns is
    /// the kind of duplication this class exists to avoid.</summary>
    public static StorageRef? ReadStorage(SqliteDataReader reader, string prefix = "") =>
        NullableString(reader, $"{prefix}storage_kind") is { } kind
            ? new StorageRef
            {
                Kind = kind,
                Ref = NullableString(reader, $"{prefix}storage_ref") ?? "",
                EntryName = NullableString(reader, $"{prefix}entry_name"),
                Volumes = (int)(NullableLong(reader, $"{prefix}volumes") ?? 1),
                Raw = (NullableLong(reader, $"{prefix}raw") ?? 0) != 0,
                VolumeSizes = [.. ParseVolumeSizes(NullableString(reader, $"{prefix}volume_sizes"))],
            }
            : null;

    /// <summary>Reads back what <see cref="BindStorage"/> stored: the volume sizes as one comma-joined column, which
    /// keeps a variable-length list out of the schema without a second table nothing else would ever join to.</summary>
    public static IReadOnlyList<long> ParseVolumeSizes(string? stored) =>
        stored is { Length: > 0 }
            ? [.. stored.Split(',').Select(v => long.Parse(v, CultureInfo.InvariantCulture))]
            : [];

    /// <summary>Sets a parameter, adding it the first time. <see cref="DBNull"/> rather than null, because
    /// <see cref="SqliteParameter"/> treats a null <c>Value</c> as "not supplied" and fails the statement.</summary>
    public static void Set(SqliteCommand command, string name, object? value)
    {
        if (command.Parameters.Contains(name))
            command.Parameters[name].Value = value ?? DBNull.Value;
        else
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    /// <summary>Reassembles the <see cref="DateTimeOffset"/> exactly as the index wire format does, so the two agree
    /// on what a stored (ticks, offset) pair means.</summary>
    public static DateTimeOffset Dto(long utcTicks, int offsetMinutes)
    {
        var offset = TimeSpan.FromMinutes(offsetMinutes);
        return new DateTimeOffset(utcTicks + offset.Ticks, offset);
    }

    public static string? NullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static long? NullableLong(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }
}
