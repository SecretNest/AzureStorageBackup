using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>One scanned entry as the run's work database holds it: the metadata the scan produced, plus the grouping
/// verdict, so the pipeline never has to re-classify a path it has already seen.</summary>
public sealed record ScanRow(
    string Path, EntryKind Kind, long Length, DateTimeOffset ModifiedAt, string Permissions, string? Target,
    FileCategory Category, string? GroupKey);

/// <summary>How far one draft entry has got. <c>Confirmed</c> is the only state that counts towards the run's
/// files/bytes; <c>Unreadable</c> carries a reason and keeps the previous version's content; <c>Dropped</c> is a row
/// the run decided not to write into the new index at all.</summary>
public enum DraftState
{
    Pending = 0,
    Confirmed = 1,
    Unreadable = 2,
    Dropped = 3,
}

/// <summary>One row of the version being built. <see cref="Seq"/> is the source order the index must be serialized
/// back in, which is why the draft is read by <c>seq</c> and not by path.</summary>
public sealed record DraftRow(int Seq, string Path, DraftState State, IndexEntry Entry);

/// <summary>
/// The same row with everything the final entry is decided from: the diff's verdict and the entry it produced, the
/// previous version's entry (null when the path is new), and what the run recorded afterwards — the settled identity
/// of a file that changed while it was being processed, the tail hash the compression pass produced, and the reason
/// a path that was readable at diff time could not be reopened.
/// <para>
/// <see cref="HasCurrent"/> is not <c>Entry is not null</c>: every row has entry columns, because they cannot be
/// null in the table, but a change under a directory that could not be listed never had a current entry at all and
/// its columns stand in for one. Only <see cref="RunLedger.FinalEntriesAsync"/> knows the difference matters.
/// </para>
/// </summary>
public sealed record DraftFullRow(
    int Seq, string Path, DraftState State, ChangeKind Kind, bool HasCurrent, IndexEntry Entry,
    IndexEntry? Previous, EntryOverride? Override, string? TailSet, string? PostDiffReason);

/// <summary>A block of content this run has claimed an address for: what a later file with the same content must be
/// pointed at instead of uploading it a second time.</summary>
public sealed record ReservationRow(string Ref, bool Raw, int Volumes, IReadOnlyList<long> VolumeSizes);

/// <summary>
/// The scratch database of one backup run: the scan, the draft of the new version, the dedup reservations this run
/// has claimed, and the resume records read back out of the journal. Everything in here grows with the file count,
/// and everything in here dies with the run — <see cref="DisposeAsync"/> deletes the file.
/// <para>
/// <b>Ordinal path order is a <c>path_key BLOB</c>, not the text collation.</b> The diff merges this database's scan
/// cursor against the catalog's entry cursor and compares the two heads with <c>string.CompareOrdinal</c>, so both
/// cursors must be in UTF-16 ordinal order. SQLite's default <c>BINARY</c> collation on a TEXT column is UTF-8 byte
/// order, and the two disagree the moment a surrogate pair appears: U+1F600 is <c>F0 9F 98 80</c> in UTF-8 and
/// <c>D83D DE00</c> in UTF-16, so UTF-8 sorts it <em>after</em> U+FFFF (<c>EF BF BF</c>) while ordinal sorts it
/// <em>before</em>. A mismatch there does not throw; it makes the merge read "later" as "missing" and report a
/// deletion for a file that is still on disk. So <c>scan</c> carries <c>path_key</c> = the path's UTF-16 big-endian
/// bytes, whose <c>memcmp</c> order is exactly <c>StringComparer.Ordinal</c>, and <see cref="ScanOrderedAsync"/>
/// orders by it. (<c>draft</c> needs no such column: it is only ever read by <c>seq</c>.)
/// </para>
/// <para>
/// <b>One writer task, many short-lived readers.</b> SQLite allows a single writer, and a run writes from the
/// scanner, the differ, the uploaders and the resume ledger at once; funnelling every write through one channel and
/// one connection is what keeps them from serialising on a lock nobody can see. Writes are committed in batches
/// (2 000 items or 200 ms, whichever comes first) because a transaction per row would fsync-free but still journal
/// per row, and the scan is millions of rows. Reads open their own connection and dispose it; WAL means they do not
/// wait for the writer and the writer does not wait for them.
/// </para>
/// <para>
/// <b>A write cannot fail silently.</b> Nobody is awaiting the background task, so a failing statement is stored and
/// rethrown from the next <see cref="FlushAsync"/> — and from <see cref="DisposeAsync"/> if no flush ever saw it.
/// The alternative is a run that finishes "successfully" on a draft that is missing rows, which is the one failure
/// this class exists to prevent.
/// </para>
/// <para>
/// The file is pure scratch: <c>synchronous=OFF</c>, because a crash means the run is over and the whole file is
/// discarded, so there is nothing a sync could protect.
/// </para>
/// </summary>
public sealed partial class RunWorkDb : IAsyncDisposable
{
    // ---- schema ---------------------------------------------------------------------------------------------

