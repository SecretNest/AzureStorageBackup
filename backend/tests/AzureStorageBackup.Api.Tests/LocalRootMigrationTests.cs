using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The bucket/quota arithmetic on its own. Most of it is exercised end to end by
/// <see cref="VersionCatalogSampleTests"/> against a real catalog; what is pinned here is the branch a cap of 200 can
/// never reach — a cap smaller than the number of non-empty buckets, where the "every non-empty bucket is guaranteed
/// one slot" rule and the cap contradict each other.
/// </summary>
public sealed class SamplePlanTests
{
    [Fact]
    public void A_Non_Empty_Bucket_Is_Guaranteed_One_Slot()
    {
        // 500 small files and a single large one: on pure proportion the large bucket rounds down to 0 and that file —
        // exactly the kind that goes missing when the wrong disk is mounted — is never looked at.
        var quotas = SamplePlan.Quotas([0, 500, 0, 1], max: 200);

        Assert.Equal(1, quotas[3]);
        Assert.Equal(200, quotas.Sum());
    }

    /// <summary>The guarantee outranks the cap, never the other way round: with three non-empty buckets and room for
    /// two, the cap is overshot by one rather than a whole size class going unlooked-at. An empty bucket still gets
    /// nothing, so the overshoot is bounded by the number of buckets.</summary>
    [Fact]
    public void No_Bucket_Is_Dropped_Even_When_The_Cap_Cannot_Fit_Them_All()
    {
        var quotas = SamplePlan.Quotas([10, 10, 10, 0], max: 2);

        Assert.Equal([1, 1, 1, 0], quotas);
    }

    /// <summary>Positions spread across the whole bucket, not taken from its head — see <see cref="SamplePlan.Offsets"/>.</summary>
    [Fact]
    public void Offsets_Spread_Across_The_Bucket()
    {
        Assert.Equal([0, 5, 10, 15], SamplePlan.Offsets(available: 20, quota: 4));
        Assert.Equal([0, 1, 2], SamplePlan.Offsets(available: 3, quota: 9));   // a quota bigger than the bucket takes it whole
        Assert.Empty(SamplePlan.Offsets(available: 20, quota: 0));
    }
}

/// <summary>
/// The stratified sample, now answered by SQL over the catalog instead of by walking a whole in-memory index. Every
/// case below is the corresponding case of the old <c>LocalRootMigration.Sample</c> unit tests, moved onto real rows:
/// the guarantees (cap, bucket coverage, even spread, exclusion of carried-over entries) are what the local-root
/// preview's verdict rests on, and they have to hold against the storage that now produces them, not against
/// arithmetic in isolation.
/// </summary>
public sealed class VersionCatalogSampleTests
{
    private static IndexEntry Entry(string path, long length, DateTimeOffset? unreadableAt = null) => new()
    {
        Path = path,
        Kind = "file",
        Length = length,
        Mtime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Permissions = "644",
        UnreadableAt = unreadableAt,
    };

    private static async Task<IReadOnlyList<IndexEntry>> SampleAsync(IEnumerable<IndexEntry> entries, int max)
    {
        await using var catalog = await TestCatalogs.ImportAsync(
            new VersionIndex { Version = 1, Entries = [.. entries] });
        return await catalog.SampleAsync(1, max, CancellationToken.None);
    }

    [Fact]
    public async Task Sample_Takes_Everything_When_Below_The_Cap()
    {
        var entries = Enumerable.Range(0, 30).Select(i => Entry($"f{i}", i)).ToList();

        var sample = await SampleAsync(entries, max: 200);

        Assert.Equal(30, sample.Count);
        Assert.Equal(entries.Select(e => e.Path).OrderBy(p => p), sample.Select(e => e.Path).OrderBy(p => p));
    }

    [Fact]
    public async Task Sample_Never_Exceeds_The_Cap()
    {
        var entries = Enumerable.Range(0, 5000).Select(i => Entry($"f{i}", i * 1000L)).ToList();

        var sample = await SampleAsync(entries, max: 200);

        Assert.Equal(200, sample.Count);
        Assert.Equal(200, sample.Select(e => e.Path).Distinct().Count());
    }

