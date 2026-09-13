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
    private const string SelectNextPresentSql = "SELECT MIN(version) FROM versions WHERE version > @v";
    private const string SelectMaxPresentSql = "SELECT MAX(version) FROM versions";

    private const string UpsertVersionSql = """
        INSERT INTO versions (version, identity, entry_count, imported_at) VALUES (@v, @identity, @count, @at)
          ON CONFLICT (version) DO UPDATE SET identity=@identity, entry_count=@count, imported_at=@at
        """;

    private const string DeleteVersionSql = "DELETE FROM versions WHERE version=@v";

    /// <summary>A row is reachable when some retained version lies in its interval. After a version is dropped, the
    /// rows nothing reaches go. Only rows that end at or before the newest retained version, or start after it, can
    /// be unreachable: a row spanning the newest version is reached by it. The two disjuncts are separate
    /// statements so each can use an index.</summary>
    private const string DeleteUnreachableEntriesSql = """
        DELETE FROM entries WHERE version_to <= @max
          AND NOT EXISTS (SELECT 1 FROM versions v WHERE v.version >= entries.version_from AND v.version < entries.version_to);
        DELETE FROM entries WHERE version_from > @max;
        DELETE FROM dirs WHERE version_to <= @max
          AND NOT EXISTS (SELECT 1 FROM versions v WHERE v.version >= dirs.version_from AND v.version < dirs.version_to);
        DELETE FROM dirs WHERE version_from > @max;
        """;

    private const string DeletePerVersionRowsSql = """
        DELETE FROM empty_dirs WHERE version=@v;
        DELETE FROM unrecoverable WHERE version=@v;
        DELETE FROM import_issues WHERE version=@v;
        DELETE FROM entry_order WHERE version=@v;
        """;

    private const string InsertEmptyDirSql = "INSERT OR IGNORE INTO empty_dirs (version, path, seq) VALUES (@v, @path, @seq)";
    private const string InsertIssueSql = "INSERT OR IGNORE INTO import_issues (version, path, issue) VALUES (@v, @path, @issue)";
    private const string InsertUnrecoverableListSql = "INSERT OR IGNORE INTO unrecoverable (version, path, seq) VALUES (@v, @path, @seq)";

    /// <summary>Appends at the end of the version's list, reproducing the <c>List.Add</c> order the check and repair
    /// flows write their unrecoverable paths in — that order is part of the index's bytes.</summary>
    private const string AppendUnrecoverableListSql = """
        INSERT OR IGNORE INTO unrecoverable (version, path, seq)
          VALUES (@v, @path, (SELECT COALESCE(MAX(seq), -1) + 1 FROM unrecoverable WHERE version=@v))
        """;
    private const string RemoveUnrecoverableListSql = "DELETE FROM unrecoverable WHERE version=@v AND path=@path";

    private const string SelectCoveringRowSql =
        "SELECT id, version_from, version_to FROM entries WHERE path=@path AND version_from <= @v AND version_to > @v";
    private const string ShrinkRowToVersionSql = "UPDATE entries SET version_from=@from, version_to=@to WHERE id=@id";
    private const string CopyRowSql = $"""
        INSERT INTO entries (version_from, version_to, parent, path_fold, path_key, {EntryRowMapper.Columns}, unrecoverable)
        SELECT @from, @to, parent, path_fold, path_key, {EntryRowMapper.Columns}, unrecoverable FROM entries WHERE id=@id
        """;
    private const string PatchUnreadableSql =
        "UPDATE entries SET unreadable_ticks=@unreadable_ticks, unreadable_offset=@unreadable_offset WHERE id=@id";
    private const string PatchStorageSql =
        "UPDATE entries SET storage_kind=@storage_kind, storage_ref=@storage_ref, entry_name=@entry_name, " +
        "volumes=@volumes, raw=@raw, volume_sizes=@volume_sizes WHERE id=@id";
    private const string PatchUnrecoverableFlagSql = "UPDATE entries SET unrecoverable=@flag WHERE id=@id";

    private const string SelectEmptyDirsSql = "SELECT path FROM empty_dirs WHERE version=@v ORDER BY seq";
    private const string SelectUnrecoverableSql = "SELECT path FROM unrecoverable WHERE version=@v ORDER BY seq";
    private const string HasEntryOrderSql = "SELECT EXISTS (SELECT 1 FROM entry_order WHERE version=@v)";

    /// <summary>The columns are named <c>e.</c> because <c>entry_order</c> carries a <c>path</c> too and an
    /// unqualified one would be ambiguous; SQLite still reports each result column under its bare name, which is what
    /// <see cref="EntryRowMapper.Read"/> looks for.</summary>
    private static readonly string SelectEntriesByOrderSql = $"""
        SELECT {EntryRowMapper.PrefixedColumns("e.")} FROM entry_order o
        JOIN entries e ON e.path = o.path AND e.version_from <= @v AND e.version_to > @v
        WHERE o.version=@v ORDER BY o.seq
        """;

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
    /// the schema, so a reader can never resurrect a catalog somebody just deleted.
    /// <para>
    /// Does <b>not</b> run <c>PRAGMA quick_check</c> — that is <see cref="QuickCheckAsync"/>, a separate call the
    /// caller opts into, because it is a full scan of the file and this method needs to stay cheap enough to call on
    /// every open. <see cref="VersionCatalogStore"/> is the one caller that needs it, and only wants to pay for it
    /// once per path per process (a real catalog can run to gigabytes), not on every open of an already-known-good
    /// file.
    /// </para>
    /// </summary>
    public static async Task<VersionCatalog> OpenAsync(string path, bool readOnly, CancellationToken ct)
    {
        var connection = new SqliteConnection(CatalogSql.ConnectionString(path, readOnly));
        try
        {
            await connection.OpenAsync(ct);
            CatalogSql.ApplyPragmas(connection, readOnly);
            var legacy = CatalogSql.FormatOf(connection) < CatalogSql.Format
                && (CatalogSql.HasTable(connection, "v1_entries") || CatalogSql.HasTable(connection, "entries"));
            if (legacy && readOnly)
                throw new CatalogFormatException(path);
            if (!readOnly && !legacy)
            {
                CatalogSql.EnsureSchema(connection);
                CatalogSql.MarkCurrent(connection);
            }

            return new VersionCatalog(path, connection) { NeedsUpgrade = legacy };
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>True when the file is format 1 (or a conversion was interrupted): the schema has not been applied
    /// and every query would fail. <see cref="VersionCatalogStore"/> runs the conversion before handing such a
    /// catalog out; nothing else opens one.</summary>
    public bool NeedsUpgrade { get; private init; }

    /// <summary>
    /// Takes the content-keyed indexes (<see cref="CatalogSql.GlobalIndexNames"/>) down ahead of a bulk import, so
    /// that the versions about to be imported are not inserted, row by row, at random into three B-trees that span
    /// the whole history. <see cref="RebuildGlobalIndexesAsync"/> is the other half; if it never runs (a stop, a
    /// crash), the next write open's schema pass rebuilds them, and until then queries are slower, never wrong.
    /// </summary>
    public async Task DropGlobalIndexesAsync(CancellationToken ct)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = CatalogSql.DropGlobalIndexesSql;
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Recreates whatever <see cref="DropGlobalIndexesAsync"/> took down: one sort and one sequential write
    /// per index, in place of a random read per row per index.</summary>
    public Task RebuildGlobalIndexesAsync(CancellationToken ct) => OffThePoolAsync(async () =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = CatalogSql.GlobalIndexSchema;
        await command.ExecuteNonQueryAsync(ct);
    }, ct);

    /// <summary>
    /// Runs one of the catalog's long operations on a dedicated thread. Microsoft.Data.Sqlite is synchronous
    /// underneath — its <c>…Async</c> methods complete on the calling thread — so a million-row import, a
    /// <c>CREATE INDEX</c> over the whole history or a full-file <c>quick_check</c> holds whatever thread it started on
    /// for minutes. On a thread-pool thread that is one worker gone for the duration, next to the upload workers'
    /// own blocking work; Kestrel logged thread-pool starvation for the whole of the 2026-09-13 run. A long-running
    /// task gets its own thread from the start, and since nothing inside truly yields, it stays there to the end.
    /// </summary>
    private static Task OffThePoolAsync(Func<Task> work, CancellationToken ct) =>
        Task.Factory.StartNew(work, ct, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();

    /// <summary>
    /// The version size, as a fraction of the history already in the catalog, from which one version's import is
    /// cheaper bracketed by <see cref="DropGlobalIndexesAsync"/> and <see cref="RebuildGlobalIndexesAsync"/> than
    /// inserted into the live content-keyed indexes. A hundredth. The rebuild sorts every row of the history once
    /// per index — a few microseconds a row, sequential; the inserts read a random page per row per index, and
    /// once the history outgrows the cache a random page on a NAS is a millisecond or more. The ratio between
    /// those two per-row costs is where the trade turns, and a hundred leaves the small daily version of a big
    /// history on the live-index side while the version that is a real share of it takes the sort.
    /// <para>
    /// Field, 2026-09-12: with the indexes kept live for every single version, a version of a few million rows
    /// into an 8 GB catalog read 1.2 GB/min from disk for over an hour, 40% of one core, 91 MB of memory — the
    /// migration's read amplification (<see cref="CatalogSql.GlobalIndexNames"/>) on the routine path.
    /// </para>
    /// </summary>
    internal const int BulkImportRatio = 100;

    /// <summary>Whether one version's import should take the content-keyed indexes down — see <see cref="BulkImportRatio"/>.
    /// Nothing to insert brackets nothing; the first version of an empty history rebuilds three empty trees, which
    /// costs nothing and keeps the decision a single rule.</summary>
    internal static bool PrefersRebuild(long versionRows, long historyRows) =>
        versionRows > 0 && versionRows * BulkImportRatio >= historyRows;

    /// <summary>The rows the catalog holds, as the sum of every version's declared count: what
    /// <see cref="PrefersRebuild"/> measures a version against. Read off the versions table, not counted over
    /// entries — on a catalog of gigabytes the question has to be free.</summary>
    public async Task<long> HistoryRowsAsync(CancellationToken ct)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(entry_count), 0) FROM versions";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>How many of <see cref="CatalogSql.GlobalIndexNames"/> the file currently has — all of them on a
    /// healthy catalog, none in the middle of a bulk import.</summary>
    internal async Task<int> GlobalIndexCountAsync(CancellationToken ct)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name IN ("
            + string.Join(", ", CatalogSql.GlobalIndexNames.Select(name => $"'{name}'")) + ")";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Runs <c>PRAGMA quick_check</c> and turns anything other than a single "ok" row into the same
    /// <see cref="SqliteException"/> shape SQLite itself raises for a corrupt file (<c>SQLITE_CORRUPT</c>), so
    /// <see cref="VersionCatalogStore"/> has one error code to catch regardless of which check caught the damage —
    /// whether that is <see cref="CatalogSql.ApplyPragmas"/>'s own statements refusing a file that is not a database
    /// at all (<c>SQLITE_NOTADB</c>, raised earlier, inside <see cref="OpenAsync"/> itself), or a page deep inside an
    /// otherwise well-formed file that only this full scan would find. Public, not run automatically by
    /// <see cref="OpenAsync"/>, so the caller controls when the scan happens.
    /// </summary>
    public Task QuickCheckAsync(CancellationToken ct) => OffThePoolAsync(() => QuickCheckCoreAsync(ct), ct);

    private async Task QuickCheckCoreAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Microsoft.Data.Sqlite only looks at the token before it starts a statement, and its Cancel() is a no-op — so
        // on its own a Stop pressed during the check would wait for the whole read of the file. sqlite3_interrupt is
        // the engine's own way out: the running statement returns SQLITE_INTERRUPT at its next step, which is
        // reported below as the cancellation the caller asked for.
        var handle = _connection.Handle;
        using var interrupt = ct.Register(() => { if (handle is not null) SQLitePCL.raw.sqlite3_interrupt(handle); });
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
            // Only the first row is read, and that is the whole answer: quick_check returns exactly one row, the single
            // string "ok", for a healthy file, and one row per problem otherwise — so a first row that is not "ok" (or
            // no row at all) already means damage, and the rest of the rows would only be more detail about it.
            var ok = await reader.ReadAsync(ct) && reader.GetString(0) == "ok";
            if (!ok)
                throw new SqliteException("Catalog failed PRAGMA quick_check.", 11 /* SQLITE_CORRUPT */);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 9 /* SQLITE_INTERRUPT */ && ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("The catalog check was interrupted.", ex, ct);
        }
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

    /// <summary>
    /// Writes the version back out in the frozen cloud format (§3.2), entry by entry in the order the index was read
    /// in, so a version imported from the cloud and written back is the same bytes.
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
        // Path order is the order an index has been written in since the M4 diff; only a version whose entries
        // arrived in some other order carries an entry_order table, and then that table is the authority.
        var ordered = await ExistsAsync(HasEntryOrderSql, ct, ("@v", version));
        await foreach (var entry in QueryEntriesAsync(ordered ? SelectEntriesByOrderSql : SelectEntriesByPathSql, version, ct))
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
            using var flag = Command(PatchUnrecoverableFlagSql);
            using var append = Command(AppendUnrecoverableListSql);
            using var remove = Command(RemoveUnrecoverableListSql);
            foreach (var patch in patches)
            {
                ct.ThrowIfCancellationRequested();
                // The row that covers (version, path) may span other versions; the patch must not leak into them.
                // A path the version does not have gets no row and no patch, as the old UPDATE … WHERE affected none.
                var id = await IsolateAsync(patch.Version, patch.Path, ct);
                if (id is null)
                    continue;

                if (patch.UnreadableAt is { } at)
                {
                    Set(unreadable, "@id", id);
                    Set(unreadable, "@unreadable_ticks", at.UtcTicks);
                    Set(unreadable, "@unreadable_offset", (int)at.Offset.TotalMinutes);
                    await unreadable.ExecuteNonQueryAsync(ct);
                }

                if (patch.Storage is not null)
                {
                    Set(storage, "@id", id);
                    EntryRowMapper.BindStorage(storage, patch.Storage);
                    await storage.ExecuteNonQueryAsync(ct);
                }

                if (patch.Unrecoverable is { } on)
                {
                    Set(flag, "@id", id);
                    Set(flag, "@flag", on ? 1 : 0);
                    await flag.ExecuteNonQueryAsync(ct);
                    var list = on ? append : remove;
                    Set(list, "@v", patch.Version);
                    Set(list, "@path", patch.Path);
                    await list.ExecuteNonQueryAsync(ct);
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

    /// <summary>
    /// The row that covers (<paramref name="version"/>, <paramref name="path"/>), narrowed to that one version: a row
    /// <c>[a, b)</c> with <c>a &lt; version</c> or <c>b &gt; next</c> — <c>next</c> being the retained version after
    /// this one, or <see cref="CatalogSql.OpenEnd"/> — is split into up to three. The outer pieces keep the old values
    /// under new ids, the original becomes <c>[version, next)</c> and is the one returned. The split lands on version
    /// boundaries rather than on <c>version + 1</c> so that no piece covers a version that does not exist: such a
    /// piece is reachable by nothing, survives retention (which only deletes what no version reaches) and would still
    /// answer the content lookups, which carry no version predicate. Splits are never merged back; a repaired path
    /// gains at most two rows per patch. Null when the version has no entry at the path. The original is shrunk
    /// before the copies are inserted, or the copy that keeps <c>version_from = a</c> would collide with it on the
    /// unique <c>(path, version_from)</c>.
    /// </summary>
    private async Task<long?> IsolateAsync(int version, string path, CancellationToken ct)
    {
        long id; int from, to;
        using (var find = Command(SelectCoveringRowSql))
        {
            Set(find, "@path", path);
            Set(find, "@v", version);
            await using var reader = (SqliteDataReader)await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            id = reader.GetInt64(0);
            from = reader.GetInt32(1);
            to = reader.GetInt32(2);
        }

        var next = await NextPresentAsync(version, ct) ?? CatalogSql.OpenEnd;
        if (from == version && to == next)
            return id;

        using (var shrink = Command(ShrinkRowToVersionSql))
        {
            Set(shrink, "@id", id);
            Set(shrink, "@from", version);
            Set(shrink, "@to", next);
            await shrink.ExecuteNonQueryAsync(ct);
        }

        using var copy = Command(CopyRowSql);
        Set(copy, "@id", id);
        if (from < version)
        {
            Set(copy, "@from", from);
            Set(copy, "@to", version);
            await copy.ExecuteNonQueryAsync(ct);
        }
        if (to > next)
        {
            Set(copy, "@from", next);
            Set(copy, "@to", to);
            await copy.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    /// <summary>The retained version after <paramref name="version"/>, or null when it is the newest: where a row
    /// started at <paramref name="version"/> ends when the version stops describing the path.</summary>
    internal async Task<int?> NextPresentAsync(int version, CancellationToken ct)
    {
        using var command = Command(SelectNextPresentSql);
        Set(command, "@v", version);
        return await command.ExecuteScalarAsync(ct) is long next ? (int)next : null;
    }

    /// <summary>The newest retained version, or -1 when none is left — the bound
    /// <see cref="DeleteUnreachableEntriesSql"/> judges reachability against.</summary>
    private async Task<int> MaxPresentAsync(CancellationToken ct)
    {
        using var command = Command(SelectMaxPresentSql);
        return await command.ExecuteScalarAsync(ct) is long max ? (int)max : -1;
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
}

/// <summary>A read-only open met a format-1 catalog. Readers cannot convert (they hold no write lock), so the
/// store answers this by taking the lock, converting, and opening again.</summary>
public sealed class CatalogFormatException(string path)
    : IOException($"Catalog '{path}' is in an older format and has to be upgraded by a write open first.")
{
    public string Path { get; } = path;
}
