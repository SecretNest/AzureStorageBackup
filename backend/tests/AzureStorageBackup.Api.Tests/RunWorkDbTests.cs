using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using static AzureStorageBackup.Api.Tests.IndexAssert;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The per-run scratch database. Everything a run used to hold in dictionaries — the scan, the draft of the new
/// version, the dedup reservations, the journal's records — now lives in a file, so these tests pin the properties
/// the in-memory structures gave for free and SQL does not: the scan's <em>ordinal</em> path order (the diff merges
/// two cursors with <c>string.CompareOrdinal</c>, so a byte-order cursor would silently mismatch), updates landing in
/// the order they were enqueued even though a background task applies them, a writer fault reaching the caller rather
/// than quietly dropping rows, and readers seeing committed writes while the writer is still running.
/// </summary>
public sealed class RunWorkDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "asb-runwork-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => CancellationToken.None;

    public RunWorkDbTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leaked handle must not fail a test that already passed */ }
    }

    private Task<RunWorkDb> OpenAsync(string runId = "run") => new RunWorkDbFactory(_dir).CreateAsync(runId, Ct);

    private static readonly DateTimeOffset Mtime = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));

    private static ScanRow Scan(
        string path, FileCategory category = FileCategory.SingleFile, string? groupKey = null, long length = 10) =>
        new(path, EntryKind.File, length, Mtime, "0644", null, category, groupKey);

    private static IndexEntry Entry(string path, long length = 10) =>
        new() { Path = path, Kind = "file", Length = length, Mtime = Mtime, Permissions = "0644" };

    private static JournalRecord Blob(
        string path, string blobRef, string? fullHash, long length = 10, string head = "head", string tail = "tail") =>
        new()
        {
            Kind = "blob", Ref = blobRef, Path = path, FullHash = fullHash, HeadHash = head, TailHash = tail,
            Length = length, Raw = true, MtimeUtcTicks = 12345, Volumes = 2, VolumeSizes = [7, 3],
        };

    // ---- Test 1: the scan cursor is in ordinal order, not UTF-8 byte order -----------------------------------

    /// <summary>
    /// The diff walks the scan and the previous version side by side and compares the two heads with
    /// <c>string.CompareOrdinal</c>; if the scan comes back in any other order, the merge mistakes "later" for
    /// "missing" and reports deletions that never happened. The surrogate pair is the case that separates the two
    /// candidate orders: ordinal (UTF-16) puts U+1F600 before U+FFFF, UTF-8 byte order puts it after.
    /// </summary>
    [Fact]
    public async Task Scan_rows_come_back_in_ordinal_path_order()
    {
        string[] paths = ["b", "a/x", "a-x", "A", "z\U0001F600", "z\uFFFF"];

        await using var db = await OpenAsync();
        foreach (var path in paths)
            await db.InsertScanAsync(Scan(path), Ct);
        await db.FlushAsync(Ct);

        var got = new List<string>();
        await foreach (var row in db.ScanOrderedAsync(Ct))
            got.Add(row.Path);

        Assert.Equal([.. paths.Order(StringComparer.Ordinal)], got);
        // Spelled out as well, so a change of collation cannot pass by agreeing with a comparer that also changed.
        Assert.Equal(["A", "a-x", "a/x", "b", "z\U0001F600", "z\uFFFF"], got);
        Assert.Equal(paths.Length, await db.ScanCountAsync(Ct));
        Assert.Equal(FileCategory.SingleFile, await db.CategoryAsync("a/x", Ct));
        Assert.Null(await db.CategoryAsync("nosuch", Ct));
    }

    // ---- Test 2: the draft's updates land in the order they were enqueued -------------------------------------

    [Fact]
    public async Task Draft_updates_apply_in_enqueue_order()
    {
        await using var db = await OpenAsync();
        await db.InsertDraftAsync(0, "p", DraftState.Pending, Entry("p", 10), Ct);
        await db.InsertDraftAsync(1, "d/q", DraftState.Confirmed, Entry("d/q", 40), Ct);
        await db.InsertDraftAsync(2, "d/r", DraftState.Unreadable, Entry("d/r", 5), Ct);

        var storage = new StorageRef { Kind = "blob", Ref = "data/abc", Volumes = 2, Raw = true, VolumeSizes = [7, 3] };
        await db.UpdateDraftStorageAsync("p", storage, Ct);
        // Twice, with different values: the writer applies these on a background task, and the row must end up
        // holding what the *last* caller said, not whichever update the batch happened to run first.
        await db.UpdateDraftTailAsync("p", "stale-tail-hash", Ct);
        await db.UpdateDraftTailAsync("p", "tail-hash", Ct);
        await db.UpdateDraftOverrideAsync("p", "full-hash", "head-hash", 99, Mtime.AddDays(1), Ct);
        await db.FlushAsync(Ct);

        var row = await db.DraftAsync("p", Ct);
        Assert.NotNull(row);
        Assert.Equal(0, row.Seq);
        Assert.Equal(DraftState.Pending, row.State);
        AssertSameEntry(
            Entry("p") with
            {
                Length = 99, Mtime = Mtime.AddDays(1),
                HeadHash = "head-hash", TailHash = "tail-hash", FullHash = "full-hash", Storage = storage,
            },
            row.Entry);

        var bySeq = new List<DraftRow>();
        await foreach (var d in db.DraftOrderedBySeqAsync(Ct))
            bySeq.Add(d);
        Assert.Equal(["p", "d/q", "d/r"], bySeq.Select(d => d.Path));
        Assert.Equal(DraftState.Unreadable, bySeq[2].State);

        // Files/Bytes count the confirmed rows only; "p" is still pending and "d/r" is unreadable.
        Assert.Equal((1L, 40L, 1L), await db.DraftStatsAsync(Ct));

        var unreadable = new List<string>();
        await foreach (var path in db.DraftUnreadableUnderAsync("d", Ct))
            unreadable.Add(path);
        Assert.Equal(["d/r"], unreadable);
    }

    // ---- Test 3: a writer fault reaches the caller ------------------------------------------------------------

    /// <summary>
    /// The writes are applied on a background task, so a failing statement has nobody to throw at. Swallowing it
    /// would leave the run building a version out of a draft that is missing rows — the one failure mode this
    /// database must not have.
    /// </summary>
    [Fact]
    public async Task Flush_surfaces_a_writer_fault()
    {
        await using var db = await OpenAsync();
        await db.InsertScanAsync(Scan("ok"), Ct);
        await db.EnqueueRawSqlAsync("INSERT INTO scan (path, path_key) VALUES ('bad', NULL)", Ct);

        await Assert.ThrowsAsync<SqliteException>(() => db.FlushAsync(Ct));
    }

    // ---- Test 3b: a fault outside the batch's own try still reaches the caller ---------------------------------

    /// <summary>
    /// Not every way a batch can fail is a failing statement. <c>BEGIN</c> itself can be refused — another writer
    /// holds the file (<c>SQLITE_BUSY</c>), or the disk is gone (<c>SQLITE_IOERR</c>) — and that happens before a
    /// single statement of the batch has run. An escape there would kill the writer task with nobody left reading
    /// the channel: the caller inside <see cref="RunWorkDb.FlushAsync"/> would be waiting on a marker that can never
    /// be completed, which is a hang, not an error.
    /// <para>
    /// The batch here carries <em>only</em> the flush marker, no statements at all, so the only step that can fail
    /// is the <c>BEGIN</c> — if it ever stopped being the failing step, this test would go green by finishing rather
    /// than by silently exercising something else. The timeout token is what turns a regression into a failure
    /// instead of a hung test run.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Flush_surfaces_a_failure_to_open_the_batch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var db = await OpenAsync();

        // Refuse the writer's connection the right to write, through the same channel so it lands on that very
        // connection. The batch carrying it has already taken its write lock, so it still commits; the *next*
        // batch's BEGIN is the one that cannot start. A held lock from a second connection would say the same thing
        // with SQLITE_BUSY, but Microsoft.Data.Sqlite retries busy for its 30 s command timeout first, and a test
        // that has to outwait that is a test that fails by hanging.
        await db.EnqueueRawSqlAsync("PRAGMA query_only=1", timeout.Token);
        await db.FlushAsync(timeout.Token);

        await Assert.ThrowsAsync<SqliteException>(() => db.FlushAsync(timeout.Token));
    }

    // ---- Test 4: how many candidates each directory group still has -------------------------------------------

    [Fact]
    public async Task DirectoryCandidates_counts_per_group_key()
    {
        await using var db = await OpenAsync();
        await db.InsertScanAsync(Scan("d/a", FileCategory.DirectoryGroup, "d"), Ct);
        await db.InsertScanAsync(Scan("d/b", FileCategory.DirectoryGroup, "d"), Ct);
        await db.InsertScanAsync(Scan("e/c", FileCategory.DirectoryGroup, "e"), Ct);
        await db.InsertScanAsync(Scan("d/big"), Ct);                                    // single file, not a candidate
        await db.InsertScanAsync(Scan("d/x", FileCategory.CrossDirectoryGroup), Ct);     // cross-directory, no group key
        await db.FlushAsync(Ct);

        var counts = new List<(string Dir, int Count)>();
        await foreach (var candidate in db.DirectoryCandidatesAsync(Ct))
            counts.Add(candidate);

        Assert.Equal([("d", 2), ("e", 1)], [.. counts.OrderBy(c => c.Dir, StringComparer.Ordinal)]);
    }

    // ---- Test 5: the resume table keeps the first record for a path -------------------------------------------

    /// <summary>
    /// <c>LegacyJournalResume.BuildBlobs</c>, the dictionary this replaced, did this with <c>TryAdd</c> over
    /// volumes already sorted newest first, so the first record wins. The table has to keep that rule, or a resume would answer with the older upload.
    /// </summary>
    [Fact]
    public async Task Resume_blob_by_path_keeps_the_first_record()
    {
        await using var db = await OpenAsync();
        await db.InsertResumeRecordAsync(Blob("p", "data/new", "full-new"), Ct);
        await db.InsertResumeRecordAsync(Blob("p", "data/old", "full-old"), Ct);
        await db.InsertResumeRecordAsync(Blob("q", "data/q", fullHash: null), Ct);   // no content identity: not a resume point
        // Dedup points two paths at one address all the time, so a ref is not unique here.
        await db.InsertResumeRecordAsync(Blob("a", "data/new", "full-new"), Ct);
        await db.FlushAsync(Ct);

        var kept = await db.ResumeBlobByPathAsync("p", Ct);
        Assert.NotNull(kept);
        Assert.Equal("data/new", kept.Ref);
        Assert.Equal("full-new", kept.FullHash);
        Assert.Equal(12345, kept.MtimeUtcTicks);
        Assert.True(kept.Raw);
        Assert.Equal(2, kept.Volumes);
        Assert.Equal<long[]>([7, 3], [.. kept.VolumeSizes]);

        Assert.Null(await db.ResumeBlobByPathAsync("q", Ct));
        Assert.Equal(2, await db.ResumeRecordCountAsync(Ct));   // "p" and "a"; "q" was never stored

        var byContent = await db.ResumeBlobByContentAsync("full-new", 10, "head", "tail", Ct);
        Assert.Equal("data/new", byContent?.Ref);
        Assert.Null(await db.ResumeBlobByContentAsync("full-new", 11, "head", "tail", Ct));

        // Two paths hold "data/new", so the lookup must pick the same one every time rather than whichever row the
        // index happened to reach first: the lowest path.
        Assert.Equal("a", (await db.ResumeBlobByRefAsync("data/new", Ct))?.Path);
        Assert.Null(await db.ResumeBlobByRefAsync("data/old", Ct));
    }

    // ---- Test 6: a pack is matched by its member set ----------------------------------------------------------

    [Fact]
    public async Task Resume_pack_matches_by_members_key()
    {
        JournalMember[] members =
        [
            new("a.txt", "a.txt", "hash-a", 11),
            new("b.txt", "b.txt", "hash-b", 22),
        ];
        var pack = new JournalRecord
        {
            Kind = "pack", Ref = "p0007", StoreOnly = true, Members = members, Volumes = 3, VolumeSizes = [1, 2, 3],
        };

        await using var db = await OpenAsync();
        await db.InsertResumeRecordAsync(pack, Ct);
        await db.InsertResumeRecordAsync(pack with { Ref = "p0008" }, Ct);   // same member set: the first wins
        await db.FlushAsync(Ct);

        var found = await db.ResumePackAsync(RunWorkDb.MemberKey(members), Ct);
        Assert.NotNull(found);
        Assert.Equal("p0007", found.Ref);
        Assert.True(found.StoreOnly);
        Assert.Equal(3, found.Volumes);
        Assert.Equal<long[]>([1, 2, 3], [.. found.VolumeSizes]);
        Assert.Equal((IEnumerable<JournalMember>)members, found.Members);

        // Order is part of the key: PackInfo.Members is written in member order, so a reversed set is a different pack.
        Assert.Null(await db.ResumePackAsync(RunWorkDb.MemberKey([members[1], members[0]]), Ct));
        Assert.Equal(1, await db.ResumeRecordCountAsync(Ct));
    }

    // ---- Test 7: the scratch file does not outlive the run -----------------------------------------------------

    [Fact]
    public async Task Dispose_deletes_the_files()
    {
        var factory = new RunWorkDbFactory(_dir);
        var db = await factory.CreateAsync("run-a", Ct);
        var path = db.Path;
        await db.InsertScanAsync(Scan("x"), Ct);
        await db.FlushAsync(Ct);
        Assert.True(File.Exists(path));

        await db.DisposeAsync();

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        // The file is gone, so a flush cannot mean anything any more; it says so rather than pretending to succeed.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => db.FlushAsync(Ct));

        // What a killed process leaves behind is cleared at startup, the way DiffWorkQueue.ClearStale does it.
        await File.WriteAllTextAsync(Path.Combine(_dir, "stale.db"), "junk", Ct);
        await File.WriteAllTextAsync(Path.Combine(_dir, "stale.db-wal"), "junk", Ct);
        // The side databases a run opens beside its work database (PackLeaderStore's {runId}.aliases.db, named by
        // SidePath) are named from the same run id and are as much "left by a run that is over" as the work
        // database itself — the same sweep has to take them.
        await File.WriteAllTextAsync(factory.SidePath("run-a", "aliases"), "junk", Ct);
        RunWorkDbFactory.ClearStale(_dir);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    // ---- Test 8: reads run against a live writer ---------------------------------------------------------------

    /// <summary>
    /// The whole pipeline reads this database while the scanner is still filling it, so a reader must not have to
    /// wait for the writer to close. WAL plus a connection per read is what buys that; this pins it at a size where
    /// several write batches have to have been committed.
    /// </summary>
    [Fact]
    public async Task Reads_see_committed_writes_while_the_writer_is_open()
    {
        await using var db = await OpenAsync();
        for (var i = 0; i < 5_000; i++)
            await db.InsertScanAsync(Scan($"f/{i:D5}"), Ct);
        await db.InsertReservationAsync("content-key", new ReservationRow("data/abc", Raw: true, Volumes: 2, [7, 3]), Ct);
        await db.InsertReservedHeadAsync("head-key", Ct);
        await db.FlushAsync(Ct);

        Assert.Equal(5_000, await db.ScanCountAsync(Ct));

        var streamed = 0;
        await foreach (var _ in db.ScanOrderedAsync(Ct))
            streamed++;
        Assert.Equal(5_000, streamed);

        // Field by field: ReservationRow's generated equality compares the volume-size list by reference.
        var reservation = await db.ReservationAsync("content-key", Ct);
        Assert.NotNull(reservation);
        Assert.Equal("data/abc", reservation.Ref);
        Assert.True(reservation.Raw);
        Assert.Equal(2, reservation.Volumes);
        Assert.Equal<long[]>([7, 3], [.. reservation.VolumeSizes]);
        Assert.Null(await db.ReservationAsync("other", Ct));
        Assert.True(await db.ReservedHeadAsync("head-key", Ct));
        Assert.False(await db.ReservedHeadAsync("other", Ct));

        // Still usable after all that reading: the writer was never closed.
        await db.InsertScanAsync(Scan("f/last"), Ct);
        await db.FlushAsync(Ct);
        Assert.Equal(5_001, await db.ScanCountAsync(Ct));
    }
}