    /// <summary>
    /// One string for the whole schema, so a task that adds a draft column has a single place to add it. The entry
    /// columns are spelled by <see cref="EntryRowMapper"/>, the same list the catalog's <c>entries</c> table uses, so
    /// an <see cref="IndexEntry"/> read out of either database goes through one mapping — and the previous version's
    /// entry is the same list again under <c>prev_</c>, generated rather than copied.
    /// <para>
    /// A <c>draft</c> row holds everything the new version's entry is built from: the diff's verdict
    /// (<c>change_kind</c>) and the entry it produced, the previous version's entry for the paths this run could not
    /// read, and — in their own columns, never on top of the diff's — the facts the run establishes later (the
    /// storage it uploaded to, the tail hash the compression pass fell out with, the identity a file that changed
    /// mid-run settled on, and a path that stopped being readable after the diff had already passed it). Keeping the
    /// two apart is what lets <c>RunLedger.FinalEntriesAsync</c> apply the same precedence the orchestrator's
    /// dictionaries used to, rather than a lossy approximation of it.
    /// </para>
    /// <para>
    /// <c>WITHOUT ROWID</c> everywhere the primary key <em>is</em> the row's identity: it drops the extra rowid index
    /// and clusters the rows by that key, which is the order every one of these tables is probed in.
    /// </para>
    /// </summary>
    private static readonly string Schema = $"""
        CREATE TABLE IF NOT EXISTS scan (
          path TEXT PRIMARY KEY, path_key BLOB NOT NULL, kind INTEGER NOT NULL, length INTEGER NOT NULL,
          mtime_ticks INTEGER NOT NULL, mtime_offset INTEGER NOT NULL, perms TEXT NOT NULL, target TEXT,
          category INTEGER NOT NULL, group_key TEXT) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS scan_order ON scan (path_key);
        CREATE INDEX IF NOT EXISTS scan_group ON scan (category, group_key);

        CREATE TABLE IF NOT EXISTS draft (
          seq INTEGER NOT NULL, state INTEGER NOT NULL, reason TEXT, storage_source INTEGER NOT NULL DEFAULT 0,
          change_kind INTEGER NOT NULL DEFAULT {(int)ChangeKind.Unchanged},
          has_current INTEGER NOT NULL DEFAULT 1, has_previous INTEGER NOT NULL DEFAULT 0,
          override_full TEXT, override_head TEXT, override_length INTEGER, override_mtime_ticks INTEGER,
          override_mtime_offset INTEGER, tail_set TEXT, post_diff_reason TEXT,
          {EntryRowMapper.ColumnDefinitions},
          {EntryRowMapper.NullableColumnDefinitions(PrevPrefix)},
          PRIMARY KEY (path)) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS draft_seq ON draft (seq);
        CREATE INDEX IF NOT EXISTS draft_state ON draft (state, path);
        CREATE INDEX IF NOT EXISTS draft_change_kind ON draft (change_kind, path);

        CREATE TABLE IF NOT EXISTS reservations (
          content_key TEXT PRIMARY KEY, ref TEXT NOT NULL, raw INTEGER NOT NULL, volumes INTEGER NOT NULL,
          volume_sizes TEXT) WITHOUT ROWID;
        -- Dedup asks this table both ways round: by content ("has this run already uploaded this?") and by address
        -- ("is this address already taken by something this run uploaded?"), the second being what stops a later
        -- file from claiming an address whose volumes are already written.
        CREATE INDEX IF NOT EXISTS reservations_ref ON reservations (ref);
        CREATE TABLE IF NOT EXISTS reserved_heads (head_key TEXT PRIMARY KEY) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS resume_blobs (
          path TEXT PRIMARY KEY, ref TEXT NOT NULL, full_hash TEXT NOT NULL, head_hash TEXT, tail_hash TEXT,
          length INTEGER NOT NULL, raw INTEGER NOT NULL, mtime_ticks INTEGER, volumes INTEGER NOT NULL,
          volume_sizes TEXT) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS resume_blobs_content ON resume_blobs (full_hash, length, head_hash, tail_hash);
        CREATE INDEX IF NOT EXISTS resume_blobs_ref ON resume_blobs (ref);

        CREATE TABLE IF NOT EXISTS resume_packs (
          members_key TEXT PRIMARY KEY, ref TEXT NOT NULL, store_only INTEGER NOT NULL, volumes INTEGER NOT NULL,
          volume_sizes TEXT) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS resume_pack_members (
          members_key TEXT NOT NULL, seq INTEGER NOT NULL, path TEXT NOT NULL, entry_name TEXT NOT NULL,
          full_hash TEXT NOT NULL, length INTEGER NOT NULL,
          PRIMARY KEY (members_key, seq)) WITHOUT ROWID;
        """;

    /// <summary>The name the previous version's entry is stored under in <c>draft</c>, for
    /// <see cref="EntryRowMapper.PrefixedColumns"/> and its siblings.</summary>
    private const string PrevPrefix = "prev_";

    // ---- write statements -----------------------------------------------------------------------------------

