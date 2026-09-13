using System.Globalization;
using System.Runtime.CompilerServices;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The read side of the catalog: browsing one directory at a time, the dedup lookups that used to be in-memory
/// dictionaries, and the whole-file questions retention and repair ask. Split off <c>VersionCatalog.cs</c> (opening,
/// versions, serialization and patching) and <c>VersionCatalog.Import.cs</c> (the merge that writes a version) purely
/// for size — one class, three files.
/// </summary>
public sealed partial class VersionCatalog
{
    /// <summary>A version the catalog does not hold answers nothing at all, as it did when every row named one
    /// version. Without this an open-ended row would report the newest version's content under any later number, and
    /// a gap retention left behind would report its predecessor's. Uncorrelated, so SQLite evaluates it once per
    /// statement against the <c>versions</c> primary key.</summary>
    private const string Retained = "EXISTS (SELECT 1 FROM versions WHERE version=@v)";

    /// <summary>The interval filter every per-version query wears: the row whose <c>[version_from, version_to)</c>
    /// contains <c>@v</c> is what the path looked like at that version.</summary>
    private const string Current = $"version_from <= @v AND version_to > @v AND {Retained}";

    private const string SelectEntrySql = $"SELECT {EntryRowMapper.Columns} FROM entries WHERE path=@path AND {Current}";

    /// <summary>Ordered by the UTF-16BE <c>path_key</c>, not the <c>path</c> TEXT column: this is the cursor the diff
    /// merges against the run's scan cursor (<c>RunWorkDb.ScanOrderedAsync</c>), and both have to agree on ordinal
    /// order for a surrogate pair to land in the same place on each side. See <see cref="CatalogSql.PathKey"/>.</summary>
    private const string SelectEntriesByPathSql = $"SELECT {EntryRowMapper.Columns} FROM entries WHERE {Current} ORDER BY path_key";

    private const string SelectEntriesByStorageSql =
        $"SELECT {EntryRowMapper.Columns} FROM entries WHERE {Current} ORDER BY storage_ref, storage_kind, path_key";

    /// <summary>A directory's subdirectories, each already told whether it is worth expanding, so the UI's next click is a lookup rather than a guess.</summary>
    private const string SelectChildDirsSql = $"""
        SELECT d.path,
               EXISTS (SELECT 1 FROM entries e WHERE e.parent=d.path AND e.version_from <= @v AND e.version_to > @v)
            OR EXISTS (SELECT 1 FROM dirs c WHERE c.parent=d.path AND c.version_from <= @v AND c.version_to > @v)
        FROM dirs d WHERE d.parent=@parent AND d.version_from <= @v AND d.version_to > @v AND {Retained} ORDER BY d.path
        """;

    private const string SelectChildEntriesSql = $"SELECT {EntryRowMapper.Columns} FROM entries WHERE parent=@parent AND {Current} ORDER BY path";

    private const string SelectUnreadableSql =
        $"SELECT path, unreadable_ticks, unreadable_offset FROM entries WHERE {Current} AND unreadable_ticks IS NOT NULL ORDER BY path_key";

    private const string SelectStatsSql = $"SELECT COUNT(*), COALESCE(SUM(length), 0) FROM entries WHERE {Current}";

    private const string IsUnrecoverableSql = "SELECT EXISTS (SELECT 1 FROM unrecoverable WHERE version=@v AND path=@path)";

    /// <summary>The entries a local-root comparison may look at: everything except the ones whose size and mtime were
    /// carried over from an earlier version and so were never guaranteed to match the disk.</summary>
    private const string SelectComparableEntriesSql =
        $"SELECT {EntryRowMapper.Columns} FROM entries WHERE {Current} AND unreadable_ticks IS NULL ORDER BY path_key";

    /// <summary>Paths that differ only in case: on a case-insensitive restore target they would overwrite one another, so a check has to report them.</summary>
    private const string SelectCaseCollisionsSql = $"""
        SELECT path, @v FROM entries WHERE {Current} AND path_fold IN
          (SELECT path_fold FROM entries WHERE {Current} GROUP BY path_fold HAVING COUNT(*) > 1)
        ORDER BY path_fold, path
        """;