    /// <summary>
    /// All four buckets have to be represented. Pile everything into one bucket and a half-wrong migration like "only
    /// the subdirectory with the large files got mounted right" goes undetected.
    /// </summary>
    [Fact]
    public async Task Sample_Covers_All_Four_Size_Buckets()
    {
        var entries = new List<IndexEntry>();
        for (var i = 0; i < 300; i++) entries.Add(Entry($"empty/{i}", 0));
        for (var i = 0; i < 300; i++) entries.Add(Entry($"small/{i}", 1024));
        for (var i = 0; i < 300; i++) entries.Add(Entry($"medium/{i}", 50L * 1024 * 1024));
        for (var i = 0; i < 300; i++) entries.Add(Entry($"large/{i}", 500L * 1024 * 1024));

        var sample = await SampleAsync(entries, max: 200);

        Assert.Contains(sample, e => e.Path.StartsWith("empty/"));
        Assert.Contains(sample, e => e.Path.StartsWith("small/"));
        Assert.Contains(sample, e => e.Path.StartsWith("medium/"));
        Assert.Contains(sample, e => e.Path.StartsWith("large/"));
    }

    /// <summary>
    /// Index order approximates directory order, and the catalog preserves it as <c>seq</c>: taking the head piles the
    /// whole sample into the first subdirectory, so "only one of the subdirectories got mounted" is exactly what slips
    /// through. It has to be spread out evenly.
    /// </summary>
    [Fact]
    public async Task Sample_Spreads_Across_The_Index_Instead_Of_Taking_The_Head()
    {
        var entries = Enumerable.Range(0, 1000).Select(i => Entry($"dir{i / 100}/f{i}", 1024)).ToList();

        var sample = await SampleAsync(entries, max: 200);

        var dirs = sample.Select(e => e.Path.Split('/')[0]).Distinct().ToList();
        Assert.Equal(10, dirs.Count);
    }

    /// <summary>
    /// The size/mtime of an UnreadableAt entry are carried over from the previous version and were never guaranteed to
    /// match the disk, so judging on them only manufactures false mismatches.
    /// </summary>
    [Fact]
    public async Task Sample_Excludes_Entries_Carrying_UnreadableAt()
    {
        var sample = await SampleAsync(
            [Entry("good", 100), Entry("stale", 100, unreadableAt: DateTimeOffset.UtcNow)], max: 200);

        Assert.Single(sample);
        Assert.Equal("good", sample[0].Path);
    }

    /// <summary>When a bucket holds fewer entries than its quota, the leftover quota goes to the other buckets rather than wasting sample budget.</summary>
    [Fact]
    public async Task Sample_Reallocates_Quota_From_Underfilled_Buckets()
    {
        var entries = new List<IndexEntry> { Entry("only-big", 500L * 1024 * 1024) };
        for (var i = 0; i < 500; i++) entries.Add(Entry($"small/{i}", 1024));

        var sample = await SampleAsync(entries, max: 200);

        Assert.Equal(200, sample.Count);
        Assert.Contains(sample, e => e.Path == "only-big");
    }

