using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class JournalResumeTests
{
    private static JournalRecord Blob(string path, string full) => new()
    {
        Kind = "blob", Ref = "data/" + full, Path = path, FullHash = full,
        HeadHash = "h" + full, TailHash = "t" + full, Length = 100, Volumes = 1, VolumeSizes = [100],
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
    /// 同一条路径在两卷里记着不同内容（两次挂起之间文件被改过）时，胜出的必须是**新的那一卷**，
    /// 而且与两卷送进来的先后无关。
    /// <para>
    /// 不定序不会漏传（内容判据对不上就当没有，照传不误），但会让"上一轮传过的那一版这轮还算不算数"
    /// 随运行掷骰子——同样的输入两次跑出不同的重传量，这种事不该留在恢复路径上。
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
        // 中断之后文件被改过：路径还在，内容不是那一份了，绝不能复用。
        Assert.Null(r.FindBlob("a.bin", "zzz", 100, "hzzz", "tzzz"));
        // 同内容不同路径：journal 是按路径记的，索引里这是两条条目。
        Assert.Null(r.FindBlob("copy.bin", "aaa", 100, "haaa", "taaa"));
    }

    [Fact]
    public void Pack_matches_only_on_the_exact_member_set()
    {
        var m1 = new JournalMember("a.txt", "0001_a.txt", "ha", 5);
        var m2 = new JournalMember("b.txt", "0002_b.txt", "hb", 7);
        var r = new JournalResume([Pack("p000000010001", m1, m2)]);

        Assert.Equal("p000000010001", r.FindPack([m1, m2])!.Ref);
        Assert.Null(r.FindPack([m1]));                                            // 少一个成员
        Assert.Null(r.FindPack([m1, m2, new JournalMember("c.txt", "0003_c.txt", "hc", 9)]));  // 多一个
        Assert.Null(r.FindPack([m1, m2 with { FullHash = "changed" }]));           // 成员内容变了
        Assert.Null(r.FindPack([m1, m2 with { Length = 8 }]));                     // 成员长度变了
        Assert.Null(r.FindPack([m2, m1]));                                         // 同一组成员，顺序变了
    }

    [Fact]
    public void Duplicate_records_across_journals_take_the_first()
    {
        // 反复挂起/恢复会攒下多卷 journal，同一条路径可能被记过不止一次。
        var r = new JournalResume([Blob("a.bin", "aaa"), Blob("a.bin", "aaa")]);
        Assert.Equal(1, r.RecordCount);
        Assert.Equal("data/aaa", r.FindBlob("a.bin", "aaa", 100, "haaa", "taaa")!.Ref);
    }

    [Fact]
    public void Records_without_a_path_are_ignored()
    {
        // 头坏一半、字段缺失的行不该把查找表带崩。
        var r = new JournalResume([new JournalRecord { Kind = "blob", Ref = "data/x" }]);
        Assert.Null(r.FindBlob("x", "x", 1, "x", "x"));
    }
}