    // The most recently introduced copy wins: on interval rows a row carries the version the entry first appeared
    // at, not every version that still holds it, so "greatest version_from" is the latest time this content was
    // written somewhere — which is the answer the in-memory map (rebuilt version by version, each overwriting the
    // last) used to give, for every case where the content is still current. A copy that was introduced earlier and
    // survives into newer versions loses to one introduced later, and either is a correct address for the content.
    private const string FindBlobByContentSql = """
        SELECT storage_ref, raw, volumes, volume_sizes FROM entries
        WHERE full_hash=@f AND length=@l AND head_hash IS @h AND tail_hash IS @t AND storage_kind='blob' AND unrecoverable=0
        ORDER BY version_from DESC LIMIT 1
        """;

    // Healthy rows first (the most recently introduced wins among them), damaged rows only as a fallback (the
    // earliest introduced wins) — the precedence the in-memory build had from "normal rows overwrite, damaged rows
    // TryAdd", read off each row's own version_from rather than off every version that holds it.
    //
    // The three lookups below all require a full hash, because the in-memory build skipped an entry that had none
    // before it ever looked at its storage (`if (e.FullHash is null) continue;`): such an entry owns no address,
    // marks no address damaged and puts nothing in the prescreen. Without the filter a hash-less row in a *newer*
    // version wins the ORDER BY here and shadows the real owner, and an occupied address is reported free — which is
    // how brand new content ends up written over somebody else's blob. (FindBlobByContent and FindPackMember need no
    // such filter: they bind full_hash = @f, which a NULL never matches.)
    private const string FindRefOwnerSql = """
        SELECT full_hash, length, head_hash, tail_hash, unrecoverable FROM entries
        WHERE storage_ref=@r AND storage_kind='blob' AND full_hash IS NOT NULL
        ORDER BY unrecoverable ASC, CASE WHEN unrecoverable THEN version_from ELSE -version_from END ASC LIMIT 1
        """;

    private const string IsDamagedRefSql =
        "SELECT EXISTS (SELECT 1 FROM entries WHERE storage_ref=@r AND storage_kind='blob' AND unrecoverable=1 " +
        "AND full_hash IS NOT NULL)";

    private const string HeadSeenSql =
        "SELECT EXISTS (SELECT 1 FROM entries WHERE length=@l AND head_hash=@h AND storage_kind='blob' " +
        "AND unrecoverable=0 AND full_hash IS NOT NULL)";

    // The earliest introduced wins, with the path order to break a tie inside one version: references pile onto the
    // oldest pack holding the content, which is the one compaction is least likely to rewrite.
    private const string FindPackMemberSql = """
        SELECT storage_ref, COALESCE(entry_name, path), tail_hash FROM entries
        WHERE full_hash=@f AND length=@l AND head_hash=@h AND storage_kind='pack' AND unrecoverable=0
        ORDER BY version_from ASC, path_key ASC LIMIT 1
        """;

    /// <summary>One row per (kind, ref, volume count), not per ref: the orphan sweep protects <b>every volume</b> of
    /// a referenced object, and for a single-file blob the only place that count is recorded is the entry itself (a
    /// pack's lives in the info file). Every distinct count is emitted rather than the largest, because the name
    /// sets of two counts are DISJOINT, not nested: <see cref="VolumeBlobIO.VolumeNames"/> gives the bare
    /// <c>data/h</c> for one volume and <c>data/h.001</c>… for more. Collapsing a version that records 1 and a
    /// version that records 3 onto the larger would leave the live bare name out of the protected set entirely.
    /// The caller unions the names, so emitting both counts closes the gap.</summary>
    private const string SelectDistinctRefsSql =
        "SELECT DISTINCT storage_kind, storage_ref, volumes FROM entries WHERE storage_ref IS NOT NULL " +
        "ORDER BY storage_kind, storage_ref, volumes";

    private const string SelectLivePackMembersSql = """
        SELECT storage_ref, COALESCE(entry_name, path), length, full_hash FROM entries
        WHERE storage_kind='pack' AND full_hash IS NOT NULL
        ORDER BY storage_ref, COALESCE(entry_name, path), version_from DESC
        """;

    /// <summary>One interval row stands for every version in its interval; the join with <c>versions</c> expands it
    /// back to the one-row-per-version shape repair reads.</summary>
    private static readonly string SelectEntriesReferencingSql = $"""
        SELECT v.version, {EntryRowMapper.PrefixedColumns("e.")} FROM entries e
        JOIN versions v ON v.version >= e.version_from AND v.version < e.version_to
        WHERE e.storage_ref=@r ORDER BY v.version, e.path_key
        """;

    private static readonly string SelectPackMembersSql = $"""
        SELECT v.version, {EntryRowMapper.PrefixedColumns("e.")} FROM entries e
        JOIN versions v ON v.version >= e.version_from AND v.version < e.version_to
        WHERE e.storage_kind='pack' AND e.storage_ref=@r ORDER BY v.version, e.path_key
        """;

