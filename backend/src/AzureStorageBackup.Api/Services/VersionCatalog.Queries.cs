using System.Globalization;
using System.Runtime.CompilerServices;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The read side of the catalog: browsing one directory at a time, the dedup lookups that used to be in-memory
/// dictionaries, and the whole-file questions retention and repair ask. Split off <c>VersionCatalog.cs</c> (which
/// holds opening, import, serialization and patching) purely for size — one class, two files.
/// </summary>
public sealed partial class VersionCatalog
{
    private const string SelectEntrySql = $"SELECT {EntryRowMapper.Columns} FROM entries WHERE version=@v AND path=@path";

    /// <summary>Ordered by the UTF-16BE <c>path_key</c>, not the <c>path</c> TEXT column: this is the cursor the diff
    /// merges against the run's scan cursor (<c>RunWorkDb.ScanOrderedAsync</c>), and both have to agree on ordinal
    /// order for a surrogate pair to land in the same place on each side. See <see cref="CatalogSql.PathKey"/>.</summary>
    private const string SelectEntriesByPathSql = $"SELECT {EntryRowMapper.Columns} FROM entries WHERE version=@v ORDER BY path_key";

    /// <summary>A range scan over the (version, path) primary key, not <c>LIKE</c>: "d" must take in "d/x" without
    /// also taking in "dd/x", and the bound is the byte right after '/'.</summary>
    private const string SelectEntriesUnderSql =
        $"SELECT {EntryRowMapper.Columns} FROM entries WHERE version=@v AND (path=@prefix OR (path>=@lo AND path<@hi)) ORDER BY seq";

    private const string SelectEntriesByStorageSql =
        $"SELECT {EntryRowMapper.Columns} FROM entries WHERE version=@v ORDER BY storage_kind, storage_ref, seq";

    /// <summary>A directory's subdirectories, each already told whether it is worth expanding, so the UI's next click is a lookup rather than a guess.</summary>
    private const string SelectChildDirsSql = """
        SELECT d.path,
               EXISTS (SELECT 1 FROM entries e WHERE e.version=@v AND e.parent=d.path)
            OR EXISTS (SELECT 1 FROM dirs c WHERE c.version=@v AND c.parent=d.path)
        FROM dirs d WHERE d.version=@v AND d.parent=@parent ORDER BY d.path
        """;

    private const string SelectChildEntriesSql = $"SELECT {EntryRowMapper.Columns} FROM entries WHERE version=@v AND parent=@parent ORDER BY path";

    private const string SelectUnreadableSql =
        "SELECT path, unreadable_ticks, unreadable_offset FROM entries WHERE version=@v AND unreadable_ticks IS NOT NULL ORDER BY seq";

    private const string SelectStatsSql = "SELECT COUNT(*), COALESCE(SUM(length), 0) FROM entries WHERE version=@v";

    /// <summary>Paths that differ only in case: on a case-insensitive restore target they would overwrite one another, so a check has to report them.</summary>
    private const string SelectCaseCollisionsSql = """
        SELECT path, version FROM entries WHERE version=@v AND path_fold IN
          (SELECT path_fold FROM entries WHERE version=@v GROUP BY path_fold HAVING COUNT(*) > 1)
        ORDER BY path_fold, path
        """;

    // Last version wins, mirroring the in-memory map that was rebuilt version by version and overwritten as it went.
    private const string FindBlobByContentSql = """
        SELECT storage_ref, raw, volumes, volume_sizes FROM entries
        WHERE full_hash=@f AND length=@l AND head_hash IS @h AND tail_hash IS @t AND storage_kind='blob' AND unrecoverable=0
        ORDER BY version DESC LIMIT 1
        """;

