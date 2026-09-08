using System.Runtime.CompilerServices;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The read side of <see cref="RunWorkDb"/>. Split out because the writer half — the channel, the batching, the fault
/// handling — is a different subject from "what questions the pipeline asks this file", and reading either one is
/// easier without the other in the way.
/// <para>
/// Every method here opens its own connection and disposes it. That is deliberate: a shared reader connection would
/// serialise the scanner's counts, the differ's cursor and the uploader's dedup probes onto one handle, and a
/// long-lived one would pin a WAL snapshot and keep the write-ahead log growing for the length of the run. WAL means
/// the open is cheap and the reader never waits for the writer.
/// </para>
/// </summary>
public sealed partial class RunWorkDb
{
    private const string SelectScanColumns =
        "path, kind, length, mtime_ticks, mtime_offset, perms, target, category, group_key";

    /// <summary>Ordered by the UTF-16BE key, not by the path text — see the class remarks on <see cref="RunWorkDb"/>
    /// for the surrogate pair that separates the two orders.</summary>
    private const string SelectScanOrderedSql = $"SELECT {SelectScanColumns} FROM scan ORDER BY path_key";

    private const string SelectScanCountSql = "SELECT COUNT(*) FROM scan";

    private const string SelectDirectoryCandidatesSql =
        "SELECT group_key, COUNT(*) FROM scan WHERE category=@category AND group_key IS NOT NULL GROUP BY group_key";

    private const string SelectCategorySql = "SELECT category FROM scan WHERE path=@path";

    private const string SelectDraftBySeqSql = $"SELECT seq, state, {EntryRowMapper.Columns} FROM draft ORDER BY seq";

    private const string SelectDraftSql = $"SELECT seq, state, {EntryRowMapper.Columns} FROM draft WHERE path=@path";

    /// <summary>One pass for all three numbers: the draft can hold millions of rows, and three separate scans of it
    /// to answer one progress line is three times the work for the same answer. The literals are
    /// <see cref="DraftState.Confirmed"/> and <see cref="DraftState.Unreadable"/> — an enum cast cannot go into a
    /// <c>const</c> string, so they are spelled out here and nowhere else.</summary>
    private const string SelectDraftStatsSql = """
        SELECT COALESCE(SUM(CASE WHEN state=1 THEN 1 ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN state=1 THEN length ELSE 0 END), 0),
               COALESCE(SUM(CASE WHEN state=2 THEN 1 ELSE 0 END), 0)
          FROM draft
        """;

    /// <summary>A range scan over the primary key, not <c>LIKE</c>: "d" must take in "d/x" without also taking in
    /// "dd/x", and the upper bound is the byte right after '/'.</summary>
    private const string SelectDraftUnreadableUnderSql =
        "SELECT path FROM draft WHERE state=@state AND (path=@prefix OR (path>=@lo AND path<@hi)) ORDER BY path";

    private const string SelectDraftUnreadableAllSql =
        "SELECT path FROM draft WHERE state=@state ORDER BY path";

    /// <summary>Everything the new version's entry for one path is built from, in the order the index has to be
    /// serialized back in: the diff's entry, the previous version's entry, and what the run recorded afterwards.
    /// The order is the <c>draft_seq</c> index's, not the table's (which is clustered by path).</summary>
    private static readonly string SelectDraftFullBySeqSql =
        "SELECT seq, state, change_kind, has_current, has_previous, override_full, override_head, override_length, " +
        "override_mtime_ticks, override_mtime_offset, tail_set, post_diff_reason, " +
        $"{EntryRowMapper.Columns}, {EntryRowMapper.PrefixedColumns(PrevPrefix)} FROM draft ORDER BY seq";

    /// <summary>Which draft rows become an entry in the new version, as one predicate the streaming read and the two
    /// counts all share — a second copy of "which rows count" that drifted would make the run report a file total the
    /// index it just wrote does not have. It is <c>RunLedger.FinalEntriesAsync</c>'s branches, in order: an entry that
    /// could not be read (this run or after the diff) survives only if there is a previous version to carry forward,
    /// and anything the run dropped, or never had a current entry for, is not written at all. The literals are
    /// <see cref="DraftState.Unreadable"/> and <see cref="DraftState.Dropped"/>, spelled out because an enum cast
    /// cannot go into a <c>const</c> string.</summary>
    private const string DraftFinalPredicate = """
        ((state=2 OR post_diff_reason IS NOT NULL) AND has_previous=1)
          OR (state NOT IN (2, 3) AND post_diff_reason IS NULL AND has_current=1)
        """;

