using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class JournalResumeTests
{
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

    private static JournalContent Volume(int startedAtHour, params JournalRecord[] records) => new(
        new JournalHeader
        {
            RunId = "r" + startedAtHour, ConfigId = 1, StartedAt = DateTimeOffset.UnixEpoch.AddHours(startedAtHour),
            BaselineVersion = 0, LocalRoot = "/data/src", EncryptionIdentity = "plain",
        },
        records);

    /// <summary>
    /// When the same path is recorded with different content in two volumes (the file was modified between two suspends), the
    /// winner must be **the newer volume**, regardless of the order the two are handed in.
    /// <para>
    /// Leaving it unordered loses no upload (if the content tests do not match we treat it as absent and upload anyway), but it
    /// makes "does the version uploaded last run still count this run" a dice roll per run — the same input producing different re-upload volumes on two runs has no business on the resume path.
    /// </para>
    /// </summary>
    [Fact]
    public void The_newest_volume_wins_a_path_recorded_twice()
    {
        var older = Volume(0, Blob("a.bin", "aaa"));
        var newer = Volume(1, Blob("a.bin", "zzz"));

        foreach (var volumes in new[] { new[] { older, newer }, [newer, older] })
        {
            var r = JournalResume.FromVolumes(volumes);
            Assert.Equal(1, r.RecordCount);
            Assert.Equal("data/zzz", r.FindBlob("a.bin", "zzz", 100, "hzzz", "tzzz")!.Ref);
            Assert.Null(r.FindBlob("a.bin", "aaa", 100, "haaa", "taaa"));
            Assert.Equal(["data/zzz"], r.ConfirmedBlobs().Select(b => b.Blob.Ref));
        }
    }

    [Fact]
    public void No_volumes_gives_the_empty_resume()
        => Assert.True(JournalResume.FromVolumes([]).IsEmpty);

    [Fact]
    public void Empty_resume_finds_nothing()
    {
        Assert.True(JournalResume.Empty.IsEmpty);
        Assert.Null(JournalResume.Empty.FindBlob("a.bin", "aaa", 100, "haaa", "taaa"));
    }

    [Fact]
    public void Blob_needs_path_and_content_to_both_match()
    {
        var r = new JournalResume([Blob("a.bin", "aaa")]);
        Assert.Equal("data/aaa", r.FindBlob("a.bin", "aaa", 100, "haaa", "taaa")!.Ref);
        // The file was modified after the interruption: the path is still there, the content is not that one any more, and it must never be reused.
        Assert.Null(r.FindBlob("a.bin", "zzz", 100, "hzzz", "tzzz"));
        // Same content at a different path: the journal records by path, and in the index these are two separate entries.
        Assert.Null(r.FindBlob("copy.bin", "aaa", 100, "haaa", "taaa"));
    }

    [Fact]
    public void Pack_matches_only_on_the_exact_member_set()
    {
        var m1 = new JournalMember("a.txt", "0001_a.txt", "ha", 5);
        var m2 = new JournalMember("b.txt", "0002_b.txt", "hb", 7);
        var r = new JournalResume([Pack("p000000010001", m1, m2)]);

        Assert.Equal("p000000010001", r.FindPack([m1, m2])!.Ref);
        Assert.Null(r.FindPack([m1]));                                            // one member short
        Assert.Null(r.FindPack([m1, m2, new JournalMember("c.txt", "0003_c.txt", "hc", 9)]));  // one member too many
        Assert.Null(r.FindPack([m1, m2 with { FullHash = "changed" }]));           // a member's content changed
        Assert.Null(r.FindPack([m1, m2 with { Length = 8 }]));                     // a member's length changed
        Assert.Null(r.FindPack([m2, m1]));                                         // the same member set, in a different order
    }

    [Fact]
    public void Duplicate_records_across_journals_take_the_first()
    {
        // Repeated suspend/resume piles up several journal volumes, and the same path may have been recorded more than once.
        var r = new JournalResume([Blob("a.bin", "aaa"), Blob("a.bin", "aaa")]);
        Assert.Equal(1, r.RecordCount);
        Assert.Equal("data/aaa", r.FindBlob("a.bin", "aaa", 100, "haaa", "taaa")!.Ref);
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
    public void Untouched_blob_needs_a_recorded_mtime_and_both_metadata_tests()
    {
        var mtime = DateTimeOffset.UnixEpoch.AddHours(3);
        var r = new JournalResume([Blob("a.bin", "aaa", mtime.UtcTicks)]);

        // The positive control: without it the three refusals below could all be "the path is not in the table".
        Assert.Equal("data/aaa", r.FindUntouchedBlob("a.bin", mtime, 100)!.Ref);

        Assert.Null(r.FindUntouchedBlob("a.bin", mtime.AddTicks(1), 100));  // touched: a different last-write time
        Assert.Null(r.FindUntouchedBlob("a.bin", mtime, 101));              // touched: a different length

        // The record predates the field. It cannot say whether the file has been touched, so it must not be read as
        // saying no.
        var old = new JournalResume([Blob("a.bin", "aaa")]);
        Assert.Null(old.FindUntouchedBlob("a.bin", mtime, 100));
        // …and it still takes part in the content test, which is the route it took before the field existed.
        Assert.Equal("data/aaa", old.FindBlob("a.bin", "aaa", 100, "haaa", "taaa")!.Ref);
    }

    [Fact]
    public void Records_without_a_path_are_ignored()
    {
        // A half-broken line with missing fields must not bring the lookup table down.
        var r = new JournalResume([new JournalRecord { Kind = "blob", Ref = "data/x" }]);
        Assert.Null(r.FindBlob("x", "x", 1, "x", "x"));
    }
}