    // Healthy rows first (latest wins among them), damaged rows only as a fallback (earliest wins) — the precedence
    // the in-memory build had from "normal rows overwrite, damaged rows TryAdd".
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
        ORDER BY unrecoverable ASC, CASE WHEN unrecoverable THEN version ELSE -version END ASC LIMIT 1
        """;

    private const string IsDamagedRefSql =
        "SELECT EXISTS (SELECT 1 FROM entries WHERE storage_ref=@r AND storage_kind='blob' AND unrecoverable=1 " +
        "AND full_hash IS NOT NULL)";

    private const string HeadSeenSql =
        "SELECT EXISTS (SELECT 1 FROM entries WHERE length=@l AND head_hash=@h AND storage_kind='blob' " +
        "AND unrecoverable=0 AND full_hash IS NOT NULL)";

    // First version wins: references pile onto the oldest pack holding the content, which is the one compaction is
    // least likely to rewrite.
    private const string FindPackMemberSql = """
        SELECT storage_ref, COALESCE(entry_name, path), tail_hash FROM entries
        WHERE full_hash=@f AND length=@l AND head_hash=@h AND storage_kind='pack' AND unrecoverable=0
        ORDER BY version ASC, seq ASC LIMIT 1
        """;

    private const string SelectDistinctRefsSql =
        "SELECT DISTINCT storage_ref FROM entries WHERE storage_ref IS NOT NULL ORDER BY storage_ref";

    private const string SelectLivePackMembersSql = """
        SELECT storage_ref, COALESCE(entry_name, path), length, full_hash FROM entries
        WHERE storage_kind='pack' AND full_hash IS NOT NULL
        ORDER BY storage_ref, COALESCE(entry_name, path), version DESC
        """;

    private const string SelectEntriesReferencingSql =
        $"SELECT version, {EntryRowMapper.Columns} FROM entries WHERE storage_ref=@r ORDER BY version, seq";

    private const string SelectPackMembersSql =
        $"SELECT version, {EntryRowMapper.Columns} FROM entries WHERE storage_kind='pack' AND storage_ref=@r ORDER BY version, seq";

    private const string SelectUnrecoverableAnyVersionSql = "SELECT DISTINCT path FROM unrecoverable ORDER BY path";

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

    /// <summary>The directory itself and everything beneath it, in source order. An empty prefix means the whole version.</summary>
    public IAsyncEnumerable<IndexEntry> EntriesUnderAsync(int version, string dirPrefix, CancellationToken ct) =>
        dirPrefix.Length == 0
            ? QueryEntriesAsync(SelectEntriesBySeqSql, version, ct)
            : EntriesUnderCoreAsync(version, dirPrefix, ct);

    private async IAsyncEnumerable<IndexEntry> EntriesUnderCoreAsync(
        int version, string dirPrefix, [EnumeratorCancellation] CancellationToken ct)
    {
        using var command = Command(SelectEntriesUnderSql);
        Set(command, "@v", version);
        Set(command, "@prefix", dirPrefix);
        Set(command, "@lo", dirPrefix + "/");
        Set(command, "@hi", dirPrefix + "0");   // '0' is the byte right after '/'
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return EntryRowMapper.Read(reader);
    }

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
            using var command = Command($"SELECT {EntryRowMapper.Columns} FROM entries WHERE version=@v AND path IN ({placeholders})");
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

    /// <summary>Every storage ref any retained version references, blobs and packs alike: the set an orphan sweep compares the container against.</summary>
    public IAsyncEnumerable<string> DistinctRefsAsync(CancellationToken ct) =>
        QueryStringsAsync(SelectDistinctRefsSql, ct);

    /// <summary>Refs these versions reference and no other version does — exactly what may be deleted when they are retired.</summary>
    public async Task<IReadOnlyList<string>> RefsOnlyInAsync(IReadOnlyCollection<int> versions, string kind, CancellationToken ct)
    {
        if (versions.Count == 0)
            return [];

        // Interpolated rather than parameterised because the list appears twice and its length varies; the values are
        // ints straight off the info file's version numbers, so there is nothing here a string could smuggle in.
        var list = string.Join(", ", versions.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        using var command = Command($"""
            SELECT DISTINCT e.storage_ref FROM entries e
            WHERE e.version IN ({list}) AND e.storage_kind=@k AND e.storage_ref IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM entries o WHERE o.storage_ref=e.storage_ref AND o.storage_kind=@k AND o.version NOT IN ({list}))
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
    /// weight against. The newest version's copy of a member wins, since that is the one a restore would extract.</summary>
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
                continue;   // the same member in an older version — the rows are ordered so the winner comes first

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
