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

    [Fact]
    public void Empty_resume_finds_nothing()
    {
        Assert.True(JournalResume.Empty.IsEmpty);
        Assert.False(JournalResume.Empty.MayResumeBlob("a.bin", 100, "haaa"));
        Assert.Null(JournalResume.Empty.FindBlob("a.bin", "aaa", 100, "haaa", "taaa"));
    }

    [Fact]
    public void Prescreen_matches_on_path_length_and_head()
    {
        var r = new JournalResume([Blob("a.bin", "aaa")]);
        Assert.True(r.MayResumeBlob("a.bin", 100, "haaa"));
        Assert.False(r.MayResumeBlob("b.bin", 100, "haaa"));   // 路径不同
        Assert.False(r.MayResumeBlob("a.bin", 101, "haaa"));   // 长度变了
        Assert.False(r.MayResumeBlob("a.bin", 100, "other"));  // 文件头变了
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
