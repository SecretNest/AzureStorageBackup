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

    /// <summary>一个列不出内容的目录此前会让整轮备份崩在扫描阶段。它必须被记下来而不是抛出——
    /// 但**记成空目录是更糟的答案**：还原时会重建出一个空目录，其下的文件全部无声消失。
    /// 同理也不能什么都不记：diff 会因为"没扫到"把整棵子树判成删除。</summary>
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
            Assert.NotEmpty(reported.Reason); // 原因原文要带上，操作员据此判断是权限还是介质问题

            // 绝不能被当成空目录——那会让还原重建一个空壳，掩盖掉里面的文件。
            Assert.DoesNotContain("locked", result.EmptyDirs);

            // 其余部分照常扫描，不受牵连。
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

        // 只判 IsInScope 会在 docs 处就把整棵剪掉，2026 永远到不了。
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

        // docs 自身被排除，只是为了下降到 docs/2026 才走进去。它绝不能进 EmptyDirs——
        // 那会让还原凭空重建出一个用户明确排除掉的目录。docs/scratch 同理。
        var scope = ScopeRuleSet.Parse("- docs\n+ docs/2026");
        var result = await Scanner().ScanAsync(
            _root, new IgnoreRuleSet([]), new ScanOptions { Scope = scope });

        Assert.DoesNotContain("docs", result.EmptyDirs);
        Assert.DoesNotContain("docs/scratch", result.EmptyDirs);
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

        // 范围留下 photos，忽略规则再从中剔掉 .log —— 两层串联，互不干扰。
        Assert.Equal(["photos/a.jpg"], result.Entries.Select(e => e.Path));
    }
}