    /// <summary>The length of the entry that row yields: a carried-forward entry weighs what the previous version
    /// said, and everything else weighs what finally went into the index — the override's length when the file
    /// changed while it was being processed.</summary>
    private const string DraftFinalLength =
        "CASE WHEN state=2 OR post_diff_reason IS NOT NULL THEN prev_length ELSE COALESCE(override_length, length) END";

    private const string SelectDraftFinalStatsSql =
        $"SELECT COUNT(*), COALESCE(SUM({DraftFinalLength}), 0) FROM draft WHERE {DraftFinalPredicate}";

    /// <summary>One pass for the whole summary. <c>prev_length</c> is summed for every kind and only read for
    /// deletions, which is where a deleted file's size still exists: a deletion is synthesized from the previous
    /// version's entry and has no current one.</summary>
    private const string SelectDraftChangeCountsSql =
        "SELECT change_kind, COUNT(*), COALESCE(SUM(prev_length), 0) FROM draft GROUP BY change_kind";

    private const string SelectDraftPathsOfKindSql =
        "SELECT path FROM draft WHERE change_kind=@change_kind ORDER BY seq";

    /// <summary>The same rows with the reason beside them. <c>reason</c> is only ever filled in for an unreadable
    /// verdict, so <c>COALESCE</c> only stands in for a row of some other kind — which this statement is never asked
    /// for.</summary>
    private const string SelectDraftPathsAndReasonsOfKindSql =
        "SELECT path, COALESCE(reason, '') FROM draft WHERE change_kind=@change_kind ORDER BY seq";

    /// <summary>A range scan over the primary key, not <c>LIKE</c>: "d" must take in "d/x" without also taking in
    /// "dd/x", and the upper bound is the byte right after '/'. Strictly <em>under</em> the directory, matching
    /// <c>PathUnder.IsUnder</c> — the directory's own row is not one of its contents.</summary>
    private const string SelectDraftKindUnderCountSql =
        "SELECT COUNT(*) FROM draft WHERE change_kind=@change_kind AND path>=@lo AND path<@hi";

    private const string SelectDraftKindCountSql =
        "SELECT COUNT(*) FROM draft WHERE change_kind=@change_kind";

    /// <summary>The whole entry is selected for a question about six of its columns, so that the column list stays
    /// <see cref="EntryRowMapper"/>'s one definition; this is a single-row lookup by primary key, and naming the
    /// storage columns a second time here would cost more than the columns do.</summary>
    private const string SelectDraftStorageSql =
        $"SELECT {EntryRowMapper.Columns} FROM draft WHERE path=@path AND storage_source=1";

    private const string SelectDraftHasOverrideSql =
        "SELECT 1 FROM draft WHERE path=@path AND override_full IS NOT NULL";

    private const string SelectDraftPostDiffUnreadableSql =
        "SELECT 1 FROM draft WHERE path=@path AND post_diff_reason IS NOT NULL";

    private const string SelectDraftPostDiffUnreadableCountSql =
        "SELECT COUNT(*) FROM draft WHERE post_diff_reason IS NOT NULL";

    private const string SelectReservationSql =
        "SELECT ref, raw, volumes, volume_sizes FROM reservations WHERE content_key=@content_key";

    /// <summary>The same row from the other side. LIMIT 1 because content addressing gives one address one content,
    /// so a second row on the same address cannot exist without dedup having already gone wrong.</summary>
    private const string SelectReservationByRefSql =
        "SELECT content_key, ref, raw, volumes, volume_sizes FROM reservations WHERE ref=@ref LIMIT 1";

    private const string SelectReservedHeadSql = "SELECT 1 FROM reserved_heads WHERE head_key=@head_key";

    private const string SelectResumeBlobColumns =
        "path, ref, full_hash, head_hash, tail_hash, length, raw, mtime_ticks, volumes, volume_sizes";