    /// <summary>OR IGNORE, not OR REPLACE: the scanner emits a path once, and if it somehow emits one twice the first
    /// sighting is the one the rest of the run has already been told about.</summary>
    private const string InsertScanSql = """
        INSERT OR IGNORE INTO scan (path, path_key, kind, length, mtime_ticks, mtime_offset, perms, target, category, group_key)
          VALUES (@path, @path_key, @kind, @length, @mtime_ticks, @mtime_offset, @perms, @target, @category, @group_key)
        """;

    /// <summary>OR REPLACE: a second insert for the same path is a deliberate restatement of the whole row, and it
    /// discards the updates applied to the previous one (<c>WITHOUT ROWID</c> replace is delete + insert, so
    /// <c>reason</c> and <c>storage_source</c> go back to their defaults). The differ writes each path once.
    /// <para>
    /// This one states the entry and nothing else, so the columns it does not name fall to their defaults: the row
    /// counts as <see cref="ChangeKind.Unchanged"/> (nothing this run did) and as carrying a current entry (it was
    /// just given one). <see cref="SeedDraftAsync"/> is the one that states a whole change.
    /// </para></summary>
    private const string InsertDraftSql =
        $"INSERT OR REPLACE INTO draft (seq, state, {EntryRowMapper.Columns}) " +
        $"VALUES (@seq, @state, {EntryRowMapper.Parameters})";

    /// <summary>The diff's whole verdict for one path: the entry it produced, the previous version's entry beside it,
    /// and what kind of change it was. Everything the run establishes afterwards lands in the <c>override_*</c> /
    /// <c>tail_set</c> / <c>post_diff_reason</c> columns, never on top of these, so the precedence
    /// <c>RunLedger.FinalEntriesAsync</c> applies at the end is still there to be applied.</summary>
    private static readonly string SeedDraftSql =
        "INSERT OR REPLACE INTO draft (seq, state, reason, change_kind, has_current, has_previous, " +
        $"{EntryRowMapper.Columns}, {EntryRowMapper.PrefixedColumns(PrevPrefix)}) " +
        "VALUES (@seq, @state, @reason, @change_kind, @has_current, @has_previous, " +
        $"{EntryRowMapper.Parameters}, {EntryRowMapper.PrefixedParameters(PrevPrefix)})";

    /// <summary><c>storage_source=1</c> marks the storage as this run's own, as opposed to the value carried over
    /// from the previous version when the file turned out to be unchanged — the two look identical in the entry, and
    /// the run ledger has to tell them apart to account for what it actually uploaded.</summary>
    private const string UpdateDraftStorageSql = """
        UPDATE draft SET storage_kind=@storage_kind, storage_ref=@storage_ref, entry_name=@entry_name,
          volumes=@volumes, raw=@raw, volume_sizes=@volume_sizes, storage_source=1 WHERE path=@path
        """;

    private const string UpdateDraftTailSql = "UPDATE draft SET tail_hash=@tail_hash WHERE path=@path";

    private const string UpdateDraftOverrideSql = """
        UPDATE draft SET full_hash=@full_hash, head_hash=@head_hash, length=@length,
          mtime_ticks=@mtime_ticks, mtime_offset=@mtime_offset WHERE path=@path
        """;


    /// <summary>The run's own values, in their own columns. The <c>UpdateDraft…</c> statements above overwrite what
    /// the diff recorded; these three keep both, because the final entry's tail hash falls back through the diff's
    /// value to the previous version's, and "the run set no tail hash" and "the diff found none" are two different
    /// facts that an in-place update would flatten into one.</summary>
    private const string SetDraftOverrideSql = """
        UPDATE draft SET override_full=@override_full, override_head=@override_head, override_length=@override_length,
          override_mtime_ticks=@override_mtime_ticks, override_mtime_offset=@override_mtime_offset WHERE path=@path
        """;

    private const string SetDraftTailSql = "UPDATE draft SET tail_set=@tail_set WHERE path=@path";

    /// <summary>Deliberately leaves <c>state</c> alone: the diff's unreadable verdict is what the operator is warned
    /// about path by path, and a file that only became unreadable when the upload stage reopened it is counted on its
    /// own line (see <c>BackupRunResult</c>'s unreadable total, which adds the two).</summary>
    private const string SetDraftPostDiffUnreadableSql =
        "UPDATE draft SET post_diff_reason=@post_diff_reason WHERE path=@path";

    private const string InsertReservationSql = """
        INSERT OR IGNORE INTO reservations (content_key, ref, raw, volumes, volume_sizes)
          VALUES (@content_key, @ref, @raw, @volumes, @volume_sizes)
        """;

    private const string InsertReservedHeadSql = "INSERT OR IGNORE INTO reserved_heads (head_key) VALUES (@head_key)";

    /// <summary>OR IGNORE keyed by <c>path</c>, matching the <c>TryAdd</c> of the dictionary this replaced: the caller
    /// feeds the volumes newest first, so the first record for a path is the newest one and must win.</summary>
    private const string InsertResumeBlobSql = """
        INSERT OR IGNORE INTO resume_blobs (path, ref, full_hash, head_hash, tail_hash, length, raw, mtime_ticks, volumes, volume_sizes)
          VALUES (@path, @ref, @full_hash, @head_hash, @tail_hash, @length, @raw, @mtime_ticks, @volumes, @volume_sizes)
        """;