    /// <summary>A version with nothing comparable in it samples nothing — which is what makes the preview answer
    /// NoBaseline rather than a 0% match rate against an index that never had anything comparable to begin with.</summary>
    [Fact]
    public async Task Sample_Of_A_Version_Whose_Entries_Are_All_Carried_Over_Is_Empty()
    {
        var sample = await SampleAsync([Entry("stale", 10, unreadableAt: DateTimeOffset.UtcNow)], max: 200);

        Assert.Empty(sample);
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

    /// <summary>The sample the catalog hands Inspect; the endpoint passes exactly this list straight through.</summary>
    private static IReadOnlyList<IndexEntry> Sample(params IndexEntry[] entries) => entries;

    [Fact]
    public void Everything_Present_And_Same_Size_Is_Ok()
    {
        for (var i = 0; i < 20; i++) WriteFile($"d/f{i}", 10);
        var sample = Sample([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect(_root, sample);

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
        var sample = Sample([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect(_root, sample);

        Assert.Equal(nameof(LocalRootVerdict.NeedsConfirm), r.Verdict);
        Assert.Equal(10, r.Matched);
        Assert.Equal(10, r.Missing);
        Assert.NotEmpty(r.Examples);
        Assert.True(r.Examples.Count <= 10, "examples are capped at 10");
    }

    [Fact]
    public void An_Empty_Directory_Is_Rejected()
    {
        var sample = Sample([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect(_root, sample);

        Assert.Equal(nameof(LocalRootVerdict.Rejected), r.Verdict);
        Assert.Equal(0, r.Matched);
        Assert.Equal(0.0, r.MatchRate);
    }

    /// <summary>A size that does not match usually means the wrong directory was typed in — it counts as a mismatch just as much as "the file is not there".</summary>
    [Fact]
    public void Size_Mismatch_Counts_As_A_Miss()
    {
        for (var i = 0; i < 20; i++) WriteFile($"d/f{i}", 99);
        var sample = Sample([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect(_root, sample);

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
        // The sample says mtime 2026-01-01, the disk says "just now", so all 20 entries differ.
        var sample = Sample([.. Enumerable.Range(0, 20).Select(i => Entry($"d/f{i}", 10))]);

        var r = LocalRootMigration.Inspect(_root, sample);

        Assert.Equal(20, r.MtimeDiffers);
        Assert.Equal(nameof(LocalRootVerdict.Ok), r.Verdict);
        Assert.Equal(20, r.Matched);
    }

    /// <summary>A symlink's IndexEntry.Length is always 0 (LocalFileScanner.ScanDirectory builds the symlink
    /// ScannedEntry with a hard-coded length of 0), so size cannot be compared.</summary>
    [Fact]
    public void Symlinks_Are_Matched_On_Existence_Only()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d"));
        File.WriteAllBytes(Path.Combine(_root, "d", "target"), new byte[123]);
        File.CreateSymbolicLink(Path.Combine(_root, "d", "link"), Path.Combine(_root, "d", "target"));

        var r = LocalRootMigration.Inspect(_root, Sample(Entry("d/link", 0, kind: "symlink"), Entry("d/target", 123)));

        Assert.Equal(nameof(LocalRootVerdict.Ok), r.Verdict);
        Assert.Equal(2, r.Matched);
    }

    /// <summary>
    /// A symlink pointing at a directory is a symlink entry all the same (LocalFileScanner only looks at LinkTarget being
    /// non-null and does not distinguish linking to a file from linking to a directory), while FileInfo.Exists answers false
    /// for such a link. The old `link.Exists &amp;&amp; ...` therefore judged every intact directory link as Missing, dragging the
    /// match rate of a perfectly correct migration down and forcing it onto the force path. This case pins that pitfall.
    /// </summary>
    [Fact]
    public void A_Symlink_Pointing_At_A_Directory_Is_Matched()
    {
        Directory.CreateDirectory(Path.Combine(_root, "d", "target"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "d", "link"), Path.Combine(_root, "d", "target"));

        var r = LocalRootMigration.Inspect(_root, Sample(Entry("d/link", 0, kind: "symlink")));

        Assert.Equal(1, r.Matched);
        Assert.Equal(0, r.Missing);
        Assert.Equal(nameof(LocalRootVerdict.Ok), r.Verdict);
    }

    /// <summary>The index says symlink but the disk holds a plain file: LinkTarget is null, so it counts as a mismatch.</summary>
    [Fact]
    public void A_Plain_File_Where_The_Index_Says_Symlink_Is_Missing()
    {
        WriteFile("d/link", 10);

        var r = LocalRootMigration.Inspect(_root, Sample(Entry("d/link", 0, kind: "symlink")));

        Assert.Equal(0, r.Matched);
        Assert.Equal(1, r.Missing);
        Assert.Contains("d/link", r.Examples);
    }

    /// <summary>The link is not under the new root at all: still Missing — "we do not compare size" is no reason to wave everything through.</summary>
    [Fact]
    public void A_Symlink_That_Is_Not_There_At_All_Is_Missing()
    {
        var r = LocalRootMigration.Inspect(_root, Sample(Entry("d/link", 0, kind: "symlink")));

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
        var r = LocalRootMigration.Inspect(_root, Sample(Entry("d/f", 10)));

        Assert.Equal(nameof(LocalRootVerdict.Rejected), r.Verdict);
        Assert.Equal(1, r.Sampled);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void A_Null_Sample_Has_Nothing_To_Compare()
    {
        var r = LocalRootMigration.Inspect(_root, sample: null);

        Assert.Equal(nameof(LocalRootVerdict.NoBaseline), r.Verdict);
        Assert.NotNull(r.Reason);
    }

    /// <summary>
    /// An empty sample is also no baseline, not a 0% match. That is what the catalog answers for a version whose every
    /// entry carries UnreadableAt — see
    /// <see cref="VersionCatalogSampleTests.Sample_Of_A_Version_Whose_Entries_Are_All_Carried_Over_Is_Empty"/> for the
    /// other half of that path.
    /// </summary>
    [Fact]
    public void An_Empty_Sample_Is_NoBaseline()
    {
        var r = LocalRootMigration.Inspect(_root, Sample());

        Assert.Equal(nameof(LocalRootVerdict.NoBaseline), r.Verdict);
        Assert.NotNull(r.Reason);
    }

    [Fact]
    public void Inspect_Never_Touches_The_New_Root()
    {
        WriteFile("d/f", 10);
        var before = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();

        LocalRootMigration.Inspect(_root, Sample(Entry("d/f", 10)));

        var after = Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories).OrderBy(x => x).ToList();
        Assert.Equal(before, after);
    }
}