    private const string SelectResumeBlobByPathSql =
        $"SELECT {SelectResumeBlobColumns} FROM resume_blobs WHERE path=@path";

    /// <summary>A ref is not unique in this table — the previous run may have pointed two paths at one address, and
    /// dedup does exactly that. <c>ORDER BY path LIMIT 1</c> so the answer is the same row every time rather than
    /// whichever the index happened to visit first: a lookup that changes its mind between calls is worse than one
    /// that is merely arbitrary.</summary>
    private const string SelectResumeBlobByRefSql =
        $"SELECT {SelectResumeBlobColumns} FROM resume_blobs WHERE ref=@ref ORDER BY path LIMIT 1";

    private const string SelectResumeBlobByContentSql =
        $"SELECT {SelectResumeBlobColumns} FROM resume_blobs " +
        "WHERE full_hash=@full_hash AND length=@length AND head_hash=@head_hash AND tail_hash=@tail_hash";

    /// <summary>The prescreen's journal half. It asks less than <c>ResumeLedger.ConfirmedBlobsAsync</c> does — a record
    /// with a head but no tail answers yes here and would not have been a confirmed block — because the prescreen is
    /// allowed to be generous: a false positive costs one extra read of a file, a miss costs a whole compression.</summary>
    private const string SelectResumeHeadSeenSql =
        "SELECT EXISTS (SELECT 1 FROM resume_blobs WHERE length=@length AND head_hash=@head_hash)";

    private const string SelectResumePackSql =
        "SELECT ref, store_only, volumes, volume_sizes FROM resume_packs WHERE members_key=@members_key";

    private const string SelectResumePackMembersSql =
        "SELECT path, entry_name, full_hash, length FROM resume_pack_members WHERE members_key=@members_key ORDER BY seq";

    /// <summary>Blobs plus packs, which is what the in-memory resume table counted, so <c>ResumeLedger</c> reading
    /// from here reports the same number the in-memory table reported.</summary>
    private const string SelectResumeRecordCountSql =
        "SELECT (SELECT COUNT(*) FROM resume_blobs) + (SELECT COUNT(*) FROM resume_packs)";

    // ---- scan -----------------------------------------------------------------------------------------------