    private const string InsertResumePackSql = """
        INSERT OR IGNORE INTO resume_packs (members_key, ref, store_only, volumes, volume_sizes)
          VALUES (@members_key, @ref, @store_only, @volumes, @volume_sizes)
        """;

    private const string InsertResumePackMemberSql = """
        INSERT OR IGNORE INTO resume_pack_members (members_key, seq, path, entry_name, full_hash, length)
          VALUES (@members_key, @seq, @path, @entry_name, @full_hash, @length)
        """;

    // ---- batching -------------------------------------------------------------------------------------------

    /// <summary>How many applied statements close a transaction. Big enough that the per-commit cost disappears into
    /// the noise on a million-row scan, small enough that a reader never waits long for the rows it needs.</summary>
    private const int BatchItems = 2_000;

    /// <summary>…and how long an unfinished batch may stay uncommitted. Without it, the last few rows of a stage sit
    /// invisible to every reader until the next stage happens to push the count over the limit.</summary>
    private const int BatchMilliseconds = 200;

    /// <summary>
    /// The queue depth between the producers (the scan, the differ, the pipeline stages) and the single writer
    /// below. Bounded, because this is the one structure in a run whose size follows the file count rather than
    /// anything the run controls: on a disk slower than the scan, closures queue faster than SQLite commits them
    /// and the backlog is memory nobody budgeted for. 16k operations is far more than a 2000-statement batch needs
    /// to stay fed — the writer never runs dry in practice — and <see cref="BoundedChannelFullMode.Wait"/> turns the
    /// overflow into the only sane thing: the producer waits for the disk, which is exactly what it should do.
    /// <para>
    /// It cannot deadlock. The pump never awaits a producer — it only reads — and a fault does not strand anyone
    /// either: <see cref="DrainFaultedAsync"/> keeps consuming after the fault is recorded, so a producer blocked on
    /// a full channel is woken by the drain and its own <see cref="FlushAsync"/> is what reports the failure.
    /// </para>
    /// </summary>
    private const int WriteQueueDepth = 16_384;

    private readonly Channel<WriteOp> _channel = Channel.CreateBounded<WriteOp>(
        new BoundedChannelOptions(WriteQueueDepth) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly Task _writer;

    private volatile Exception? _fault;
    private volatile bool _faultObserved;
    private volatile bool _disposed;

    private RunWorkDb(string path)
    {
        Path = path;
        _writer = Task.Run(PumpAsync);
    }

    /// <summary>The scratch file's path. Deleted, along with its <c>-wal</c> and <c>-shm</c> companions, by
    /// <see cref="DisposeAsync"/>.</summary>
    public string Path { get; }

    /// <summary>The file's stem — the run id the factory named it after. It is what the run's other scratch files are
    /// named from (the serialized index the finish writes beside this database), so that everything one run leaves on
    /// the temp volume carries the same name and a leftover can be traced back to the run that made it.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Creates the file and its schema, then starts the writer. The schema is created here rather than on
    /// the writer task so that a broken path or an unwritable directory fails the caller directly instead of turning
    /// into a fault the first flush has to report.</summary>
    public static async Task<RunWorkDb> CreateAsync(string path, CancellationToken ct)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using (var connection = new SqliteConnection(ConnectionString(path, create: true)))
        {
            await connection.OpenAsync(ct);
            ApplyPragmas(connection, writer: true);
            using var command = connection.CreateCommand();
            command.CommandText = Schema;
            await command.ExecuteNonQueryAsync(ct);
        }

        return new RunWorkDb(path);
    }

    /// <summary>
    /// <c>Pooling=false</c> for the same reason the catalog turns it off: a pooled connection collected without being
    /// disposed hands its live handle to the next Open of the same connection string, and the symptom is a
    /// <c>SQLite Error 0: 'not an error'</c> from an unrelated statement. This class opens a connection per read, so
    /// it would be the loudest possible place to get that wrong.
    /// </summary>
    /// <param name="create">Only the writer may create the file. A reader opens <c>ReadWrite</c>, so that a read
    /// racing <see cref="DisposeAsync"/> fails loudly instead of conjuring an empty database under the deleted name
    /// and answering every question with "no rows" — a silent wrong answer where the run wanted an error.</param>
    private static string ConnectionString(string path, bool create) => new SqliteConnectionStringBuilder
    {
        DataSource = ProcessPrivateSqlite.DataSource(path),
        Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
        Pooling = false,
        Cache = SqliteCacheMode.Private,
    }.ToString();

