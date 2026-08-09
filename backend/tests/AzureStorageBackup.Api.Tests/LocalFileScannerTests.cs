using System.Text;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class LocalFileScannerTests : IDisposable
{
    private readonly string _root;

    public LocalFileScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string WriteFile(string relative, byte[] content)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    private string WriteText(string relative, string content) => WriteFile(relative, Encoding.UTF8.GetBytes(content));

    private LocalFileScanner Scanner() => new();

    [Fact]
    public async Task Scans_Single_File_With_Relative_Path_And_Length()
    {
        WriteText("hello.txt", "hello world");

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]));

        var entry = Assert.Single(result.Entries);
        Assert.Equal("hello.txt", entry.Path);
        Assert.Equal(EntryKind.File, entry.Kind);
        Assert.Equal(11, entry.Length);
    }

    [Fact]
    public async Task Nested_Paths_Use_Forward_Slashes_Relative_To_Root()
    {
        WriteText("sub/dir/a.txt", "x");

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]));

        var entry = Assert.Single(result.Entries);
        Assert.Equal("sub/dir/a.txt", entry.Path);
    }

    [Fact]
    public async Task Records_Modification_Time_And_Unix_Permissions()
    {
        var full = WriteText("a.txt", "x");
#pragma warning disable CA1416 // tests run on Linux only
        File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead); // 0644