    /// <summary>The whole scan in ordinal path order, streamed. This is the cursor the diff merges against the
    /// previous version's entries.</summary>
    public async IAsyncEnumerable<ScanRow> ScanOrderedAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectScanOrderedSql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return ReadScan(reader);
    }

    public async Task<long> ScanCountAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectScanCountSql);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>How many candidate members each directory group has. The pipeline counts them down as the diff
    /// reaches each one, and seals the directory's pack when the count hits zero.</summary>
    public async IAsyncEnumerable<(string Dir, int Count)> DirectoryCandidatesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectDirectoryCandidatesSql);
        Set(command, "@category", (int)FileCategory.DirectoryGroup);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return (reader.GetString(0), reader.GetInt32(1));
    }

    public async Task<FileCategory?> CategoryAsync(string path, CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectCategorySql);
        Set(command, "@path", path);
        return await command.ExecuteScalarAsync(ct) is long category ? (FileCategory)category : null;
    }

    // ---- draft ----------------------------------------------------------------------------------------------

    /// <summary>The draft in source order — the order the index has to be serialized back in.</summary>
    public async IAsyncEnumerable<DraftRow> DraftOrderedBySeqAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectDraftBySeqSql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return ReadDraft(reader);
    }

    public async Task<DraftRow?> DraftAsync(string path, CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectDraftSql);
        Set(command, "@path", path);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadDraft(reader) : null;
    }

    /// <summary>The run's headline numbers: confirmed files and their bytes, and how many entries this run could not
    /// read.</summary>
    public async Task<(long Files, long Bytes, long Unreadable)> DraftStatsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectDraftStatsSql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2))
            : (0, 0, 0);
    }

    /// <summary>The unreadable paths inside one directory subtree; an empty prefix means the whole draft. A
    /// directory that could not be listed carries its whole subtree with it, which is why this is asked by prefix
    /// rather than one path at a time.</summary>
    public async IAsyncEnumerable<string> DraftUnreadableUnderAsync(
        string dir, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(
            connection, dir.Length == 0 ? SelectDraftUnreadableAllSql : SelectDraftUnreadableUnderSql);
        Set(command, "@state", (int)DraftState.Unreadable);
        if (dir.Length > 0)
        {
            Set(command, "@prefix", dir);
            Set(command, "@lo", dir + "/");
            Set(command, "@hi", dir + "0");   // '0' is the byte right after '/'
        }

        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return reader.GetString(0);
    }

    /// <summary>The draft in source order, every column of it: what <c>RunLedger.FinalEntriesAsync</c> turns into the
    /// new version's entries, one row at a time.</summary>
    public async IAsyncEnumerable<DraftFullRow> DraftFullOrderedBySeqAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectDraftFullBySeqSql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return ReadDraftFull(reader);
    }

    /// <summary>How many entries the new version will have, and what they weigh — computed over the same rows
    /// <see cref="DraftFullOrderedBySeqAsync"/> would yield an entry for, in SQL, because the caller asking for the
    /// numbers must not have to stream a million entries to count them.</summary>
    public async Task<(long Files, long Bytes)> DraftFinalStatsAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectDraftFinalStatsSql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetInt64(0), reader.GetInt64(1)) : (0, 0);
    }

    /// <summary>The run's summary line: how many paths the diff added, changed and deleted, and how many bytes went
    /// with the deletions.</summary>
    public async Task<(int New, int Modified, int Deleted, long DeletedBytes)> DraftChangeCountsAsync(
        CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectDraftChangeCountsSql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);

        var (added, modified, deleted, deletedBytes) = (0, 0, 0, 0L);
        while (await reader.ReadAsync(ct))
        {
            var count = reader.GetInt32(1);
            switch ((ChangeKind)reader.GetInt64(0))
            {
                case ChangeKind.Added: added = count; break;
                case ChangeKind.Modified: modified = count; break;
                case ChangeKind.Deleted: deleted = count; deletedBytes = reader.GetInt64(2); break;
                default: break;   // MetadataOnly / Unchanged / Unreadable: nothing this run touched
            }
        }

        return (added, modified, deleted, deletedBytes);
    }

    /// <summary>The paths the diff gave one verdict, in the order the diff emitted them.</summary>
    public async IAsyncEnumerable<string> DraftPathsOfKindAsync(
        ChangeKind kind, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectDraftPathsOfKindSql);
        Set(command, "@change_kind", (int)kind);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return reader.GetString(0);
    }

    /// <summary>The same paths with the reason the diff gave for each, in emission order. The warning the operator
    /// gets quotes the system's own words for every path — "in use", "permission denied" and "device read error" need
    /// different things done about them — so the reason travels with the path rather than being fetched per row.</summary>
    public async IAsyncEnumerable<(string Path, string Reason)> DraftPathsAndReasonsOfKindAsync(
        ChangeKind kind, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectDraftPathsAndReasonsOfKindSql);
        Set(command, "@change_kind", (int)kind);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>How many of them lie inside one directory subtree; the root ("" or ".") covers everything. A
    /// directory that could not be listed carries its whole subtree with it, which is why this is asked by prefix
    /// rather than one path at a time.</summary>
    public async Task<int> DraftKindUnderCountAsync(ChangeKind kind, string dir, CancellationToken ct)
    {
        var wholeTree = dir is "" or ".";
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(
            connection, wholeTree ? SelectDraftKindCountSql : SelectDraftKindUnderCountSql);
        Set(command, "@change_kind", (int)kind);
        if (!wholeTree)
        {
            Set(command, "@lo", dir + "/");
            Set(command, "@hi", dir + "0");   // '0' is the byte right after '/'
        }

        return (int)(long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Where <em>this run</em> put a path, or null. Storage carried over from the previous version sits in
    /// the same columns and is deliberately not an answer: the callers ask this to find out what the run itself
    /// uploaded.</summary>
    public async Task<StorageRef?> DraftStorageAsync(string path, CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectDraftStorageSql);
        Set(command, "@path", path);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? EntryRowMapper.ReadStorage(reader) : null;
    }

    public Task<bool> DraftHasOverrideAsync(string path, CancellationToken ct) =>
        DraftFlagAsync(SelectDraftHasOverrideSql, path, ct);

    public Task<bool> DraftPostDiffUnreadableAsync(string path, CancellationToken ct) =>
        DraftFlagAsync(SelectDraftPostDiffUnreadableSql, path, ct);

    public async Task<long> DraftPostDiffUnreadableCountAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectDraftPostDiffUnreadableCountSql);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<bool> DraftFlagAsync(string sql, string path, CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, sql);
        Set(command, "@path", path);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    // ---- reservations ---------------------------------------------------------------------------------------

    public async Task<ReservationRow?> ReservationAsync(string contentKey, CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectReservationSql);
        Set(command, "@content_key", contentKey);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ReservationRow(
                reader.GetString(0), reader.GetInt64(1) != 0, reader.GetInt32(2),
                EntryRowMapper.ParseVolumeSizes(EntryRowMapper.NullableString(reader, "volume_sizes")))
            : null;
    }

    /// <summary>
    /// Which content this run has already uploaded to an address, if any. Collision avoidance needs it from this
    /// side: once a finished upload's claim has left the in-flight table, this row is the only thing standing
    /// between a later file and an address whose volumes are already written.
    /// </summary>
    public async Task<(string ContentKey, ReservationRow Row)?> ReservationByRefAsync(string @ref, CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectReservationByRefSql);
        Set(command, "@ref", @ref);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), new ReservationRow(
                reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt32(3),
                EntryRowMapper.ParseVolumeSizes(EntryRowMapper.NullableString(reader, "volume_sizes"))))
            : null;
    }

    public async Task<bool> ReservedHeadAsync(string headKey, CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectReservedHeadSql);
        Set(command, "@head_key", headKey);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    // ---- resume ---------------------------------------------------------------------------------------------

    /// <summary>The record the previous run wrote for this path, if any. The caller still has to check the content
    /// or the mtime against it — this only says "a record exists", exactly as the in-memory dictionary did.</summary>
    public Task<JournalRecord?> ResumeBlobByPathAsync(string path, CancellationToken ct) =>
        ResumeBlobAsync(SelectResumeBlobByPathSql, "@path", path, ct);

    /// <summary>The record that occupies a blob ref. Dedup needs it: an address this run's journal already claimed
    /// must not be handed out again for different content.</summary>
    public Task<JournalRecord?> ResumeBlobByRefAsync(string @ref, CancellationToken ct) =>
        ResumeBlobAsync(SelectResumeBlobByRefSql, "@ref", @ref, ct);

    /// <summary>A record matched on the full content identity, so the same content at a <em>different</em> path can
    /// reuse the address the last run already took (see <c>ResumeLedger.ConfirmedBlobsAsync</c>).</summary>
    public async Task<JournalRecord?> ResumeBlobByContentAsync(
        string fullHash, long length, string headHash, string tailHash, CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectResumeBlobByContentSql);
        Set(command, "@full_hash", fullHash);
        Set(command, "@length", length);
        Set(command, "@head_hash", headHash);
        Set(command, "@tail_hash", tailHash);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadResumeBlob(reader) : null;
    }

    /// <summary>Whether the adopted journal already holds a block with this length and head hash: the second of the
    /// dedup prescreen's three sources (the catalog's retained versions and this run's own reserved heads are the
    /// other two).</summary>
    public async Task<bool> ResumeHeadSeenAsync(long length, string headHash, CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectResumeHeadSeenSql);
        Set(command, "@length", length);
        Set(command, "@head_hash", headHash);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) != 0;
    }

    private async Task<JournalRecord?> ResumeBlobAsync(
        string sql, string parameter, string value, CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, sql);
        Set(command, parameter, value);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadResumeBlob(reader) : null;
    }

    /// <summary>The pack the previous run sealed for exactly this member set, members and all. Matching is exact by
    /// <see cref="MemberKey"/>; see that method for why a partial match would be a lie about the archive.</summary>
    public async Task<JournalRecord?> ResumePackAsync(string membersKey, CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectResumePackSql);
        Set(command, "@members_key", membersKey);

        string reference;
        bool storeOnly;
        int volumes;
        IReadOnlyList<long> volumeSizes;
        await using (var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return null;
            reference = reader.GetString(0);
            storeOnly = reader.GetInt64(1) != 0;
            volumes = reader.GetInt32(2);
            volumeSizes = EntryRowMapper.ParseVolumeSizes(EntryRowMapper.NullableString(reader, "volume_sizes"));
        }

        var members = new List<JournalMember>();
        using var memberCommand = Command(connection, SelectResumePackMembersSql);
        Set(memberCommand, "@members_key", membersKey);
        await using (var reader = (SqliteDataReader)await memberCommand.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                members.Add(new JournalMember(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }

        return new JournalRecord
        {
            Kind = "pack", Ref = reference, StoreOnly = storeOnly, Members = members,
            Volumes = volumes, VolumeSizes = volumeSizes,
        };
    }

    public async Task<int> ResumeRecordCountAsync(CancellationToken ct)
    {
        await using var connection = await OpenReadAsync(ct);
        using var command = Command(connection, SelectResumeRecordCountSql);
        return (int)(long)(await command.ExecuteScalarAsync(ct))!;
    }

    // ---- plumbing -------------------------------------------------------------------------------------------

    private async Task<SqliteConnection> OpenReadAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SqliteConnection(ConnectionString(Path, create: false));
        try
        {
            await connection.OpenAsync(ct);
            ApplyPragmas(connection, writer: false);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static ScanRow ReadScan(SqliteDataReader reader) => new(
        reader.GetString(0),
        (EntryKind)reader.GetInt64(1),
        reader.GetInt64(2),
        EntryRowMapper.Dto(reader.GetInt64(3), reader.GetInt32(4)),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        (FileCategory)reader.GetInt64(7),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    private static DraftRow ReadDraft(SqliteDataReader reader) => new(
        reader.GetInt32(0), reader.GetString(reader.GetOrdinal("path")),
        (DraftState)reader.GetInt64(1), EntryRowMapper.Read(reader));

    /// <summary>Read by name throughout: this row names forty-odd columns from three different sources, and an
    /// ordinal list of that length is a transcription error waiting to happen.</summary>
    private static DraftFullRow ReadDraftFull(SqliteDataReader reader) => new(
        reader.GetInt32(reader.GetOrdinal("seq")),
        reader.GetString(reader.GetOrdinal("path")),
        (DraftState)reader.GetInt64(reader.GetOrdinal("state")),
        (ChangeKind)reader.GetInt64(reader.GetOrdinal("change_kind")),
        reader.GetInt64(reader.GetOrdinal("has_current")) != 0,
        EntryRowMapper.Read(reader),
        reader.GetInt64(reader.GetOrdinal("has_previous")) != 0 ? EntryRowMapper.Read(reader, PrevPrefix) : null,
        EntryRowMapper.NullableString(reader, "override_full") is { } full
            ? new EntryOverride(
                full,
                EntryRowMapper.NullableString(reader, "override_head"),
                EntryRowMapper.NullableLong(reader, "override_length") ?? 0,
                EntryRowMapper.Dto(
                    EntryRowMapper.NullableLong(reader, "override_mtime_ticks") ?? 0,
                    (int)(EntryRowMapper.NullableLong(reader, "override_mtime_offset") ?? 0)))
            : null,
        EntryRowMapper.NullableString(reader, "tail_set"),
        EntryRowMapper.NullableString(reader, "post_diff_reason"));

    private static JournalRecord ReadResumeBlob(SqliteDataReader reader) => new()
    {
        Kind = "blob",
        Path = reader.GetString(0),
        Ref = reader.GetString(1),
        FullHash = reader.GetString(2),
        HeadHash = EntryRowMapper.NullableString(reader, "head_hash"),
        TailHash = EntryRowMapper.NullableString(reader, "tail_hash"),
        Length = reader.GetInt64(5),
        Raw = reader.GetInt64(6) != 0,
        MtimeUtcTicks = EntryRowMapper.NullableLong(reader, "mtime_ticks"),
        Volumes = reader.GetInt32(8),
        VolumeSizes = EntryRowMapper.ParseVolumeSizes(EntryRowMapper.NullableString(reader, "volume_sizes")),
    };
}