    /// <summary>
    /// <c>synchronous=OFF</c>, because this file is scratch: a crash ends the run and the file is thrown away, so
    /// there is nothing for a sync to protect. WAL, so the readers never block behind the writer. 64 MiB of page
    /// cache (<c>cache_size</c> is negative = KiB) for the index probes dedup and resume make over a file that can
    /// reach hundreds of MiB, and <c>temp_store=MEMORY</c> so the <c>GROUP BY</c> and <c>ORDER BY</c> sorts do not
    /// spill onto the very disk this whole exercise is trying not to depend on.
    /// </summary>
    /// <param name="writer">Only the writer sets <c>journal_mode</c>: it is a property of the file, not of the
    /// connection, and re-declaring it from a reader while the writer holds a transaction is a lock nobody needs.</param>
    private static void ApplyPragmas(SqliteConnection connection, bool writer)
    {
        using var command = connection.CreateCommand();
        command.CommandText = (writer ? "PRAGMA journal_mode=WAL;" : "") + """
            PRAGMA synchronous=OFF;
            PRAGMA cache_size=-65536;
            PRAGMA temp_store=MEMORY;
            PRAGMA busy_timeout=30000;
            PRAGMA foreign_keys=OFF;
            """;
        command.ExecuteNonQuery();
    }

    // ---- writes ---------------------------------------------------------------------------------------------

    public ValueTask InsertScanAsync(ScanRow row, CancellationToken ct) => EnqueueAsync(statements =>
    {
        var command = statements.For(InsertScanSql);
        Set(command, "@path", row.Path);
        Set(command, "@path_key", CatalogSql.PathKey(row.Path));
        Set(command, "@kind", (int)row.Kind);
        Set(command, "@length", row.Length);
        Set(command, "@mtime_ticks", row.ModifiedAt.UtcTicks);
        Set(command, "@mtime_offset", (int)row.ModifiedAt.Offset.TotalMinutes);
        Set(command, "@perms", row.Permissions);
        Set(command, "@target", row.Target);
        Set(command, "@category", (int)row.Category);
        Set(command, "@group_key", row.GroupKey);
        command.ExecuteNonQuery();
    }, ct);

    public ValueTask InsertDraftAsync(int seq, string path, DraftState state, IndexEntry entry, CancellationToken ct) =>
        EnqueueAsync(statements =>
        {
            var command = statements.For(InsertDraftSql);
            EntryRowMapper.Bind(command, entry);
            Set(command, "@path", path);   // after Bind: the draft is keyed by the path the caller names, not by the entry's own
            Set(command, "@seq", seq);
            Set(command, "@state", (int)state);
            command.ExecuteNonQuery();
        }, ct);

    /// <summary>
    /// Files one change of the diff as a draft row. The state is the diff's verdict: an unreadable path keeps the
    /// previous version's content, a deleted one is not written into the new index at all, and everything else is
    /// pending until the upload side settles it.
    /// <para>
    /// The entry columns hold what the new version would say if nothing else happened — the scanned metadata plus the
    /// hashes the diff resolved and the storage carried over from the previous version. The <c>prev_</c> columns hold
    /// the previous version's entry verbatim, so a path this run cannot read can be carried forward without going
    /// back to the catalog for it, and a deleted path's size is still there to report. When the diff has no current
    /// entry at all (everything under a directory that could not be listed, and every deletion), the previous entry
    /// stands in for the columns that cannot be null and <c>has_current</c> is 0, so the stand-in is never mistaken
    /// for something this run saw.
    /// </para>
    /// </summary>
    public ValueTask SeedDraftAsync(int seq, FileChange change, CancellationToken ct) =>
        EnqueueAsync(statements =>
        {
            var command = statements.For(SeedDraftSql);
            EntryRowMapper.Bind(command, DraftEntry(change));
            EntryRowMapper.BindOptional(command, change.Previous, PrevPrefix);
            Set(command, "@path", change.Path);   // after Bind: the draft is keyed by the path the diff names
            Set(command, "@seq", seq);
            Set(command, "@state", (int)(change.Kind switch
            {
                ChangeKind.Unreadable => DraftState.Unreadable,
                ChangeKind.Deleted => DraftState.Dropped,
                _ => DraftState.Pending,
            }));
            Set(command, "@reason", change.UnreadableReason);
            Set(command, "@change_kind", (int)change.Kind);
            Set(command, "@has_current", change.Current is null ? 0 : 1);
            Set(command, "@has_previous", change.Previous is null ? 0 : 1);
            command.ExecuteNonQuery();
        }, ct);

    /// <summary>What the new version would say about this path on the diff's evidence alone. A change with neither a
    /// current nor a previous entry is not something the differ emits today, but it still has to produce a row
    /// rather than throw — the run counts every change it is given, and the placeholder can never be read back as an
    /// entry: <c>has_current</c> is 0 and there is no previous to carry forward.</summary>
    private static IndexEntry DraftEntry(FileChange change) => change.Current is { } current
        ? new IndexEntry
        {
            Path = change.Path,
            Kind = current.Kind == EntryKind.File ? "file" : "symlink",
            Length = current.Length,
            Mtime = current.ModifiedAt,
            Permissions = current.Permissions,
            HeadHash = change.HeadHash,
            TailHash = change.TailHash,
            FullHash = change.FullHash,
            Target = current.Target,
            Storage = change.CarriedStorage,
        }
        : change.Previous ?? new IndexEntry
        {
            Path = change.Path, Kind = "file", Length = 0, Mtime = default, Permissions = "",
        };

    public ValueTask UpdateDraftStorageAsync(string path, StorageRef storage, CancellationToken ct) =>
        EnqueueAsync(statements =>
        {
            var command = statements.For(UpdateDraftStorageSql);
            EntryRowMapper.BindStorage(command, storage);
            Set(command, "@path", path);
            command.ExecuteNonQuery();
        }, ct);

