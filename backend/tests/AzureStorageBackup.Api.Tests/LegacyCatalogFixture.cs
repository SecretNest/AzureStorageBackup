using System.Globalization;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Tests;

/// <summary>Writes a catalog in the format-1 layout (one row per version per path, <c>seq</c>, <c>WITHOUT ROWID</c>
/// keyed by (version, path)) exactly as 2026.9.13.1 wrote it, so the conversion has a real file to convert.</summary>
internal static class LegacyCatalogFixture
{
    private const string Schema = $"""
        PRAGMA journal_mode=WAL;
        CREATE TABLE versions (version INTEGER PRIMARY KEY, identity INTEGER NOT NULL, entry_count INTEGER NOT NULL, imported_at TEXT NOT NULL);
        CREATE TABLE entries (version INTEGER NOT NULL, seq INTEGER NOT NULL, parent TEXT NOT NULL, path_fold TEXT NOT NULL,
          path_key BLOB NOT NULL, {EntryRowMapper.ColumnDefinitions}, unrecoverable INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE INDEX entries_seq ON entries (version, seq);
        CREATE INDEX entries_parent ON entries (version, parent);
        CREATE INDEX entries_fold ON entries (version, path_fold);
        CREATE INDEX entries_path_key ON entries (version, path_key);
        CREATE INDEX entries_storage ON entries (version, storage_kind, storage_ref, seq);
        CREATE INDEX entries_content ON entries (full_hash, length);
        CREATE INDEX entries_ref ON entries (storage_ref);
        CREATE INDEX entries_head ON entries (length, head_hash);
        CREATE TABLE dirs (version INTEGER NOT NULL, path TEXT NOT NULL, parent TEXT NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE INDEX dirs_parent ON dirs (version, parent);
        CREATE TABLE empty_dirs (version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE TABLE unrecoverable (version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE TABLE import_issues (version INTEGER NOT NULL, path TEXT NOT NULL, issue TEXT NOT NULL, PRIMARY KEY (version, path, issue)) WITHOUT ROWID;
        """;

    /// <summary>Each index's entries are inserted in the order given — the fixture deliberately does not sort, so a
    /// test can hand it a version whose seq order is not path order.</summary>
    public static async Task WriteAsync(string path, IReadOnlyList<VersionIndex> versions, long identity = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = new SqliteConnection(CatalogSql.ConnectionString(path, readOnly: false));
        await connection.OpenAsync();
        using (var schema = connection.CreateCommand()) { schema.CommandText = Schema; schema.ExecuteNonQuery(); }

        foreach (var index in versions)
        {
            await using var tx = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = $"INSERT INTO entries (version, seq, parent, path_fold, path_key, {EntryRowMapper.Columns}, unrecoverable) " +
                                 $"VALUES (@version, @seq, @parent, @path_fold, @path_key, {EntryRowMapper.Parameters}, @unrecoverable)";
            using var dir = connection.CreateCommand();
            dir.Transaction = tx;
            dir.CommandText = "INSERT OR IGNORE INTO dirs (version, path, parent) VALUES (@v, @path, @parent)";
            var seq = 0;
            foreach (var e in index.Entries)
            {
                EntryRowMapper.Set(insert, "@version", index.Version);
                EntryRowMapper.Set(insert, "@seq", seq++);
                EntryRowMapper.Set(insert, "@parent", VersionCatalog.ParentOf(e.Path));
                EntryRowMapper.Set(insert, "@path_fold", e.Path.ToUpperInvariant());
                EntryRowMapper.Set(insert, "@path_key", CatalogSql.PathKey(e.Path));
                EntryRowMapper.Set(insert, "@unrecoverable", index.UnrecoverablePaths.Contains(e.Path) ? 1 : 0);
                EntryRowMapper.Bind(insert, e);
                insert.ExecuteNonQuery();
                for (var slash = e.Path.IndexOf('/'); slash > 0; slash = e.Path.IndexOf('/', slash + 1))
                {
                    var d = e.Path[..slash];
                    EntryRowMapper.Set(dir, "@v", index.Version); EntryRowMapper.Set(dir, "@path", d); EntryRowMapper.Set(dir, "@parent", VersionCatalog.ParentOf(d));
                    dir.ExecuteNonQuery();
                }
            }
            using var lists = connection.CreateCommand();
            lists.Transaction = tx;
            for (var i = 0; i < index.EmptyDirs.Count; i++)
            {
                lists.CommandText = "INSERT INTO empty_dirs (version, path, seq) VALUES (@v, @p, @s)";
                lists.Parameters.Clear(); lists.Parameters.AddWithValue("@v", index.Version); lists.Parameters.AddWithValue("@p", index.EmptyDirs[i]); lists.Parameters.AddWithValue("@s", i);
                lists.ExecuteNonQuery();
                // the empty dir is a browsable node
                EntryRowMapper.Set(dir, "@v", index.Version); EntryRowMapper.Set(dir, "@path", index.EmptyDirs[i]); EntryRowMapper.Set(dir, "@parent", VersionCatalog.ParentOf(index.EmptyDirs[i]));
                dir.ExecuteNonQuery();
            }
            for (var i = 0; i < index.UnrecoverablePaths.Count; i++)
            {
                lists.CommandText = "INSERT INTO unrecoverable (version, path, seq) VALUES (@v, @p, @s)";
                lists.Parameters.Clear(); lists.Parameters.AddWithValue("@v", index.Version); lists.Parameters.AddWithValue("@p", index.UnrecoverablePaths[i]); lists.Parameters.AddWithValue("@s", i);
                lists.ExecuteNonQuery();
            }
            lists.CommandText = "INSERT INTO versions (version, identity, entry_count, imported_at) VALUES (@v, @i, @c, @at)";
            lists.Parameters.Clear();
            lists.Parameters.AddWithValue("@v", index.Version); lists.Parameters.AddWithValue("@i", identity);
            lists.Parameters.AddWithValue("@c", index.Entries.Count); lists.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            lists.ExecuteNonQuery();
            await tx.CommitAsync();
        }
    }
}