#pragma warning restore CA1416

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]));

        var e = Assert.Single(result.Entries);
        Assert.Equal("0644", e.Permissions);
        Assert.Equal(File.GetLastWriteTimeUtc(full), e.ModifiedAt.UtcDateTime);
    }

    [Fact]
    public async Task Ignored_Files_Are_Excluded()
    {
        WriteText("keep.txt", "a");
        WriteText("skip.log", "b");
        WriteText("nested/deep.log", "c");

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet(["*.log"]));

        Assert.Equal(["keep.txt"], result.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task Ignored_Directory_Skips_Whole_Subtree()
    {
        WriteText("keep.txt", "a");
        WriteText("node_modules/pkg/index.js", "b");

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet(["node_modules/"]));

        Assert.Equal(["keep.txt"], result.Entries.Select(e => e.Path));
        Assert.DoesNotContain(result.EmptyDirs, d => d.StartsWith("node_modules"));
    }

    [Fact]
    public async Task Symlinks_Are_Skipped_By_Default()
    {
        WriteText("real.txt", "a");
        File.CreateSymbolicLink(Path.Combine(_root, "link.txt"), Path.Combine(_root, "real.txt"));

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]));

        Assert.Equal(["real.txt"], result.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task Symlinks_Included_With_Target_When_Opted_In()
    {
        WriteText("real.txt", "a");
        File.CreateSymbolicLink(Path.Combine(_root, "link.txt"), Path.Combine(_root, "real.txt"));

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]), new ScanOptions { IncludeSymlinks = true });

        var link = result.Entries.Single(e => e.Path == "link.txt");
        Assert.Equal(EntryKind.Symlink, link.Kind);
        Assert.Equal(Path.Combine(_root, "real.txt"), link.Target);
    }

    [Fact]
    public async Task Empty_Leaf_Directory_Is_Recorded()
    {
        Directory.CreateDirectory(Path.Combine(_root, "emptydir"));

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]));

        Assert.Empty(result.Entries);
        Assert.Equal(["emptydir"], result.EmptyDirs);
    }

    [Fact]
    public async Task Only_Deepest_Empty_Directory_Is_Recorded()
    {
        // a/b/c empty chain: only the deepest leaf needs recording (mkdir -p recreates parents).
        Directory.CreateDirectory(Path.Combine(_root, "a", "b", "c"));

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]));

        Assert.Equal(["a/b/c"], result.EmptyDirs);
    }

    [Fact]
    public async Task Directory_Containing_Files_Is_Not_An_Empty_Dir()
    {
        WriteText("dir/file.txt", "a");

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]));

        Assert.Empty(result.EmptyDirs);
    }

    /// <summary>A directory whose contents cannot be listed used to crash the whole backup run in the scan stage. It has to be
    /// recorded rather than thrown — but **recording it as an empty directory is the worse answer**: restore would recreate an empty
    /// directory with every file beneath it silently gone. By the same token, recording nothing is no good either: diff would judge the whole subtree deleted because it "wasn't scanned".</summary>
    [SkippableFact]
    public async Task An_Unreadable_Directory_Is_Recorded_Instead_Of_Throwing()
    {
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");
        WriteText("ok/keep.txt", "readable");
        WriteText("locked/secret.txt", "unreachable");
        var locked = Path.Combine(_root, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]));

            var reported = Assert.Single(result.Unreadable);
            Assert.Equal("locked", reported.Path);
            Assert.True(reported.IsDirectory);
            Assert.NotEmpty(reported.Reason); // The verbatim reason has to come along, so the operator can tell a permission problem from a media one

            // It must never be treated as an empty directory — that would have restore recreate an empty shell, hiding the files inside it.
            Assert.DoesNotContain("locked", result.EmptyDirs);

            // The rest is scanned as usual, unaffected.
            Assert.Contains(result.Entries, e => e.Path == "ok/keep.txt");
            Assert.DoesNotContain(result.Entries, e => e.Path.StartsWith("locked/", StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(locked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Scope_Prunes_Whole_Subtrees()
    {
        WriteText("photos/a.jpg", "x");
        WriteText("music/b.mp3", "y");

        var scope = ScopeRuleSet.Parse("-\n+ photos");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet([]), new ScanOptions { Scope = scope });

        Assert.Equal(["photos/a.jpg"], result.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task Scope_Descends_Into_An_Excluded_Directory_To_Reach_A_Re_Included_One()
    {
        WriteText("docs/2025/old.pdf", "x");
        WriteText("docs/2026/q1.pdf", "y");

        // Judging on IsInScope alone would prune the whole tree at docs, and 2026 would never be reached.
        var scope = ScopeRuleSet.Parse("- docs\n+ docs/2026");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet([]), new ScanOptions { Scope = scope });

        Assert.Equal(["docs/2026/q1.pdf"], result.Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task A_Directory_Only_Passed_Through_Is_Not_Recorded_As_Empty()
    {
        WriteText("docs/2026/q1.pdf", "y");
        Directory.CreateDirectory(Path.Combine(_root, "docs", "scratch"));

        // docs itself is excluded and is only entered in order to descend to docs/2026. It must never enter EmptyDirs —
        // that would have restore conjure up a directory the user explicitly excluded. Same for docs/scratch.
        var scope = ScopeRuleSet.Parse("- docs\n+ docs/2026");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet([]), new ScanOptions { Scope = scope });

        Assert.DoesNotContain("docs", result.EmptyDirs);
        Assert.DoesNotContain("docs/scratch", result.EmptyDirs);
    }

    [Fact]
    public async Task A_Directory_That_Ends_With_Zero_Kept_Children_And_Is_Itself_Out_Of_Scope_Is_Not_Recorded_As_Empty()
    {
        // docs holds only one excluded file and no docs/2026 — so after descending into docs, keptChildren really does end at 0
        // (unlike the "passed through" case, where docs/2026/q1.pdf propped keptChildren up).
        // But docs still has to be descended into, because the `+ docs/2026` rule hangs beneath it (MayContainIncluded is true).
        // This is the scenario that genuinely reaches the `if (keptChildren == 0 && !IsInScope(self))` branch:
        // deleting the two guard lines would not fail the case above, but it would fail this one.
        WriteText("docs/other.txt", "x");

        var scope = ScopeRuleSet.Parse("- docs\n+ docs/2026");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet([]), new ScanOptions { Scope = scope });

        Assert.DoesNotContain("docs", result.EmptyDirs);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task An_In_Scope_Empty_Directory_Is_Still_Recorded()
    {
        Directory.CreateDirectory(Path.Combine(_root, "photos", "empty"));

        var scope = ScopeRuleSet.Parse("-\n+ photos");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet([]), new ScanOptions { Scope = scope });

        Assert.Contains("photos/empty", result.EmptyDirs);
    }

    [Fact]
    public async Task Scope_And_Ignore_Apply_Independently()
    {
        WriteText("photos/a.jpg", "x");
        WriteText("photos/debug.log", "y");
        WriteText("music/c.mp3", "z");

        var scope = ScopeRuleSet.Parse("-\n+ photos");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet(["*.log"]), new ScanOptions { Scope = scope });

        // Scope keeps photos, then the ignore rules strip .log out of it — two layers in series, neither interfering with the other.
        Assert.Equal(["photos/a.jpg"], result.Entries.Select(e => e.Path));
    }
}
