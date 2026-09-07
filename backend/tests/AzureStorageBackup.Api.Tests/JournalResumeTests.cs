using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The resume's decision rules, one at a time: what counts as "the previous run already uploaded this" and what does
/// not. They used to be asked of an in-memory <c>JournalResume</c> and are now asked of <see cref="ResumeLedger"/>
/// over a real work database — the same questions, the same answers, and the reason each rule is drawn where it is has
/// not moved either.
/// <para>
/// <see cref="ResumeLedgerTests"/> checks the two implementations against each other wholesale; these are the cases
/// worth naming, so that a rule that changes says which rule it was.
/// </para>
/// </summary>
public sealed class JournalResumeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "asb-journalresume-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => CancellationToken.None;

    public JournalResumeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leaked handle must not fail a test that already passed */ }
    }

    /// <summary>A fresh run id per call: a factory reuses the file named after the run id, so two databases in one
    /// test have to be two runs or the second one deletes the first.</summary>
    private Task<RunWorkDb> OpenAsync() =>
        new RunWorkDbFactory(_dir).CreateAsync(Guid.NewGuid().ToString("N"), Ct);

    /// <summary>Feed the records in exactly as <see cref="BackupRunControl.OpenJournalAsync"/> does, and hand back the
    /// ledger reading them. The flush matters: the writer applies in batches, and an unflushed batch is invisible.</summary>
    private static async Task<ResumeLedger> LedgerAsync(RunWorkDb work, params JournalRecord[] records)
    {
        foreach (var record in records)
            await work.InsertResumeRecordAsync(record, Ct);
        await work.FlushAsync(Ct);
        return new ResumeLedger(work);
    }

    /// <param name="mtimeTicks">Null by default, which is what every journal written before the field existed
    /// carries — see <see cref="Untouched_blob_needs_a_recorded_mtime_and_both_metadata_tests"/>.</param>
    private static JournalRecord Blob(string path, string full, long? mtimeTicks = null) => new()
    {
        Kind = "blob", Ref = "data/" + full, Path = path, FullHash = full,
        HeadHash = "h" + full, TailHash = "t" + full, Length = 100, Volumes = 1, VolumeSizes = [100],
        MtimeUtcTicks = mtimeTicks,
    };

    private static JournalRecord Pack(string packId, params JournalMember[] members) => new()
    {
        Kind = "pack", Ref = packId, Members = members, VolumeSizes = [500], Volumes = 1,
    };

    /// <summary>
    /// The same path recorded twice — repeated suspend/resume piles up several journal volumes, and the file may have
    /// been modified in between. The record fed in **first** is the one that answers.
    /// <para>
    /// That is only half the rule: what makes "first" mean "the newest volume" is the order
    /// <see cref="BackupRunControl.OpenJournalAsync"/> feeds the volumes in, and that half is pinned in
    /// <c>JournalAdoptionTests.The_newest_volume_wins_a_path_recorded_twice</c>. Here it is the ledger's half — the
    /// shadowed record is out of reach of every lookup, including the one that feeds dedup.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_first_record_for_a_path_wins()
    {
        await using var work = await OpenAsync();
        var ledger = await LedgerAsync(work, Blob("a.bin", "zzz"), Blob("a.bin", "aaa"));

        Assert.Equal(1, await ledger.RecordCountAsync(Ct));
        Assert.Equal("data/zzz", (await ledger.FindBlobAsync("a.bin", "zzz", 100, "hzzz", "tzzz", Ct))!.Ref);
        Assert.Null(await ledger.FindBlobAsync("a.bin", "aaa", 100, "haaa", "taaa", Ct));
        Assert.Equal(["data/zzz"], (await ledger.ConfirmedBlobsAsync(Ct)).Select(b => b.Blob.Ref));
    }

    [Fact]
    public async Task Blob_needs_path_and_content_to_both_match()
    {
        await using var work = await OpenAsync();
        var ledger = await LedgerAsync(work, Blob("a.bin", "aaa"));

        Assert.Equal("data/aaa", (await ledger.FindBlobAsync("a.bin", "aaa", 100, "haaa", "taaa", Ct))!.Ref);
        // The file was modified after the interruption: the path is still there, the content is not that one any more, and it must never be reused.
        Assert.Null(await ledger.FindBlobAsync("a.bin", "zzz", 100, "hzzz", "tzzz", Ct));
        // Same content at a different path: the journal records by path, and in the index these are two separate entries.
        Assert.Null(await ledger.FindBlobAsync("copy.bin", "aaa", 100, "haaa", "taaa", Ct));
    }

    [Fact]
    public async Task Pack_matches_only_on_the_exact_member_set()
    {
        var m1 = new JournalMember("a.txt", "0001_a.txt", "ha", 5);
        var m2 = new JournalMember("b.txt", "0002_b.txt", "hb", 7);
        await using var work = await OpenAsync();
        var ledger = await LedgerAsync(work, Pack("p000000010001", m1, m2));

        Assert.Equal("p000000010001", (await ledger.FindPackAsync([m1, m2], Ct))!.Ref);
        Assert.Null(await ledger.FindPackAsync([m1], Ct));                                            // one member short
        Assert.Null(await ledger.FindPackAsync(
            [m1, m2, new JournalMember("c.txt", "0003_c.txt", "hc", 9)], Ct));                        // one member too many
        Assert.Null(await ledger.FindPackAsync([m1, m2 with { FullHash = "changed" }], Ct));          // a member's content changed
        Assert.Null(await ledger.FindPackAsync([m1, m2 with { Length = 8 }], Ct));                    // a member's length changed
        Assert.Null(await ledger.FindPackAsync([m2, m1], Ct));                                        // the same member set, in a different order
    }

    /// <summary>
    /// The cheap resume test: path plus length plus mtime, no read at all. It is the rule in this class that would
    /// silently accept the wrong file if it were got wrong, and of its three ways to say no, the first is the one
    /// that matters most.
    /// <para>
    /// **A record that cannot answer must not answer.** Every journal written before the mtime field existed has a
    /// null there, and a comparison that let null through would turn this into a match on path alone — reusing last
    /// run's blob for a file that has been rewritten since, which is not a missed upload but a wrong one: the index
    /// would name content the file no longer has, and nothing downstream re-derives that. The other two are the
    /// metadata test itself, and they are what makes this exactly as strict as the diff.
    /// </para>
    /// <para>
    /// Unit-level on purpose. These four assertions used to live only in an Azurite-backed integration case, which
    /// skips wholesale on a machine without Azurite — so on such a machine the one rule that could accept the wrong
    /// file was guarded by nothing at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Untouched_blob_needs_a_recorded_mtime_and_both_metadata_tests()
    {
        var mtime = DateTimeOffset.UnixEpoch.AddHours(3);
        await using var work = await OpenAsync();
        var ledger = await LedgerAsync(work, Blob("a.bin", "aaa", mtime.UtcTicks));

        // The positive control: without it the three refusals below could all be "the path is not in the table".
        Assert.Equal("data/aaa", (await ledger.FindUntouchedBlobAsync("a.bin", mtime, 100, Ct))!.Ref);

        Assert.Null(await ledger.FindUntouchedBlobAsync("a.bin", mtime.AddTicks(1), 100, Ct));  // touched: a different last-write time
        Assert.Null(await ledger.FindUntouchedBlobAsync("a.bin", mtime, 101, Ct));              // touched: a different length

        // The record predates the field. It cannot say whether the file has been touched, so it must not be read as
        // saying no.
        await using var oldWork = await OpenAsync();
        var old = await LedgerAsync(oldWork, Blob("a.bin", "aaa"));
        Assert.Null(await old.FindUntouchedBlobAsync("a.bin", mtime, 100, Ct));
        // …and it still takes part in the content test, which is the route it took before the field existed.
        Assert.Equal("data/aaa", (await old.FindBlobAsync("a.bin", "aaa", 100, "haaa", "taaa", Ct))!.Ref);
    }

    [Fact]
    public async Task Records_without_a_path_are_ignored()
    {
        // A half-broken line with missing fields must not bring the lookup table down.
        await using var work = await OpenAsync();
        var ledger = await LedgerAsync(work, new JournalRecord { Kind = "blob", Ref = "data/x" });

        Assert.Null(await ledger.FindBlobAsync("x", "x", 1, "x", "x", Ct));
        Assert.True(await ledger.IsEmptyAsync(Ct));
    }
}
