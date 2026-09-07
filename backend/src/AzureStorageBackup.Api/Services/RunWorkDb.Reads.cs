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

    private const string SelectReservationSql =
        "SELECT ref, raw, volumes, volume_sizes FROM reservations WHERE content_key=@content_key";

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

    private const string SelectResumePackSql =
        "SELECT ref, store_only, volumes, volume_sizes FROM resume_packs WHERE members_key=@members_key";

    private const string SelectResumePackMembersSql =
        "SELECT path, entry_name, full_hash, length FROM resume_pack_members WHERE members_key=@members_key ORDER BY seq";

    /// <summary>Blobs plus packs, which is what <c>JournalResume.RecordCount</c> counts, so a resume ledger reading
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
    /// reuse the address the last run already took (see <c>JournalResume.ConfirmedBlobs</c>).</summary>
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
