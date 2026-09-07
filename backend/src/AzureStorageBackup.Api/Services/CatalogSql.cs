using System.Globalization;
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
    public static string ConnectionString(string path, bool readOnly) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
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

    /// <summary>
    /// The secondary indexes are the whole point of the file: <c>entries_content</c> and <c>entries_head</c> answer
    /// dedup, <c>entries_parent</c> answers browsing, <c>entries_ref</c> answers retention and repair, and
    /// <c>entries_seq</c> preserves the source order a serialized index has to be written back in. <c>WITHOUT
    /// ROWID</c> on the tables whose primary key is the whole row's identity saves the extra rowid index and stores
    /// entries clustered by (version, path), which is the order the diff walks.
    /// </summary>
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS versions (
          version INTEGER PRIMARY KEY, identity INTEGER NOT NULL, entry_count INTEGER NOT NULL, imported_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS entries (
          version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, parent TEXT NOT NULL, path_fold TEXT NOT NULL,
          kind TEXT NOT NULL, length INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL, mtime_offset INTEGER NOT NULL, perms TEXT NOT NULL,
          head_hash TEXT, tail_hash TEXT, full_hash TEXT, target TEXT, unreadable_ticks INTEGER, unreadable_offset INTEGER,
          storage_kind TEXT, storage_ref TEXT, entry_name TEXT, volumes INTEGER NOT NULL DEFAULT 1, raw INTEGER NOT NULL DEFAULT 0,
          volume_sizes TEXT, unrecoverable INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS entries_seq     ON entries (version, seq);
        CREATE INDEX IF NOT EXISTS entries_parent  ON entries (version, parent);
        CREATE INDEX IF NOT EXISTS entries_fold    ON entries (version, path_fold);
        CREATE INDEX IF NOT EXISTS entries_content ON entries (full_hash, length);
        CREATE INDEX IF NOT EXISTS entries_ref     ON entries (storage_ref);
        CREATE INDEX IF NOT EXISTS entries_head    ON entries (length, head_hash);
        CREATE INDEX IF NOT EXISTS entries_storage ON entries (version, storage_kind, storage_ref, seq);
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

    /// <summary>
    /// Binds <paramref name="entry"/> onto a command that may be reused for the next row: the parameter is created on
    /// the first call and only its value is replaced afterwards, which is what makes a million-entry import one
    /// prepared statement rather than a million.
    /// </summary>
    public static void Bind(SqliteCommand command, IndexEntry entry)
    {
        Set(command, "@path", entry.Path);
        Set(command, "@kind", entry.Kind);
        Set(command, "@length", entry.Length);
        // The mtime is split exactly the way the wire format splits it (UTC ticks + offset minutes) so that a version
        // imported from the cloud and serialized back out is byte-identical, down to the offset of a timestamp.
        Set(command, "@mtime_ticks", entry.Mtime.UtcTicks);
        Set(command, "@mtime_offset", (int)entry.Mtime.Offset.TotalMinutes);
        Set(command, "@perms", entry.Permissions);
        Set(command, "@head_hash", entry.HeadHash);
        Set(command, "@tail_hash", entry.TailHash);
        Set(command, "@full_hash", entry.FullHash);
        Set(command, "@target", entry.Target);
        Set(command, "@unreadable_ticks", entry.UnreadableAt?.UtcTicks);
        Set(command, "@unreadable_offset", entry.UnreadableAt is { } u ? (int)u.Offset.TotalMinutes : null);
        BindStorage(command, entry.Storage);
    }

    /// <summary>Binds just the storage columns; repair patches one entry's storage without touching the rest of the row.</summary>
    public static void BindStorage(SqliteCommand command, StorageRef? storage)
    {
        Set(command, "@storage_kind", storage?.Kind);
        Set(command, "@storage_ref", storage?.Ref);
        Set(command, "@entry_name", storage?.EntryName);
        Set(command, "@volumes", storage?.Volumes ?? 1);
        Set(command, "@raw", storage?.Raw == true ? 1 : 0);
        Set(command, "@volume_sizes", storage is { VolumeSizes.Count: > 0 }
            ? string.Join(',', storage.VolumeSizes.Select(v => v.ToString(CultureInfo.InvariantCulture)))
            : null);
    }

    public static IndexEntry Read(SqliteDataReader reader) => new()
    {
        Path = reader.GetString(reader.GetOrdinal("path")),
        Kind = reader.GetString(reader.GetOrdinal("kind")),
        Length = reader.GetInt64(reader.GetOrdinal("length")),
        Mtime = Dto(reader.GetInt64(reader.GetOrdinal("mtime_ticks")), reader.GetInt32(reader.GetOrdinal("mtime_offset"))),
        Permissions = reader.GetString(reader.GetOrdinal("perms")),
        HeadHash = NullableString(reader, "head_hash"),
        TailHash = NullableString(reader, "tail_hash"),
        FullHash = NullableString(reader, "full_hash"),
        Target = NullableString(reader, "target"),
        UnreadableAt = NullableLong(reader, "unreadable_ticks") is { } ticks
            ? Dto(ticks, (int)(NullableLong(reader, "unreadable_offset") ?? 0))
            : null,
        Storage = NullableString(reader, "storage_kind") is { } kind
            ? new StorageRef
            {
                Kind = kind,
                Ref = NullableString(reader, "storage_ref") ?? "",
                EntryName = NullableString(reader, "entry_name"),
                Volumes = (int)(NullableLong(reader, "volumes") ?? 1),
                Raw = (NullableLong(reader, "raw") ?? 0) != 0,
                VolumeSizes = [.. ParseVolumeSizes(NullableString(reader, "volume_sizes"))],
            }
            : null,
    };

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