    public ValueTask UpdateDraftTailAsync(string path, string tailHash, CancellationToken ct) =>
        EnqueueAsync(statements =>
        {
            var command = statements.For(UpdateDraftTailSql);
            Set(command, "@path", path);
            Set(command, "@tail_hash", tailHash);
            command.ExecuteNonQuery();
        }, ct);

    /// <summary>Replaces the content identity and the metadata that goes with it, for when reading the file told the
    /// run something the scan's <c>stat</c> could not (the file changed under the scan, or a deferred full hash came
    /// back).</summary>
    public ValueTask UpdateDraftOverrideAsync(
        string path, string fullHash, string? headHash, long length, DateTimeOffset mtime, CancellationToken ct) =>
        EnqueueAsync(statements =>
        {
            var command = statements.For(UpdateDraftOverrideSql);
            Set(command, "@path", path);
            Set(command, "@full_hash", fullHash);
            Set(command, "@head_hash", headHash);
            Set(command, "@length", length);
            Set(command, "@mtime_ticks", mtime.UtcTicks);
            Set(command, "@mtime_offset", (int)mtime.Offset.TotalMinutes);
            command.ExecuteNonQuery();
        }, ct);

    /// <summary>The identity a file that changed while it was being processed finally settled on, kept beside the
    /// diff's rather than over it (see <see cref="SetDraftOverrideSql"/>).</summary>
    public ValueTask SetDraftOverrideAsync(
        string path, string fullHash, string? headHash, long length, DateTimeOffset mtime, CancellationToken ct) =>
        EnqueueAsync(statements =>
        {
            var command = statements.For(SetDraftOverrideSql);
            Set(command, "@path", path);
            Set(command, "@override_full", fullHash);
            Set(command, "@override_head", headHash);
            Set(command, "@override_length", length);
            Set(command, "@override_mtime_ticks", mtime.UtcTicks);
            Set(command, "@override_mtime_offset", (int)mtime.Offset.TotalMinutes);
            command.ExecuteNonQuery();
        }, ct);

    /// <summary>The tail hash the compression pass produced for a single-file blob — the most authoritative one,
    /// because those are the bytes that actually went into the archive.</summary>
    public ValueTask SetDraftTailAsync(string path, string tailHash, CancellationToken ct) =>
        EnqueueAsync(statements =>
        {
            var command = statements.For(SetDraftTailSql);
            Set(command, "@path", path);
            Set(command, "@tail_set", tailHash);
            command.ExecuteNonQuery();
        }, ct);

    /// <summary>Readable when the diff passed it, unreadable when the compress/upload stage reopened it. The index
    /// treats this exactly as diff-time unreadability — no blob was produced, so the previous version's entry is
    /// carried forward — but the run counts it separately.</summary>
    public ValueTask SetDraftPostDiffUnreadableAsync(string path, string reason, CancellationToken ct) =>
        EnqueueAsync(statements =>
        {
            var command = statements.For(SetDraftPostDiffUnreadableSql);
            Set(command, "@path", path);
            Set(command, "@post_diff_reason", reason);
            command.ExecuteNonQuery();
        }, ct);

    public ValueTask InsertReservationAsync(string contentKey, ReservationRow row, CancellationToken ct) =>
        EnqueueAsync(statements =>
        {
            var command = statements.For(InsertReservationSql);
            Set(command, "@content_key", contentKey);
            Set(command, "@ref", row.Ref);
            Set(command, "@raw", row.Raw ? 1 : 0);
            Set(command, "@volumes", row.Volumes);
            Set(command, "@volume_sizes", EntryRowMapper.FormatVolumeSizes(row.VolumeSizes));
            command.ExecuteNonQuery();
        }, ct);

    public ValueTask InsertReservedHeadAsync(string headKey, CancellationToken ct) =>
        EnqueueAsync(statements =>
        {
            var command = statements.For(InsertReservedHeadSql);
            Set(command, "@head_key", headKey);
            command.ExecuteNonQuery();
        }, ct);