    private const string SelectUnrecoverableAnyVersionSql = "SELECT DISTINCT path FROM unrecoverable ORDER BY path";

    private const string SelectImportIssuesSql = "SELECT path, issue FROM import_issues WHERE version=@v ORDER BY path";

    /// <summary>One row per storage object the version references. <c>entry_name</c> is selected as NULL on purpose:
    /// a pack's members each carry their own name, so no single row's value is true of the group as a whole, and what
    /// the caller wants here is the object (where to download it from, how many volumes, how big they are) rather than
    /// any one member of it. The other three bare columns under the <c>GROUP BY</c> are safe precisely because they
    /// are not per-member: <c>volumes</c>, <c>raw</c> and <c>volume_sizes</c> describe the object, so every row of a
    /// group carries the same values and SQLite's choice of which row to read them from cannot matter.
    /// <para>
    /// <c>unrecoverable=0</c> is not a nicety. An entry a check gave up on is never restored — it is either
    /// substituted, in which case the content comes from a different object entirely, or it has no substitute and is
    /// skipped — so its object never becomes a download group, and counting it here would only distort what the
    /// caller is about to plan. Worse, a damaged version's abandoned pack is typically gone from the info file too,
    /// which makes its download size unknowable, and a single unknowable object is enough to cost the whole restore
    /// its download denominator (that total is all-or-nothing by design).
    /// </para>
    /// Grouped, so the result is bounded by the number of packs and blobs rather than by the entry count.</summary>
    private const string SelectStorageGroupSizesSql = $"""
        SELECT storage_kind, storage_ref, NULL AS entry_name, volumes, raw, volume_sizes, SUM(length)
        FROM entries WHERE {Current} AND storage_kind IS NOT NULL AND unrecoverable=0
        GROUP BY storage_kind, storage_ref ORDER BY storage_kind, storage_ref
        """;

    // ---- browsing -------------------------------------------------------------------------------------------

    public async Task<IndexEntry?> GetEntryAsync(int version, string path, CancellationToken ct)
    {
        using var command = Command(SelectEntrySql);
        Set(command, "@v", version);
        Set(command, "@path", path);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? EntryRowMapper.Read(reader) : null;
    }

    /// <summary>The direct children of one directory ("" is the root): subdirectories from the <c>dirs</c> table, files
    /// from <c>entries</c>. Two indexed lookups instead of a walk over every path in the version.</summary>
    public async Task<IReadOnlyList<CatalogChild>> ChildrenAsync(int version, string parent, CancellationToken ct)
    {
        var children = new List<CatalogChild>();

        using (var dirs = Command(SelectChildDirsSql))
        {
            Set(dirs, "@v", version);
            Set(dirs, "@parent", parent);
            await using var reader = (SqliteDataReader)await dirs.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                children.Add(new CatalogChild(NameOf(reader.GetString(0)), IsDir: true, reader.GetBoolean(1), Entry: null));
        }

        using (var files = Command(SelectChildEntriesSql))
        {
            Set(files, "@v", version);
            Set(files, "@parent", parent);
            await using var reader = (SqliteDataReader)await files.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var entry = EntryRowMapper.Read(reader);
                children.Add(new CatalogChild(NameOf(entry.Path), IsDir: false, HasChildren: false, entry));
            }
        }

