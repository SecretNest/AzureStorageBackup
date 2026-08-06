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

/// <summary>
/// 校验与分档。每个用例都在临时目录上真跑一遍文件系统比对——这层逻辑的价值
/// 全在"它到底怎么看待磁盘上的东西"，用假文件系统测等于什么都没测。
/// </summary>
public sealed class LocalRootMigrationInspectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lrm-" + Guid.NewGuid().ToString("N")[..8]);

    public LocalRootMigrationInspectTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WriteFile(string relative, long length)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[length]);
    }

    private static IndexEntry Entry(string path, long length, string kind = "file") => new()
    {
        Path = path,
        Kind = kind,
        Length = length,
        Mtime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Permissions = "644",
    };

    private static VersionIndex Index(params IndexEntry[] entries) =>
        new() { Version = 1, Entries = [.. entries] };

    [Fact]
    public void Everything_Present_And_Same_Size_Is_Ok()
    {
        for (var i = 0; i < 20; i++) WriteFile($"d/f{i}", 10);
        var index = Index([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect(_root, index);

        Assert.Equal(nameof(LocalRootVerdict.Ok), r.Verdict);
        Assert.Equal(20, r.Sampled);
        Assert.Equal(20, r.Matched);
        Assert.Equal(1.0, r.MatchRate);
        Assert.Empty(r.Examples);
    }

    [Fact]
    public void Half_The_Files_Missing_Needs_Confirmation()
    {
        for (var i = 0; i < 10; i++) WriteFile($"d/f{i}", 10);
        var index = Index([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect(_root, index);

        Assert.Equal(nameof(LocalRootVerdict.NeedsConfirm), r.Verdict);
        Assert.Equal(10, r.Matched);
        Assert.Equal(10, r.Missing);
        Assert.NotEmpty(r.Examples);
        Assert.True(r.Examples.Count <= 10, "examples are capped at 10");
    }

    [Fact]
    public void An_Empty_Directory_Is_Rejected()
    {
        var index = Index([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect(_root, index);

        Assert.Equal(nameof(LocalRootVerdict.Rejected), r.Verdict);
        Assert.Equal(0, r.Matched);
        Assert.Equal(0.0, r.MatchRate);
    }

    /// <summary>size 对不上说明多半填错了目录——它和"文件不存在"同等地算作不匹配。</summary>
    [Fact]
    public void Size_Mismatch_Counts_As_A_Miss()
    {
        for (var i = 0; i < 20; i++) WriteFile($"d/f{i}", 99);
        var index = Index([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect(_root, index);

        Assert.Equal(20, r.SizeMismatch);
        Assert.Equal(0, r.Matched);
        Assert.Equal(nameof(LocalRootVerdict.Rejected), r.Verdict);
    }

    /// <summary>
    /// mtime 只统计不判定：跨文件系统搬迁时它经常整体偏移，让它参与判定会把一次
    /// 完全正确的迁移判成 Rejected。
    /// </summary>
    [Fact]
    public void Mtime_Differences_Are_Counted_But_Never_Judged()
    {
        for (var i = 0; i < 20; i++) WriteFile($"d/f{i}", 10);
        // 索引里的 mtime 是 2026-01-01，磁盘上的是"刚刚"，20 条全都对不上。
        var index = Index([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect(_root, index);

        Assert.Equal(20, r.MtimeDiffers);
        Assert.Equal(nameof(LocalRootVerdict.Ok), r.Verdict);
        Assert.Equal(20, r.Matched);
    }

    /// <summary>symlink 的 IndexEntry.Length 恒为 0（LocalFileScanner.cs:170），不能拿 size 比。</summary>
    [Fact]
    public void Symlinks_Are_Matched_On_Existence_Only()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d"));
        File.WriteAllBytes(Path.Combine(_root, "d", "target"), new byte[123]);
        File.CreateSymbolicLink(Path.Combine(_root, "d", "link"), Path.Combine(_root, "d", "target"));

        var index = Index(Entry("d/link", 0, kind: "symlink"), Entry("d/target", 123));

        var r = LocalRootMigration.Inspect(_root, index);

        Assert.Equal(nameof(LocalRootVerdict.Ok), r.Verdict);
        Assert.Equal(2, r.Matched);
    }

    /// <summary>
    /// 指向目录的符号链接同样是 symlink 条目（LocalFileScanner 只看 LinkTarget 非空，
    /// 不区分链到文件还是链到目录），而 FileInfo.Exists 对这种链接答 false。曾经的
    /// `link.Exists && ...` 于是把每一个完好的目录链接判成 Missing，把一次完全正确的
    /// 迁移的匹配率生生压下去、逼进 force 那条路。这条用例把那个坑钉住。
    /// </summary>
    [Fact]
    public void A_Symlink_Pointing_At_A_Directory_Is_Matched()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d", "target"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "d", "link"), Path.Combine(_root, "d", "target"));

        var r = LocalRootMigration.Inspect(_root, Index(Entry("d/link", 0, kind: "symlink")));

        Assert.Equal(1, r.Matched);
        Assert.Equal(0, r.Missing);
        Assert.Equal(nameof(LocalRootVerdict.Ok), r.Verdict);
    }

    /// <summary>索引说是链接、磁盘上却是个普通文件：LinkTarget 为 null，算不匹配。</summary>
    [Fact]
    public void A_Plain_File_Where_The_Index_Says_Symlink_Is_Missing()
    {
        WriteFile("d/link", 10);

        var r = LocalRootMigration.Inspect(_root, Index(Entry("d/link", 0, kind: "symlink")));

        Assert.Equal(0, r.Matched);
        Assert.Equal(1, r.Missing);
        Assert.Contains("d/link", r.Examples);
    }

    /// <summary>链接压根不在新根下：同样是 Missing，不能因为"不比 size"就一律放行。</summary>
    [Fact]
    public void A_Symlink_That_Is_Not_There_At_All_Is_Missing()
    {
        var r = LocalRootMigration.Inspect(_root, Index(Entry("d/link", 0, kind: "symlink")));

        Assert.Equal(0, r.Matched);
        Assert.Equal(1, r.Missing);
    }

    /// <summary>
    /// 有基线就一定比对。这条曾经被"配置当前的根为空"整段短路成 NoBaseline 免检放行——
    /// 而根为空正是导入缺 SourceRootHint 的那种配置，用户对着它猜挂载点的可能性最大。
    /// Inspect 现在压根不知道当前根是什么，这条用例守住"判据只剩基线"这一点。
    /// </summary>
    [Fact]
    public void A_Usable_Baseline_Is_Always_Compared()
    {
        var r = LocalRootMigration.Inspect(_root, Index(Entry("d/f", 10)));

        Assert.Equal(nameof(LocalRootVerdict.Rejected), r.Verdict);
        Assert.Equal(1, r.Sampled);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void A_Null_Baseline_Has_Nothing_To_Compare()
    {
        var r = LocalRootMigration.Inspect(_root, baseline: null);

        Assert.Equal(nameof(LocalRootVerdict.NoBaseline), r.Verdict);
        Assert.NotNull(r.Reason);
    }

    /// <summary>索引里一条可比条目都没有（全是 UnreadableAt）也是无基线，不是 0% 匹配。</summary>
    [Fact]
    public void A_Baseline_With_No_Comparable_Entries_Is_NoBaseline()
    {
        var stale = new IndexEntry
        {
            Path = "d/f", Kind = "file", Length = 10,
            Mtime = DateTimeOffset.UnixEpoch, Permissions = "644",
            UnreadableAt = DateTimeOffset.UtcNow,
        };

        var r = LocalRootMigration.Inspect(_root, Index(stale));

        Assert.Equal(nameof(LocalRootVerdict.NoBaseline), r.Verdict);
    }

    [Fact]
    public void Inspect_Never_Touches_The_New_Root()
    {
        WriteFile("d/f", 10);
        var before = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();

        LocalRootMigration.Inspect(_root, Index(Entry("d/f", 10)));

        var after = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();
        Assert.Equal(before, after);
    }
}
