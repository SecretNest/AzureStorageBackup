using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class LocalRootMigrationSampleTests
{
    private static IndexEntry Entry(string path, long length, string kind = "file",
        DateTimeOffset? unreadableAt = null) => new()
    {
        Path = path,
        Kind = kind,
        Length = length,
        Mtime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Permissions = "644",
        UnreadableAt = unreadableAt,
    };

    [Fact]
    public void Sample_Takes_Everything_When_Below_The_Cap()
    {
        var entries = Enumerable.Range(0, 30).Select(i => Entry($"f{i}", i)).ToList();

        var sample = LocalRootMigration.Sample(entries, max: 200);

        Assert.Equal(30, sample.Count);
        Assert.Equal(entries.Select(e => e.Path).OrderBy(p => p), sample.Select(e => e.Path).OrderBy(p => p));
    }

    [Fact]
    public void Sample_Never_Exceeds_The_Cap()
    {
        var entries = Enumerable.Range(0, 5000).Select(i => Entry($"f{i}", i * 1000L)).ToList();

        var sample = LocalRootMigration.Sample(entries, max: 200);

        Assert.Equal(200, sample.Count);
        Assert.Equal(200, sample.Select(e => e.Path).Distinct().Count());
    }

    /// <summary>
    /// 四档都要有代表。全压在一档上，就检不出"只有大文件那个子目录挂对了"这种半错迁移。
    /// </summary>
    [Fact]
    public void Sample_Covers_All_Four_Size_Buckets()
    {
        var entries = new List<IndexEntry>();
        for (var i = 0; i < 300; i++) entries.Add(Entry($"empty/{i}", 0));
        for (var i = 0; i < 300; i++) entries.Add(Entry($"small/{i}", 1024));
        for (var i = 0; i < 300; i++) entries.Add(Entry($"medium/{i}", 50L * 1024 * 1024));
        for (var i = 0; i < 300; i++) entries.Add(Entry($"large/{i}", 500L * 1024 * 1024));

        var sample = LocalRootMigration.Sample(entries, max: 200);

        Assert.Contains(sample, e => e.Path.StartsWith("empty/"));
        Assert.Contains(sample, e => e.Path.StartsWith("small/"));
        Assert.Contains(sample, e => e.Path.StartsWith("medium/"));
        Assert.Contains(sample, e => e.Path.StartsWith("large/"));
    }

    /// <summary>
    /// 索引顺序近似目录序：取头部会把样本全压在第一个子目录里，
    /// 于是"只挂上了其中一个子目录"恰好检不出来。必须等距铺开。
    /// </summary>
    [Fact]
    public void Sample_Spreads_Across_The_Index_Instead_Of_Taking_The_Head()
    {
        var entries = Enumerable.Range(0, 1000).Select(i => Entry($"dir{i / 100}/f{i}", 1024)).ToList();

        var sample = LocalRootMigration.Sample(entries, max: 200);

        var dirs = sample.Select(e => e.Path.Split('/')[0]).Distinct().ToList();
        Assert.Equal(10, dirs.Count);
    }

    /// <summary>
    /// UnreadableAt 条目的 size/mtime 沿用上一版本，本就不保证与磁盘一致，
    /// 拿来判定只会制造假不匹配。
    /// </summary>
    [Fact]
    public void Sample_Excludes_Entries_Carrying_UnreadableAt()
    {
        var entries = new List<IndexEntry>
        {
            Entry("good", 100),
            Entry("stale", 100, unreadableAt: DateTimeOffset.UtcNow),
        };

        var sample = LocalRootMigration.Sample(entries, max: 200);

        Assert.Single(sample);
        Assert.Equal("good", sample[0].Path);
    }

    /// <summary>某档条目数少于分配名额时，剩余名额让给其它档，不白白浪费样本。</summary>
    [Fact]
    public void Sample_Reallocates_Quota_From_Underfilled_Buckets()
    {
        var entries = new List<IndexEntry> { Entry("only-big", 500L * 1024 * 1024) };
        for (var i = 0; i < 500; i++) entries.Add(Entry($"small/{i}", 1024));

        var sample = LocalRootMigration.Sample(entries, max: 200);

        Assert.Equal(200, sample.Count);
        Assert.Contains(sample, e => e.Path == "only-big");
    }
}