        return children;
    }

    /// <summary>Every entry of the version in <see cref="StringComparer.Ordinal"/> path order — the order the diff
    /// walks the local scan in. Ordered by <c>path_key</c>, not the <c>path</c> TEXT column, because SQLite's default
    /// BINARY collation compares UTF-8 bytes, which agrees with ordinal order only until a surrogate pair shows up.
    /// See <see cref="CatalogSql.PathKey"/>.</summary>
    public IAsyncEnumerable<IndexEntry> EntriesAsync(int version, CancellationToken ct) =>
        QueryEntriesAsync(SelectEntriesByPathSql, version, ct);

    /// <summary>Entries grouped by the blob or pack they live in, so a consumer can finish one download before the next
    /// group starts instead of buffering the whole version to sort it.</summary>
    public IAsyncEnumerable<IndexEntry> EntriesByStorageAsync(int version, CancellationToken ct) =>
        QueryEntriesAsync(SelectEntriesByStorageSql, version, ct);

    /// <summary>The entries at a known set of paths (a restore selection, a repair list). Chunked, because SQLite caps
    /// a statement's parameters and a selection can be arbitrarily long; missing paths are simply absent.</summary>
    public async Task<IReadOnlyList<IndexEntry>> EntriesAtAsync(int version, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        const int chunkSize = 500;
        var entries = new List<IndexEntry>();
        foreach (var chunk in paths.Chunk(chunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var placeholders = string.Join(", ", Enumerable.Range(0, chunk.Length).Select(i => $"@p{i}"));
            using var command = Command($"SELECT {EntryRowMapper.Columns} FROM entries WHERE {Current} AND path IN ({placeholders})");
            Set(command, "@v", version);
            for (var i = 0; i < chunk.Length; i++)
                Set(command, $"@p{i}", chunk[i]);
            await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                entries.Add(EntryRowMapper.Read(reader));
        }

        return entries;
    }

    /// <summary>Paths whose content is carried over from an earlier version because this run could not read the file, and since when.</summary>
    public async Task<IReadOnlyList<(string Path, DateTimeOffset UnreadableAt)>> UnreadableAsync(int version, CancellationToken ct)
    {
        using var command = Command(SelectUnreadableSql);
        Set(command, "@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        var rows = new List<(string, DateTimeOffset)>();
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), EntryRowMapper.Dto(reader.GetInt64(1), reader.GetInt32(2))));
        return rows;
    }

    public async Task<(long Files, long Bytes)> StatsAsync(int version, CancellationToken ct)
    {
        using var command = Command(SelectStatsSql);
        Set(command, "@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
    }

    /// <summary>Whether this one path is among the version's unrecoverable ones. The whole list
    /// (<see cref="UnrecoverableAsync"/>) is the wrong question when the caller only wants to know about a single
    /// file — the restore dialog's "which versions can this path be substituted from" asks it once per retained
    /// version, and a version can hold a million paths.</summary>
    public Task<bool> IsUnrecoverableAsync(int version, string path, CancellationToken ct) =>
        ExistsAsync(IsUnrecoverableSql, ct, ("@v", version), ("@path", path));

    /// <summary>
    /// A stratified sample of the version, for comparing a proposed new local root against what was backed up:
    /// four buckets by length, each with a share of the budget, sampled evenly inside the bucket rather than from its
    /// head. <see cref="SamplePlan"/> owns that arithmetic, so this and the preview's verdict cannot drift apart on
    /// which rows a sample is made of.
    /// </summary>
    /// <remarks>
    /// Bounded by the request, never by the version's size: four <c>COUNT(*)</c>s, then one single-row fetch per
    /// allotted slot — at most <paramref name="max"/> of them, save for the degenerate case where <paramref name="max"/>
    /// is smaller than the number of non-empty buckets and the one-slot-per-bucket guarantee wins (at most four rows
    /// either way). Each fetch is an <c>OFFSET</c> into its bucket. A version whose entire
    /// comparable set fits inside the budget is read in one streaming pass instead — the offsets would pick every row
    /// anyway, and one scan is cheaper than N seeks to get the same answer.
    /// </remarks>
    public async Task<IReadOnlyList<IndexEntry>> SampleAsync(int version, int max, CancellationToken ct)
    {
        if (max <= 0)
            return [];

        var counts = new int[SamplePlan.BucketCount];
        var pool = 0;
        for (var bucket = 0; bucket < counts.Length; bucket++)
        {
            using var count = Command(
                $"SELECT COUNT(*) FROM entries WHERE {Current} AND unreadable_ticks IS NULL AND {SamplePlan.Predicate(bucket)}");
            Set(count, "@v", version);
            counts[bucket] = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
            pool += counts[bucket];
        }

        if (pool == 0)
            return [];

        if (pool <= max)
        {
            var all = new List<IndexEntry>(pool);
            await foreach (var entry in QueryEntriesAsync(SelectComparableEntriesSql, version, ct))
                all.Add(entry);
            return all;
        }

        var quotas = SamplePlan.Quotas(counts, max);
        var sample = new List<IndexEntry>(max);
        for (var bucket = 0; bucket < counts.Length; bucket++)
        {
            if (quotas[bucket] <= 0)
                continue;

            // One prepared statement per bucket, re-bound per offset: the picked positions are spread across the
            // bucket, so they cannot be collapsed into a single range.
            using var pick = Command(
                $"SELECT {EntryRowMapper.Columns} FROM entries WHERE {Current} AND unreadable_ticks IS NULL " +
                $"AND {SamplePlan.Predicate(bucket)} ORDER BY path_key LIMIT 1 OFFSET @n");
            Set(pick, "@v", version);
            foreach (var offset in SamplePlan.Offsets(counts[bucket], quotas[bucket]))
            {
                ct.ThrowIfCancellationRequested();
                Set(pick, "@n", offset);
                await using var reader = (SqliteDataReader)await pick.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    sample.Add(EntryRowMapper.Read(reader));
            }
        }

        return sample;
    }

    public async Task<IReadOnlyList<(string Path, int Version)>> CaseCollisionsAsync(int version, CancellationToken ct)
    {
        using var command = Command(SelectCaseCollisionsSql);
        Set(command, "@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        var rows = new List<(string, int)>();
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetInt32(1)));
        return rows;
    }

    /// <summary>What the import had to drop on the floor, path by path. Today that is only <c>duplicate</c>: a path
    /// the version names twice gets one row, the first one won, and the rest were recorded here — which is how a
    /// reader still learns that the version contradicted itself at that path, rather than being handed the arbitrary
    /// survivor as if it were authoritative.</summary>
    public async Task<IReadOnlyList<(string Path, string Issue)>> ImportIssuesAsync(int version, CancellationToken ct)
    {
        using var command = Command(SelectImportIssuesSql);
        Set(command, "@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    /// <summary>Every storage object the version references, with the source bytes its members add up to — the totals a
    /// restore has to declare to its progress tracker <em>before</em> it starts walking, because the download
    /// denominator is all-or-nothing (it may only be published when every group can answer it, and a streaming walk
    /// only learns that after the last group has gone by).</summary>
    public async IAsyncEnumerable<(StorageRef Storage, long Bytes)> StorageGroupSizesAsync(
        int version, [EnumeratorCancellation] CancellationToken ct)
    {
        using var command = Command(SelectStorageGroupSizesSql);
        Set(command, "@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return (EntryRowMapper.ReadStorage(reader)!, reader.GetInt64(6));
    }

    // ---- dedup, across every retained version ---------------------------------------------------------------

    /// <summary>Content that is already a single-file blob in the cloud. The identity is all four fields; a missing
    /// head or tail is "different content", never a wildcard, because the answer decides what an entry points at.</summary>
    public async Task<CatalogBlobHit?> FindBlobByContentAsync(string fullHash, long length, string? head, string? tail, CancellationToken ct)
    {
        using var command = Command(FindBlobByContentSql);
        Set(command, "@f", fullHash);
        Set(command, "@l", length);
        Set(command, "@h", head);
        Set(command, "@t", tail);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new CatalogBlobHit(
            reader.GetString(0),
            reader.GetInt64(1) != 0,
            (int)reader.GetInt64(2),
            EntryRowMapper.ParseVolumeSizes(reader.IsDBNull(3) ? null : reader.GetString(3)));
    }

    /// <summary>Which content holds a blob ref — collision avoidance asks this before claiming an address.</summary>
    public async Task<CatalogRefOwner?> FindRefOwnerAsync(string storageRef, CancellationToken ct)
    {
        using var command = Command(FindRefOwnerSql);
        Set(command, "@r", storageRef);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new CatalogRefOwner(
            reader.IsDBNull(0) ? "" : reader.GetString(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4) != 0);
    }

    /// <summary>Whether some retained version declares this ref's content unrecoverable: the upload side's cue to
    /// replace the blob rather than trust what is already at that address.</summary>
    public Task<bool> IsDamagedRefAsync(string storageRef, CancellationToken ct) =>
        ExistsAsync(IsDamagedRefSql, ct, ("@r", storageRef));

    /// <summary>The dedup prescreen: might there be existing content with this length and head hash? A false positive
    /// only costs one extra read; a miss compresses a file that never needed compressing.</summary>
    public Task<bool> HeadSeenAsync(long length, string headHash, CancellationToken ct) =>
        ExistsAsync(HeadSeenSql, ct, ("@l", length), ("@h", headHash));

    /// <summary>Content that already sits inside an existing pack, so a new entry can point at the member instead of packing another box.</summary>
    public async Task<CatalogPackMember?> FindPackMemberAsync(string fullHash, long length, string headHash, CancellationToken ct)
    {
        using var command = Command(FindPackMemberSql);
        Set(command, "@f", fullHash);
        Set(command, "@l", length);
        Set(command, "@h", headHash);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new CatalogPackMember(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))
            : null;
    }

    // ---- maintenance ----------------------------------------------------------------------------------------

    /// <summary>Every storage object any retained version references, blobs and packs alike, once per volume count
    /// it is recorded under: the set an
    /// orphan sweep compares the container against. The kind travels with the ref because a pack and a blob may
    /// perfectly well share one (they are addressed in different namespaces) and only the kind says which blob names
    /// the object occupies; the volume count travels with it for the reason given on
    /// <see cref="SelectDistinctRefsSql"/>.</summary>
    public async IAsyncEnumerable<(string Kind, string Ref, int Volumes)> DistinctRefsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var command = Command(SelectDistinctRefsSql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return (reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
    }

    /// <summary>Refs whose every row is reachable only through these versions — exactly what may be deleted when they
    /// are retired.</summary>
    public async Task<IReadOnlyList<string>> RefsOnlyInAsync(IReadOnlyCollection<int> versions, string kind, CancellationToken ct)
    {
        if (versions.Count == 0)
            return [];

        // Interpolated rather than parameterised because the list appears twice and its length varies; the values are
        // ints straight off the info file's version numbers, so there is nothing here a string could smuggle in.
        var list = string.Join(", ", versions.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        using var command = Command($"""
            SELECT e.storage_ref FROM entries e
            WHERE e.storage_kind=@k AND e.storage_ref IS NOT NULL
            GROUP BY e.storage_ref
            HAVING SUM(EXISTS (SELECT 1 FROM versions v WHERE v.version NOT IN ({list}) AND v.version >= e.version_from AND v.version < e.version_to)) = 0
               AND SUM(EXISTS (SELECT 1 FROM versions v WHERE v.version IN ({list}) AND v.version >= e.version_from AND v.version < e.version_to)) > 0
            ORDER BY e.storage_ref
            """);
        Set(command, "@k", kind);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        var refs = new List<string>();
        while (await reader.ReadAsync(ct))
            refs.Add(reader.GetString(0));
        return refs;
    }

    /// <summary>Every pack member still referenced by some version, once each: what compaction weighs a pack's dead
    /// weight against. The most recently introduced row for a member wins, since that is the one a restore would
    /// extract; the deduplication is on (pack, member name), so the other rows only repeat what it already says.</summary>
    public async IAsyncEnumerable<(string PackId, string EntryName, long Length, string FullHash)> LivePackMembersAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var command = Command(SelectLivePackMembersSql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        string? lastPack = null;
        string? lastName = null;
        while (await reader.ReadAsync(ct))
        {
            var pack = reader.GetString(0);
            var name = reader.GetString(1);
            if (pack == lastPack && name == lastName)
                continue;   // another row for the same member — the rows are ordered so the winner comes first

            lastPack = pack;
            lastName = name;
            yield return (pack, name, reader.GetInt64(2), reader.GetString(3));
        }
    }

    /// <summary>Which versions' entries point at a ref — repair uses it to find every index a damaged blob shows up in.</summary>
    public IAsyncEnumerable<(int Version, IndexEntry Entry)> EntriesReferencingAsync(string storageRef, CancellationToken ct) =>
        QueryVersionedEntriesAsync(SelectEntriesReferencingSql, storageRef, ct);

    public IAsyncEnumerable<(int Version, IndexEntry Entry)> PackMembersAsync(string packId, CancellationToken ct) =>
        QueryVersionedEntriesAsync(SelectPackMembersSql, packId, ct);

    /// <summary>Paths any version declares unrecoverable, once each: restore offers to substitute them from another version.</summary>
    public IAsyncEnumerable<string> UnrecoverableAnyVersionAsync(CancellationToken ct) =>
        QueryStringsAsync(SelectUnrecoverableAnyVersionSql, ct);

    // ---- plumbing -------------------------------------------------------------------------------------------

    private static string NameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>Runs a <c>SELECT EXISTS(…)</c>. The token comes before the parameters because <c>params</c> has to be last.</summary>
    private async Task<bool> ExistsAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql);
        foreach (var (name, value) in parameters)
            Set(command, name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
    }

    private async IAsyncEnumerable<string> QueryStringsAsync(string sql, [EnumeratorCancellation] CancellationToken ct)
    {
        using var command = Command(sql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return reader.GetString(0);
    }

    private async IAsyncEnumerable<(int Version, IndexEntry Entry)> QueryVersionedEntriesAsync(
        string sql, string storageRef, [EnumeratorCancellation] CancellationToken ct)
    {
        using var command = Command(sql);
        Set(command, "@r", storageRef);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return (reader.GetInt32(0), EntryRowMapper.Read(reader));
    }
}
