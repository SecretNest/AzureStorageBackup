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
    /// All four buckets have to be represented. Pile everything into one bucket and a half-wrong migration like "only the subdirectory with the large files got mounted right" goes undetected.
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
    /// Index order approximates directory order: taking the head piles the whole sample into the first subdirectory,
    /// so "only one of the subdirectories got mounted" is exactly what slips through. It has to be spread out evenly.
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
    /// The size/mtime of an UnreadableAt entry are carried over from the previous version and were never guaranteed to match
    /// the disk, so judging on them only manufactures false mismatches.
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

    /// <summary>When a bucket holds fewer entries than its quota, the leftover quota goes to the other buckets rather than wasting sample budget.</summary>
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
/// Validation and tiering. Every case really runs the filesystem comparison against a temp directory — the whole value of
/// this layer is in "how it actually sees the things on disk", and testing it against a fake filesystem tests nothing.
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

    /// <summary>A size that does not match usually means the wrong directory was typed in — it counts as a mismatch just as much as "the file is not there".</summary>
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
    /// mtime is counted but never judged on: moving across filesystems often shifts it wholesale, and letting it into the
    /// verdict would turn a perfectly correct migration into a Rejected.
    /// </summary>
    [Fact]
    public void Mtime_Differences_Are_Counted_But_Never_Judged()
    {
        for (var i = 0; i < 20; i++) WriteFile($"d/f{i}", 10);
        // The index says mtime 2026-01-01, the disk says "just now", so all 20 entries differ.
        var index = Index([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect(_root, index);

        Assert.Equal(20, r.MtimeDiffers);
        Assert.Equal(nameof(LocalRootVerdict.Ok), r.Verdict);
        Assert.Equal(20, r.Matched);
    }

    /// <summary>A symlink's IndexEntry.Length is always 0 (LocalFileScanner.cs:170), so size cannot be compared.</summary>
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
    /// A symlink pointing at a directory is a symlink entry all the same (LocalFileScanner only looks at LinkTarget being
    /// non-null and does not distinguish linking to a file from linking to a directory), while FileInfo.Exists answers false
    /// for such a link. The old `link.Exists && ...` therefore judged every intact directory link as Missing, dragging the
    /// match rate of a perfectly correct migration down and forcing it onto the force path. This case pins that pitfall.
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

    /// <summary>The index says symlink but the disk holds a plain file: LinkTarget is null, so it counts as a mismatch.</summary>
    [Fact]
    public void A_Plain_File_Where_The_Index_Says_Symlink_Is_Missing()
    {
        WriteFile("d/link", 10);

        var r = LocalRootMigration.Inspect(_root, Index(Entry("d/link", 0, kind: "symlink")));

        Assert.Equal(0, r.Matched);
        Assert.Equal(1, r.Missing);
        Assert.Contains("d/link", r.Examples);
    }

    /// <summary>The link is not under the new root at all: still Missing — "we do not compare size" is no reason to wave everything through.</summary>
    [Fact]
    public void A_Symlink_That_Is_Not_There_At_All_Is_Missing()
    {
        var r = LocalRootMigration.Inspect(_root, Index(Entry("d/link", 0, kind: "symlink")));

        Assert.Equal(0, r.Matched);
        Assert.Equal(1, r.Missing);
    }

    /// <summary>
    /// If there is a baseline, we always compare. This used to be short-circuited wholesale into NoBaseline and waved through
    /// whenever "the config's current root is empty" — and an empty root is exactly the config an import without a
    /// SourceRootHint leaves behind, the one the user is most likely to be guessing a mount point for.
    /// Inspect no longer has any idea what the current root is, and this case guards that "the baseline is the only criterion left".
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

    /// <summary>An index with no comparable entries at all (every one carrying UnreadableAt) is also no baseline, not a 0% match.</summary>
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
