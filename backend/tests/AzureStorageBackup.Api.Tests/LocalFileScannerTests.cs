using System.Security.Cryptography;
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

    private static string Sha256Hex(byte[] data) => "sha256:" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

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
    public async Task Computes_FullHash_As_Sha256_Of_Whole_File()
    {
        var content = Encoding.UTF8.GetBytes("some content to hash");
        WriteFile("a.bin", content);

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]));

        Assert.Equal(Sha256Hex(content), Assert.Single(result.Entries).FullHash);
    }

    [Fact]
    public async Task HeadHash_Covers_Only_First_N_Bytes()
    {
        // Two files sharing the first 8 bytes but differing afterwards.
        var head = new byte[8];
        for (var i = 0; i < head.Length; i++) head[i] = (byte)i;
        var a = head.Concat(new byte[] { 1, 1, 1 }).ToArray();
        var b = head.Concat(new byte[] { 2, 2, 2 }).ToArray();
        WriteFile("a.bin", a);
        WriteFile("b.bin", b);

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]), new ScanOptions { HeadHashBytes = 8 });

        var ea = result.Entries.Single(e => e.Path == "a.bin");
        var eb = result.Entries.Single(e => e.Path == "b.bin");
        Assert.Equal(ea.HeadHash, eb.HeadHash);            // same head -> same headHash
        Assert.NotEqual(ea.FullHash, eb.FullHash);         // differing tail -> different fullHash
        Assert.Equal(Sha256Hex(head), ea.HeadHash);
    }

    [Fact]
    public async Task HeadHash_Equals_FullHash_When_File_Smaller_Than_Window()
    {
        var content = Encoding.UTF8.GetBytes("tiny");
        WriteFile("a.bin", content);

        var result = await Scanner().ScanAsync(_root, new IgnoreRuleSet([]), new ScanOptions { HeadHashBytes = 4096 });

        var e = Assert.Single(result.Entries);
        Assert.Equal(e.FullHash, e.HeadHash);
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
}