    /// <summary>
    /// Files one journal record. The filters are the in-memory resume table's, verbatim: a blob record needs a path and a
    /// full hash (an incomplete identity has no business answering a resume question), and a pack record needs
    /// members. Anything else is dropped rather than stored as a row nothing can match.
    /// </summary>
    public ValueTask InsertResumeRecordAsync(JournalRecord record, CancellationToken ct) => EnqueueAsync(statements =>
    {
        if (record.Kind == "blob" && record is { Path: { } path, FullHash: { } fullHash })
        {
            var command = statements.For(InsertResumeBlobSql);
            Set(command, "@path", path);
            Set(command, "@ref", record.Ref);
            Set(command, "@full_hash", fullHash);
            Set(command, "@head_hash", record.HeadHash);
            Set(command, "@tail_hash", record.TailHash);
            Set(command, "@length", record.Length);
            Set(command, "@raw", record.Raw ? 1 : 0);
            Set(command, "@mtime_ticks", record.MtimeUtcTicks);
            Set(command, "@volumes", record.Volumes);
            Set(command, "@volume_sizes", EntryRowMapper.FormatVolumeSizes(record.VolumeSizes));
            command.ExecuteNonQuery();
        }
        else if (record.Kind == "pack" && record.Members.Count > 0)
        {
            var key = MemberKey(record.Members);
            var command = statements.For(InsertResumePackSql);
            Set(command, "@members_key", key);
            Set(command, "@ref", record.Ref);
            Set(command, "@store_only", record.StoreOnly ? 1 : 0);
            Set(command, "@volumes", record.Volumes);
            Set(command, "@volume_sizes", EntryRowMapper.FormatVolumeSizes(record.VolumeSizes));
            command.ExecuteNonQuery();

            var member = statements.For(InsertResumePackMemberSql);
            for (var i = 0; i < record.Members.Count; i++)
            {
                Set(member, "@members_key", key);
                Set(member, "@seq", i);
                Set(member, "@path", record.Members[i].Path);
                Set(member, "@entry_name", record.Members[i].EntryName);
                Set(member, "@full_hash", record.Members[i].FullHash);
                Set(member, "@length", record.Members[i].Length);
                member.ExecuteNonQuery();
            }
        }
    }, ct);

    /// <summary>
    /// Canonical key of a member set: path + full hash + length, joined in order. Copied verbatim from
    /// the in-memory resume table's <c>MemberKey</c> — the two must agree exactly, because a resume that reads its pack records out
    /// of this database has to reach the same verdict the in-memory table reached.
    /// <para>
    /// <c>EntryName</c> is deliberately out: in this repo it is always the member's own path, so it adds nothing to
    /// the key. Order is deliberately in: <c>PackInfo.Members</c> is written in member order, and a key blind to
    /// order would let two orderings of one set match each other, leaving the info file at odds with the archive.
    /// </para>
    /// </summary>
    public static string MemberKey(IReadOnlyList<JournalMember> members)
        => string.Join('\n', members.Select(m => $"{m.Path}\0{m.FullHash}\0{m.Length}"));

    /// <summary>
    /// A test hook for the one thing no public write can produce: a statement that fails. It goes through the same
    /// channel and the same transaction as everything else, so what it exercises is the real fault path.
    /// </summary>
    internal ValueTask EnqueueRawSqlAsync(string sql, CancellationToken ct) => EnqueueAsync(statements =>
    {
        using var command = statements.Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = statements.Transaction;
        command.ExecuteNonQuery();
    }, ct);

    /// <summary>Waits until every write enqueued before this call has been committed. Also the moment a writer fault
    /// reaches the caller.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFaulted();
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(new WriteOp(null, marker), ct);
        try
        {
            await marker.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;   // the caller's cancellation, not the writer's problem
        }
        catch
        {
            _faultObserved = true;
            throw;
        }
    }

    private ValueTask EnqueueAsync(Action<Statements> apply, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _channel.Writer.WriteAsync(new WriteOp(apply, null), ct);
    }

    private void ThrowIfFaulted()
    {
        if (_fault is not { } fault)
            return;
        _faultObserved = true;
        ExceptionDispatchInfo.Capture(fault).Throw();
    }

    private static void Set(SqliteCommand command, string name, object? value) => EntryRowMapper.Set(command, name, value);

    // ---- the writer task ------------------------------------------------------------------------------------

    /// <summary>One unit of work on the writer's channel: either a statement to apply, or a flush marker to complete
    /// after the commit that includes everything queued before it.</summary>
    private readonly record struct WriteOp(Action<Statements>? Apply, TaskCompletionSource? Marker);

    /// <summary>
    /// The writer connection's prepared statements, one per SQL text, kept for the life of the run.
    /// Microsoft.Data.Sqlite prepares on first execute and reuses the prepared statement as long as the command
    /// object and its <c>CommandText</c> survive — so reusing the command and rebinding its parameters is what turns
    /// a million-row scan into one prepared statement instead of a million.
    /// </summary>
    private sealed class Statements(SqliteConnection connection) : IDisposable
    {
        private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);

        public SqliteConnection Connection => connection;

        /// <summary>The batch's transaction. Microsoft.Data.Sqlite refuses a command that does not name the
        /// connection's pending transaction, so every handed-out command is re-pointed at the current one.</summary>
        public SqliteTransaction? Transaction { get; set; }

        public SqliteCommand For(string sql)
        {
            if (!_commands.TryGetValue(sql, out var command))
            {
                command = connection.CreateCommand();
                command.CommandText = sql;
                _commands[sql] = command;
            }

            command.Transaction = Transaction;
            return command;
        }

        public void Dispose()
        {
            foreach (var command in _commands.Values)
                command.Dispose();
        }
    }

    /// <summary>
    /// The writer task, and the <b>only</b> place a fault is recorded. Nothing may escape this method: the pump is
    /// the sole reader of the channel, so an exception that got out would leave <see cref="_fault"/> null with nobody
    /// left to complete a marker — a caller already inside <see cref="FlushAsync"/> would wait forever on a marker
    /// that can never be completed, and the next <see cref="FlushAsync"/> would sail past
    /// <see cref="ThrowIfFaulted"/> and enqueue into a channel no one is reading.
    /// <para>
    /// So the capture is here, around the whole of <see cref="PumpCoreAsync"/>, rather than around the batch loop
    /// alone. The steps outside a batch's own <c>try</c> are precisely the ones most likely to fail for reasons that
    /// have nothing to do with the statements — opening the connection, <c>BEGIN</c> against a file another writer
    /// holds (<c>SQLITE_BUSY</c>) or a failing disk (<c>SQLITE_IOERR</c>), and finalizing the transaction object.
    /// </para>
    /// </summary>
    private async Task PumpAsync()
    {
        // Shared with the core so that a batch killed by one of those steps still has its pending flush markers
        // failed here, instead of being left hanging with the batch that was carrying them.
        var markers = new List<TaskCompletionSource>();
        try
        {
            await PumpCoreAsync(markers).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _fault = ex;
            foreach (var pending in markers)
                pending.TrySetException(ex);
            await DrainFaultedAsync(ex).ConfigureAwait(false);
        }
    }

    /// <summary>The loop itself. Faults are not handled here — every one of them, from the batch's statements to the
    /// <c>BEGIN</c> that opened it, leaves through <see cref="PumpAsync"/>, which is the only place that records
    /// one.</summary>
    private async Task PumpCoreAsync(List<TaskCompletionSource> markers)
    {
        await using var connection = new SqliteConnection(ConnectionString(Path, create: false));
        await connection.OpenAsync().ConfigureAwait(false);
        ApplyPragmas(connection, writer: true);

        using var statements = new Statements(connection);
        var reader = _channel.Reader;

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            markers.Clear();
            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
            statements.Transaction = transaction;
            var deadline = Environment.TickCount64 + BatchMilliseconds;
            var applied = 0;
            var completed = false;
            Exception? fault = null;

            try
            {
                while (applied < BatchItems)
                {
                    if (reader.TryRead(out var op))
                    {
                        if (op.Marker is { } marker)
                            markers.Add(marker);
                        else
                        {
                            op.Apply!(statements);
                            applied++;
                        }

                        continue;
                    }

                    // Somebody is already awaiting this batch, so holding it open for the rest of the window would
                    // only make them wait for rows nobody has written yet. Commit now.
                    if (markers.Count > 0)
                        break;

                    // Nothing queued right now: hold the transaction open for the rest of the batch window, then
                    // commit whatever it has. The cancellation source is what removes this waiter from the channel
                    // when the window runs out, instead of leaving one behind per idle batch.
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0)
                        break;
                    using var window = new CancellationTokenSource((int)remaining);
                    try
                    {
                        if (!await reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
                        {
                            completed = true;
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                fault = ex;
            }

            if (fault is null)
            {
                try { await transaction.CommitAsync().ConfigureAwait(false); }
                catch (Exception ex) { fault = ex; }
            }

            if (fault is not null)
            {
                try { await transaction.RollbackAsync().ConfigureAwait(false); }
                catch { /* the batch is lost either way; the fault below is the one that matters */ }
            }

            statements.Transaction = null;
            await transaction.DisposeAsync().ConfigureAwait(false);

            // The batch rolled back, so its rows are gone and every later row would be written on top of a draft
            // nobody can trust. Stop applying and let the fault out to PumpAsync, the one place that records it,
            // fails the markers this batch was carrying, and keeps draining so no caller waits forever. Rethrown
            // through ExceptionDispatchInfo so the stack still points at the statement that failed.
            if (fault is not null)
                ExceptionDispatchInfo.Capture(fault).Throw();

            foreach (var pending in markers)
                pending.TrySetResult();
            if (completed)
                return;
        }
    }

    private async Task DrainFaultedAsync(Exception fault)
    {
        while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            while (_channel.Reader.TryRead(out var op))
                op.Marker?.TrySetException(fault);
    }

    /// <summary>
    /// Stops the writer and deletes the file. Rethrows a writer fault that no <see cref="FlushAsync"/> ever saw — a
    /// run that never flushed after its last write would otherwise discard the evidence along with the file.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _channel.Writer.TryComplete();
        try { await _writer.ConfigureAwait(false); }
        catch (Exception ex) { _fault ??= ex; }

        Delete(Path);
        Delete(Path + "-wal");
        Delete(Path + "-shm");

        if (_fault is { } fault && !_faultObserved)
        {
            _faultObserved = true;
            ExceptionDispatchInfo.Capture(fault).Throw();
        }
    }

    internal static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* a leftover scratch file wastes disk; failing the run over it would waste the run */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }
}

/// <summary>
/// The one LINQ operator the streaming cursors need. <c>System.Linq.Async</c> would bring the whole set, and the
/// whole point of these cursors is that nothing between the database and the consumer materializes — an operator
/// library invites exactly the <c>ToList</c> that undoes it. Six lines, one call site, no dependency.
/// </summary>
internal static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<TResult> Select<TSource, TResult>(
        this IAsyncEnumerable<TSource> source, Func<TSource, TResult> selector,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in source.WithCancellation(ct))
            yield return selector(item);
    }
}
